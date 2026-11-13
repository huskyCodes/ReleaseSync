import AppKit
import Foundation
import Observation
import ReleaseSyncKit

/// What is installed on this Mac and the installing, updating and removing that changes it. The installed list belongs to this Mac (it is not
/// part of the synced library).
@MainActor
@Observable
final class InstallModel {
    /// A question the person has to answer before an install goes on.
    struct Question: Identifiable {
        enum Kind { case unsigned([String]), quitApp(String) }
        let id = UUID()
        let appTitle: String
        let kind: Kind
        fileprivate let answer: CheckedContinuation<Bool, Never>
    }

    private(set) var records: [String: InstallRecord] = [:]
    /// Apps being installed or removed right now, with how far along they are.
    private(set) var busy: [String: InstallProgress] = [:]
    var question: Question?
    let installer: MacInstaller
    private let log: ActivityLog
    private let toasts: Toasts

    init(installer: MacInstaller = MacInstaller(), log: ActivityLog, toasts: Toasts) { self.installer = installer; self.log = log; self.toasts = toasts }

    /// Made-up installed apps for the demo.
    init(showing records: [String: InstallRecord], log: ActivityLog, toasts: Toasts) {
        self.installer = MacInstaller(); self.records = records; self.log = log; self.toasts = toasts; self.isDemo = true
    }
    private(set) var isDemo = false

    // MARK: what is installed

    func load(releases: [String: AppRelease] = [:]) async {
        guard !isDemo else { return }
        records = await installer.store.all()
        await reconcile(releases: releases)
    }

    /// Looks at the apps on disk: one that has been removed is forgotten, and one that updated itself is recorded at the version it has now.
    func reconcile(releases: [String: AppRelease]) async {
        guard !isDemo else { return }
        for (key, rec) in records {
            let url = URL(fileURLWithPath: rec.bundlePath)
            guard FileManager.default.fileExists(atPath: url.path) else {
                try? await installer.store.remove(key); records[key] = nil
                log.add("\(rec.bundlePath.split(separator: "/").last.map(String.init) ?? key) is no longer on this Mac, so it is shown as not installed", .info)
                continue
            }
            let now = MacInstaller.read(url)
            guard let seen = now.version, seen != rec.bundleVersion else { continue }
            var updated = rec
            updated.bundleVersion = seen
            let tags = releases[key]?.releases.map(\.tag) ?? []
            if let tag = VersionUtil.matchTag(seen, in: tags) { updated.version = tag } else { updated.version = seen }
            try? await installer.store.set(updated); records[key] = updated
            log.add("\((url.lastPathComponent as NSString).deletingPathExtension) was updated outside ReleaseSync, to \(updated.version)", .info)
        }
    }

    func installedVersion(for key: String) -> String? { records[key]?.version }
    func progress(for key: String) -> InstallProgress? { busy[key] }
    func facts(_ app: SyncApp, _ info: AppRelease?) -> ReleaseFacts { ReleaseFacts(app, info, installed: installedVersion(for: app.key), busy: busy[app.key]?.text) }

    // MARK: installing

    /// Installs (or updates to) `tag`, or the latest release. Errors become toasts and activity lines, never crashes.
    func install(_ app: SyncApp, info: AppRelease?, tag: String? = nil, unattended: Bool = false) async {
        guard busy[app.key] == nil, let info else { return }
        let target = tag ?? info.latestTag ?? ""
        let release = info.releases.first { $0.tag == target }
        let assets = (release?.assets).flatMap { $0.isEmpty ? nil : $0 } ?? (target == info.latestTag ? info.assets : [])
        guard let asset = MacAssets.pick(assets, pattern: app.assetPattern) else {
            report("\(app.title): there's no download for the Mac in \(target).", .warn, toast: !unattended)
            return
        }
        let wasInstalled = records[app.key] != nil
        log.add("\(wasInstalled ? "Updating" : "Installing") \(app.fullName) \(target) (\(asset.name))", .info)
        busy[app.key] = InstallProgress(fraction: 0, text: "Starting…")
        defer { busy[app.key] = nil }
        var request = MacInstaller.Request(key: app.key, repo: app.repo, displayName: app.displayName, tag: target, asset: asset, siblings: assets,
                                           trustUnsigned: app.trustUnsigned, unattended: unattended, existingPath: records[app.key]?.bundlePath)
        for attempt in 0..<2 {
            do {
                let key = app.key
                let result = try await installer.install(request, progress: { [weak self] p in Task { @MainActor in self?.busy[key] = p } },
                                                         confirm: { [weak self] findings in await self?.ask(app.title, .unsigned(findings)) ?? false })
                if let record = result.record {
                    records[app.key] = record
                    report("\(app.title) is now at \(target)" + (result.assessment.map { " (\($0.summary))" } ?? ""), result.assessment?.signature == .unsigned ? .warn : .success, toast: !unattended, toastText: "\(app.title) \(wasInstalled ? "updated to" : "installed,") \(target)")
                } else if let note = result.handedOff {
                    report("\(app.title): \(note)", .info, toast: true)
                }
                return
            } catch InstallError.appRunning(let name, _) where attempt == 0 && !unattended {
                guard await ask(app.title, .quitApp(name)) else { log.add("\(app.title): not installed, because it is open", .info); return }
                request.quitRunningApp = true
            } catch InstallError.cancelled {
                log.add("Cancelled \(app.title)", .warn); if !unattended { toasts.show("Cancelled \(app.title)", .info) }
                return
            } catch {
                let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                report("\(app.title): \(message)", .error, toast: !unattended)
                return
            }
        }
    }

    /// Updates every app that has one waiting, a few at a time.
    func updateAll(_ items: [(app: SyncApp, info: AppRelease?)]) async {
        let todo = items.filter { facts($0.app, $0.info).hasUpdate }
        guard !todo.isEmpty else { return }
        log.add("Updating \(todo.count) app\(todo.count == 1 ? "" : "s")", .info)
        for chunk in stride(from: 0, to: todo.count, by: 2).map({ Array(todo[$0..<min($0 + 2, todo.count)]) }) {
            await withTaskGroup(of: Void.self) { group in
                for item in chunk { group.addTask { @MainActor in await self.install(item.app, info: item.info) } }
            }
        }
    }

    func uninstall(_ app: SyncApp) async {
        guard let record = records[app.key], busy[app.key] == nil else { return }
        busy[app.key] = InstallProgress(fraction: nil, text: "Removing…")
        defer { busy[app.key] = nil }
        do {
            try await installer.uninstall(record)
            records[app.key] = nil
            report("\(app.title) was moved to the Trash. Its settings and data were left alone.", .success, toast: true, toastText: "\(app.title) was moved to the Trash")
        } catch InstallError.appRunning(let name, _) {
            if await ask(app.title, .quitApp(name)) {
                do { try await installer.uninstall(record, quitRunningApp: true); records[app.key] = nil; report("\(app.title) was moved to the Trash.", .success, toast: true) }
                catch { report("\(app.title): \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)", .error, toast: true) }
            }
        } catch { report("\(app.title): \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)", .error, toast: true) }
    }

    // MARK: on this Mac

    func reveal(_ app: SyncApp) {
        guard let path = records[app.key]?.bundlePath else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    func open(_ app: SyncApp) {
        guard let path = records[app.key]?.bundlePath else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }

    // MARK: questions and messages

    private func ask(_ title: String, _ kind: Question.Kind) async -> Bool {
        await withCheckedContinuation { cont in
            if let old = question { old.answer.resume(returning: false) }
            question = Question(appTitle: title, kind: kind, answer: cont)
        }
    }

    func answer(_ yes: Bool) {
        guard let q = question else { return }
        question = nil
        q.answer.resume(returning: yes)
    }

    private func report(_ text: String, _ kind: ActivityLog.Entry.Kind, toast: Bool, toastText: String? = nil) {
        log.add(text, kind)
        guard toast else { return }
        toasts.show(toastText ?? text, kind == .error ? .error : (kind == .warn ? .warn : (kind == .success ? .success : .info)))
    }
}
