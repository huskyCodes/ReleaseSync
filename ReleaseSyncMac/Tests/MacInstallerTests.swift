import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

/// Hands over files from disk instead of the internet.
struct LocalDownloader: Downloading {
    var checksumTexts: [String: String] = [:]
    func download(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (DownloadProgress) -> Void) async throws {
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.copyItem(at: URL(fileURLWithPath: url.path), to: destination)
        progress(DownloadProgress(done: 10, total: 10))
    }
    func text(_ url: URL, maxBytes: Int) async throws -> String {
        guard let t = checksumTexts[url.lastPathComponent] else { throw DownloadError(message: "none") }
        return t
    }
}

final class Calls: @unchecked Sendable {
    private let lock = NSLock(); private var list: [[String]] = []
    func add(_ x: [String]) { lock.lock(); list.append(x); lock.unlock() }
    var all: [[String]] { lock.lock(); defer { lock.unlock() }; return list }
}

final class MacInstallerTests: XCTestCase {
    var root: URL!
    var fm: FileManager { .default }

    override func setUp() {
        root = fm.temporaryDirectory.appendingPathComponent("RSInstall-" + UUID().uuidString, isDirectory: true)
        try? fm.createDirectory(at: root, withIntermediateDirectories: true)
    }
    override func tearDown() { try? fm.removeItem(at: root) }

    var applications: URL { root.appendingPathComponent("Applications", isDirectory: true) }
    var backups: URL { root.appendingPathComponent("Backups", isDirectory: true) }

    func installer(downloader: Downloading = LocalDownloader()) -> MacInstaller {
        try? fm.createDirectory(at: applications, withIntermediateDirectories: true)
        let m = MacInstaller(paths: InstallPaths(applications: applications, backups: backups, downloads: root.appendingPathComponent("Downloads")),
                             store: InstallStore(url: root.appendingPathComponent("installs.json")), downloader: downloader)
        m.applyQuarantine = false
        return m
    }

    /// A tiny app: an Info.plist and an executable. Not signed.
    @discardableResult
    func makeApp(named name: String, in dir: URL, id: String = "test.releasesync.fake", version: String = "1.0") throws -> URL {
        let app = dir.appendingPathComponent(name + ".app", isDirectory: true)
        let macos = app.appendingPathComponent("Contents/MacOS", isDirectory: true)
        try fm.createDirectory(at: macos, withIntermediateDirectories: true)
        let plist: [String: Any] = ["CFBundleIdentifier": id, "CFBundleShortVersionString": version, "CFBundleExecutable": "fake", "CFBundleName": name, "CFBundlePackageType": "APPL"]
        try (plist as NSDictionary).write(to: app.appendingPathComponent("Contents/Info.plist"))
        let exe = macos.appendingPathComponent("fake")
        try "#!/bin/sh\nexit 0\n".write(to: exe, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: exe.path)
        return app
    }

    func zip(of app: URL, as name: String) async throws -> URL {
        let out = root.appendingPathComponent("served", isDirectory: true)
        try fm.createDirectory(at: out, withIntermediateDirectories: true)
        let z = out.appendingPathComponent(name)
        let r = try await ProcessRunner().run("/usr/bin/ditto", ["-c", "-k", "--keepParent", app.path, z.path], timeout: 60)
        XCTAssertEqual(r.status, 0, r.error)
        return z
    }

    func request(_ file: URL, version: String = "v1.0", digest: String? = nil, trust: Bool = false, unattended: Bool = false, existing: String? = nil, quit: Bool = false) -> MacInstaller.Request {
        MacInstaller.Request(key: "github:fake/pixel", repo: "pixel", displayName: nil, tag: version,
                             asset: AssetInfo(name: file.lastPathComponent, url: file.absoluteString, size: 10, digest: digest), siblings: [], trustUnsigned: trust, unattended: unattended,
                             quitRunningApp: quit, existingPath: existing)
    }

    func noProgress() -> @Sendable (InstallProgress) -> Void { { _ in } }

    func testAnUnsignedAppInAZipAsksFirstAndThenInstalls() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel-1.0-mac.zip")
        let asked = Calls()
        let result = try await installer().install(request(z), progress: noProgress()) { findings in asked.add(findings); return true }
        XCTAssertEqual(asked.all.count, 1)
        XCTAssertTrue(asked.all[0].joined().contains("isn't signed"))
        let record = try XCTUnwrap(result.record)
        XCTAssertEqual(record.bundlePath, applications.appendingPathComponent("Pixel.app").path)
        XCTAssertEqual(record.bundleVersion, "1.0"); XCTAssertEqual(record.version, "v1.0"); XCTAssertEqual(record.bundleId, "test.releasesync.fake")
        XCTAssertTrue(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app/Contents/MacOS/fake").path))
        XCTAssertTrue(fm.isExecutableFile(atPath: applications.appendingPathComponent("Pixel.app/Contents/MacOS/fake").path), "permissions are kept")
        let saved = await InstallStore(url: root.appendingPathComponent("installs.json")).record(for: "github:fake/pixel")
        XCTAssertEqual(saved?.assetName, "Pixel-1.0-mac.zip")
        XCTAssertFalse((try fm.contentsOfDirectory(atPath: applications.path)).contains { $0.hasPrefix(".ReleaseSync-") }, "no staging left behind")
    }

    func testSayingNoInstallsNothing() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel.zip")
        do { _ = try await installer().install(request(z), progress: noProgress()) { _ in false }; XCTFail() } catch { XCTAssertEqual(error as? InstallError, .cancelled) }
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
    }

    func testNobodyThereMeansAnythingNeedingAnswerIsSkippedAndTrustedAppsSkipTheQuestion() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel.zip")
        do { _ = try await installer().install(request(z, unattended: true), progress: noProgress()) { _ in XCTFail("nobody to ask"); return true }; XCTFail() }
        catch let e as InstallError { if case .skipped = e {} else { XCTFail("\(e)") } }
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
        let asked = Calls()
        _ = try await installer().install(request(z, trust: true), progress: noProgress()) { f in asked.add(f); return false }
        XCTAssertTrue(asked.all.isEmpty, "the person said not to ask again for this app")
        XCTAssertTrue(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
    }

    func testAWrongChecksumStopsEverything() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel.zip")
        do { _ = try await installer().install(request(z, digest: "sha256:" + String(repeating: "0", count: 64), trust: true), progress: noProgress()) { _ in true }; XCTFail() }
        catch let e as InstallError { if case .blocked(let m) = e { XCTAssertTrue(m.contains("checksum")) } else { XCTFail("\(e)") } }
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
    }

    func testARightChecksumFromTheProjectsOwnFileIsUsed() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel.zip")
        let sha = try FileHash.sha256(of: z)
        var downloader = LocalDownloader(); downloader.checksumTexts = ["SHA256SUMS": "\(sha)  Pixel.zip\n"]
        var req = request(z)
        req.siblings = [AssetInfo(name: "SHA256SUMS", url: "https://example.test/SHA256SUMS")]
        let asked = Calls()
        let result = try await installer(downloader: downloader).install(req, progress: noProgress()) { f in asked.add(f); return true }
        XCTAssertEqual(result.assessment?.checksumMatched, true)
        XCTAssertFalse(asked.all.joined().joined().contains("no checksum"))
        var wrong = LocalDownloader(); wrong.checksumTexts = ["SHA256SUMS": "\(String(repeating: "1", count: 64))  Pixel.zip\n"]
        do { _ = try await installer(downloader: wrong).install(req, progress: noProgress()) { _ in true }; XCTFail() } catch let e as InstallError { if case .blocked = e {} else { XCTFail("\(e)") } }
    }

    func testAnUpdateKeepsTheOldVersionAside() async throws {
        let v1 = try makeApp(named: "Pixel", in: root.appendingPathComponent("src1"), version: "1.0")
        let v2 = try makeApp(named: "Pixel", in: root.appendingPathComponent("src2"), version: "2.0")
        let z1 = try await zip(of: v1, as: "Pixel-1.zip"), z2 = try await zip(of: v2, as: "Pixel-2.zip")
        let inst = installer()
        let first = try await inst.install(request(z1, version: "v1.0", trust: true), progress: noProgress()) { _ in true }
        let second = try await inst.install(request(z2, version: "v2.0", trust: true, existing: first.record?.bundlePath), progress: noProgress()) { _ in true }
        XCTAssertEqual(second.record?.bundleVersion, "2.0")
        XCTAssertEqual(MacInstaller.read(applications.appendingPathComponent("Pixel.app")).version, "2.0")
        let kept = try fm.contentsOfDirectory(atPath: backups.appendingPathComponent(InstallPlanner.safeName("github:fake/pixel")).path)
        XCTAssertEqual(kept.count, 1)
        let oldApp = backups.appendingPathComponent(InstallPlanner.safeName("github:fake/pixel")).appendingPathComponent(kept[0]).appendingPathComponent("Pixel.app")
        XCTAssertEqual(MacInstaller.read(oldApp).version, "1.0", "the version that was replaced is still there")
        XCTAssertEqual(kept[0].hasPrefix("1.0-"), true)
    }

    func testAnAppThatIsOpenIsNotReplaced() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"), id: "com.apple.finder")      // Finder is always running
        let z = try await zip(of: app, as: "Pixel.zip")
        do { _ = try await installer().install(request(z, trust: true), progress: noProgress()) { _ in true }; XCTFail() }
        catch let e as InstallError { if case .appRunning(_, let id) = e { XCTAssertEqual(id, "com.apple.finder") } else { XCTFail("\(e)") } }
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
    }

    func testAZipWithALinkLeadingOutsideIsRefused() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        try fm.createSymbolicLink(atPath: app.appendingPathComponent("Contents/escape").path, withDestinationPath: "/etc")
        let z = try await zip(of: app, as: "Pixel.zip")
        do { _ = try await installer().install(request(z, trust: true), progress: noProgress()) { _ in true }; XCTFail() }
        catch let e as InstallError { if case .blocked = e {} else { XCTFail("\(e)") } }
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
        // a link that stays inside the app is normal (frameworks use them)
        let ok = try makeApp(named: "Pixel", in: root.appendingPathComponent("src2"))
        try fm.createSymbolicLink(atPath: ok.appendingPathComponent("Contents/Current").path, withDestinationPath: "MacOS")
        let z2 = try await zip(of: ok, as: "Pixel2.zip")
        _ = try await installer().install(request(z2, trust: true), progress: noProgress()) { _ in true }
        XCTAssertTrue(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
    }

    func testADiskImageIsMountedCopiedAndEjected() async throws {
        let src = root.appendingPathComponent("dmgsrc", isDirectory: true)
        try makeApp(named: "Pixel", in: src)
        try "read me".write(to: src.appendingPathComponent("README.txt"), atomically: true, encoding: .utf8)
        let out = root.appendingPathComponent("served", isDirectory: true)
        try fm.createDirectory(at: out, withIntermediateDirectories: true)
        let dmg = out.appendingPathComponent("Pixel-1.0.dmg")
        let made = try await ProcessRunner().run("/usr/bin/hdiutil", ["create", "-quiet", "-srcfolder", src.path, "-format", "UDZO", "-volname", "RSTestVolume", dmg.path], timeout: 120)
        try XCTSkipIf(made.status != 0, "can't make a disk image here: \(made.error)")
        let result = try await installer().install(request(dmg, trust: true), progress: noProgress()) { _ in true }
        XCTAssertEqual(result.record?.bundleVersion, "1.0")
        XCTAssertTrue(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app/Contents/Info.plist").path))
        let mounted = try await ProcessRunner().run("/sbin/mount", [], timeout: 10)
        XCTAssertFalse(mounted.output.contains("RSTestVolume"), "the disk image is ejected afterwards")
    }

    func testUninstallTrashesTheAppAndForgetsIt() async throws {
        let app = try makeApp(named: "Pixel", in: root.appendingPathComponent("src"))
        let z = try await zip(of: app, as: "Pixel.zip")
        let inst = installer()
        let trashed = Calls()
        inst.trash = { url in trashed.add([url.path]); try FileManager.default.removeItem(at: url) }
        let result = try await inst.install(request(z, trust: true), progress: noProgress()) { _ in true }
        try await inst.uninstall(try XCTUnwrap(result.record))
        XCTAssertEqual(trashed.all.count, 1)
        XCTAssertFalse(fm.fileExists(atPath: applications.appendingPathComponent("Pixel.app").path))
        let left = await inst.store.record(for: "github:fake/pixel")
        XCTAssertNil(left)
    }

    func testOnlyHttpsIsDownloaded() async throws {
        let d = HTTPSDownloader()
        do { try await d.download(URL(string: "http://example.test/a.dmg")!, to: root.appendingPathComponent("a.dmg"), expectedSize: 1) { _ in }; XCTFail() }
        catch { XCTAssertTrue((error as? DownloadError)?.message.contains("https") == true) }
        do { _ = try await d.text(URL(string: "ftp://example.test/x")!, maxBytes: 10); XCTFail() } catch {}
    }

    func testHashingAFile() throws {
        let f = root.appendingPathComponent("x"); try Data("hello".utf8).write(to: f)
        XCTAssertEqual(try FileHash.sha256(of: f), "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
    }
}
