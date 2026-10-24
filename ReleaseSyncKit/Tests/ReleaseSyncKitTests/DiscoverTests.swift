import XCTest
@testable import ReleaseSyncKit

final class Clock: @unchecked Sendable {
    private let lock = NSLock(); private var t: Timestamp
    init(_ t: Timestamp) { self.t = t }
    var now: Timestamp { lock.lock(); defer { lock.unlock() }; return t }
    func advance(_ ticks: Int64) { lock.lock(); t = t + ticks; lock.unlock() }
}

final class DiscoverFixtureTests: XCTestCase {
    func testOrderingAndMovementMatchTheEngine() throws {
        let d = try Fixtures.json("discover/ranking.json")
        var items: [RepoInfo] = []
        for i in d.list("Items") { var r = RepoInfo(); r.fullName = i.str("FullName")!; r.name = String(r.fullName.split(separator: "/")[1]); r.stars = Int(i.whole("Stars")!); items.append(r) }
        var ranks: [String: [Int]] = [:]
        if case .object(let m)? = d.member("Baseline") { for e in m { if case .array(let a) = e.value { ranks[e.key] = a.compactMap { if case .number(let n) = $0 { Int(n) } else { nil } } } } }
        XCTAssertEqual(ranks.count, 7)
        var checked = 0
        for c in d.list("Cases") {
            guard case .number(let hours)? = c.member("AgeHours") else { return XCTFail() }
            let baseline = DiscoverService.baseline(ranks: ranks, age: Double(hours)! * 3600)
            for m in c.list("Modes") {
                let mode = try XCTUnwrap(DiscoverMode(rawValue: m.str("Mode")!))
                let ordered = DiscoverService.order(items, mode: mode, baseline: baseline)
                XCTAssertEqual(ordered.map(\.fullName), m.list("Order").compactMap { if case .string(let s) = $0 { s } else { nil } }, "\(c.str("Name")!) / \(mode)")
                for (n, (repo, note)) in zip(ordered, m.list("Notes")).enumerated() {
                    let item = DiscoverService.annotate(repo, rank: n + 1, baseline: baseline)
                    XCTAssertEqual(item.rank, Int(note.whole("Rank")!)); XCTAssertEqual(item.move.rawValue, note.str("MoveKind") ?? "", repo.fullName)
                    XCTAssertEqual(item.moveText, note.str("MoveText") ?? ""); XCTAssertEqual(item.gainText, note.str("GainText") ?? "", "\(c.str("Name")!) \(repo.fullName)"); XCTAssertEqual(item.gainShort, note.str("GainShort") ?? "")
                    checked += 1
                }
            }
        }
        XCTAssertEqual(checked, 4 * 5 * 7)
        let none = DiscoverService.annotate(items[0], rank: 3, baseline: nil)
        XCTAssertEqual(none.move, .none); XCTAssertEqual(none.moveText, ""); XCTAssertEqual(none.gainText, "")
    }
}

final class DiscoverServiceTests: XCTestCase {
    let start = Timestamp(text: "2026-03-15T12:00:00Z")!

    func repoJSON(_ full: String, stars: Int, desc: String = "A project", archived: Bool = false) -> String {
        #"{"full_name":"\#(full)","name":"\#(full.split(separator: "/")[1])","owner":{"login":"\#(full.split(separator: "/")[0])"},"description":"\#(desc)","stargazers_count":\#(stars),"archived":\#(archived)}"#
    }

    /// A GitHub whose searches return the given projects, counting what was asked.
    func web(_ results: @escaping @Sendable (String) -> [String], delaySearch: Bool = false) -> (FakeWeb, Asked) {
        let w = FakeWeb(), asked = Asked()
        w.answer = { r in
            asked.add(r.url!.absoluteString)
            let comps = URLComponents(url: r.url!, resolvingAgainstBaseURL: false)!
            if r.url!.path == "/search/repositories" {
                let q = comps.queryItems!.first { $0.name == "q" }!.value!
                return reply(200, #"{"total_count":1,"items":[\#(results(q).joined(separator: ","))]}"#, headers: ["x-ratelimit-resource": "search", "x-ratelimit-remaining": "25"])
            }
            if r.url!.path.hasSuffix("/releases") { return reply(200, r.url!.path.contains("macapp") ? #"[{"tag_name":"v1","assets":[{"name":"M.dmg","browser_download_url":"https://e.test/M.dmg"}]}]"# : #"[{"tag_name":"v1","assets":[{"name":"W.exe","browser_download_url":"https://e.test/W.exe"}]}]"#, headers: ["x-ratelimit-remaining": "4000"]) }
            return reply(404)
        }
        return (w, asked)
    }

    func service(_ w: FakeWeb, clock: Clock, blobs: BlobStore? = nil, token: String? = "t") -> DiscoverService {
        DiscoverService(client: SourceClient(token: token, send: w.send, now: { clock.now.date }, sleep: { clock.advance(Int64($0 * 1e7)) }), blobs: blobs, clock: { clock.now })
    }

    func testTheSearchesForEachMode() async throws {
        let (w, asked) = web { _ in [] }
        let clock = Clock(start)
        let svc = service(w, clock: clock)
        let cat = DiscoverCatalog.find("utilities")
        for mode in [DiscoverMode.popular, .new, .updated, .trending] { _ = try? await svc.candidates(mode, cat, force: true) }
        let queries = asked.paths.compactMap { URLComponents(string: $0)?.queryItems?.first { $0.name == "q" }?.value }
        XCTAssertTrue(queries.contains("topic:macos topic:utility archived:false stars:>150"))
        XCTAssertTrue(queries.contains("topic:macos topic:utility archived:false stars:>40 created:>2026-01-14"))
        XCTAssertTrue(queries.contains("topic:macos topic:utility archived:false stars:>200 pushed:>2026-03-08"))
        XCTAssertTrue(queries.contains("topic:macos topic:utility archived:false stars:>40 created:>2025-03-15 pushed:>2026-03-01"), "trending: young and active")
        XCTAssertTrue(queries.contains("topic:macos topic:utility archived:false stars:>1000 pushed:>2026-03-08"), "and the busy established ones")
    }

    func testFeaturedAsksForSixPicksAtATime() async throws {
        let (w, asked) = web { _ in [self.repoJSON("a/b", stars: 5)] }
        let svc = service(w, clock: Clock(start))
        let cat = DiscoverCatalog.find("utilities")
        _ = try await svc.candidates(.featured, cat, force: true)
        let queries = asked.paths.compactMap { URLComponents(string: $0)?.queryItems?.first { $0.name == "q" }?.value }
        XCTAssertEqual(queries.count, 2, "11 picks in chunks of 6")
        XCTAssertEqual(queries[0].split(separator: " ").count + queries[1].split(separator: " ").count, 11)
        XCTAssertTrue(queries.allSatisfy { $0.hasPrefix("repo:") })
    }

    func testFeaturedForEverythingTakesAFewFromEachCategoryOnce() async throws {
        let (w, asked) = web { _ in [self.repoJSON("a/b", stars: 5)] }
        _ = try await service(w, clock: Clock(start)).candidates(.featured, DiscoverCatalog.find("all"), force: true)
        let picks = asked.paths.compactMap { URLComponents(string: $0)?.queryItems?.first { $0.name == "q" }?.value }.flatMap { $0.split(separator: " ").map(String.init) }
        XCTAssertEqual(picks.count, Set(picks).count, "each project once")
        XCTAssertTrue(picks.allSatisfy { $0.hasPrefix("repo:") })
        let expected = Set(DiscoverCatalog.categories.flatMap { $0.seeds.prefix(5) }.map { "repo:" + $0 })
        XCTAssertEqual(Set(picks), expected)
    }

    func testNoiseIsLeftOutAndDuplicatesCollapse() async throws {
        let (w, _) = web { _ in [self.repoJSON("x/awesome-mac", stars: 900), self.repoJSON("x/real-app", stars: 500), self.repoJSON("X/Real-App", stars: 500), self.repoJSON("x/old", stars: 800, archived: true),
                                 self.repoJSON("x/zero", stars: 0), self.repoJSON("x/links", stars: 700, desc: "A curated list of things"), self.repoJSON("x/another", stars: 100, desc: "A list of apps")] }
        let (items, _) = try await service(w, clock: Clock(start)).candidates(.popular, DiscoverCatalog.find("media"), force: true)
        XCTAssertEqual(items.map(\.fullName), ["x/real-app"])
    }

    func testASavedListIsShownAtOnceAndRefreshedWhenStale() async throws {
        let (w, asked) = web { _ in [self.repoJSON("x/app", stars: 500)] }
        let clock = Clock(start), blobs = MemoryBlobStore()
        let cat = DiscoverCatalog.find("media")
        let first = service(w, clock: clock, blobs: blobs)
        _ = try await first.candidates(.popular, cat, force: false)
        let afterFirst = asked.paths.count
        XCTAssertEqual(afterFirst, cat.topics.count, "one search per topic")

        clock.advance(Timestamp.hours(2))
        let second = service(w, clock: clock, blobs: blobs)
        _ = try await second.candidates(.popular, cat, force: false)
        XCTAssertEqual(asked.paths.count, afterFirst, "fresh enough: nothing asked, even after a restart")

        clock.advance(Timestamp.hours(10))
        let (stale, _) = try await second.candidates(.popular, cat, force: false)
        XCTAssertEqual(stale.count, 1, "the old list is shown straight away…")
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertGreaterThan(asked.paths.count, afterFirst, "…and refreshed quietly behind it")

        clock.advance(Timestamp.days(30))
        let before = asked.paths.count
        _ = try await second.candidates(.popular, cat, force: false)
        XCTAssertGreaterThan(asked.paths.count, before, "too old to show: fetched first")
    }

    func testAFailedSearchDoesNotKeepAHalfListAsComplete() async throws {
        let calls = Asked()
        let w = FakeWeb()
        w.answer = { r in
            calls.add("x")
            return calls.paths.count == 2 ? reply(500) : reply(200, #"{"items":[\#(self.repoJSON("x/app", stars: 50))]}"#)
        }
        let svc = service(w, clock: Clock(start))
        let cat = DiscoverCatalog.find("media")
        let (items, _) = try await svc.candidates(.popular, cat, force: true)
        XCTAssertEqual(items.count, 1, "what did arrive is shown")
        let before = calls.paths.count
        _ = try await svc.candidates(.popular, cat, force: false)
        XCTAssertGreaterThan(calls.paths.count, before, "but it isn't remembered as complete")
    }

    func testEverythingFailingIsReportedButNothingFoundIsNot() async throws {
        let bad = FakeWeb(); bad.answer = { _ in reply(500) }
        do { _ = try await service(bad, clock: Clock(start)).candidates(.popular, DiscoverCatalog.find("media"), force: true); XCTFail() } catch let e as SourceError { XCTAssertEqual(e.message, "GitHub returned an error (500).") }
        let (empty, _) = web { _ in [] }
        let (items, _) = try await service(empty, clock: Clock(start)).candidates(.popular, DiscoverCatalog.find("media"), force: true)
        XCTAssertTrue(items.isEmpty)
    }

    func testSearchesArePacedNotRefused() async throws {
        let slept = Asked()
        let clock = Clock(start)
        let (w, _) = web { _ in [] }
        let client = SourceClient(token: "t", send: w.send, now: { clock.now.date }, sleep: { slept.add("\($0)"); clock.advance(Int64($0 * 1e7)) })
        for _ in 0..<20 { _ = try await client.search("q", sort: "stars", perPage: 5) }
        XCTAssertFalse(slept.paths.isEmpty, "after the first 16 it waits")
        let available = await client.searchesAvailable
        XCTAssertLessThan(available, 2)
        let anonymous = SourceClient(send: w.send, now: { clock.now.date }, sleep: { _ in })
        let avail = await anonymous.searchesAvailable
        XCTAssertEqual(avail, 6, "six at once without a token")
    }

    func testMacDownloadCheckIsRememberedAndOnlyLooksAtRecentReleases() async throws {
        let (w, asked) = web { _ in [] }
        let clock = Clock(start), blobs = MemoryBlobStore()
        let svc = service(w, clock: clock, blobs: blobs)
        var mac = RepoInfo(); mac.fullName = "o/macapp"; mac.name = "macapp"; mac.ownerLogin = "o"
        var win = RepoInfo(); win.fullName = "o/winapp"; win.name = "winapp"; win.ownerLogin = "o"
        let isMac = try await svc.hasMacBuild(mac), isWin = try await svc.hasMacBuild(win)
        XCTAssertTrue(isMac); XCTAssertFalse(isWin)
        let n = asked.paths.count
        let again = try await svc.hasMacBuild(mac)
        XCTAssertTrue(again); XCTAssertEqual(asked.paths.count, n, "remembered")
        clock.advance(Timestamp.days(4))
        _ = try await svc.hasMacBuild(mac)
        XCTAssertEqual(asked.paths.count, n + 1, "asked again after three days")
    }

    func testRateLimitedMacChecksStopInsteadOfHidingProjects() async throws {
        let w = FakeWeb(); w.answer = { _ in reply(403, "{}", headers: ["x-ratelimit-remaining": "0"]) }
        var r = RepoInfo(); r.fullName = "o/a"; r.name = "a"; r.ownerLogin = "o"
        do { _ = try await service(w, clock: Clock(start)).hasMacBuild(r); XCTFail() } catch let e as SourceError { XCTAssertTrue(e.rateLimited) }
        let broken = FakeWeb(); broken.answer = { _ in reply(404) }
        let unknown = try await service(broken, clock: Clock(start)).hasMacBuild(r)
        XCTAssertFalse(unknown, "a project with no releases page has no Mac build")
    }

    func testRankingsAreRememberedAtMostOncePerDayAndCompared() async throws {
        let (w, _) = web { _ in [] }
        let clock = Clock(start), blobs = MemoryBlobStore()
        let svc = service(w, clock: clock, blobs: blobs)
        func ranked(_ stars: Int) -> [DiscoverItem] { var r = RepoInfo(); r.fullName = "o/a"; r.stars = stars; var i = DiscoverItem(r); i.rank = 1; return [i] }
        let key = DiscoverService.key(.trending, "all")
        let none = await svc.baseline(key, week: false)
        XCTAssertNil(none)
        await svc.record(key, ranked(100))
        clock.advance(Timestamp.hours(5)); await svc.record(key, ranked(105))
        let tooSoon = await svc.baseline(key, week: false)
        XCTAssertNil(tooSoon, "too little time has passed to compare")
        clock.advance(Timestamp.hours(16))         // 21 hours since the first
        let dayBase = await svc.baseline(key, week: false)
        let day = try XCTUnwrap(dayBase)
        XCTAssertEqual(day.snapshot.ranks["o/a"], [1, 100], "the second look was skipped: less than 20 hours after the first")
        clock.advance(Timestamp.days(2)); await svc.record(key, ranked(130))
        clock.advance(Timestamp.days(5))
        let weekBase = await svc.baseline(key, week: true)
        let week = try XCTUnwrap(weekBase)
        XCTAssertEqual(week.snapshot.ranks["o/a"], [1, 100], "about a week back, or the oldest there is")
        for _ in 0..<20 { clock.advance(Timestamp.days(1)); await svc.record(key, ranked(200)) }
        let saved = await blobs.saves
        XCTAssertGreaterThan(saved, 3)
        let reloaded = service(w, clock: clock, blobs: blobs); await reloaded.load()
        let has = await reloaded.hasHistory(key)
        XCTAssertTrue(has)
    }

    func testTheCatalogIsSane() {
        XCTAssertEqual(DiscoverCatalog.categories.first?.key, "all")
        XCTAssertEqual(Set(DiscoverCatalog.categories.map(\.key)).count, DiscoverCatalog.categories.count)
        for c in DiscoverCatalog.categories {
            XCTAssertFalse(c.topics.isEmpty, c.key)
            XCTAssertTrue(c.topics.allSatisfy { !$0.lowercased().contains("windows") }, "\(c.key): a Mac catalog")
            for seed in c.seeds { XCTAssertEqual(seed.split(separator: "/").count, 2, seed); XCTAssertFalse(seed.contains(" ")) }
        }
        XCTAssertEqual(DiscoverCatalog.find("nonsense").key, "all")
    }
}

/// Remembers what was asked, from any thread.
final class Asked: @unchecked Sendable {
    private let lock = NSLock(); private var list: [String] = []
    func add(_ p: String) { lock.lock(); list.append(p); lock.unlock() }
    var paths: [String] { lock.lock(); defer { lock.unlock() }; return list }
}
