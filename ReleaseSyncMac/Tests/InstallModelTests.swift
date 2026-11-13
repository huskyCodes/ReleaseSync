import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class InstallFactsTests: XCTestCase {
    func app(_ tweak: (inout SyncApp) -> Void = { _ in }) -> SyncApp { var a = SyncApp(); a.owner = "fake"; a.repo = "tool"; a.key = "github:fake/tool"; tweak(&a); return a }
    func info(latest: String = "v2.0", tags: [String] = ["v2.0", "v1.5", "v1.0"], mac: Bool = true) -> AppRelease {
        var i = AppRelease(); i.latestTag = latest; i.lastChecked = Timestamp.now(); i.latestPublished = Timestamp.now()
        i.assets = [AssetInfo(name: mac ? "Tool-universal.dmg" : "Tool-setup.exe", url: "https://e.test/x")]
        i.releases = tags.enumerated().map { n, t in var s = ReleaseSummary(); s.tag = t; s.published = Timestamp.now() - Timestamp.days(Int64(n) * 30); s.assets = i.assets; return s }
        return i
    }

    func testNotInstalledOffersInstall() {
        let f = ReleaseFacts(app(), info(), installed: nil)
        XCTAssertEqual(f.actionText, "Install"); XCTAssertEqual(f.statusText, "Not installed"); XCTAssertFalse(f.hasUpdate)
    }

    func testAnOlderInstalledVersionOffersAnUpdate() {
        let f = ReleaseFacts(app(), info(), installed: "v1.5")
        XCTAssertTrue(f.hasUpdate); XCTAssertEqual(f.actionText, "Update"); XCTAssertEqual(f.statusText, "Update available"); XCTAssertEqual(f.versionText, "v1.5  →  v2.0")
    }

    func testTheLatestVersionIsUpToDateAndHasNoButton() {
        let f = ReleaseFacts(app(), info(), installed: "v2.0")
        XCTAssertFalse(f.hasUpdate); XCTAssertNil(f.actionText); XCTAssertEqual(f.statusText, "Up to date"); XCTAssertEqual(f.versionText, "v2.0")
    }

    func testIgnoredOrSkippedUpdatesAreNotOffered() {
        let ignored = ReleaseFacts(app { $0.ignoreUpdates = true }, info(), installed: "v1.5")
        XCTAssertFalse(ignored.hasUpdate); XCTAssertTrue(ignored.updateIgnored); XCTAssertEqual(ignored.statusText, "Update ignored"); XCTAssertNil(ignored.actionText)
        let skipped = ReleaseFacts(app { $0.skippedVersion = "v2.0" }, info(), installed: "v1.5")
        XCTAssertFalse(skipped.hasUpdate); XCTAssertEqual(skipped.statusText, "Skipped v2.0")
    }

    func testWhileWorkingTheStatusSaysSoAndThereIsNoButton() {
        let f = ReleaseFacts(app(), info(), installed: "v1.5", busy: "Downloading")
        XCTAssertEqual(f.statusText, "Working…"); XCTAssertNil(f.actionText)
    }

    func testAnAppThatUpdatedItselfPastWhatIsKnownIsNoticed() {
        XCTAssertTrue(ReleaseFacts(app(), info(tags: ["v2.0", "v1.5"]), installed: "v1.9").versionUnrecognized)
        XCTAssertFalse(ReleaseFacts(app(), info(tags: ["v2.0", "v1.5"]), installed: "v1.5").versionUnrecognized)
        XCTAssertFalse(ReleaseFacts(app(), info(), installed: "v2.0").versionUnrecognized)
        XCTAssertFalse(ReleaseFacts(app(), info(), installed: nil).versionUnrecognized)
    }

    func testAnUnknownInstalledTagIsTreatedAsOlderThanTheKnownReleases() {
        // an app that updated itself leaves a version that isn't a release: it must not look newer than the latest, nor like a rollback
        let f = ReleaseFacts(app(), info(), installed: "9.9.9-custom")
        XCTAssertNotNil(f.publishedOf("9.9.9-custom"))
        XCTAssertLessThan(f.publishedOf("9.9.9-custom")!, f.publishedOf("v1.0")!)
    }

    func testNoMacFileMeansNoButtonAndAReason() {
        let f = ReleaseFacts(app(), info(mac: false), installed: nil)
        XCTAssertNil(f.actionText)
        XCTAssertTrue(f.noInstallableFileText?.contains("Tool-setup.exe") == true)
        XCTAssertNil(ReleaseFacts(app(), info(), installed: nil).noInstallableFileText)
    }

    func testDifferentTagStylesAreComparedByTheirDates() {
        var i = info(latest: "nx2-build-90", tags: ["nx2-build-90", "build-83"], mac: true)
        i.releases[0].published = Timestamp.now(); i.releases[1].published = Timestamp.now() - Timestamp.days(30)
        XCTAssertTrue(ReleaseFacts(app(), i, installed: "build-83").hasUpdate)
        i.releases[0].published = Timestamp.now() - Timestamp.days(60)
        XCTAssertFalse(ReleaseFacts(app(), i, installed: "build-83").hasUpdate)
    }
}

@MainActor
final class InstallModelTests: XCTestCase {
    var root: URL!
    var fm: FileManager { .default }
    override func setUp() { root = fm.temporaryDirectory.appendingPathComponent("RSModel-" + UUID().uuidString, isDirectory: true); try? fm.createDirectory(at: root, withIntermediateDirectories: true) }
    override func tearDown() { try? fm.removeItem(at: root) }

    func zipOfApp(version: String) async throws -> URL {
        let src = root.appendingPathComponent("src-\(version)", isDirectory: true)
        let app = src.appendingPathComponent("Tool.app", isDirectory: true)
        try fm.createDirectory(at: app.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        try (["CFBundleIdentifier": "test.releasesync.model", "CFBundleShortVersionString": version, "CFBundleExecutable": "t"] as NSDictionary).write(to: app.appendingPathComponent("Contents/Info.plist"))
        try "#!/bin/sh\n".write(to: app.appendingPathComponent("Contents/MacOS/t"), atomically: true, encoding: .utf8)
        let z = root.appendingPathComponent("Tool-\(version)-mac.zip")
        _ = try await ProcessRunner().run("/usr/bin/ditto", ["-c", "-k", "--keepParent", app.path, z.path], timeout: 60)
        return z
    }

    func make() -> (InstallModel, ActivityLog) {
        let log = ActivityLog()
        let applications = root.appendingPathComponent("Applications", isDirectory: true)
        try? fm.createDirectory(at: applications, withIntermediateDirectories: true)
        let installer = MacInstaller(paths: InstallPaths(applications: applications, backups: root.appendingPathComponent("Backups"), downloads: root.appendingPathComponent("Downloads")),
                                     store: InstallStore(url: root.appendingPathComponent("installs.json")), downloader: LocalDownloader())
        installer.applyQuarantine = false
        return (InstallModel(installer: installer, log: log, toasts: Toasts()), log)
    }

    func app(trust: Bool) -> SyncApp { var a = SyncApp(); a.owner = "fake"; a.repo = "tool"; a.key = "github:fake/tool"; a.trustUnsigned = trust; return a }
    func info(_ zip: URL, tag: String) -> AppRelease {
        var i = AppRelease(); i.latestTag = tag; i.lastChecked = Timestamp.now()
        i.assets = [AssetInfo(name: zip.lastPathComponent, url: zip.absoluteString, size: 10)]
        var r = ReleaseSummary(); r.tag = tag; r.assets = i.assets; i.releases = [r]
        return i
    }

    func testInstallingRecordsTheVersionAndTheActivityLogSaysSo() async throws {
        let (m, log) = make()
        let z = try await zipOfApp(version: "1.0")
        await m.install(app(trust: true), info: info(z, tag: "v1.0"))
        XCTAssertEqual(m.installedVersion(for: "github:fake/tool"), "v1.0")
        XCTAssertNil(m.progress(for: "github:fake/tool"), "done")
        XCTAssertTrue(log.entries.contains { $0.text.contains("is now at v1.0") })
        let f = m.facts(app(trust: true), info(z, tag: "v1.0"))
        XCTAssertEqual(f.statusText, "Up to date")
    }

    func testAnUnsignedAppWaitsForTheAnswer() async throws {
        let (m, _) = make()
        let z = try await zipOfApp(version: "1.0")
        let task = Task { await m.install(self.app(trust: false), info: self.info(z, tag: "v1.0")) }
        for _ in 0..<200 where m.question == nil { try await Task.sleep(for: .milliseconds(20)) }
        let q = try XCTUnwrap(m.question)
        if case .unsigned(let findings) = q.kind { XCTAssertTrue(findings.joined().contains("isn't signed")) } else { XCTFail() }
        XCTAssertNil(m.installedVersion(for: "github:fake/tool"), "nothing is installed while the question is open")
        m.answer(false)
        await task.value
        XCTAssertNil(m.installedVersion(for: "github:fake/tool"))
        XCTAssertNil(m.question)

        let again = Task { await m.install(self.app(trust: false), info: self.info(z, tag: "v1.0")) }
        for _ in 0..<200 where m.question == nil { try await Task.sleep(for: .milliseconds(20)) }
        m.answer(true)
        await again.value
        XCTAssertEqual(m.installedVersion(for: "github:fake/tool"), "v1.0")
    }

    func testAnAppWithNoMacFileSaysSoInsteadOfDoingAnything() async throws {
        let (m, log) = make()
        var i = AppRelease(); i.latestTag = "v1"; i.assets = [AssetInfo(name: "Tool-setup.exe", url: "https://e.test/x")]
        await m.install(app(trust: true), info: i)
        XCTAssertNil(m.installedVersion(for: "github:fake/tool"))
        XCTAssertTrue(log.entries.last?.text.contains("no download for the Mac") == true)
    }

    func testAnAppRemovedOutsideReleaseSyncIsForgottenAndOneThatUpdatedItselfIsNoticed() async throws {
        let (m, log) = make()
        let z = try await zipOfApp(version: "1.0")
        await m.install(app(trust: true), info: info(z, tag: "v1.0"))
        let path = try XCTUnwrap(m.records["github:fake/tool"]?.bundlePath)
        // the app updates itself to 1.5
        try (["CFBundleIdentifier": "test.releasesync.model", "CFBundleShortVersionString": "1.5", "CFBundleExecutable": "t"] as NSDictionary).write(to: URL(fileURLWithPath: path).appendingPathComponent("Contents/Info.plist"))
        var known = AppRelease(); var r1 = ReleaseSummary(); r1.tag = "v1.5"; var r0 = ReleaseSummary(); r0.tag = "v1.0"; known.releases = [r1, r0]
        await m.reconcile(releases: ["github:fake/tool": known])
        XCTAssertEqual(m.installedVersion(for: "github:fake/tool"), "v1.5", "matched to the release it is")
        XCTAssertTrue(log.entries.contains { $0.text.contains("updated outside ReleaseSync") })
        try fm.removeItem(atPath: path)
        await m.reconcile(releases: [:])
        XCTAssertNil(m.installedVersion(for: "github:fake/tool"))
    }

    func testUpdateAllOnlyTouchesAppsWithUpdates() async throws {
        let (m, _) = make()
        let z1 = try await zipOfApp(version: "1.0"), z2 = try await zipOfApp(version: "2.0")
        await m.install(app(trust: true), info: info(z1, tag: "v1.0"))
        var latest = info(z2, tag: "v2.0"); var old = ReleaseSummary(); old.tag = "v1.0"; old.assets = info(z1, tag: "v1.0").assets; latest.releases.append(old)
        await m.updateAll([(app(trust: true), latest)])
        XCTAssertEqual(m.installedVersion(for: "github:fake/tool"), "v2.0")
    }
}
