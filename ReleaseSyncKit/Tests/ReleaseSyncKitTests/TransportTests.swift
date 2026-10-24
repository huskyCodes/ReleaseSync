import XCTest
@testable import ReleaseSyncKit

/// A pretend GitHub: records every request and answers from a closure. Nothing here touches the network.
final class FakeWeb: @unchecked Sendable {
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    var answer: @Sendable (URLRequest) throws -> HTTPReply = { _ in HTTPReply(status: 500) }
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return seen }
    var send: HTTPSend { { [self] r in self.lock.lock(); self.seen.append(r); self.lock.unlock(); return try self.answer(r) } }
}

func reply(_ status: Int = 200, _ json: String = "{}", headers: [String: String] = [:]) -> HTTPReply { HTTPReply(status: status, headers: headers, body: Data(json.utf8)) }

final class GistTransportTests: XCTestCase {
    let token = "fake-token-0123456789"
    let file = GistTransport.fileName

    func testReadsAGistItAlreadyKnows() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(200, #"{"files":{"\#(self.file)":{"content":"hello"}}}"#) }
        let t = GistTransport(token: token, gistId: " abc123 ", send: web.send)
        let text = try await t.read()
        XCTAssertEqual(text, "hello")
        XCTAssertEqual(web.requests.count, 1)
        let r = web.requests[0]
        XCTAssertEqual(r.url?.absoluteString, "https://api.github.com/gists/abc123")
        XCTAssertEqual(r.httpMethod, "GET")
        XCTAssertEqual(r.value(forHTTPHeaderField: "Authorization"), "Bearer \(token)")
        XCTAssertEqual(r.value(forHTTPHeaderField: "Accept"), "application/vnd.github+json")
        XCTAssertNotNil(r.value(forHTTPHeaderField: "User-Agent"))
    }

    func testFindsTheGistOnALaterPage() async throws {
        let web = FakeWeb()
        let filler = "[" + (0..<100).map { _ in #"{"id":"x","files":{"notes.txt":{}}}"# }.joined(separator: ",") + "]"
        web.answer = { r in
            let url = r.url!.absoluteString
            if url.hasSuffix("page=1") { return reply(200, filler) }
            if url.hasSuffix("page=2") { return reply(200, #"[{"id":"other","files":{"a.txt":{}}},{"id":"found1","files":{"\#(self.file)":{}}}]"#) }
            return reply(200, #"{"files":{"\#(self.file)":{"content":"from the gist"}}}"#)
        }
        let t = GistTransport(token: token, send: web.send)
        let text = try await t.read()
        XCTAssertEqual(text, "from the gist")
        let id = await t.gistId
        XCTAssertEqual(id, "found1")
        XCTAssertEqual(web.requests.map { $0.url!.absoluteString }, [
            "https://api.github.com/gists?per_page=100&page=1", "https://api.github.com/gists?per_page=100&page=2", "https://api.github.com/gists/found1",
        ])
    }

    func testNoGistYetMeansNothingStored() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(200, #"[{"id":"x","files":{"notes.txt":{}}}]"#) }      // a short page ends the search
        let t = GistTransport(token: token, gistId: "   ", send: web.send)
        let text = try await t.read()
        XCTAssertNil(text)
        XCTAssertEqual(web.requests.count, 1)
    }

    func testCreatesASecretGistThenUpdatesIt() async throws {
        let web = FakeWeb()
        web.answer = { r in reply(r.httpMethod == "POST" ? 201 : 200, #"{"id":"newgist"}"#) }
        let t = GistTransport(token: token, send: web.send)
        try await t.write("first \"text\" with <odd> + characters")
        var body = try JSONParser.parse(String(decoding: try XCTUnwrap(web.requests[0].httpBody), as: UTF8.self))
        XCTAssertEqual(web.requests[0].httpMethod, "POST")
        XCTAssertEqual(web.requests[0].url?.absoluteString, "https://api.github.com/gists")
        XCTAssertEqual(body.str("description"), "ReleaseSync library (synced between your devices)")
        XCTAssertEqual(body.flag("public"), false)
        XCTAssertEqual(body.member("files")?.member(file)?.str("content"), "first \"text\" with <odd> + characters")
        let id = await t.gistId
        XCTAssertEqual(id, "newgist")

        try await t.write("second")
        XCTAssertEqual(web.requests[1].httpMethod, "PATCH")
        XCTAssertEqual(web.requests[1].url?.absoluteString, "https://api.github.com/gists/newgist")
        body = try JSONParser.parse(String(decoding: try XCTUnwrap(web.requests[1].httpBody), as: UTF8.self))
        XCTAssertEqual(body.member("files")?.member(file)?.str("content"), "second")
        XCTAssertNil(body.member("public"))
    }

    func testTheTokenGoesOnlyToTheApiHost() async throws {
        let web = FakeWeb()
        web.answer = { r in
            if r.url?.host == "api.github.com" { return reply(200, #"{"files":{"\#(self.file)":{"truncated":true,"raw_url":"https://gist.githubusercontent.com/user/abc/raw/file","content":"cut off"}}}"#) }
            return reply(200, "the whole file")
        }
        let t = GistTransport(token: token, gistId: "abc", send: web.send)
        let text = try await t.read()
        XCTAssertEqual(text, "the whole file")
        XCTAssertEqual(web.requests.count, 2)
        XCTAssertEqual(web.requests[0].value(forHTTPHeaderField: "Authorization"), "Bearer \(token)")
        XCTAssertEqual(web.requests[1].url?.host, "gist.githubusercontent.com")
        XCTAssertNil(web.requests[1].value(forHTTPHeaderField: "Authorization"))
        for r in web.requests where r.url?.host != "api.github.com" { XCTAssertFalse(r.allHTTPHeaderFields?.values.contains { $0.contains(token) } ?? false) }
    }

    func testATruncatedFileWithoutASafeAddressIsNotUsed() async throws {
        for raw in [#""raw_url":"http://gist.githubusercontent.com/x""#, #""other":1"#] {
            let web = FakeWeb()
            web.answer = { _ in reply(200, #"{"files":{"\#(self.file)":{"truncated":true,\#(raw),"content":"cut off"}}}"#) }
            let t = GistTransport(token: token, gistId: "abc", send: web.send)
            do { _ = try await t.read(); XCTFail("should not use cut-off text") } catch let e as SyncError { XCTAssertEqual(e.kind, .general) }
            XCTAssertEqual(web.requests.count, 1)
        }
    }

    func testGistIdsAreEscapedInTheAddress() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(200, #"{"files":{}}"#) }
        let text = try await GistTransport(token: token, gistId: "a/b c", send: web.send).read()
        XCTAssertNil(text)
        XCTAssertEqual(web.requests[0].url?.absoluteString, "https://api.github.com/gists/a%2Fb%20c")
    }

    func testErrorsAreExplainedAndNeverShowTheToken() async throws {
        let cases: [(HTTPReply?, URLError?, String)] = [
            (reply(401), nil, "GitHub didn't accept your token. Add a fresh one in Settings."),
            (reply(403, headers: ["x-ratelimit-remaining": "0"]), nil, "GitHub's hourly request limit is used up. Sync will work again when it resets."),
            (reply(403, headers: ["x-ratelimit-remaining": "12"]), nil, "GitHub wouldn't open the sync gist. Your token needs the “gist” permission (a classic token with the gist box ticked), or the gist was deleted."),
            (reply(404), nil, "GitHub wouldn't open the sync gist. Your token needs the “gist” permission (a classic token with the gist box ticked), or the gist was deleted."),
            (reply(500), nil, "GitHub answered 500."),
            (reply(200, "<html>not json</html>"), nil, "GitHub sent back something unexpected."),
            (nil, URLError(.timedOut), "GitHub took too long to answer."),
            (nil, URLError(.notConnectedToInternet), "Couldn't reach GitHub. Check your internet connection."),
        ]
        for (answer, failure, message) in cases {
            let web = FakeWeb()
            web.answer = { _ in if let failure { throw failure }; return answer! }
            let t = GistTransport(token: token, gistId: "abc", send: web.send)
            do { _ = try await t.read(); XCTFail(message) } catch let e as SyncError {
                XCTAssertEqual(e.message, message)
                XCTAssertEqual(e.kind, .general)
                XCTAssertFalse(e.message.contains(token))
            }
            do { try await t.write("x"); XCTFail(message) } catch let e as SyncError { XCTAssertEqual(e.message, message) }
        }
    }

    func testASyncRoundThroughAGistIsEncryptedWhenAPassphraseIsSet() async throws {
        let web = FakeWeb()
        let stored = Stored()
        web.answer = { r in
            if r.httpMethod == "GET", r.url?.path == "/gists" { return reply(200, "[]") }          // no gist made yet
            if r.httpMethod == "GET" { return stored.value.map { reply(200, JSONWriter.write(.object([JSONMember("files", .object([JSONMember(GistTransport.fileName, .object([JSONMember("content", .string($0))]))]))]))) } ?? reply(404) }
            if let body = r.httpBody, let v = try? JSONParser.parse(String(decoding: body, as: UTF8.self)), let c = v.member("files")?.member(GistTransport.fileName)?.str("content") { stored.value = c }
            return reply(200, #"{"id":"g1"}"#)
        }
        let me = SyncDevice(id: "dev-1", name: "Example Mac", platform: "Mac", appVersion: "0.1")
        var lib = SyncDoc(); var app = SyncApp(); app.key = "github:fake/app"; app.owner = "fake"; app.repo = "app"; lib.apps = [app]
        let t = GistTransport(token: token, send: web.send)
        _ = try await SyncService.run(transport: t, local: lib, passphrase: "fake passphrase for tests", device: me, now: Timestamp(text: "2026-01-01T12:00:00Z")!)
        let file = try XCTUnwrap(stored.value)
        XCTAssertTrue(SyncCrypto.isEncrypted(file))
        XCTAssertFalse(file.contains("fake/app"))
        XCTAssertEqual(try SyncDoc(text: try SyncCrypto.decrypt(file, passphrase: "fake passphrase for tests")).apps.map(\.key), ["github:fake/app"])
    }
    final class Stored: @unchecked Sendable { var value: String? }
}

final class FolderTransportTests: XCTestCase {
    var dir: URL!
    override func setUp() { dir = FileManager.default.temporaryDirectory.appendingPathComponent("releasesynckit-test-" + UUID().uuidString) }
    override func tearDown() { try? FileManager.default.removeItem(at: dir) }

    func testNothingStoredYet() async throws {
        let text = try await FolderTransport(file: dir.appendingPathComponent("ReleaseSync/library.json")).read()
        XCTAssertNil(text)
    }

    func testWritesCreateTheFolderAndReadBack() async throws {
        let t = FolderTransport(file: dir.appendingPathComponent("ReleaseSync/library.json"))
        try await t.write("one ✓")
        try await t.write("two ✓")
        let text = try await t.read()
        XCTAssertEqual(text, "two ✓")
        let bytes = try Data(contentsOf: t.file)
        XCTAssertFalse(bytes.starts(with: [0xEF, 0xBB, 0xBF]), "no byte-order mark")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: t.file.deletingLastPathComponent().path), ["library.json"], "no leftovers")
    }

    func testAByteOrderMarkFromAnotherProgramIsIgnored() async throws {
        let f = dir.appendingPathComponent("library.json")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try (Data([0xEF, 0xBB, 0xBF]) + Data("{}".utf8)).write(to: f)
        let text = try await FolderTransport(file: f).read()
        XCTAssertEqual(text, "{}")
    }

    func testASyncRoundThroughAFolder() async throws {
        let t = FolderTransport(file: dir.appendingPathComponent("ReleaseSync/library.json"))
        let me = SyncDevice(id: "dev-1", name: "Example Mac", platform: "Mac", appVersion: "0.1")
        let first = try await SyncService.run(transport: t, local: SyncDoc(), passphrase: nil, device: me, now: Timestamp(text: "2026-01-01T12:00:00Z")!)
        XCTAssertTrue(first.firstSync && first.pushed)
        let again = try await SyncService.run(transport: t, local: first.doc, passphrase: nil, device: me, now: Timestamp(text: "2026-01-01T13:00:00Z")!)
        XCTAssertFalse(again.firstSync || again.pushed || again.changed)
    }
}
