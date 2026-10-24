import XCTest
@testable import ReleaseSyncKit

final class CryptoFixtureTests: XCTestCase {
    func testEnvelopesFromTheEngineOpen() throws {
        let cases = try Fixtures.folders("crypto")
        XCTAssertEqual(cases.count, 4)
        for name in cases {
            let info = try Fixtures.json("crypto/\(name)/case.json")
            let envelope = try Fixtures.text("crypto/\(name)/envelope.json")
            XCTAssertTrue(SyncCrypto.isEncrypted(envelope), name)
            XCTAssertEqual(try SyncCrypto.decrypt(envelope, passphrase: info.str("Passphrase")), try Fixtures.text("crypto/\(name)/plaintext.json"), name)
            for case .string(let wrong) in info.list("WrongPassphrases") {
                XCTAssertThrowsError(try SyncCrypto.decrypt(envelope, passphrase: wrong), "\(name): \(wrong)") { XCTAssertEqual($0 as? SyncError, SyncError.wrongPassphrase) }
            }
        }
    }

    /// With the same salt and nonce, Swift must write the very same envelope the engine wrote.
    func testEncryptingGivesTheEnginesBytes() throws {
        func bytes(_ seed: Int, _ n: Int) -> Data { Data((0..<n).map { UInt8(truncatingIfNeeded: seed * 37 + $0 * 11) }) }
        let seeds = ["01-plain-ascii": (1, 2, 200_000), "02-non-ascii-passphrase": (3, 4, 200_000), "03-spaces-are-kept": (5, 6, 200_000), "04-fewer-iterations": (7, 8, 1000)]
        for (name, s) in seeds {
            let pass = try XCTUnwrap(try Fixtures.json("crypto/\(name)/case.json").str("Passphrase"))
            let plain = try Fixtures.text("crypto/\(name)/plaintext.json")
            let made = try SyncCrypto.encrypt(plain, passphrase: pass, salt: bytes(s.0, 16), nonce: bytes(s.1, 12), iterations: s.2)
            XCTAssertEqual(made + "\n", try Fixtures.text("crypto/\(name)/envelope.json"), name)
        }
    }

    func testBrokenEnvelopesFailTheSameWay() throws {
        let cases = try Fixtures.json("crypto/errors.json").list("Cases")
        XCTAssertGreaterThanOrEqual(cases.count, 9)
        for c in cases {
            let name = try XCTUnwrap(c.str("Name"))
            XCTAssertThrowsError(try SyncCrypto.decrypt(try XCTUnwrap(c.str("EnvelopeText")), passphrase: c.str("Passphrase")), name) { error in
                let e = error as? SyncError
                XCTAssertEqual(e?.kind.rawValue, c.str("Error"), name)
                XCTAssertEqual(e?.message, c.str("Message"), name)
            }
        }
    }

    func testLockedFilesAreRecognised() throws {
        for c in try Fixtures.json("crypto/is-encrypted.json").list("Cases") {
            XCTAssertEqual(SyncCrypto.isEncrypted(try XCTUnwrap(c.str("Text"))), c.flag("Encrypted"), c.str("Name") ?? "")
        }
    }

    func testRoundTripAndShape() throws {
        let sealed = try SyncCrypto.encrypt("héllo ✓", passphrase: "fake passphrase for tests")
        XCTAssertEqual(try SyncCrypto.decrypt(sealed, passphrase: "fake passphrase for tests"), "héllo ✓")
        guard case .object(let m) = try JSONParser.parse(sealed) else { return XCTFail() }
        XCTAssertEqual(m.map(\.key), ["releasesync-enc", "Kdf", "Iter", "Salt", "Nonce", "Tag", "Data"])
        XCTAssertEqual(m.first { $0.key == "Iter" }?.value, .int(200_000))
        // a fresh salt and nonce every time
        XCTAssertNotEqual(sealed, try SyncCrypto.encrypt("héllo ✓", passphrase: "fake passphrase for tests"))
    }
}
