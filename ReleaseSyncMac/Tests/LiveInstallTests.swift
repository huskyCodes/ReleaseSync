import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

/// A real download from the internet, off unless asked for: `TEST_RUNNER_RELEASESYNC_LIVE_TEST=1 xcodebuild … test -only-testing:ReleaseSyncTests/LiveInstallTests`.
/// It installs a well-known open-source Mac app into a temporary folder (never /Applications) and checks what macOS says about it.
final class LiveInstallTests: XCTestCase {
    func testARealProjectInstallsAndIsRecognisedAsNotarized() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["RELEASESYNC_LIVE_TEST"] == "1", "needs the internet; set RELEASESYNC_LIVE_TEST=1")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("RSLive-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let applications = root.appendingPathComponent("Applications", isDirectory: true)
        try FileManager.default.createDirectory(at: applications, withIntermediateDirectories: true)
        let client = SourceClient()
        let found = try await client.releases(source: "github", owner: "rxhanson", repo: "Rectangle", etag: nil)
        let release = try XCTUnwrap(found.releases.first)
        let asset = try XCTUnwrap(MacAssets.pick(release.assets))
        print("LIVE: \(release.tag) \(asset.name) \(asset.size) bytes digest=\(asset.digest ?? "none")")
        let installer = MacInstaller(paths: InstallPaths(applications: applications, backups: root.appendingPathComponent("Backups"), downloads: root.appendingPathComponent("Downloads")),
                                     store: InstallStore(url: root.appendingPathComponent("installs.json")))
        installer.applyQuarantine = false
        let request = MacInstaller.Request(key: "github:rxhanson/rectangle", repo: "Rectangle", displayName: nil, tag: release.tag, asset: asset, siblings: release.assets, unattended: true)
        let result = try await installer.install(request, progress: { _ in }, confirm: { _ in false })
        let record = try XCTUnwrap(result.record)
        print("LIVE: installed \(record.bundlePath) version \(record.bundleVersion ?? "?") notarized=\(String(describing: record.notarized)) signer=\(record.signer ?? "?") summary=\(result.assessment?.summary ?? "?")")
        XCTAssertTrue(FileManager.default.fileExists(atPath: record.bundlePath))
        XCTAssertEqual(result.assessment?.signature, .signed)
        XCTAssertEqual(result.assessment?.notarized, true)
        try await installer.uninstall(record)       // moves the temporary copy to the Trash
    }
}
