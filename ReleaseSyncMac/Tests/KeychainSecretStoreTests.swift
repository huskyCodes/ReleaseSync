import XCTest
@testable import ReleaseSync

/// Uses the real Keychain, with its own item names, and removes them afterwards.
final class KeychainSecretStoreTests: XCTestCase {
    var store: KeychainSecretStore!
    let target = SecretTarget.gitHubToken

    override func setUp() { store = KeychainSecretStore(prefix: "ReleaseSyncTests-\(UUID().uuidString)/") }
    override func tearDown() { for t in [SecretTarget.gitHubToken, SecretTarget.syncPassphrase, SecretTarget.virusTotalKey] { try? store.delete(t) } }

    func testNothingStoredYet() throws {
        XCTAssertNil(try store.read(target))
    }

    func testWriteReadReplaceDelete() throws {
        try store.write(target, "fake-token-one")
        XCTAssertEqual(try store.read(target), "fake-token-one")
        try store.write(target, "fake-token-two")
        XCTAssertEqual(try store.read(target), "fake-token-two")
        try store.delete(target)
        XCTAssertNil(try store.read(target))
        XCTAssertNoThrow(try store.delete(target), "removing something that isn't there is fine")
    }

    func testTargetsAreKeptApart() throws {
        try store.write(SecretTarget.gitHubToken, "token")
        try store.write(SecretTarget.syncPassphrase, "  a passphrase kept exactly ✓  ")
        XCTAssertEqual(try store.read(SecretTarget.gitHubToken), "token")
        XCTAssertEqual(try store.read(SecretTarget.syncPassphrase), "  a passphrase kept exactly ✓  ")
        XCTAssertNil(try store.read(SecretTarget.virusTotalKey))
    }

    func testTheNamesMatchTheWindowsApp() {
        XCTAssertEqual(SecretTarget.gitHubToken, "ReleaseSync/GitHubToken")
        XCTAssertEqual(SecretTarget.syncPassphrase, "ReleaseSync/SyncPassphrase")
        XCTAssertEqual(SecretTarget.virusTotalKey, "ReleaseSync/VirusTotalKey")
    }
}
