import XCTest
@testable import ReleaseSyncKit

/// Structure only: the numbers and shapes that need no fixture. The wire formats are tested against Fixtures/lan as those files arrive.
final class LanStructureTests: XCTestCase {
    func testTheNumbersAreTheSpecsNumbers() {
        XCTAssertEqual(Lan.serviceType, "_releasesync._tcp")
        XCTAssertEqual(Lan.pairingServiceType, "_releasesync-pair._tcp")
        XCTAssertEqual([Lan.pingInterval, Lan.deadAfter, Lan.handshakeTimeout, Lan.pairingWindow, Lan.pairingStepTimeout, Lan.pairingConfirmWait], [2, 5, 5, 120, 5, 60])
        XCTAssertEqual(Lan.redialBackoff, [1, 2, 4, 8, 16, 30])
        XCTAssertEqual(Lan.unknownBlockFirst, 300); XCTAssertEqual(Lan.pairedBlockFirst, 600); XCTAssertEqual(Lan.blockCap, 3600)
        XCTAssertEqual(Lan.maxRecordPlaintext, 512 * 1024)
        XCTAssertEqual([Lan.maxPaired, Lan.maxConnections, Lan.maxInstalled], [16, 16, 1000])
        XCTAssertEqual(Lan.noiseProtocolName.utf8.count, 31, "so the Noise hash starts as the name padded with zeros")
    }

    func testPairIdsAreThirtyTwoLowerCaseHexDigits() {
        let id = LanPairRecord.makePairId { Data((0..<$0).map { UInt8($0 * 17) }) }
        XCTAssertEqual(id, "00112233445566778899aabbccddeeff")
        XCTAssertTrue(LanPairRecord.isValidPairId(id))
        for bad in ["", "ABC", String(repeating: "g", count: 32), String(repeating: "a", count: 31), String(repeating: "A", count: 32), id + "0", "../" + String(repeating: "a", count: 29)] {
            XCTAssertFalse(LanPairRecord.isValidPairId(bad), bad)
        }
    }

    func testAPairRecordKeepsNoSecretAndSurvivesSavingAndLoading() throws {
        let r = LanPairRecord(pairId: "00112233445566778899aabbccddeeff", peerName: "Example PC", peerPlatform: "Windows", role: .joiner, createdAt: Timestamp(text: "2026-01-01T12:00:00Z")!, peerDeviceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", paused: true)
        let data = try JSONEncoder().encode(r)
        XCTAssertEqual(try JSONDecoder().decode(LanPairRecord.self, from: data), r)
        let text = String(decoding: data, as: UTF8.self).lowercased()
        XCTAssertFalse(text.contains("psk") || text.contains("secret") || text.contains("key"), text)
    }

    func testUpdatesWaitingOnAPeer() {
        XCTAssertTrue(LanInstall(key: "github:fake/a", version: "1.0", latest: "1.1").hasUpdate)
        XCTAssertFalse(LanInstall(key: "github:fake/a", version: "1.1", latest: "1.1").hasUpdate)
        XCTAssertFalse(LanInstall(key: "github:fake/a", version: "1.0").hasUpdate)
        XCTAssertFalse(LanInstall(key: "github:fake/a", version: "", latest: "1.1").hasUpdate)
    }
}
