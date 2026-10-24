import Foundation

/// Somewhere a small amount of data can be kept between runs. The Mac app keeps it encrypted; tests keep it in memory.
public protocol BlobStore: Sendable {
    func load() async throws -> Data?
    func save(_ data: Data) async throws
}

public actor MemoryBlobStore: BlobStore {
    public private(set) var data: Data?
    public private(set) var saves = 0
    public init(data: Data? = nil) { self.data = data }
    public func load() async throws -> Data? { data }
    public func save(_ d: Data) async throws { data = d; saves += 1 }
}

/// One release as remembered, kept so older versions can be browsed without another request.
public struct ReleaseSummary: Codable, Equatable, Sendable {
    public var tag = ""
    public var name: String?
    public var notes: String?
    public var published: Timestamp?
    public var prerelease = false
    public var url: String?
    public var assets: [AssetInfo] = []
    public init() {}
}

/// What a project's page said last time.
public struct RepoMeta: Codable, Equatable, Sendable {
    public var description: String?
    public var language: String?
    public var stars = 0
    public var topics: [String] = []
    public var avatarUrl: String?
    public var fetched = Timestamp.zero
    public init() {}
}

/// Everything remembered about one tracked app's releases.
public struct AppRelease: Codable, Equatable, Sendable {
    public var etag: String?
    public var latestTag: String?
    public var latestName: String?
    public var latestNotes: String?
    public var latestPublished: Timestamp?
    public var latestPrerelease = false
    public var releaseUrl: String?
    /// The downloads of the latest release.
    public var assets: [AssetInfo] = []
    /// The most recent releases, newest first.
    public var releases: [ReleaseSummary] = []
    public var lastChecked: Timestamp?
    public var lastError: String?
    public var meta: RepoMeta?
    public init() {}

    public var hasRelease: Bool { !(latestTag ?? "").isEmpty }
}

/// An app to check.
public struct CheckTarget: Equatable, Sendable {
    public var key: String
    public var source: String
    public var owner: String
    public var repo: String
    public var includePrereleases: Bool
    public init(key: String, source: String, owner: String, repo: String, includePrereleases: Bool = false) {
        self.key = key; self.source = source; self.owner = owner; self.repo = repo; self.includePrereleases = includePrereleases
    }
}

public struct CheckSummary: Equatable, Sendable {
    public var checked = 0
    public var unchanged = 0
    public var failed = 0
    /// The checking stopped early to protect GitHub's allowance.
    public var stoppedEarly = false
    public var firstError: String?
    public var skipped = 0
}

/// Remembers each tracked app's releases and checks them, politely: conditional requests (an unchanged project costs nothing against GitHub's
/// allowance), a few at a time, and a stop before the allowance runs out.
public actor ReleaseStore {
    /// Details about a project (description, stars, avatar) are refreshed this often.
    public static let metaLife = Timestamp.days(7)
    /// A check that is not asked for by hand skips an app that was looked at more recently than this.
    public static let automaticInterval = Timestamp.hours(1) / 4
    /// GitHub requests stop when fewer than this many are left in the hour.
    public static let reserve = 40

    private var entries: [String: AppRelease] = [:]
    private let client: SourceClient
    private let blobs: BlobStore?
    private var loaded = false
    private var running = false

    public init(client: SourceClient, blobs: BlobStore? = nil) { self.client = client; self.blobs = blobs }

    // MARK: remembered data

    public func load() async {
        guard !loaded else { return }
        loaded = true
        guard let blobs, let data = try? await blobs.load(), let decoded = try? JSONDecoder().decode([String: AppRelease].self, from: data) else { return }
        entries = decoded
    }

    public func all() -> [String: AppRelease] { entries }
    public func entry(_ key: String) -> AppRelease? { entries[key] }

    private func save() async {
        guard let blobs, let data = try? JSONEncoder().encode(entries) else { return }
        try? await blobs.save(data)
    }

    public func forget() async { entries = [:]; await save() }

    // MARK: checking

    static func select(_ releases: [ReleaseInfo], includePrereleases: Bool) -> ReleaseInfo? {
        releases.first { !$0.draft && (includePrereleases || !$0.prerelease) }
    }

    static func apply(_ releases: [ReleaseInfo], to entry: inout AppRelease, includePrereleases: Bool) {
        guard let rel = select(releases, includePrereleases: includePrereleases) else {
            entry.lastError = releases.isEmpty ? "No releases have been published for this repository." : "No stable releases yet. Turn on pre-releases to track the newest build."
            return
        }
        entry.latestTag = rel.tag; entry.latestName = rel.name; entry.latestNotes = rel.body.map { String($0.prefix(3000)) }
        entry.latestPublished = rel.publishedAt; entry.latestPrerelease = rel.prerelease; entry.releaseUrl = rel.htmlUrl
        entry.assets = rel.assets.filter { $0.state != "uploading" }
        entry.releases = releases.filter { !$0.draft && !$0.tag.isEmpty }.prefix(15).map { r in
            var s = ReleaseSummary()
            s.tag = r.tag; s.name = r.name; s.notes = r.body.map { String($0.prefix(3000)) }; s.published = r.publishedAt; s.prerelease = r.prerelease; s.url = r.htmlUrl
            s.assets = r.assets.filter { $0.state != "uploading" }
            return s
        }
        entry.lastError = nil
    }

    /// Checks the apps for new releases. `manual` ignores how recently an app was looked at and its saved ETag.
    /// `progress` is told how many have finished. Safe to call while another check runs: the second call does nothing.
    public func check(_ targets: [CheckTarget], manual: Bool, concurrency: Int = 4, now: Timestamp = .now(), progress: (@Sendable (Int, Int) -> Void)? = nil) async -> CheckSummary {
        await load()
        var summary = CheckSummary()
        guard !running else { return summary }
        running = true
        defer { running = false }

        // forget apps that are no longer in the library
        let wanted = Set(targets.map(\.key))
        for k in entries.keys where !wanted.contains(k) { entries[k] = nil }

        let queue = targets.filter { t in
            guard !manual, let last = entries[t.key]?.lastChecked, entries[t.key]?.lastError == nil else { return true }
            return now.ticks - last.ticks >= Self.automaticInterval
        }
        summary.skipped = targets.count - queue.count
        var next = 0, done = 0, stop = false
        let width = max(1, min(concurrency, 8))

        await withTaskGroup(of: (String, Result<AppRelease, Error>, Bool).self) { group in
            func launch() async -> Bool {
                guard next < queue.count, !stop else { return false }
                // a polite stop before GitHub's hourly allowance is gone
                let rate = await client.rate
                if rate.remaining >= 0 && rate.remaining < Self.reserve && queue[next].source == "github" {
                    stop = true; summary.stoppedEarly = true
                    summary.firstError = "GitHub's allowance for requests is nearly used up, so checking paused. It resets at \(rate.reset.map { $0.formatted(date: .omitted, time: .shortened) } ?? "the top of the hour")."
                    return false
                }
                let target = queue[next]; next += 1
                let current = entries[target.key] ?? AppRelease()
                group.addTask { [client] in
                    var entry = current
                    do {
                        // details about the project are fetched once, then now and then
                        if entry.meta == nil || now.ticks - entry.meta!.fetched.ticks > Self.metaLife {
                            if let info = try? await client.repo(source: target.source, owner: target.owner, repo: target.repo) {
                                var m = RepoMeta()
                                m.description = info.description; m.language = info.language; m.stars = info.stars; m.topics = info.topics; m.fetched = now
                                m.avatarUrl = info.avatarUrl
                                entry.meta = m
                            } else if entry.meta == nil { var m = RepoMeta(); m.fetched = now; entry.meta = m }       // don't ask again at once
                        }
                        let etag = manual || !entry.hasRelease ? nil : entry.etag
                        let result = try await client.releases(source: target.source, owner: target.owner, repo: target.repo, etag: etag)
                        if result.notModified { entry.lastError = nil }
                        else { entry.etag = result.etag; Self.apply(result.releases, to: &entry, includePrereleases: target.includePrereleases) }
                        entry.lastChecked = now
                        return (target.key, .success(entry), result.notModified)
                    } catch {
                        entry.lastError = error.localizedDescription
                        entry.lastChecked = now
                        return (target.key, .failure(error), false)
                    }
                }
                return true
            }

            for _ in 0..<width { _ = await launch() }
            while let (key, result, notModified) = await group.next() {
                done += 1
                switch result {
                case .success(let entry): entries[key] = entry; summary.checked += 1; if notModified { summary.unchanged += 1 }
                case .failure(let error):
                    var entry = entries[key] ?? AppRelease(); entry.lastError = error.localizedDescription; entry.lastChecked = now; entries[key] = entry
                    summary.failed += 1
                    if summary.firstError == nil { summary.firstError = error.localizedDescription }
                    if (error as? SourceError)?.rateLimited == true { stop = true; summary.stoppedEarly = true }
                }
                progress?(done, queue.count)
                _ = await launch()
            }
        }
        await save()
        return summary
    }
}
