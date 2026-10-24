import XCTest
@testable import ReleaseSyncKit

final class PresenceFixtureTests: XCTestCase {
    func testLightsMatchTheEngine() throws {
        let cases = try Fixtures.json("presence.json").list("Online")
        XCTAssertEqual(cases.count, 8)
        for c in cases {
            let name = try XCTUnwrap(c.str("Name"))
            let device = try SyncDevice(json: try XCTUnwrap(c.member("Device")))
            let now = try XCTUnwrap(Timestamp(text: try XCTUnwrap(c.str("Now"))))
            XCTAssertEqual(device.isOnline(at: now), c.flag("IsOnline"), name)
        }
    }

    func testInstalledListIsReadTheSameWay() throws {
        let v = try Fixtures.json("presence.json")
        let inv = try XCTUnwrap(v.member("Inventory"))
        let device = try SyncDevice(json: try XCTUnwrap(inv.member("Device")))
        let expected = inv.list("Keys").map { "\($0.str("Key") ?? "")=\($0.str("Version") ?? "")" }
        XCTAssertEqual(device.inventory.map { "\($0.key)=\($0.version)" }, expected)
        XCTAssertEqual(device.installed?.count, 7, "the list as written is not tidied")

        // 1002 entries: only the first 1000 in key order
        let many = SyncDevice(id: "x", name: "x", platform: "Windows", appVersion: "1", installed: (0..<1002).reversed().map { SyncInstall(key: String(format: "github:fake/app-%04d", $0), version: "1.0") })
        let tooMany = try XCTUnwrap(v.member("TooMany"))
        XCTAssertEqual(many.inventory.count, Int(tooMany.whole("Count") ?? -1))
        XCTAssertEqual(many.inventory.first?.key, tooMany.str("First"))
        XCTAssertEqual(many.inventory.last?.key, tooMany.str("Last"))
    }

    func testUpdatesWaitingMatchTheEngine() throws {
        let cases = try Fixtures.json("presence.json").list("Updates")
        XCTAssertEqual(cases.count, 11)
        for c in cases {
            let item = SyncInstall(key: "github:fake/x", version: c.str("Version") ?? "", latest: c.str("Latest"))
            XCTAssertEqual(item.hasUpdate, c.flag("HasUpdate"), "\(c.str("Version") ?? "") -> \(c.str("Latest") ?? "nil")")
        }
    }

    func testNewerFormatIsRefusedAndOlderIsRead() async throws {
        XCTAssertEqual(SyncDoc.supportedVersion, 3)
        XCTAssertEqual(SyncService.heartbeat, Timestamp.hours(2))
        let older = try SyncDoc(text: #"{"Version":2,"Devices":{"d":{"Id":"d","Name":"PC","LastSeen":"2026-01-01T12:00:00Z"}}}"#)
        XCTAssertNil(older.devices["d"]?.online)
        XCTAssertNil(older.devices["d"]?.installed)
        XCTAssertFalse(older.text().contains("Online"), "a device that didn't say is written without the field")
    }
}
