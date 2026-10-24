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
}
