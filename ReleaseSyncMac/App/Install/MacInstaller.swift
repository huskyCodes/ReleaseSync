import AppKit
import CoreServices
import Foundation
import ReleaseSyncKit

struct InstallProgress: Sendable, Equatable {
    /// 0...1, or nil when it can't be measured.
    var fraction: Double?
    var text: String
}

enum InstallError: Error, LocalizedError, Equatable {
    /// The person has to take it from here (a license to read, an installer that asks for their password).
    case needsManual(String)
    case appRunning(name: String, bundleId: String)
    case blocked(String)
    case skipped(String)
    case failed(String)
    case cancelled

    var errorDescription: String? {
        switch self {
        case .needsManual(let m), .blocked(let m), .skipped(let m), .failed(let m): return m
        case .appRunning(let name, _): return "\(name) is open. Quit it first, or let ReleaseSync quit it."
        case .cancelled: return "Cancelled."
        }
    }
}

struct InstallPaths: Sendable {
    /// Where new apps go (/Applications; ~/Applications when that isn't writable).
    var applications: URL
    /// Where the version being replaced is kept, so it can be put back.
    var backups: URL
    /// Where downloads are kept while they are used.
    var downloads: URL

    static func standard() -> InstallPaths {
        let fm = FileManager.default
        let support = (fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? fm.temporaryDirectory).appendingPathComponent("ReleaseSync", isDirectory: true)
        let caches = (fm.urls(for: .cachesDirectory, in: .userDomainMask).first ?? fm.temporaryDirectory).appendingPathComponent("ReleaseSync", isDirectory: true)
        return InstallPaths(applications: URL(fileURLWithPath: "/Applications", isDirectory: true), backups: support.appendingPathComponent("Backups", isDirectory: true),
                            downloads: caches.appendingPathComponent("Downloads", isDirectory: true))
    }
}

struct InstallResult: Sendable {
    /// The app that was installed; nil when the install was handed to the person.
    var record: InstallRecord?
    var assessment: Assessment?
    /// Set when ReleaseSync opened something for the person to finish (a package installer).
    var handedOff: String?
}

/// Downloads a release file, checks it, and puts the app on this Mac. macOS's own tools do the work (hdiutil, ditto, codesign, spctl); nothing
/// goes through a shell, nothing is installed before it has been checked, and the version being replaced is kept so it can be put back.
final class MacInstaller: @unchecked Sendable {
    let paths: InstallPaths
    let store: InstallStore
    private let runner: ProcessRunning
    private let downloader: Downloading
    private let keepBackups: Int
    var keepDownloads = false
    /// How an app is thrown away (the Trash; tests use their own).
    var trash: @Sendable (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }
    /// Marks installed apps as downloaded from the internet, so macOS asks the person once the first time they open it (like a browser download).
    var applyQuarantine = true
    private let fm = FileManager.default

    init(paths: InstallPaths = .standard(), store: InstallStore = InstallStore(url: InstallStore.defaultURL()), runner: ProcessRunning = ProcessRunner(),
         downloader: Downloading = HTTPSDownloader(), keepBackups: Int = 2) {
        self.paths = paths; self.store = store; self.runner = runner; self.downloader = downloader; self.keepBackups = keepBackups
    }

    struct Request: Sendable {
        var key: String
        var repo: String
        var displayName: String?
        var tag: String
        var asset: AssetInfo
        /// The release's other files (checksum files live among them).
        var siblings: [AssetInfo]
        var trustUnsigned = false
        /// Nobody is there to answer questions: anything that needs a person's say-so is skipped.
        var unattended = false
        /// Quit the app if it is open (the person said so).
        var quitRunningApp = false
        /// Where the app already is, to update it in place.
        var existingPath: String?
    }

    // MARK: install

    func install(_ r: Request, progress: @escaping @Sendable (InstallProgress) -> Void, confirm: @escaping @Sendable ([String]) async -> Bool) async throws -> InstallResult {
        guard let url = URL(string: r.asset.url) else { throw InstallError.failed("This download has no usable address.") }
        let kind = MacAssets.kind(of: r.asset.name)
        guard kind != .unknown else { throw InstallError.failed("ReleaseSync can't install \(r.asset.name) on a Mac. It handles .dmg, .pkg and .zip files.") }

        let work = paths.downloads.appendingPathComponent(InstallPlanner.safeName(r.key), isDirectory: true).appendingPathComponent(InstallPlanner.safeName(r.tag.isEmpty ? "latest" : r.tag), isDirectory: true)
        try? fm.removeItem(at: work)
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        let file = work.appendingPathComponent(InstallPlanner.safeName(r.asset.name))
        var keepWork = false
        defer { if !keepWork && !keepDownloads { try? fm.removeItem(at: work) } }

        progress(InstallProgress(fraction: 0, text: "Downloading…"))
        do {
            try await downloader.download(url, to: file, expectedSize: r.asset.size) { p in
                let f = p.total > 0 ? Double(p.done) / Double(p.total) : nil
                progress(InstallProgress(fraction: f.map { min($0, 1) * 0.8 }, text: "Downloading \(Fmt.size(p.done))" + (p.total > 0 ? " of \(Fmt.size(p.total))" : "")))
            }
        } catch is CancellationError { throw InstallError.cancelled }
        catch { throw InstallError.failed((error as? LocalizedError)?.errorDescription ?? error.localizedDescription) }
        try Task.checkCancellation()

        progress(InstallProgress(fraction: 0.82, text: "Checking the download…"))
        let sha = try FileHash.sha256(of: file)
        let matched = await checksumMatches(sha, asset: r.asset, siblings: r.siblings)
        if matched == false { throw InstallError.blocked(Assessment(signature: .signed, gatekeeperAccepts: true, notarized: false, checksumMatched: false).verdictMessage ?? "The checksum doesn't match.") }

        switch kind {
        case .pkg:
            keepWork = true
            return try await openPackage(file, matched: matched, request: r, confirm: confirm)
        case .dmg:
            return try await installFromDiskImage(file, work: work, sha: sha, matched: matched, request: r, progress: progress, confirm: confirm)
        case .zip:
            return try await installFromZip(file, work: work, sha: sha, matched: matched, request: r, progress: progress, confirm: confirm, keepWork: &keepWork)
        case .unknown: throw InstallError.failed("Unknown file type.")
        }
    }

    private func checksumMatches(_ sha: String, asset: AssetInfo, siblings: [AssetInfo]) async -> Bool? {
        if let expected = Checksums.published(by: asset) { return expected.caseInsensitiveCompare(sha) == .orderedSame }
        for file in Checksums.candidateFiles(for: asset, among: siblings) {
            guard let url = URL(string: file.url), let text = try? await downloader.text(url, maxBytes: 1_000_000),
                  let expected = Checksums.hash(in: text, for: asset.name, checksumFileName: file.name) else { continue }
            return expected.caseInsensitiveCompare(sha) == .orderedSame
        }
        return nil
    }

    // MARK: disk image

    private func installFromDiskImage(_ dmg: URL, work: URL, sha: String, matched: Bool?, request r: Request, progress: @escaping @Sendable (InstallProgress) -> Void,
                                      confirm: @escaping @Sendable ([String]) async -> Bool) async throws -> InstallResult {
        progress(InstallProgress(fraction: 0.85, text: "Opening the disk image…"))
        let info = try await runner.run("/usr/bin/hdiutil", ["imageinfo", "-plist", dmg.path], timeout: 120)
        if info.status == 0, let plist = try? PropertyListSerialization.propertyList(from: Data(info.output.utf8), format: nil) as? [String: Any],
           let props = plist["Properties"] as? [String: Any], (props["Software License Agreement"] as? Bool) == true {
            throw InstallError.needsManual("This disk image shows a license agreement, and ReleaseSync won't accept a license for you. Open it yourself to read it and install the app.")
        }
        let mountRoot = work.appendingPathComponent("mnt", isDirectory: true)
        try fm.createDirectory(at: mountRoot, withIntermediateDirectories: true)
        let attach = try await runner.run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-noverify", "-plist", "-mountrandom", mountRoot.path, dmg.path], timeout: 300)
        guard attach.status == 0, let mount = Self.mountPoint(fromAttachPlist: attach.output) else {
            throw InstallError.failed("macOS couldn't open the disk image" + (attach.error.isEmpty ? "." : ": " + attach.error.trimmingCharacters(in: .whitespacesAndNewlines)))
        }
        let mountURL = URL(fileURLWithPath: mount)
        do {
            let result = try await installApp(from: mountURL, sha: sha, matched: matched, request: r, progress: progress, confirm: confirm)
            _ = try? await runner.run("/usr/bin/hdiutil", ["detach", mount, "-force"], timeout: 60)
            return result
        } catch {
            _ = try? await runner.run("/usr/bin/hdiutil", ["detach", mount, "-force"], timeout: 60)
            throw error
        }
    }

    static func mountPoint(fromAttachPlist text: String) -> String? {
        guard let plist = try? PropertyListSerialization.propertyList(from: Data(text.utf8), format: nil) as? [String: Any],
              let entities = plist["system-entities"] as? [[String: Any]] else { return nil }
        return entities.compactMap { $0["mount-point"] as? String }.first
    }

    // MARK: zip

    private func installFromZip(_ zip: URL, work: URL, sha: String, matched: Bool?, request r: Request, progress: @escaping @Sendable (InstallProgress) -> Void,
                                confirm: @escaping @Sendable ([String]) async -> Bool, keepWork: inout Bool) async throws -> InstallResult {
        progress(InstallProgress(fraction: 0.85, text: "Unpacking…"))
        let out = work.appendingPathComponent("unpacked", isDirectory: true)
        try fm.createDirectory(at: out, withIntermediateDirectories: true)
        let res = try await runner.run("/usr/bin/ditto", ["-x", "-k", zip.path, out.path], timeout: 600)
        guard res.status == 0 else { throw InstallError.failed("The zip couldn't be unpacked" + (res.error.isEmpty ? "." : ": " + res.error.trimmingCharacters(in: .whitespacesAndNewlines))) }
        guard !Self.containsEscape(in: out) else { throw InstallError.blocked("This zip contains a link that points outside itself, so ReleaseSync didn't use it.") }
        var root = out
        let top = (try? fm.contentsOfDirectory(atPath: out.path)) ?? []
        if !top.contains(where: { $0.lowercased().hasSuffix(".app") }) {
            if let pkg = top.first(where: { $0.lowercased().hasSuffix(".pkg") }) {
                keepWork = true
                return try await openPackage(out.appendingPathComponent(pkg), matched: matched, request: r, confirm: confirm)
            }
            let folders = top.filter { var d: ObjCBool = false; return fm.fileExists(atPath: out.appendingPathComponent($0).path, isDirectory: &d) && d.boolValue && !$0.hasPrefix(".") && $0 != "__MACOSX" }
            if folders.count == 1 { root = out.appendingPathComponent(folders[0]) }
        }
        return try await installApp(from: root, sha: sha, matched: matched, request: r, progress: progress, confirm: confirm)
    }

    /// A zip must not hold a link that leads outside the folder it was unpacked into (or one with an absolute target).
    static func containsEscape(in folder: URL) -> Bool {
        let fm = FileManager.default
        let base = folder.resolvingSymlinksInPath().path
        guard let walker = fm.enumerator(at: folder, includingPropertiesForKeys: [.isSymbolicLinkKey], options: []) else { return false }
        for case let item as URL in walker {
            guard (try? item.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true, let target = try? fm.destinationOfSymbolicLink(atPath: item.path) else { continue }
            if target.hasPrefix("/") { return true }
            let resolved = URL(fileURLWithPath: target, relativeTo: item.deletingLastPathComponent()).standardizedFileURL.path
            let normalizedBase = base.hasSuffix("/") ? base : base + "/"
            if resolved != base && !(resolved + "/").hasPrefix(normalizedBase) && !resolved.hasPrefix(folder.standardizedFileURL.path) { return true }
        }
        return false
    }

    // MARK: package

    private func openPackage(_ pkg: URL, matched: Bool?, request r: Request, confirm: @escaping @Sendable ([String]) async -> Bool) async throws -> InstallResult {
        if r.unattended { throw InstallError.skipped("\(r.asset.name) is an installer package that needs you. Open ReleaseSync and install it yourself.") }
        let sig = try await runner.run("/usr/sbin/pkgutil", ["--check-signature", pkg.path], timeout: 60)
        let signed = sig.status == 0
        var findings: [String] = []
        if !signed { findings.append("the package isn't signed by a developer") }
        if matched == nil { findings.append("the project publishes no checksum to compare against") }
        if !signed && !r.trustUnsigned {
            guard await confirm(findings) else { throw InstallError.cancelled }
        }
        let open = try await runner.run("/usr/bin/open", ["-a", "Installer", pkg.path], timeout: 30)
        guard open.status == 0 else { throw InstallError.failed("macOS couldn't open the installer.") }
        return InstallResult(record: nil, assessment: nil, handedOff: "Opened the installer for \(r.asset.name). Finish it there; macOS may ask for your password.")
    }

    // MARK: app

    private func installApp(from folder: URL, sha: String, matched: Bool?, request r: Request, progress: @escaping @Sendable (InstallProgress) -> Void,
                            confirm: @escaping @Sendable ([String]) async -> Bool) async throws -> InstallResult {
        let names = (try? fm.contentsOfDirectory(atPath: folder.path)) ?? []
        guard let appName = InstallPlanner.chooseApp(from: names, repo: r.repo, displayName: r.displayName) else {
            throw InstallError.failed("There's no app inside \(r.asset.name) for ReleaseSync to install.")
        }
        let source = folder.appendingPathComponent(appName)

        // where it goes: where it already is, else Applications, else this person's own Applications folder
        var destDir = r.existingPath.map { URL(fileURLWithPath: $0).deletingLastPathComponent() } ?? paths.applications
        if !fm.isWritableFile(atPath: destDir.path) {
            destDir = fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)
            try fm.createDirectory(at: destDir, withIntermediateDirectories: true)
        }
        let final = destDir.appendingPathComponent(appName)
        let staging = destDir.appendingPathComponent(".ReleaseSync-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        let staged = staging.appendingPathComponent(appName)

        progress(InstallProgress(fraction: 0.88, text: "Copying \(appName)…"))
        let copy = try await runner.run("/usr/bin/ditto", [source.path, staged.path], timeout: 900)
        guard copy.status == 0 else { throw InstallError.failed("Copying the app failed" + (copy.error.isEmpty ? "." : ": " + copy.error.trimmingCharacters(in: .whitespacesAndNewlines))) }
        try Task.checkCancellation()

        progress(InstallProgress(fraction: 0.92, text: "Checking the app…"))
        let assessment = try await assess(staged, checksumMatched: matched)
        switch assessment.verdict(trustUnsigned: r.trustUnsigned) {
        case .blocked(let why): throw InstallError.blocked(why)
        case .confirm(let findings):
            if r.unattended { throw InstallError.skipped("Skipped automatic install: " + findings.joined(separator: "; ") + ". Open ReleaseSync and update it yourself to look at it.") }
            guard await confirm(findings) else { throw InstallError.cancelled }
        case .ok: break
        }

        let info = Self.bundleInfo(staged)
        if let id = info.id { try await ensureNotRunning(bundleId: id, name: appName, quit: r.quitRunningApp) }

        progress(InstallProgress(fraction: 0.96, text: "Installing…"))
        var backup: URL?
        if fm.fileExists(atPath: final.path) {
            let old = Self.bundleInfo(final)
            let box = paths.backups.appendingPathComponent(InstallPlanner.safeName(r.key), isDirectory: true)
                .appendingPathComponent(InstallPlanner.safeName((old.version ?? "previous") + "-" + String(Int(Date().timeIntervalSince1970))), isDirectory: true)
            try fm.createDirectory(at: box, withIntermediateDirectories: true)
            backup = box.appendingPathComponent(appName)
            do { try fm.moveItem(at: final, to: backup!) }
            catch { throw InstallError.failed("macOS wouldn't let ReleaseSync replace the existing \(appName). It may have been installed by an administrator.") }
        }
        do { try fm.moveItem(at: staged, to: final) }
        catch {
            if let backup { try? fm.moveItem(at: backup, to: final) }
            throw InstallError.failed("The app couldn't be put in place: \(error.localizedDescription)")
        }
        if applyQuarantine { Self.markDownloaded(final) }
        pruneBackups(for: r.key)

        let now = Self.read(final)
        let record = InstallRecord(key: r.key, version: r.tag, bundlePath: final.path, bundleId: now.id, bundleVersion: now.version, installedAt: .now(), assetName: r.asset.name,
                                   sha256: sha, signer: assessment.signer, notarized: assessment.notarized)
        try await store.set(record)
        return InstallResult(record: record, assessment: assessment, handedOff: nil)
    }

    // MARK: checking an app

    func assess(_ app: URL, checksumMatched: Bool?) async throws -> Assessment {
        let sign = try await runner.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path], timeout: 300)
        let signature = Assessment.signature(exitStatus: sign.status, message: sign.combined)
        let gk = try await runner.run("/usr/sbin/spctl", ["--assess", "--type", "execute", "--verbose=4", app.path], timeout: 300)
        let verdict = Assessment.gatekeeper(exitStatus: gk.status, output: gk.combined)
        var signer = verdict.signer
        if signature == .signed, signer == nil, let display = try? await runner.run("/usr/bin/codesign", ["-dv", "--verbose=2", app.path], timeout: 60) {
            signer = Assessment.signer(fromCodesignDisplay: display.combined)
        }
        return Assessment(signature: signature, gatekeeperAccepts: verdict.accepts, notarized: verdict.notarized, signer: signer, checksumMatched: checksumMatched)
    }

    // MARK: running apps, backups, uninstall

    @MainActor private func runningCopies(_ bundleId: String) -> [NSRunningApplication] { NSRunningApplication.runningApplications(withBundleIdentifier: bundleId) }

    private func ensureNotRunning(bundleId: String, name: String, quit: Bool) async throws {
        guard !(await runningCopies(bundleId)).isEmpty else { return }
        guard quit else { throw InstallError.appRunning(name: name, bundleId: bundleId) }
        for app in await runningCopies(bundleId) { await MainActor.run { _ = app.terminate() } }
        for _ in 0..<50 {
            if (await runningCopies(bundleId)).isEmpty { return }
            try await Task.sleep(for: .milliseconds(200))
        }
        throw InstallError.appRunning(name: name, bundleId: bundleId)
    }

    private func pruneBackups(for key: String) {
        let dir = paths.backups.appendingPathComponent(InstallPlanner.safeName(key), isDirectory: true)
        guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.creationDateKey]) else { return }
        let sorted = items.sorted { ((try? $0.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast) > ((try? $1.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast) }
        for old in sorted.dropFirst(keepBackups) { try? fm.removeItem(at: old) }
    }

    /// Moves the app to the Trash (never deletes it outright). Its data and settings stay where they are.
    func uninstall(_ record: InstallRecord, quitRunningApp: Bool = false) async throws {
        if let id = record.bundleId { try await ensureNotRunning(bundleId: id, name: (record.bundlePath as NSString).lastPathComponent, quit: quitRunningApp) }
        let url = URL(fileURLWithPath: record.bundlePath)
        if fm.fileExists(atPath: url.path) {
            do { try trash(url) }
            catch { throw InstallError.failed("macOS wouldn't let ReleaseSync move \((record.bundlePath as NSString).lastPathComponent) to the Trash.") }
        }
        try await store.remove(record.key)
    }

    // MARK: bundle helpers

    static func bundleInfo(_ app: URL) -> (id: String?, version: String?) { read(app) }

    static func read(_ app: URL) -> (id: String?, version: String?) {
        guard let dict = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any] else { return (nil, nil) }
        return (dict["CFBundleIdentifier"] as? String, (dict["CFBundleShortVersionString"] as? String) ?? (dict["CFBundleVersion"] as? String))
    }

    /// Flags an app as downloaded from the internet, as a browser does, so macOS asks once before the first launch.
    static func markDownloaded(_ app: URL) {
        var values = URLResourceValues()
        values.quarantineProperties = [kLSQuarantineAgentNameKey as String: "ReleaseSync", kLSQuarantineTypeKey as String: kLSQuarantineTypeOtherDownload as String,
                                       kLSQuarantineTimeStampKey as String: Date()]
        var url = app
        try? url.setResourceValues(values)
    }
}

extension Assessment {
    /// The message for a verdict that stops the install, if it is one.
    var verdictMessage: String? { if case .blocked(let m) = verdict(trustUnsigned: true) { return m }; return nil }
}
