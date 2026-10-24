import CryptoKit
import XCTest
@testable import ReleaseSync

final class EncryptedFileBlobStoreTests: XCTestCase {
    var dir: URL!
    var file: URL { dir.appendingPathComponent("ReleaseSync/test.cache") }

    override func setUp() { dir = FileManager.default.temporaryDirectory.appendingPathComponent("releasesync-blob-" + UUID().uuidString) }
    override func tearDown() { try? FileManager.default.removeItem(at: dir) }

    func testRoundTripAndTheFileIsNotReadable() async throws {
        let secrets = InMemorySecrets()
        let store = EncryptedFileBlobStore(url: file, secrets: secrets)
        let loadedBefore = try await store.load()
        XCTAssertNil(loadedBefore)
        let secret = Data(#"{"github:fake/app":{"latestTag":"v1.0"}}"#.utf8)
        try await store.save(secret)
        let onDisk = try Data(contentsOf: file)
        XCTAssertNil(onDisk.range(of: Data("fake/app".utf8)), "not in plain text")
        XCTAssertNil(onDisk.range(of: Data("latestTag".utf8)))
        let loaded = try await EncryptedFileBlobStore(url: file, secrets: secrets).load()
        XCTAssertEqual(loaded, secret, "a new copy of the app finds it again with the key from the Keychain")
        let perms = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int
        XCTAssertEqual(perms, 0o600)
        XCTAssertEqual(try file.resourceValues(forKeys: [.isExcludedFromBackupKey]).isExcludedFromBackup, true)
    }

    func testTheKeyIsMadeOnceAndKept() async throws {
        let secrets = InMemorySecrets()
        let store = EncryptedFileBlobStore(url: file, secrets: secrets)
        try await store.save(Data("one".utf8))
        let key1 = try secrets.read(EncryptedFileBlobStore.keyTarget)
        try await store.save(Data("two".utf8))
        XCTAssertEqual(try secrets.read(EncryptedFileBlobStore.keyTarget), key1)
        XCTAssertEqual(Data(base64Encoded: try XCTUnwrap(key1))?.count, 32)
        let loaded = try await store.load()
        XCTAssertEqual(loaded, Data("two".utf8))
    }

    func testWithoutTheKeyOrWithADamagedFileNothingIsReturned() async throws {
        let secrets = InMemorySecrets()
        try await EncryptedFileBlobStore(url: file, secrets: secrets).save(Data("secret".utf8))
        let noKey = try await EncryptedFileBlobStore(url: file, secrets: InMemorySecrets()).load()
        XCTAssertNil(noKey, "a different Keychain can't open it")

        var bytes = try Data(contentsOf: file); bytes[bytes.count - 3] ^= 0xFF
        try bytes.write(to: file)
        let damaged = try await EncryptedFileBlobStore(url: file, secrets: secrets).load()
        XCTAssertNil(damaged, "a changed file fails its check")
        try Data("not even encrypted".utf8).write(to: file)
        let junk = try await EncryptedFileBlobStore(url: file, secrets: secrets).load()
        XCTAssertNil(junk)
    }
}
