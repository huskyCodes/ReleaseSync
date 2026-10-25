import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class DeviceInventoryTests: XCTestCase {
    func device(_ id: String, _ name: String) -> SyncDevice {
        SyncDevice(id: id, name: name, platform: "Windows", appVersion: "1", lastSeen: Timestamp.now())
    }
    func install(_ key: String, _ version: String, latest: String? = nil) -> PeerInstall { PeerInstall(key: key, version: version, installedAt: nil, latest: latest) }
    func app(_ repo: String) -> SyncApp { var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo)"; return a }

    func testWhereAnAppIsInstalledShowsOnlineDevicesFirst() {
        let pc = device("1", "Zed PC"), laptop = device("2", "Alpha Laptop"), other = device("3", "Elsewhere"), none = device("4", "Not heard from")
        let peers: [String: PeerState] = [
            "1": PeerState(online: true, installed: [install("github:fake/tool", "1.0", latest: "1.1")]),
            "2": PeerState(online: false, installed: [install("github:fake/tool", "1.1")]),
            "3": PeerState(online: true, installed: [install("github:fake/other", "1.0")]),
        ]
        let found = DeviceInventory.installs(of: "github:fake/tool", on: [laptop, pc, other, none], peers: peers)
        XCTAssertEqual(found.map(\.device.name), ["Zed PC", "Alpha Laptop"])
        XCTAssertEqual(found.map(\.online), [true, false])
    }

    func testNothingIsShownUntilTheLinkHasSaidSomething() {
        let pc = device("1", "PC")
        XCTAssertTrue(DeviceInventory.reporting([pc], peers: [:]).isEmpty)
        XCTAssertFalse(DeviceInventory.isOnline(pc, peers: [:]), "no link, grey light")
        XCTAssertNil(DeviceInventory.summary(of: nil))
        XCTAssertTrue(DeviceInventory.installs(of: "github:fake/a", on: [pc], peers: [:]).isEmpty)
    }

    func testKeysAreMatchedIgnoringCase() {
        let pc = device("1", "PC")
        let peers = ["1": PeerState(online: true, installed: [install("github:Fake/Tool", "1.0")])]
        XCTAssertEqual(DeviceInventory.keys(on: [pc], peers: peers, only: nil), ["github:fake/tool"])
        XCTAssertEqual(DeviceInventory.installs(of: "github:fake/tool", on: [pc], peers: peers).count, 1)
    }

    func testUpdatesWaitingCanBeNarrowedToOneDevice() {
        let pc = device("1", "PC"), tablet = device("2", "Tablet")
        let peers: [String: PeerState] = [
            "1": PeerState(online: true, installed: [install("github:fake/a", "1.0", latest: "1.1"), install("github:fake/b", "2.0", latest: "2.0")]),
            "2": PeerState(online: true, installed: [install("github:fake/c", "0.1", latest: "0.2"), install("github:fake/b", "1.0", latest: "2.0")]),
        ]
        XCTAssertEqual(DeviceInventory.updatesWaiting(on: [pc, tablet], peers: peers, only: nil), ["github:fake/a", "github:fake/b", "github:fake/c"])
        XCTAssertEqual(DeviceInventory.updatesWaiting(on: [pc, tablet], peers: peers, only: "1"), ["github:fake/a"])
        XCTAssertEqual(DeviceInventory.keys(on: [pc, tablet], peers: peers, only: "2"), ["github:fake/b", "github:fake/c"])
    }

    func testAnEmptyListCountsAsReporting() {
        let reporting = DeviceInventory.reporting([device("1", "New"), device("2", "Silent")], peers: ["1": PeerState(online: true, installed: [])])
        XCTAssertEqual(reporting.map(\.name), ["New"], "that device said it has nothing")
    }

    func testTheListIsTidiedTheWayTheSpecSays() {
        var list = [install("", "9.9"), install("github:fake/zeta", "1.0"), install("github:fake/alpha", "1.0"), install("github:fake/zeta", "2.0")]
        list += (0..<1100).map { install(String(format: "github:fake/app-%04d", $0), "1.0") }
        let kept = DeviceInventory.installed(PeerState(online: true, installed: list))
        XCTAssertEqual(kept.count, 1000)
        XCTAssertNil(kept[""])
        XCTAssertNotNil(kept["github:fake/alpha"], "'alpha' sorts before 'app-' so it is among the first 1,000")
        XCTAssertNil(kept["github:fake/zeta"], "and the last ones in key order are dropped")
    }

    func testSummaryCountsAppsAndUpdates() {
        XCTAssertEqual(DeviceInventory.summary(of: PeerState(online: true, installed: [])), "0 apps installed")
        let one = PeerState(online: true, installed: [install("github:fake/a", "1.0", latest: "1.1")])
        XCTAssertEqual(DeviceInventory.summary(of: one), "1 app installed  ·  1 update waiting")
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
