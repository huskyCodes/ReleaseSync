import XCTest
@testable import ReleaseSyncKit

/// Files written by this Swift code, kept in Fixtures/swift-written so the C# tests can read them the other way round.
/// To rewrite them after an intended change: RELEASESYNC_WRITE_SWIFT_FIXTURES=1 swift test --filter SwiftWrittenFixtureTests
final class SwiftWrittenFixtureTests: XCTestCase {
    static let passphrase = "fake passphrase for tests"
    var dir: URL { Fixtures.root.appendingPathComponent("swift-written") }
    var writing: Bool { ProcessInfo.processInfo.environment["RELEASESYNC_WRITE_SWIFT_FIXTURES"] == "1" }

    func mergeResult(_ name: String) throws -> String {
        let a = try Fixtures.doc("merge/\(name)/a.json"), b = try Fixtures.doc("merge/\(name)/b.json")
        let now = try XCTUnwrap(Timestamp(text: try XCTUnwrap(try Fixtures.json("merge/\(name)/case.json").str("Now"))))
        return SyncMerger.merge(a, b, now: now).text() + "\n"
    }

    func write(_ path: String, _ text: String) throws {
        let url = dir.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    func testMergeResultsWrittenBySwiftAreStable() throws {
        for name in try Fixtures.folders("merge") {
            let made = try mergeResult(name)
            if writing { try write("merge/\(name).json", made) }
            else { XCTAssertEqual(try String(contentsOf: dir.appendingPathComponent("merge/\(name).json"), encoding: .utf8), made, "\(name): rewrite with RELEASESYNC_WRITE_SWIFT_FIXTURES=1") }
        }
    }

    func testTheLockedFileWrittenBySwiftOpens() throws {
        let plain = try SyncDoc(text: try Fixtures.text("merge/02-union-and-ordering/expected.json")).text() + "\n"
        if writing {
            try write("crypto/plaintext.json", plain)
            try write("crypto/envelope.json", try SyncCrypto.encrypt(plain, passphrase: Self.passphrase) + "\n")      // a fresh salt and nonce each time
        }
        let envelope = try String(contentsOf: dir.appendingPathComponent("crypto/envelope.json"), encoding: .utf8)
        XCTAssertTrue(SyncCrypto.isEncrypted(envelope))
        XCTAssertEqual(try SyncCrypto.decrypt(envelope, passphrase: Self.passphrase), try String(contentsOf: dir.appendingPathComponent("crypto/plaintext.json"), encoding: .utf8))
    }
}
