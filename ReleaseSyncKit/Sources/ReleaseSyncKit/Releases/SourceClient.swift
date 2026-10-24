import Foundation

/// Something went wrong reading a project from a site. The messages are plain, and the same as the Windows app's.
public struct SourceError: Error, Equatable, LocalizedError, Sendable {
    public var message: String
    public var status: Int
    /// The site's allowance for requests is used up: wait, or add a token.
    public var rateLimited: Bool
    public var errorDescription: String? { message }
    public init(_ message: String, status: Int = 0, rateLimited: Bool = false) { self.message = message; self.status = status; self.rateLimited = rateLimited }
}

/// GitHub's allowance, read from the headers of its answers. `remaining` is -1 until GitHub has said.
public struct RateStatus: Equatable, Sendable {
    public var limit = 0
    public var remaining = -1
    public var reset: Date?
    public init(limit: Int = 0, remaining: Int = -1, reset: Date? = nil) { self.limit = limit; self.remaining = remaining; self.reset = reset }
}

/// Reads projects and their releases from GitHub, GitLab and Forgejo/Gitea sites (Codeberg) through one interface.
///
/// - The GitHub token goes only to api.github.com, never to any other site.
/// - Every request can be conditional (`If-None-Match`): when nothing changed the answer is "304 not modified", which costs nothing against
///   GitHub's hourly allowance. That is how a check of a whole library stays cheap.
public actor SourceClient {
    private var token: String?
    private let send: HTTPSend
    public private(set) var rate = RateStatus()

    public init(token: String? = nil, send: @escaping HTTPSend = HTTP.live) {
        self.token = (token?.isEmpty ?? true) ? nil : token
        self.send = send
    }

    public func setToken(_ token: String?) { self.token = (token?.isEmpty ?? true) ? nil : token }
    public var hasToken: Bool { token != nil }

    // MARK: where a source lives

    enum Kind { case github, gitlab, forgejo }

    static func split(_ source: String) -> (kind: Kind, host: String) {
        switch source {
        case "github": return (.github, "github.com")
        case "gitlab": return (.gitlab, "gitlab.com")
        case "codeberg": return (.forgejo, "codeberg.org")
        default:
            if source.hasPrefix("forgejo:") { return (.forgejo, String(source.dropFirst(8))) }
            if source.hasPrefix("gitlab:") { return (.gitlab, String(source.dropFirst(7))) }
            return (.github, "github.com")
        }
    }

    /// A site's name must look like a real public web address. The library file is data, so a name like `localhost` or `192.168.0.1` isn't followed.
    public static func isUsableHost(_ host: String) -> Bool {
        guard host.range(of: #"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+(:[0-9]{1,5})?$"#, options: .regularExpression) != nil else { return false }
        let name = host.split(separator: ":")[0]
        return name.split(separator: ".").last.map { $0.contains { $0.isLetter } } ?? false      // not an IP address
    }

    private static func escaped(_ s: String) -> String {
        s.addingPercentEncoding(withAllowedCharacters: CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")) ?? s
    }

    // MARK: public reads

    public func repo(source: String, owner: String, repo: String) async throws -> RepoInfo {
        let (kind, host) = Self.split(source)
        switch kind {
        case .github: return try await githubRepo(owner, repo)
        case .gitlab: return try await gitlabRepo(host, owner, repo)
        case .forgejo: return try await forgejoRepo(host, owner, repo, source: source)
        }
    }

    public func releases(source: String, owner: String, repo: String, etag: String?) async throws -> ReleaseResult {
        let (kind, host) = Self.split(source)
        switch kind {
        case .github: return try await githubReleases(owner, repo, etag)
        case .gitlab: return try await gitlabReleases(host, owner, repo, etag)
        case .forgejo: return try await forgejoReleases(host, owner, repo, etag)
        }
    }

    // MARK: requests

    private func get(_ url: String, accept: String, etag: String?, authorized: Bool) async throws -> HTTPReply {
        guard let u = URL(string: url), u.scheme == "https" else { throw SourceError("That address isn't usable.") }
        var r = URLRequest(url: u)
        r.timeoutInterval = 30
        r.setValue("ReleaseSync/1.0", forHTTPHeaderField: "User-Agent")
        r.setValue(accept, forHTTPHeaderField: "Accept")
        if authorized {
            r.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
            if let token { r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        }
        if let etag, !etag.isEmpty { r.setValue(etag, forHTTPHeaderField: "If-None-Match") }
        do { return try await send(r) }
        catch let e as URLError where e.code == .timedOut { throw SourceError(authorized ? "GitHub took too long to respond." : "The server took too long to respond.") }
        catch is CancellationError { throw CancellationError() }
        catch { throw SourceError(authorized ? "Can't reach GitHub. Check your internet connection." : "Can't reach the server. Check your internet connection.") }
    }

    private func json(_ reply: HTTPReply) throws -> JSONValue {
        guard let text = String(data: reply.body, encoding: .utf8), let v = try? JSONParser.parse(text) else { throw SourceError("The site sent back something unexpected.", status: reply.status) }
        return v
    }

    // MARK: GitHub

    private func updateRate(_ r: HTTPReply) {
        // searches have their own, smaller allowance; the number shown is the main one
        if let res = r.headers["x-ratelimit-resource"], res.lowercased() != "core" { return }
        if let l = r.headers["x-ratelimit-limit"].flatMap(Int.init) { rate.limit = l }
        if let rem = r.headers["x-ratelimit-remaining"].flatMap(Int.init) { rate.remaining = rem }
        if let ts = r.headers["x-ratelimit-reset"].flatMap(TimeInterval.init) { rate.reset = Date(timeIntervalSince1970: ts) }
    }

    private func githubFailure(_ r: HTTPReply, _ what: String) -> SourceError {
        switch r.status {
        case 404: return SourceError("\(what) wasn't found. If it's private, add a token with access in Settings.", status: 404)
        case 401: return SourceError("GitHub rejected your access token. Check it in Settings.", status: 401)
        case 403, 429:
            if rate.remaining == 0 || r.headers["retry-after"] != nil {
                let when = rate.reset.map { $0.formatted(date: .omitted, time: .shortened) } ?? "soon"
                let tip = token == nil ? " Add a token in Settings to raise the limit to 5,000 per hour." : ""
                return SourceError("GitHub rate limit reached. It resets at \(when).\(tip)", status: r.status, rateLimited: true)
            }
            return SourceError("GitHub denied the request (403).", status: r.status)
        default: return SourceError("GitHub returned an error (\(r.status)).", status: r.status)
        }
    }

    private func githubGet(_ path: String, what: String, etag: String? = nil) async throws -> HTTPReply {
        let reply = try await get("https://api.github.com/" + path, accept: "application/vnd.github+json", etag: etag, authorized: true)
        updateRate(reply)
        if reply.status == 304 || (200..<300).contains(reply.status) { return reply }
        throw githubFailure(reply, what)
    }

    private func githubRepo(_ owner: String, _ repo: String) async throws -> RepoInfo {
        let reply = try await githubGet("repos/\(Self.escaped(owner))/\(Self.escaped(repo))", what: "\(owner)/\(repo)")
        return RepoInfo(github: try json(reply))
    }

    private func githubReleases(_ owner: String, _ repo: String, _ etag: String?) async throws -> ReleaseResult {
        let reply = try await githubGet("repos/\(Self.escaped(owner))/\(Self.escaped(repo))/releases?per_page=20", what: "\(owner)/\(repo)", etag: etag)
        if reply.status == 304 { return ReleaseResult(notModified: true, etag: etag) }
        guard case .array(let items) = try json(reply) else { throw SourceError("The site sent back something unexpected.", status: reply.status) }
        return ReleaseResult(etag: reply.headers["etag"], releases: ReleaseInfo.sortedNewestFirst(items.map(ReleaseInfo.init(github:)).filter { !$0.draft }))
    }

    // MARK: other sites (no token is ever sent)

    private func otherGet(_ url: String, what: String, etag: String? = nil, forgejo: Bool = false, host: String = "") async throws -> HTTPReply {
        let reply = try await get(url, accept: "application/json", etag: etag, authorized: false)
        switch reply.status {
        case 304, 200..<300: return reply
        case 404: throw SourceError("\(what) wasn't found.", status: 404)
        case 429: throw SourceError("Rate limit reached for that site. Try again in a minute.", status: 429, rateLimited: true)
        default: throw SourceError(forgejo ? "\(host) returned an error (\(reply.status))." : "\(what): the server returned an error (\(reply.status)).", status: reply.status)
        }
    }

    private func usable(_ host: String) throws {
        guard Self.isUsableHost(host) else { throw SourceError("That site's address isn't usable.") }
    }

    private func gitlabRepo(_ host: String, _ owner: String, _ repo: String) async throws -> RepoInfo {
        try usable(host)
        let reply = try await otherGet("https://\(host)/api/v4/projects/\(Self.escaped(owner + "/" + repo))?license=true", what: "\(owner)/\(repo)")
        let e = try json(reply)
        let path = (try? e.string("path_with_namespace")) ?? "\(owner)/\(repo)"
        var info = RepoInfo()
        info.fullName = path
        info.name = (try? e.string("path")) ?? repo
        info.description = try? e.string("description")
        info.stars = (try? e.int("star_count")) ?? 0
        info.forks = (try? e.int("forks_count")) ?? 0
        info.openIssues = (try? e.int("open_issues_count")) ?? 0
        info.htmlUrl = try? e.string("web_url")
        info.archived = (try? e.bool("archived")) ?? false
        info.createdAt = try? e.timestamp("created_at")
        info.pushedAt = try? e.timestamp("last_activity_at")
        info.ownerLogin = path.contains("/") ? String(path[..<path.lastIndex(of: "/")!]) : owner
        info.avatarUrl = ((try? e.string("avatar_url")) ?? nil) ?? ((try? e.present("namespace")?.string("avatar_url")) ?? nil)
        info.topics = ((try? e.stringList("topics")) ?? []).filter { !$0.isEmpty }
        if let lic = e.present("license") { info.licenseName = ((try? lic.string("nickname")) ?? nil) ?? (try? lic.string("name")) ?? nil }
        info.source = host == "gitlab.com" ? "gitlab" : "gitlab:" + host
        return info
    }

    private func gitlabReleases(_ host: String, _ owner: String, _ repo: String, _ etag: String?) async throws -> ReleaseResult {
        try usable(host)
        let reply = try await otherGet("https://\(host)/api/v4/projects/\(Self.escaped(owner + "/" + repo))/releases?per_page=20", what: "\(owner)/\(repo)", etag: etag)
        if reply.status == 304 { return ReleaseResult(notModified: true, etag: etag) }
        guard case .array(let items) = try json(reply) else { throw SourceError("The site sent back something unexpected.", status: reply.status) }
        var list: [ReleaseInfo] = []
        for e in items {
            var rel = ReleaseInfo()
            rel.tag = ((try? e.string("tag_name")) ?? nil) ?? ""
            rel.name = try? e.string("name")
            rel.body = try? e.string("description")
            rel.prerelease = (try? e.bool("upcoming_release")) ?? false
            rel.publishedAt = ((try? e.timestamp("released_at")) ?? nil) ?? (try? e.timestamp("created_at")) ?? nil
            rel.htmlUrl = (try? e.present("_links")?.string("self")) ?? nil
            if let links = e.present("assets")?.present("links"), case .array(let ls) = links {
                for l in ls {
                    var a = AssetInfo()
                    a.name = ((try? l.string("name")) ?? nil) ?? ""
                    a.url = ((try? l.string("direct_asset_url")) ?? nil) ?? ((try? l.string("url")) ?? nil) ?? ""
                    if !a.url.isEmpty && !a.name.isEmpty { rel.assets.append(a) }      // GitLab also lists links to docs and other pages
                }
            }
            if !rel.tag.isEmpty { list.append(rel) }
        }
        return ReleaseResult(etag: reply.headers["etag"], releases: ReleaseInfo.sortedNewestFirst(list))
    }

    private func forgejoRepo(_ host: String, _ owner: String, _ repo: String, source: String) async throws -> RepoInfo {
        try usable(host)
        let reply = try await otherGet("https://\(host)/api/v1/repos/\(Self.escaped(owner))/\(Self.escaped(repo))", what: "\(owner)/\(repo)")
        let e = try json(reply)
        var info = RepoInfo()
        info.fullName = ((try? e.string("full_name")) ?? nil) ?? "\(owner)/\(repo)"
        info.name = ((try? e.string("name")) ?? nil) ?? repo
        info.description = try? e.string("description")
        info.stars = (try? e.int("stars_count")) ?? 0
        info.forks = (try? e.int("forks_count")) ?? 0
        info.openIssues = (try? e.int("open_issues_count")) ?? 0
        info.watchers = (try? e.int("watchers_count")) ?? 0
        info.htmlUrl = try? e.string("html_url")
        info.homepage = try? e.string("website")
        info.archived = (try? e.bool("archived")) ?? false
        info.fork = (try? e.bool("fork")) ?? false
        info.language = try? e.string("language")
        info.createdAt = try? e.timestamp("created_at")
        info.pushedAt = try? e.timestamp("updated_at")
        let o = e.present("owner")
        info.ownerLogin = ((try? o?.string("login")) ?? nil) ?? owner
        info.avatarUrl = ((try? o?.string("avatar_url")) ?? nil) ?? (try? e.string("avatar_url")) ?? nil
        info.source = source
        return info
    }

    private func forgejoReleases(_ host: String, _ owner: String, _ repo: String, _ etag: String?) async throws -> ReleaseResult {
        try usable(host)
        let reply = try await otherGet("https://\(host)/api/v1/repos/\(Self.escaped(owner))/\(Self.escaped(repo))/releases?limit=20", what: "\(owner)/\(repo)", etag: etag, forgejo: true, host: host)
        if reply.status == 304 { return ReleaseResult(notModified: true, etag: etag) }
        guard case .array(let items) = try json(reply) else { throw SourceError("The site sent back something unexpected.", status: reply.status) }
        return ReleaseResult(etag: reply.headers["etag"], releases: ReleaseInfo.sortedNewestFirst(items.map(ReleaseInfo.init(github:)).filter { !$0.draft }))
    }
}
