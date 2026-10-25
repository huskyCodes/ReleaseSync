import Foundation

public enum DiscoverMode: String, CaseIterable, Sendable, Identifiable {
    case trending = "Trending", popular = "Popular", new = "New", updated = "Updated", featured = "Featured"
    public var id: String { rawValue }

    public var blurb: String {
        switch self {
        case .popular: return "The most-starred projects of all time."
        case .new: return "Projects created in the last two months, by stars."
        case .updated: return "Well-liked projects that were updated in the past week."
        case .featured: return "Hand-picked favorites, and the tools people reach for first."
        case .trending: return "Projects gaining stars fastest right now. Movement arrows show how the ranking changed."
        }
    }
}

/// A project in a Discover list with its place and how it moved since an earlier look.
public struct DiscoverItem: Equatable, Sendable, Identifiable {
    public enum Move: String, Sendable { case none = "", up, down, same, new }
    public var repo: RepoInfo
    public var rank = 0
    public var move = Move.none
    public var moveText = ""
    public var gainText = ""
    public var gainShort = ""
    /// Whether the project's recent releases have something for the Mac. Nil until it has been looked up.
    public var hasMacBuild: Bool?
    public var id: String { repo.source + "|" + repo.fullName }
    public init(_ repo: RepoInfo) { self.repo = repo }
}

struct CachedList: Codable, Sendable { var fetched: Timestamp; var items: [RepoInfo] }
struct MacCheck: Codable, Sendable { var at: Timestamp; var mac: Bool }
/// A remembered ranking: where each project stood (rank and stars) at one moment.
struct Snapshot: Codable, Sendable, Equatable { var at: Timestamp; var ranks: [String: [Int]] }
struct DiscoverData: Codable, Sendable {
    var lists: [String: CachedList] = [:]
    var macBuilds: [String: MacCheck] = [:]
    var history: [String: [Snapshot]] = [:]
}

/// Builds Discover lists (trending, popular, new, updated, featured) from GitHub searches, remembers earlier rankings so movement can be shown,
/// and checks which projects ship Mac downloads. The same rules as `DiscoverService` in ReleaseSync.Core, with Mac downloads in place of Windows ones.
///
/// Kept cheap on purpose: a saved list is shown straight away (and quietly refreshed when it is a few hours old), one download per list at a time,
/// searches are paced, nothing is prepared in the background, and the Mac-download check is remembered for days.
public actor DiscoverService {
    public static let listLife = Timestamp.hours(6)
    public static let staleMax = Timestamp.days(14)
    public static let macCheckLife = Timestamp.days(3)
    static let maxSnapshots = 12

    public struct Baseline: Equatable, Sendable {
        let snapshot: Snapshot
        public let age: TimeInterval
        public var days: Double { age / 86_400 }
    }

    private let client: SourceClient
    private let blobs: BlobStore?
    private let clock: @Sendable () -> Timestamp
    private var data = DiscoverData()
    private var loaded = false
    private var refreshing = Set<String>()
    private var inflight: [String: Task<(items: [RepoInfo], fetched: Timestamp), Error>] = [:]

    public init(client: SourceClient, blobs: BlobStore? = nil, clock: @escaping @Sendable () -> Timestamp = { .now() }) {
        self.client = client; self.blobs = blobs; self.clock = clock
    }

    public func load() async {
        guard !loaded else { return }
        loaded = true
        guard let blobs, let bytes = try? await blobs.load(), let d = try? JSONDecoder().decode(DiscoverData.self, from: bytes) else { return }
        data = d
    }

    private func save() async {
        // keep the file small: only the freshest few lists, only recent checks
        let now = clock()
        data.lists = data.lists.filter { now.ticks - $0.value.fetched.ticks <= Timestamp.days(2) }
        for k in data.lists.sorted(by: { $0.value.fetched > $1.value.fetched }).dropFirst(14).map(\.key) { data.lists[k] = nil }
        data.macBuilds = data.macBuilds.filter { now.ticks - $0.value.at.ticks <= Timestamp.days(14) }
        guard let blobs, let bytes = try? JSONEncoder().encode(data) else { return }
        try? await blobs.save(bytes)
    }

    public static func key(_ mode: DiscoverMode, _ category: String) -> String { mode.rawValue + "|" + category }

    // MARK: candidates

    static func day(_ days: Int64, from now: Timestamp) -> String { String((now - Timestamp.days(days)).text.prefix(10)) }

    static func query(for mode: DiscoverMode, topic: String, now: Timestamp) -> String {
        switch mode {
        case .popular: return "\(topic) archived:false stars:>150"
        case .new: return "\(topic) archived:false stars:>40 created:>\(day(60, from: now))"
        case .updated: return "\(topic) archived:false stars:>200 pushed:>\(day(7, from: now))"
        default: return "\(topic) archived:false stars:>40 created:>\(day(365, from: now)) pushed:>\(day(14, from: now))"      // trending: young and active
        }
    }

    /// Lists of "awesome" links and the like are not projects to install.
    static func isNoise(_ r: RepoInfo) -> Bool {
        if r.stars < 1 || r.archived { return true }
        let n = r.name.lowercased()
        if n.hasPrefix("awesome") || n.hasSuffix("-awesome") || n.contains("awesome-list") { return true }
        let d = (r.description ?? "").lowercased()
        return d.contains("curated list") || d.contains("awesome list") || d.hasPrefix("a list of")
    }

    /// The raw (unranked) projects for a mode and category. A saved copy is shown straight away even when it is a few hours old, and quietly
    /// refreshed behind the scenes, so switching tabs is instant.
    public func candidates(_ mode: DiscoverMode, _ category: DiscoverCategory, force: Bool) async throws -> (items: [RepoInfo], fetched: Timestamp) {
        await load()
        let key = Self.key(mode, category.key)
        let now = clock()
        if !force, let hit = data.lists[key], !hit.items.isEmpty {
            let age = now.ticks - hit.fetched.ticks
            if age < Self.listLife { return (hit.items, hit.fetched) }
            if age < Self.staleMax {
                if refreshing.insert(key).inserted {
                    Task { [weak self] in _ = try? await self?.fetchShared(mode, category); await self?.doneRefreshing(key) }
                }
                return (hit.items, hit.fetched)
            }
        }
        return try await fetchShared(mode, category)
    }

    private func doneRefreshing(_ key: String) { refreshing.remove(key) }

    /// One download per list at a time: if the tab is already being prepared, the person simply waits for that same download.
    private func fetchShared(_ mode: DiscoverMode, _ category: DiscoverCategory) async throws -> (items: [RepoInfo], fetched: Timestamp) {
        let key = Self.key(mode, category.key)
        if let running = inflight[key] { return try await running.value }
        let task = Task { try await self.fetch(mode, category) }
        inflight[key] = task
        defer { inflight[key] = nil }
        return try await task.value
    }

    private func fetch(_ mode: DiscoverMode, _ category: DiscoverCategory) async throws -> (items: [RepoInfo], fetched: Timestamp) {
        let key = Self.key(mode, category.key)
        let now = clock()
        var jobs: [(query: String, perPage: Int)] = []
        if mode == .featured {
            var seeds = category.seeds
            if category.key == "all" {          // a few from every category, each project once
                var seen = Set<String>(); seeds = []
                for c in DiscoverCatalog.categories { for s in c.seeds.prefix(5) where seen.insert(s).inserted { seeds.append(s) } }
            }
            for chunk in stride(from: 0, to: seeds.count, by: 6).map({ Array(seeds[$0..<min($0 + 6, seeds.count)]) }) {
                jobs.append((chunk.map { "repo:" + $0 }.joined(separator: " "), 30))
            }
        } else {
            for t in category.topics { jobs.append((Self.query(for: mode, topic: t, now: now), category.key == "all" ? 30 : 40)) }
            // trending also needs the established projects that are busy right now, not just young ones
            if mode == .trending { for t in category.topics.prefix(2) { jobs.append(("\(t) archived:false stars:>1000 pushed:>\(Self.day(7, from: now))", 20)) } }
        }

        // all the searches at once (the client paces them): the tab loads in about the time of the slowest one
        var found: [String: RepoInfo] = [:], order: [String] = []
        var firstError: Error?, ok = 0
        await withTaskGroup(of: Result<[RepoInfo], Error>.self) { group in
            for j in jobs { group.addTask { [client] in do { return .success(try await client.search(j.query, sort: "stars", perPage: j.perPage)) } catch { return .failure(error) } } }
            for await r in group {
                switch r {
                case .success(let list): ok += 1; for repo in list where !Self.isNoise(repo) && found[repo.fullName.lowercased()] == nil { found[repo.fullName.lowercased()] = repo; order.append(repo.fullName.lowercased()) }
                case .failure(let e): if firstError == nil { firstError = e }
                }
            }
        }
        try Task.checkCancellation()
        if found.isEmpty {
            if let firstError { throw firstError }
            return ([], now)
        }
        let items = order.compactMap { found[$0] }
        if ok > 0 && firstError == nil { data.lists[key] = CachedList(fetched: now, items: items) }       // a list with a failed search is shown, but not kept as if it were complete
        await save()
        return (items, now)
    }

    // MARK: ranking and movement

    /// The earlier ranking to compare with: about a day old for "day", about a week old for "week".
    public func baseline(_ key: String, week: Bool) -> Baseline? {
        guard let list = data.history[key], !list.isEmpty else { return nil }
        let now = clock()
        let minAge = week ? Timestamp.days(6) : Timestamp.hours(18)
        let old = list.filter { now.ticks - $0.at.ticks >= Timestamp.hours(18) }.sorted { $0.at > $1.at }
        guard !old.isEmpty else { return nil }
        let pick = old.first { now.ticks - $0.at.ticks >= minAge } ?? old.last!      // too little history for a full week: use the oldest we have
        return Baseline(snapshot: pick, age: Double(now.ticks - pick.at.ticks) / Double(Timestamp.ticksPerSecond))
    }

    /// Builds a baseline from known ranks (for tests and for comparing without saved history).
    public static func baseline(ranks: [String: [Int]], age: TimeInterval) -> Baseline { Baseline(snapshot: Snapshot(at: .zero, ranks: ranks), age: age) }

    /// Puts candidates in order for a mode. Trending uses real star gains when an earlier ranking exists, and an estimate from the project's age when it doesn't.
    public static func order(_ items: [RepoInfo], mode: DiscoverMode, baseline: Baseline?, now: Timestamp = .now()) -> [RepoInfo] {
        func perDay(_ r: RepoInfo) -> Double {
            if let b = baseline, let prev = b.snapshot.ranks[r.fullName], prev.count > 1 { return Double(max(0, r.stars - prev[1])) / max(1.0, b.days) }
            let age = r.createdAt.map { max(7.0, Double(now.ticks - $0.ticks) / Double(Timestamp.ticksPerSecond) / 86_400) } ?? 365.0
            return Double(r.stars) / age
        }
        func stable(_ list: [RepoInfo], by score: (RepoInfo) -> Double) -> [RepoInfo] {
            list.enumerated().sorted { a, b in
                let (x, y) = (score(a.element), score(b.element))
                if x != y { return x > y }
                if a.element.stars != b.element.stars { return a.element.stars > b.element.stars }      // trending: ties go to the more popular
                return a.offset < b.offset
            }.map(\.element)
        }
        switch mode {
        case .trending: return stable(items) { perDay($0) * log10(Double($0.stars) + 10) }       // gains, nudged toward projects more people already trust
        default: return stable(items) { Double($0.stars) }
        }
    }

    /// Fills in the rank badge, up/down movement and star gain for one project.
    public static func annotate(_ r: RepoInfo, rank: Int, baseline: Baseline?) -> DiscoverItem {
        var item = DiscoverItem(r)
        item.rank = rank
        guard let b = baseline else { return item }
        let since = b.age / 3600 < 30 ? "since yesterday" : b.days >= 6 ? "this week" : "in \(Int(b.days.rounded(.toNearestOrAwayFromZero))) days"
        if let prev = b.snapshot.ranks[r.fullName], !prev.isEmpty {
            let delta = prev[0] - rank
            if delta > 0 { item.move = .up; item.moveText = "▲ \(delta)" }
            else if delta < 0 { item.move = .down; item.moveText = "▼ \(-delta)" }
            else { item.move = .same; item.moveText = "—" }
            if prev.count > 1, r.stars > prev[1] { item.gainText = "+\(Fmt.count(r.stars - prev[1])) stars \(since)"; item.gainShort = "+\(Fmt.count(r.stars - prev[1])) ★" }
        } else { item.move = .new; item.moveText = "NEW" }
        return item
    }

    /// Remembers this ranking (at most about once a day) so the next visit can show movement.
    public func record(_ key: String, _ ranked: [DiscoverItem]) async {
        let now = clock()
        var list = data.history[key] ?? []
        if let newest = list.map(\.at).max(), now.ticks - newest.ticks < Timestamp.hours(20) { return }
        var ranks: [String: [Int]] = [:]
        for i in ranked.prefix(60) { ranks[i.repo.fullName] = [i.rank, i.repo.stars] }
        guard !ranks.isEmpty else { return }
        list.append(Snapshot(at: now, ranks: ranks))
        while list.count > Self.maxSnapshots { list.removeFirst() }
        data.history[key] = list
        await save()
    }

    public func hasHistory(_ key: String) -> Bool { !(data.history[key] ?? []).isEmpty }

    // MARK: Mac downloads

    /// Whether the project's recent releases include something that installs on a Mac. Remembered for a few days.
    /// Throws when GitHub's allowance is used up or the site can't be reached, so the caller can keep the project rather than hide it.
    public func hasMacBuild(_ r: RepoInfo) async throws -> Bool {
        let key = r.source + "|" + r.fullName
        let now = clock()
        if let c = data.macBuilds[key], now.ticks - c.at.ticks < Self.macCheckLife { return c.mac }
        let mac: Bool
        do {
            let res = try await client.releases(source: r.source, owner: r.ownerLogin, repo: r.name, etag: nil)
            mac = res.releases.prefix(4).contains { MacAssets.looksLikeMac($0.assets) }
        } catch let e as SourceError where e.rateLimited { throw e }
        catch is CancellationError { throw CancellationError() }
        catch { mac = false }
        data.macBuilds[key] = MacCheck(at: now, mac: mac)
        return mac
    }

    public func saveMacChecks() async { await save() }

    /// Whether there is room for background work: a whole tab's searches could go out without anyone waiting, so a person's own searches go first.
    public func canPrefetch() async -> Bool { await client.searchesAvailable >= 11 }

    /// Looks up the Mac downloads of these projects ahead of time (a few at a time), so the tab is ready when it is opened.
    /// A project that can't be checked (allowance used up, site unreachable) is skipped quietly.
    public func preverify(_ repos: [RepoInfo]) async {
        for start in stride(from: 0, to: repos.count, by: 8) {
            if Task.isCancelled { break }
            let chunk = Array(repos[start..<min(start + 8, repos.count)])
            await withTaskGroup(of: Void.self) { group in
                for r in chunk { group.addTask { [self] in _ = try? await hasMacBuild(r) } }
            }
        }
        await save()
    }
}
