import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class DeviceInventoryTests: XCTestCase {
    let now = Timestamp(text: "2026-01-01T12:00:00Z")!
    func minutesAgo(_ n: Int64) -> Timestamp { now - n * 60 * Timestamp.ticksPerSecond }

    func device(_ id: String, _ name: String, online: Bool?, seen: Int64 = 1, _ installed: [SyncInstall]?) -> SyncDevice {
        SyncDevice(id: id, name: name, platform: "Windows", appVersion: "1", lastSeen: minutesAgo(seen), online: online, installed: installed)
    }
    func app(_ repo: String) -> SyncApp { var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo)"; return a }

    func testWhereAnAppIsInstalledShowsOnlineDevicesFirst() {
        let pc = device("1", "Zed PC", online: true, [SyncInstall(key: "github:fake/tool", version: "1.0", latest: "1.1")])
        let laptop = device("2", "Alpha Laptop", online: false, [SyncInstall(key: "github:fake/tool", version: "1.1")])
        let other = device("3", "Elsewhere", online: true, [SyncInstall(key: "github:fake/other", version: "1.0")])
        let none = device("4", "Older app", online: nil, nil)
        let found = DeviceInventory.installs(of: "github:fake/tool", on: [laptop, pc, other, none], now: now)
        XCTAssertEqual(found.map(\.device.name), ["Zed PC", "Alpha Laptop"])
    }

    func testKeysAreMatchedIgnoringCase() {
        let pc = device("1", "PC", online: true, [SyncInstall(key: "github:Fake/Tool", version: "1.0")])
        XCTAssertEqual(DeviceInventory.keys(on: [pc], only: nil), ["github:fake/tool"])
        XCTAssertEqual(DeviceInventory.installs(of: "github:fake/tool", on: [pc], now: now).count, 1)
    }

    func testUpdatesWaitingCanBeNarrowedToOneDevice() {
        let pc = device("1", "PC", online: true, [SyncInstall(key: "github:fake/a", version: "1.0", latest: "1.1"), SyncInstall(key: "github:fake/b", version: "2.0", latest: "2.0")])
        let tablet = device("2", "Tablet", online: true, [SyncInstall(key: "github:fake/c", version: "0.1", latest: "0.2"), SyncInstall(key: "github:fake/b", version: "1.0", latest: "2.0")])
        XCTAssertEqual(DeviceInventory.updatesWaiting(on: [pc, tablet], only: nil), ["github:fake/a", "github:fake/b", "github:fake/c"])
        XCTAssertEqual(DeviceInventory.updatesWaiting(on: [pc, tablet], only: "1"), ["github:fake/a"])
        XCTAssertEqual(DeviceInventory.keys(on: [pc, tablet], only: "2"), ["github:fake/b", "github:fake/c"])
    }

    func testOnlyDevicesThatReportAListCanBeChosen() {
        let reporting = DeviceInventory.reporting([device("1", "New", online: true, []), device("2", "Old", online: nil, nil)])
        XCTAssertEqual(reporting.map(\.name), ["New"], "an empty list still counts: that device said it has nothing")
    }

    func testSummaryCountsAppsAndUpdates() {
        XCTAssertNil(DeviceInventory.summary(of: device("1", "Old", online: nil, nil)))
        XCTAssertEqual(DeviceInventory.summary(of: device("1", "PC", online: true, [])), "0 apps installed")
        let one = device("1", "PC", online: true, [SyncInstall(key: "github:fake/a", version: "1.0", latest: "1.1")])
        XCTAssertEqual(DeviceInventory.summary(of: one), "1 app installed  ·  1 update waiting")
    }

    func testTheLightFollowsTheSharedRules() {
        XCTAssertTrue(device("1", "PC", online: true, seen: 149, nil).isOnline(at: now))
        XCTAssertFalse(device("1", "PC", online: true, seen: 151, nil).isOnline(at: now))
        XCTAssertFalse(device("1", "PC", online: false, seen: 1, nil).isOnline(at: now))
    }

    func testLibraryCanBeNarrowedToADeviceAndToUpdates() {
        let apps = [app("a"), app("b"), app("c")]
        func layout(_ status: LibraryLayout.Status, on: Set<String>?, updates: Set<String>) -> LibraryLayout.Result {
            LibraryLayout.layout(apps: apps, customGroups: [], status: status, groupFilter: nil, query: "", sort: "Name", groupItems: false, autoGroups: true,
                                 installedOnDevice: on, updatesWaiting: updates)
        }
        let everything = layout(.all, on: nil, updates: ["github:fake/b"])
        XCTAssertEqual(everything.sections.flatMap(\.apps).map(\.repo), ["a", "b", "c"])
        XCTAssertEqual(everything.updates, 1)
        XCTAssertEqual(layout(.updates, on: nil, updates: ["github:fake/b"]).sections.flatMap(\.apps).map(\.repo), ["b"])
        let onDevice = layout(.all, on: ["github:fake/a", "github:fake/b"], updates: ["github:fake/b"])
        XCTAssertEqual(onDevice.sections.flatMap(\.apps).map(\.repo), ["a", "b"])
        XCTAssertEqual(onDevice.total, 3, "the totals still describe the whole library")
    }
}
