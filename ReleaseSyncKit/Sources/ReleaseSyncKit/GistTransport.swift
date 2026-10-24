import Foundation

/// What came back from a web request.
public struct HTTPReply: Sendable {
    public var status: Int
    /// Header names in lower case.
    public var headers: [String: String]
    public var body: Data
    public init(status: Int, headers: [String: String] = [:], body: Data = Data()) { self.status = status; self.headers = headers; self.body = body }
}

/// Sends a web request. The real one uses URLSession; tests pass a fake.
public typealias HTTPSend = @Sendable (URLRequest) async throws -> HTTPReply

public enum HTTP {
    /// A private session: no cookies, no cache, nothing kept on disk.
    public static let live: HTTPSend = { request in
        let session = URLSession(configuration: .ephemeral)
        defer { session.finishTasksAndInvalidate() }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        var headers: [String: String] = [:]
        for (k, v) in http.allHeaderFields { if let k = k as? String, let v = v as? String { headers[k.lowercased()] = v } }
        return HTTPReply(status: http.statusCode, headers: headers, body: data)
    }
}

/// Keeps the file in a private (secret) GitHub Gist owned by the person. If no gist id is known, looks for one made earlier by
/// ReleaseSync and otherwise creates it on the first write. Needs a token with the "gist" permission.
///
/// The token is sent only to api.github.com. (The large-file download address is fetched without it.)
public actor GistTransport: SyncTransport {
    public static let fileName = "releasesync-library.json"
    public static let gistDescription = "ReleaseSync library (synced between your devices)"

    static let api = URL(string: "https://api.github.com/")!

    public private(set) var gistId: String?
    public nonisolated var description: String { "private GitHub Gist" }

    private let token: String
    private let send: HTTPSend

    public init(token: String, gistId: String? = nil, send: @escaping HTTPSend = HTTP.live) {
        self.token = token
        self.send = send
        let id = gistId.map { trimmed($0) }
        self.gistId = (id?.isEmpty ?? true) ? nil : id
    }

    private func request(_ method: String, _ path: String, body: JSONValue? = nil) -> URLRequest {
        var r = URLRequest(url: URL(string: path, relativeTo: GistTransport.api)!.absoluteURL)
        r.httpMethod = method
        r.timeoutInterval = 30
        r.setValue("ReleaseSync/1", forHTTPHeaderField: "User-Agent")
        r.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if let body {
            r.setValue("application/json", forHTTPHeaderField: "Content-Type")
            r.httpBody = Data(JSONWriter.write(body, indented: false).utf8)
        }
        return r
    }

    private func perform(_ r: URLRequest) async throws -> HTTPReply {
        do { return try await send(r) }
        catch let e as URLError where e.code == .timedOut { throw SyncError.general("GitHub took too long to answer.") }
        catch is CancellationError { throw CancellationError() }
        catch { throw SyncError.general("Couldn't reach GitHub. Check your internet connection.") }
    }

    private func json(_ r: URLRequest) async throws -> JSONValue {
        let reply = try await perform(r)
        guard (200..<300).contains(reply.status) else { throw GistTransport.explain(reply) }
        guard let text = String(data: reply.body, encoding: .utf8), let v = try? JSONParser.parse(text) else { throw SyncError.general("GitHub sent back something unexpected.") }
        return v
    }

    static func explain(_ reply: HTTPReply) -> SyncError {
        switch reply.status {
        case 401: return .general("GitHub didn't accept your token. Add a fresh one in Settings.")
        case 403 where reply.headers["x-ratelimit-remaining"] == "0":
            return .general("GitHub's hourly request limit is used up. Sync will work again when it resets.")
        case 403, 404:
            return .general("GitHub wouldn't open the sync gist. Your token needs the “gist” permission (a classic token with the gist box ticked), or the gist was deleted.")
        default: return .general("GitHub answered \(reply.status).")
        }
    }

    private static func escaped(_ id: String) -> String {
        id.addingPercentEncoding(withAllowedCharacters: CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")) ?? id
    }

    private func find() async throws -> String? {
        for page in 1...5 {
            let v = try await json(request("GET", "gists?per_page=100&page=\(page)"))
            guard case .array(let list) = v, !list.isEmpty else { return nil }
            for g in list {
                if case .object(let files)? = g.member("files"), files.contains(where: { $0.key == GistTransport.fileName }),
                   case .string(let id)? = g.member("id") { return id }
            }
            if list.count < 100 { return nil }
        }
        return nil
    }

    public func read() async throws -> String? {
        if gistId == nil { gistId = try await find() }
        guard let id = gistId else { return nil }
        let doc = try await json(request("GET", "gists/" + GistTransport.escaped(id)))
        guard case .object(let files)? = doc.member("files"), let file = files.first(where: { $0.key == GistTransport.fileName })?.value else { return nil }
        if case .bool(true)? = file.member("truncated") {
            guard case .string(let raw)? = file.member("raw_url"), let url = URL(string: raw), url.scheme == "https" else { throw SyncError.general("GitHub sent back something unexpected.") }
            var r = URLRequest(url: url)       // not through request(): the token goes only to api.github.com
            r.timeoutInterval = 30
            r.setValue("ReleaseSync/1", forHTTPHeaderField: "User-Agent")
            let reply = try await perform(r)
            guard (200..<300).contains(reply.status) else { throw GistTransport.explain(reply) }
            return String(data: reply.body, encoding: .utf8)
        }
        if case .string(let content)? = file.member("content") { return content }
        return nil
    }

    public func write(_ text: String) async throws {
        let files = JSONValue.object([JSONMember(GistTransport.fileName, .object([JSONMember("content", .string(text))]))])
        if let id = gistId {
            _ = try await json(request("PATCH", "gists/" + GistTransport.escaped(id), body: .object([JSONMember("files", files)])))
        } else {
            let created = try await json(request("POST", "gists", body: .object([
                JSONMember("description", .string(GistTransport.gistDescription)), JSONMember("public", .bool(false)), JSONMember("files", files),
            ])))
            guard case .string(let id)? = created.member("id") else { throw SyncError.general("GitHub sent back something unexpected.") }
            gistId = id
        }
    }
}
