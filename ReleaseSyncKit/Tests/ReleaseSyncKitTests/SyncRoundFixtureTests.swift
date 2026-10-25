import XCTest
@testable import ReleaseSyncKit

final class SyncRoundFixtureTests: XCTestCase {
    func testEverySyncRoundGivesWhatTheEngineGave() async throws {
        let cases = try Fixtures.json("sync-round.json").list("Cases")
        XCTAssertEqual(cases.count, 15)
        for c in cases {
            let input = try XCTUnwrap(c.member("Input")), expected = try XCTUnwrap(c.member("Expected"))
            let name = try XCTUnwrap(input.str("Name"))
            let remoteText: String? = try input.str("RemoteText") ?? input.present("Remote").map { JSONWriter.write($0) }
            let transport = MemoryTransport(text: remoteText)
            let local = try SyncDoc(json: try XCTUnwrap(input.member("Local")))
            let device = try SyncDevice(json: try XCTUnwrap(input.member("Self")))
            let now = try XCTUnwrap(Timestamp(text: try XCTUnwrap(input.str("Now"))))
            let pass = input.str("Passphrase")

            if let kind = expected.str("Error") {
                do {
                    _ = try await SyncService.run(transport: transport, local: local, passphrase: pass, device: device, now: now)
                    XCTFail("\(name) should have failed")
                } catch let e as SyncError {
                    XCTAssertEqual(e.kind.rawValue, kind, name)
                    XCTAssertEqual(e.message, expected.str("Message"), name)
                }
                let writes = await transport.writes
                XCTAssertEqual(writes, 0, "\(name): a failed round writes nothing")
                continue
            }

            let result = try await SyncService.run(transport: transport, local: local, passphrase: pass, device: device, now: now)
            XCTAssertEqual(result.firstSync, expected.flag("FirstSync"), name)
            XCTAssertEqual(result.changed, expected.flag("Changed"), name)
            XCTAssertEqual(result.pushed, expected.flag("Pushed"), name)
            XCTAssertEqual(canonical(result.doc), canonical(try SyncDoc(json: try XCTUnwrap(expected.member("Doc")))), name)

            let writes = await transport.writes
            XCTAssertEqual(writes, result.pushed ? 1 : 0, name)
            if result.pushed, let written = await transport.text {
                XCTAssertEqual(SyncCrypto.isEncrypted(written), expected.flag("WrittenEncrypted"), name)
                let body = SyncCrypto.isEncrypted(written) ? try SyncCrypto.decrypt(written, passphrase: pass) : written
                XCTAssertEqual(try SyncDoc(text: body), result.doc, "\(name): what was written is the merged document")
            }
        }
    }

    /// Reading on its own gives the same file contents and the same errors as a full round, and never writes.
    func testFetchOnlyReads() async throws {
        for c in try Fixtures.json("sync-round.json").list("Cases") {
            let input = try XCTUnwrap(c.member("Input")), expected = try XCTUnwrap(c.member("Expected"))
            let name = try XCTUnwrap(input.str("Name"))
            let remote: JSONValue? = input.present("Remote")
            let remoteText: String? = input.str("RemoteText") ?? remote.map { JSONWriter.write($0) }
            let transport = MemoryTransport(text: remoteText)
            let pass = input.str("Passphrase")
            if let kind = expected.str("Error") {
                do { _ = try await SyncService.fetch(transport: transport, passphrase: pass); XCTFail("\(name) should have failed") }
                catch let e as SyncError { XCTAssertEqual(e.kind.rawValue, kind, name); XCTAssertEqual(e.message, expected.str("Message"), name) }
            } else {
                let got = try await SyncService.fetch(transport: transport, passphrase: pass)
                var want: SyncDoc? = try remote.map { try SyncDoc(json: $0) }
                if want == nil, let t = remoteText, !t.allSatisfy(\.isWhitespace) { want = try SyncDoc(text: SyncCrypto.isEncrypted(t) ? try SyncCrypto.decrypt(t, passphrase: pass) : t) }
                XCTAssertEqual(got.doc, want, name)
                XCTAssertEqual(got.locked, remoteText.map { SyncCrypto.isEncrypted($0) } ?? false, name)
            }
            let writes = await transport.writes
            XCTAssertEqual(writes, 0, "\(name): reading writes nothing")
        }
    }
}
