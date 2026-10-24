import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class MemorySecrets: SecretStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: String] = [:]
    func read(_ target: String) throws -> String? { lock.lock(); defer { lock.unlock() }; return items[target] }
    func write(_ target: String, _ secret: String) throws { lock.lock(); items[target] = secret; lock.unlock() }
    func delete(_ target: String) throws { lock.lock(); items[target] = nil; lock.unlock() }
}

/// Counts what the model asks for, to prove it never writes.
actor SpyTransport: SyncTransport {
    nonisolated var description: String { "spy" }
    let text: String?
    private(set) var reads = 0, writes = 0
    init(text: String?) { self.text = text }
    func read() async throws -> String? { reads += 1; return text }
    func write(_ t: String) async throws { writes += 1 }
}

@MainActor
final class LibraryModelTests: XCTestCase {
    static func library() -> SyncDoc {
        var d = SyncDoc()
        var a = SyncApp(); a.key = "github:fake/one"; a.owner = "fake"; a.repo = "one"
        var t = TryLaterItem(); t.owner = "fake"; t.repo = "later"
        d.apps = [a]; d.tryLater = [t]
        d.devices["d1"] = SyncDevice(id: "d1", name: "Example PC", platform: "Windows", appVersion: "1.9.1", lastSeen: Timestamp.now())
        return d
    }

    func model(secrets: MemorySecrets, transport: SpyTransport) -> LibraryModel {
        LibraryModel(secrets: secrets, makeTransport: { _ in transport })
    }

    func testWithoutATokenItAsksForOneAndDoesNotTouchTheNetwork() async {
        let spy = SpyTransport(text: nil)
        let m = model(secrets: MemorySecrets(), transport: spy)
        await m.refresh()
        XCTAssertEqual(m.phase, .needsToken)
        let reads = await spy.reads
        XCTAssertEqual(reads, 0)
    }

    func testShowsTheLibraryAndNeverWrites() async throws {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let spy = SpyTransport(text: Self.library().text())
        let m = model(secrets: secrets, transport: spy)
        await m.refresh()
        XCTAssertEqual(m.phase, .loaded)
        XCTAssertEqual(m.apps.map(\.key), ["github:fake/one"])
        XCTAssertEqual(m.tryLater.map(\.repo), ["later"])
        XCTAssertEqual(m.devices.map(\.name), ["Example PC"])
        XCTAssertNotNil(m.lastFetched)
        let (reads, writes) = (await spy.reads, await spy.writes)
        XCTAssertEqual(reads, 1)
        XCTAssertEqual(writes, 0, "this version only reads the library")
    }

    func testNoLibraryYet() async throws {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = model(secrets: secrets, transport: SpyTransport(text: nil))
        await m.refresh()
        XCTAssertEqual(m.phase, .noLibrary)
        XCTAssertTrue(m.apps.isEmpty)
    }

    func testLockedLibraryAsksForThePassphraseThenOpens() async throws {
        let pass = "fake passphrase for tests"
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = model(secrets: secrets, transport: SpyTransport(text: try SyncCrypto.encrypt(Self.library().text(), passphrase: pass)))

        await m.refresh()
        XCTAssertEqual(m.phase, .needsPassphrase(wrong: false))

        try await m.save("wrong one", as: SecretTarget.syncPassphrase)
        await m.refresh()
        XCTAssertEqual(m.phase, .needsPassphrase(wrong: true))
        XCTAssertTrue(m.apps.isEmpty, "nothing from a locked file is shown")

        try await m.save(pass, as: SecretTarget.syncPassphrase)
        await m.refresh()
        XCTAssertEqual(m.phase, .loaded)
        XCTAssertEqual(m.apps.count, 1)
    }

    func testFailuresAreShownInPlainWords() async throws {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = model(secrets: secrets, transport: SpyTransport(text: "this is not a library"))
        await m.refresh()
        XCTAssertEqual(m.phase, .failed("The sync file isn't readable, so nothing was changed."))
    }

    func testRemovingTheTokenClearsWhatWasShown() async throws {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = model(secrets: secrets, transport: SpyTransport(text: Self.library().text()))
        await m.refresh()
        XCTAssertEqual(m.phase, .loaded)
        try await m.remove(SecretTarget.gitHubToken)
        await m.refresh()
        XCTAssertEqual(m.phase, .needsToken)
        XCTAssertTrue(m.apps.isEmpty)
        let stillSaved = await m.hasSecret(SecretTarget.gitHubToken)
        XCTAssertFalse(stillSaved)
    }

    func testTokensAreTrimmedButPassphrasesAreNot() async throws {
        let secrets = MemorySecrets()
        let m = model(secrets: secrets, transport: SpyTransport(text: nil))
        try await m.save("  fake-token\n", as: SecretTarget.gitHubToken)
        try await m.save("  pass phrase  ", as: SecretTarget.syncPassphrase)
        XCTAssertEqual(try secrets.read(SecretTarget.gitHubToken), "fake-token")
        XCTAssertEqual(try secrets.read(SecretTarget.syncPassphrase), "  pass phrase  ")
    }

    func testDevicesAreNewestFirst() async throws {
        var doc = Self.library()
        doc.devices["old"] = SyncDevice(id: "old", name: "Old", platform: "Windows", appVersion: "1", lastSeen: Timestamp.now() - Timestamp.days(30))
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = model(secrets: secrets, transport: SpyTransport(text: doc.text()))
        await m.refresh()
        XCTAssertEqual(m.devices.map(\.name), ["Example PC", "Old"])
    }
}
