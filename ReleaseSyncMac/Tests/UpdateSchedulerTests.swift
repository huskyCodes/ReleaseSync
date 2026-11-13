import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

@MainActor
final class UpdateSchedulerTests: XCTestCase {
    func defaults() -> UserDefaults { UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)! }

    func testOnlyNewVersionsAreAnnounced() {
        XCTAssertEqual(UpdateScheduler.announcements(waiting: [("a", "v2"), ("b", "v3")], told: ["a": "v2", "b": "v2"]), ["b"])
        XCTAssertEqual(UpdateScheduler.announcements(waiting: [("a", "v2")], told: [:]), ["a"])
        XCTAssertTrue(UpdateScheduler.announcements(waiting: [], told: ["a": "v1"]).isEmpty)
    }

    func testTheScheduleChoicesAreKept() {
        let d = defaults()
        let p = ViewPrefs(defaults: d)
        XCTAssertEqual(p.checkEveryHours, 6); XCTAssertTrue(p.notifyOfUpdates)
        p.checkEveryHours = 0; p.notifyOfUpdates = false
        let again = ViewPrefs(defaults: d)
        XCTAssertEqual(again.checkEveryHours, 0); XCTAssertFalse(again.notifyOfUpdates)
        d.set(7, forKey: "CheckEveryHours")
        XCTAssertEqual(ViewPrefs(defaults: d).checkEveryHours, 6, "an odd saved value falls back")
    }

    func testARoundTellsThePersonOnceAboutEachNewVersion() async throws {
        let add = AddModelTests()
        let r = try await add.rig()
        await r.add.open("fake/cool"); r.add.installNow = false; await r.add.add()        // in the library, with its releases known (v1.0)
        let key = "github:fake/cool"
        let log = ActivityLog()
        let install = InstallModel(showing: [key: InstallRecord(key: key, version: "v0.9", bundlePath: "/Applications/Cool.app", installedAt: .now(), assetName: "x")], log: log, toasts: Toasts())
        let prefs = ViewPrefs(defaults: defaults())
        let scheduler = UpdateScheduler(library: r.library, install: install, prefs: prefs, log: log, defaults: defaults())
        var told: [(String, String)] = []
        scheduler.notify = { title, body in told.append((title, body)) }
        await scheduler.checkAndAnnounce()
        XCTAssertEqual(told.map(\.0), ["cool has an update"])
        await scheduler.checkAndAnnounce()
        XCTAssertEqual(told.count, 1, "not again for the same version")
        prefs.notifyOfUpdates = false
        let quiet = UpdateScheduler(library: r.library, install: install, prefs: prefs, log: log, defaults: defaults())
        var quietTold = 0
        quiet.notify = { _, _ in quietTold += 1 }
        await quiet.checkAndAnnounce()
        XCTAssertEqual(quietTold, 0)
        XCTAssertTrue(log.entries.contains { $0.text.contains("1 update available") })
    }
}
