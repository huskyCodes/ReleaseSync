import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

@MainActor
final class ScanModelTests: XCTestCase {
    var root: URL!
    override func setUp() { root = FileManager.default.temporaryDirectory.appendingPathComponent("RSScan-" + UUID().uuidString, isDirectory: true); try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true) }
    override func tearDown() { try? FileManager.default.removeItem(at: root) }

    func makeApp(_ name: String, in dir: URL, id: String, version: String = "1.0", feed: String? = nil) throws {
        let contents = dir.appendingPathComponent(name + ".app/Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
        var plist: [String: Any] = ["CFBundleIdentifier": id, "CFBundleName": name, "CFBundleShortVersionString": version]
        if let feed { plist["SUFeedURL"] = feed }
        try (plist as NSDictionary).write(to: contents.appendingPathComponent("Info.plist"))
    }

    func library(_ repo: String) -> SyncApp { var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo.lowercased())"; return a }

    func testAppsAreFoundIncludingOneFolderDeepAndAppleAppsAreLeftOut() async throws {
        try makeApp("Rectangle", in: root, id: "com.knollsoft.Rectangle", version: "0.80")
        try makeApp("Safari", in: root, id: "com.apple.Safari")
        let utilities = root.appendingPathComponent("Utilities", isDirectory: true)
        try FileManager.default.createDirectory(at: utilities, withIntermediateDirectories: true)
        try makeApp("Tiny Tool", in: utilities, id: "org.example.tiny", feed: "https://github.com/someone/tiny/releases.atom")
        let deeper = utilities.appendingPathComponent("More/Deeper", isDirectory: true)
        try FileManager.default.createDirectory(at: deeper, withIntermediateDirectories: true)
        try makeApp("TooDeep", in: deeper, id: "org.example.deep")
        let scan = ScanModel(folders: [root])
        await scan.scan(library: [library("Rectangle")])
        XCTAssertEqual(Set(scan.rows.map(\.app.name)), ["Rectangle", "Tiny Tool"])
        XCTAssertEqual(scan.rows.first { $0.app.name == "Rectangle" }?.match, .library(key: "github:fake/rectangle"))
        if case .feed(let ref)? = scan.rows.first(where: { $0.app.name == "Tiny Tool" })?.match { XCTAssertEqual("\(ref.owner)/\(ref.repo)", "someone/tiny") } else { XCTFail() }
        XCTAssertEqual(scan.rows.first?.app.name, "Rectangle", "apps that match the library come first")
        XCTAssertNotNil(scan.lastScan)
    }

    func testAnAppAlreadyHereIsTrackedAtTheReleaseItIs() async throws {
        let log = ActivityLog()
        let m = InstallModel(installer: MacInstaller(paths: InstallPaths(applications: root, backups: root, downloads: root), store: InstallStore(url: root.appendingPathComponent("i.json"))), log: log, toasts: Toasts())
        var info = AppRelease(); var r = ReleaseSummary(); r.tag = "v0.80"; info.releases = [r]
        let scanned = ScannedApp(name: "Rectangle", bundleId: "com.knollsoft.Rectangle", version: "0.80", path: "/Applications/Rectangle.app")
        await m.adopt(scanned, as: library("Rectangle"), info: info)
        XCTAssertEqual(m.installedVersion(for: "github:fake/rectangle"), "v0.80", "0.80 is the release v0.80")
        XCTAssertEqual(m.records["github:fake/rectangle"]?.bundlePath, "/Applications/Rectangle.app")
        let saved = await m.installer.store.record(for: "github:fake/rectangle")
        XCTAssertNotNil(saved)
    }
}
