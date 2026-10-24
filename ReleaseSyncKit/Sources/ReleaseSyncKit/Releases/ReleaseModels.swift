import Foundation

/// A download attached to a release.
public struct AssetInfo: Equatable, Sendable, Codable {
    public var name = ""
    public var url = ""
    public var size: Int64 = 0
    public var downloads = 0
    /// GitHub says "uploading" while a file isn't ready yet.
    public var state: String?
    /// For example `sha256:0123…`, when the site provides one. Used to check a download later.
    public var digest: String?

    public init() {}
    public init(name: String, url: String, size: Int64 = 0, downloads: Int = 0, state: String? = nil, digest: String? = nil) {
        self.name = name; self.url = url; self.size = size; self.downloads = downloads; self.state = state; self.digest = digest
    }
}

/// One published release of a project.
public struct ReleaseInfo: Equatable, Sendable, Codable {
    public var tag = ""
    public var name: String?
    public var body: String?
    public var prerelease = false
    public var draft = false
    public var publishedAt: Timestamp?
    public var htmlUrl: String?
    public var assets: [AssetInfo] = []

    public init() {}
}

extension Timestamp: Codable {
    public init(from decoder: Decoder) throws { self.init(ticks: try decoder.singleValueContainer().decode(Int64.self)) }
    public func encode(to encoder: Encoder) throws { var c = encoder.singleValueContainer(); try c.encode(ticks) }
}

/// What a project's page says about it.
public struct RepoInfo: Equatable, Sendable, Codable {
    public var fullName = ""
    public var name = ""
    public var ownerLogin = ""
    public var avatarUrl: String?
    public var description: String?
    public var stars = 0
    public var language: String?
    public var topics: [String] = []
    public var htmlUrl: String?
    public var archived = false
    public var fork = false
    public var forks = 0
    public var openIssues = 0
    public var watchers = 0
    public var licenseSpdx: String?
    public var licenseName: String?
    public var homepage: String?
    public var createdAt: Timestamp?
    public var pushedAt: Timestamp?
    /// Where it lives: `github`, or for example `forgejo:git.example.test`.
    public var source = "github"

    public init() {}
}

/// The releases of a project, as one request returned them.
public struct ReleaseResult: Equatable, Sendable {
    /// The site said nothing changed since the saved copy (the request cost nothing against GitHub's allowance).
    public var notModified = false
    public var etag: String?
    public var releases: [ReleaseInfo] = []
    public init(notModified: Bool = false, etag: String? = nil, releases: [ReleaseInfo] = []) { self.notModified = notModified; self.etag = etag; self.releases = releases }
}

// MARK: reading the sites' JSON

extension AssetInfo {
    init(github v: JSONValue) {
        name = (try? v.string("name")) ?? ""
        size = Int64((try? v.int("size")) ?? 0)
        url = (try? v.string("browser_download_url")) ?? ""
        downloads = (try? v.int("download_count")) ?? 0
        state = (try? v.string("state"))
        digest = (try? v.string("digest"))
    }
}

extension ReleaseInfo {
    /// The shape GitHub and Forgejo/Gitea sites (such as Codeberg) both use.
    init(github v: JSONValue) {
        tag = (try? v.string("tag_name")) ?? ""
        name = try? v.string("name")
        body = try? v.string("body")
        prerelease = (try? v.bool("prerelease")) ?? false
        draft = (try? v.bool("draft")) ?? false
        publishedAt = try? v.timestamp("published_at")
        htmlUrl = try? v.string("html_url")
        assets = ((try? v.array("assets")) ?? []).map(AssetInfo.init(github:))
    }

    /// Releases from a list, without drafts, newest first (releases with no date last).
    static func list(github text: String) throws -> [ReleaseInfo] {
        guard case .array(let items) = try JSONParser.parse(text) else { throw JSONError("A list of releases was expected.") }
        return sortedNewestFirst(items.map(ReleaseInfo.init(github:)).filter { !$0.draft })
    }

    static func sortedNewestFirst(_ list: [ReleaseInfo]) -> [ReleaseInfo] {
        // a stable sort, as in the Windows app: releases with the same date keep the order the site gave them
        list.enumerated().sorted {
            let (a, b) = ($0.element.publishedAt?.ticks ?? 0, $1.element.publishedAt?.ticks ?? 0)
            return a != b ? a > b : $0.offset < $1.offset
        }.map(\.element)
    }
}

extension RepoInfo {
    init(github v: JSONValue) {
        fullName = (try? v.string("full_name")) ?? ""
        name = (try? v.string("name")) ?? ""
        if let owner = v.present("owner") {
            ownerLogin = (try? owner.string("login")) ?? ""
            avatarUrl = try? owner.string("avatar_url")
        }
        description = try? v.string("description")
        stars = (try? v.int("stargazers_count")) ?? 0
        language = try? v.string("language")
        topics = (try? v.stringList("topics")) ?? []
        htmlUrl = try? v.string("html_url")
        archived = (try? v.bool("archived")) ?? false
        fork = (try? v.bool("fork")) ?? false
        forks = (try? v.int("forks_count")) ?? 0
        openIssues = (try? v.int("open_issues_count")) ?? 0
        watchers = (try? v.int("subscribers_count")) ?? 0
        if let license = v.present("license") { licenseSpdx = try? license.string("spdx_id"); licenseName = try? license.string("name") }
        homepage = try? v.string("homepage")
        createdAt = try? v.timestamp("created_at")
        pushedAt = try? v.timestamp("pushed_at")
        if ownerLogin.isEmpty { ownerLogin = fullName.split(separator: "/").first.map(String.init) ?? "" }
    }
}
