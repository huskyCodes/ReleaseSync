import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class LanPairSecretsTests: XCTestCase {
    let id = "00112233445566778899aabbccddeeff"
    let secret = Data((0..<32).map { UInt8($0 &* 3) })

    func testTheItemNameIsMadeOnlyFromAValidPairId() {
        XCTAssertEqual(SecretTarget.lanPair(id), "ReleaseSync/LanPair/" + id)
        for bad in ["", "../GitHubToken", id.uppercased(), id + "0", "short"] { XCTAssertNil(SecretTarget.lanPair(bad), bad) }
    }

    func testASecretRoundTripsAndOnlyExactlyThirtyTwoBytesAreAccepted() throws {
        let s = LanPairSecrets(store: InMemorySecrets())
        XCTAssertNil(try s.read(id))
        try s.write(secret, for: id)
        XCTAssertEqual(try s.read(id), secret)
        XCTAssertTrue(try s.exists(id))
        XCTAssertThrowsError(try s.write(Data(count: 31), for: id)) { XCTAssertEqual($0 as? LanSecretError, .badSecret) }
        XCTAssertThrowsError(try s.read("not an id")) { XCTAssertEqual($0 as? LanSecretError, .badPairId) }
        try s.delete(id)
        XCTAssertNil(try s.read(id))
    }

    func testADamagedStoredSecretIsRefusedNotUsed() throws {
        let store = InMemorySecrets()
        try store.write(SecretTarget.lanPair(id)!, "not base64!!")
        XCTAssertThrowsError(try LanPairSecrets(store: store).read(id)) { XCTAssertEqual($0 as? LanSecretError, .badSecret) }
        try store.write(SecretTarget.lanPair(id)!, Data(count: 16).base64EncodedString())
        XCTAssertThrowsError(try LanPairSecrets(store: store).read(id), "a secret of the wrong size")
    }

    func testTheRealKeychainKeepsItAndItIsRemovedAfterwards() throws {
        let keychain = KeychainSecretStore(prefix: "ReleaseSyncTests-\(UUID().uuidString)/")
        let s = LanPairSecrets(store: keychain)
        defer { try? s.delete(id) }
        try s.write(secret, for: id)
        XCTAssertEqual(try s.read(id), secret)
        try s.delete(id)
        XCTAssertNil(try s.read(id))
    }
}

@MainActor
final class LanModelTests: XCTestCase {
    let secrets = LanPairSecrets(store: InMemorySecrets())
    func defaults() -> UserDefaults { UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)! }
    func pair(_ n: Int, paused: Bool = false) -> LanPairRecord {
        LanPairRecord(pairId: String(format: "%032x", n), peerName: "Device \(n)", peerPlatform: "Windows", role: .host, createdAt: Timestamp.now(), paused: paused)
    }
    let key = Data(repeating: 7, count: 32)

    func testTheRealAppCannotTurnItOnYet() {
        let m = LanModel(defaults: defaults(), secrets: secrets)
        XCTAssertFalse(LanModel.networkingBuilt)
        XCTAssertFalse(m.isAvailable)
        m.setEnabled(true)
        XCTAssertFalse(m.enabled, "nothing listens or announces in this version")
    }

    func testPairingsAreKeptWithTheirSecretsAndForgottenTogether() throws {
        let d = defaults()
        let m = LanModel(defaults: d, secrets: secrets, available: true)
        try m.add(pair(1), secret: key)
        try m.add(pair(2), secret: key)
        XCTAssertEqual(LanModel(defaults: d, secrets: secrets, available: true).pairs.map(\.peerName), ["Device 1", "Device 2"], "kept between runs")
        XCTAssertNotNil(try secrets.read(pair(1).pairId))
        try m.remove(pair(1).pairId)
        XCTAssertNil(try secrets.read(pair(1).pairId), "the secret goes with the record")
        XCTAssertEqual(m.pairs.map(\.peerName), ["Device 2"])
        try m.removeAll()
        XCTAssertTrue(m.pairs.isEmpty)
        XCTAssertNil(try secrets.read(pair(2).pairId))
        XCTAssertTrue(LanModel(defaults: d, secrets: secrets, available: true).pairs.isEmpty)
    }

    func testAtMostSixteenDevicesCanBePaired() throws {
        let m = LanModel(defaults: defaults(), secrets: secrets, available: true)
        for n in 1...16 { try m.add(pair(n), secret: key) }
        XCTAssertThrowsError(try m.add(pair(17), secret: key)) { XCTAssertTrue($0 is LanModelError) }
        XCTAssertNil(try secrets.read(pair(17).pairId), "no secret is kept for a refused pairing")
        XCTAssertNoThrow(try m.add(pair(3), secret: key), "pairing the same device again replaces it")
        XCTAssertEqual(m.pairs.count, 16)
    }

    func testPauseIsRememberedAndKeepsThePairing() throws {
        let d = defaults()
        let m = LanModel(defaults: d, secrets: secrets, available: true)
        try m.add(pair(1), secret: key)
        m.setPaused(pair(1).pairId, true)
        let again = LanModel(defaults: d, secrets: secrets, available: true)
        XCTAssertEqual(again.pairs.first?.paused, true)
        XCTAssertNotNil(try secrets.read(pair(1).pairId))
    }

    func testTheSwitchIsRememberedOnlyWhenTheFeatureExists() {
        let d = defaults()
        let m = LanModel(defaults: d, secrets: secrets, available: true)
        m.setEnabled(true)
        XCTAssertTrue(LanModel(defaults: d, secrets: secrets, available: true).enabled)
        XCTAssertFalse(LanModel(defaults: d, secrets: secrets, available: false).enabled)
    }

    func testBlockedAttemptsStayInMemoryAndAreCountedForAnHour() {
        let d = defaults()
        let m = LanModel(defaults: d, secrets: secrets, available: true)
        let now = Date()
        m.noteBlockedAttempt(address: "192.0.2.1", at: now.addingTimeInterval(-10))
        m.noteBlockedAttempt(address: "192.0.2.2", at: now.addingTimeInterval(-7200))
        XCTAssertEqual(m.blockedInLastHour(now: now).map(\.address), ["192.0.2.1"])
        for dict in [d.dictionaryRepresentation()] { XCTAssertFalse("\(dict)".contains("192.0.2"), "never written to preferences") }
        m.clearOnQuit()
        XCTAssertTrue(m.blocked.isEmpty)
    }

    func testAPairingFileWithAnOddIdIsIgnoredOnLoad() throws {
        let d = defaults()
        var odd = pair(1); odd.pairId = "../../etc"
        d.set(try JSONEncoder().encode([odd, pair(2)]), forKey: "LanPairs")
        XCTAssertEqual(LanModel(defaults: d, secrets: secrets, available: true).pairs.map(\.peerName), ["Device 2"])
    }

    func testTheDigitsAreShownInTwoGroups() {
        XCTAssertEqual(LanPairingStep.spaced("482913"), "482 913")
        XCTAssertEqual(LanPairingStep.spaced("012345"), "012 345")
    }
}
