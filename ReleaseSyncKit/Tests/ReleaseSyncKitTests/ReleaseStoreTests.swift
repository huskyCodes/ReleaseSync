import XCTest
@testable import ReleaseSyncKit

/// Counts requests and how many were in flight at once.
final class Traffic: @unchecked Sendable {
    private let lock = NSLock()
    private var inFlight = 0
    private(set) var peak = 0
    private(set) var urls: [String] = []
    private(set) var etagsSent: [String] = []
    func begin(_ r: URLRequest) { lock.lock(); inFlight += 1; peak = max(peak, inFlight); urls.append(r.url!.absoluteString); if let e = r.value(forHTTPHeaderField: "If-None-Match") { etagsSent.append(e) }; lock.unlock() }
    func end() { lock.lock(); inFlight -= 1; lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return urls.count }
}

final class ReleaseStoreTests: XCTestCase {
    let releasesJSON = """
    [{"tag_name":"v2.0.0","name":"Two","body":"Notes","prerelease":false,"draft":false,"published_at":"2026-01-02T12:00:00Z","html_url":"https://example.test/r/2",
      "assets":[{"name":"App-2.0.0-universal.dmg","size":10,"browser_download_url":"https://example.test/a.dmg","download_count":1}]},
     {"tag_name":"v2.1.0-beta.1","name":null,"body":null,"prerelease":true,"draft":false,"published_at":"2026-01-03T12:00:00Z","html_url":null,"assets":[]},
     {"tag_name":"v1.0.0","name":"One","body":"Old","prerelease":false,"draft":false,"published_at":"2025-12-01T12:00:00Z","html_url":null,"assets":[]}]
    """
    let repoJSON = #"{"full_name":"o/r","name":"r","owner":{"login":"o","avatar_url":"https://avatars.example.test/o"},"description":"Does things","stargazers_count":42,"language":"Swift","topics":["a"]}"#

    func target(_ n: Int, pre: Bool = false) -> CheckTarget { CheckTarget(key: "github:o/r\(n)", source: "github", owner: "o", repo: "r\(n)", includePrereleases: pre) }

    /// A GitHub that answers 304 when the ETag is sent, and notes how many are asked at once.
    func web(traffic: Traffic, remaining: Int = 4000, delay: Bool = false) -> FakeWeb {
        let w = FakeWeb()
        w.answer = { r in
            traffic.begin(r); defer { traffic.end() }
            if delay { Thread.sleep(forTimeInterval: 0.02) }
            let headers = ["x-ratelimit-remaining": String(remaining), "x-ratelimit-limit": "5000"]
            if r.url!.path.hasSuffix("/releases") {
                if r.value(forHTTPHeaderField: "If-None-Match") == "\"e1\"" { return reply(304, "", headers: headers) }
                return reply(200, self.releasesJSON, headers: headers.merging(["etag": "\"e1\""]) { $1 })
            }
            return reply(200, self.repoJSON, headers: headers)
        }
        return w
    }

    func testFirstCheckFillsInEverythingAndTheSecondCostsNothing() async throws {
        let traffic = Traffic()
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic).send))
        let targets = (1...3).map { target($0) }
        let first = await store.check(targets, manual: true)
        XCTAssertEqual(first.checked, 3); XCTAssertEqual(first.unchanged, 0); XCTAssertEqual(traffic.count, 6, "one request for the project's details and one for the releases")
        let entry = await store.entry("github:o/r1")
        let e = try XCTUnwrap(entry)
        XCTAssertEqual(e.latestTag, "v2.0.0", "pre-releases are skipped unless asked for")
        XCTAssertEqual(e.latestNotes, "Notes"); XCTAssertEqual(e.assets.map(\.name), ["App-2.0.0-universal.dmg"]); XCTAssertEqual(e.releases.map(\.tag), ["v2.1.0-beta.1", "v2.0.0", "v1.0.0"])
        XCTAssertEqual(e.meta?.description, "Does things"); XCTAssertEqual(e.meta?.stars, 42); XCTAssertEqual(e.etag, "\"e1\"")
        XCTAssertNotNil(e.lastChecked); XCTAssertNil(e.lastError)

        let second = await store.check(targets, manual: false, now: .now() + Timestamp.hours(2))
        XCTAssertEqual(second.unchanged, 3, "304: nothing changed")
        XCTAssertEqual(traffic.count, 9, "only the three conditional release requests; details are kept for a week")
        XCTAssertEqual(traffic.etagsSent.count, 3)
        let kept = await store.entry("github:o/r1")
        XCTAssertEqual(kept?.latestTag, "v2.0.0"); XCTAssertEqual(kept?.releases.count, 3)
    }

    func testPreReleasesAreTrackedWhenAskedFor() async throws {
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: Traffic()).send))
        _ = await store.check([target(1, pre: true)], manual: true)
        let tag = await store.entry("github:o/r1")?.latestTag
        XCTAssertEqual(tag, "v2.1.0-beta.1")
    }

    func testAnAutomaticCheckLeavesRecentlyLookedAtAppsAlone() async throws {
        let traffic = Traffic()
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic).send))
        let now = Timestamp.now()
        _ = await store.check([target(1)], manual: true, now: now)
        let before = traffic.count
        let soon = await store.check([target(1)], manual: false, now: now + Timestamp.hours(1) / 12)
        XCTAssertEqual(soon.skipped, 1); XCTAssertEqual(traffic.count, before)
        let manual = await store.check([target(1)], manual: true, now: now + Timestamp.hours(1) / 12)
        XCTAssertEqual(manual.checked, 1, "asking by hand always looks, without the saved ETag")
        XCTAssertEqual(traffic.etagsSent.count, 0)
    }

    func testOnlyAFewAreAskedAtOnce() async throws {
        let traffic = Traffic()
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic, delay: true).send))
        let summary = await store.check((1...24).map { target($0) }, manual: true, concurrency: 3)
        XCTAssertEqual(summary.checked, 24)
        XCTAssertLessThanOrEqual(traffic.peak, 3)
        XCTAssertGreaterThan(traffic.peak, 1, "but not one at a time")
    }

    func testCheckingPausesBeforeTheAllowanceRunsOut() async throws {
        let traffic = Traffic()
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic, remaining: 10).send))
        let summary = await store.check((1...20).map { target($0) }, manual: true, concurrency: 2)
        XCTAssertTrue(summary.stoppedEarly)
        XCTAssertLessThan(traffic.count, 20 * 2, "it didn't go on asking")
        XCTAssertTrue(summary.firstError?.contains("nearly used up") ?? false)
        let rate = await store.all().count
        XCTAssertLessThan(rate, 20)
    }

    func testOtherSitesAreNotHeldBackByGitHubsAllowance() async throws {
        let traffic = Traffic()
        let w = FakeWeb()
        w.answer = { r in traffic.begin(r); defer { traffic.end() }; return reply(200, r.url!.host == "api.github.com" ? self.repoJSON : "[]", headers: ["x-ratelimit-remaining": "1", "x-ratelimit-limit": "5000"]) }
        let store = ReleaseStore(client: SourceClient(token: "t", send: w.send))
        _ = await store.check([target(1)], manual: true)          // GitHub says 1 left
        let summary = await store.check([CheckTarget(key: "codeberg:o/r", source: "codeberg", owner: "o", repo: "r")], manual: true)
        XCTAssertFalse(summary.stoppedEarly)
    }

    func testAProblemWithOneAppDoesntStopTheOthers() async throws {
        let w = FakeWeb()
        w.answer = { r in
            if r.url!.path.contains("/o/bad/") { return reply(404) }
            return reply(200, r.url!.path.hasSuffix("/releases") ? self.releasesJSON : self.repoJSON, headers: ["x-ratelimit-remaining": "4000"])
        }
        let store = ReleaseStore(client: SourceClient(token: "t", send: w.send))
        let summary = await store.check([target(1), CheckTarget(key: "github:o/bad", source: "github", owner: "o", repo: "bad"), target(2)], manual: true)
        XCTAssertEqual(summary.checked, 2); XCTAssertEqual(summary.failed, 1); XCTAssertEqual(summary.firstError, "o/bad wasn't found. If it's private, add a token with access in Settings.")
        let bad = await store.entry("github:o/bad")
        XCTAssertEqual(bad?.lastError, summary.firstError); XCTAssertFalse(bad?.hasRelease ?? true)
    }

    func testNoReleasesAtAllIsSaidPlainly() async throws {
        let w = FakeWeb(); w.answer = { r in reply(200, r.url!.path.hasSuffix("/releases") ? "[]" : self.repoJSON) }
        let store = ReleaseStore(client: SourceClient(token: "t", send: w.send))
        _ = await store.check([target(1)], manual: true)
        let error = await store.entry("github:o/r1")?.lastError
        XCTAssertEqual(error, "No releases have been published for this repository.")
    }

    func testAppsLeftOutOfTheLibraryAreForgottenAndEverythingIsKeptBetweenRuns() async throws {
        let blobs = MemoryBlobStore()
        let first = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: Traffic()).send), blobs: blobs)
        _ = await first.check([target(1), target(2)], manual: true)
        let saved = await blobs.saves
        XCTAssertEqual(saved, 1)

        let traffic = Traffic()
        let second = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic).send), blobs: blobs)
        await second.load()
        let keys = await second.all().keys.sorted()
        XCTAssertEqual(keys, ["github:o/r1", "github:o/r2"], "remembered after a restart")
        let summary = await second.check([target(1)], manual: false, now: .now() + Timestamp.hours(2))
        XCTAssertEqual(summary.unchanged, 1, "the saved ETag still works")
        let after = await second.all().keys.sorted()
        XCTAssertEqual(after, ["github:o/r1"], "an app no longer in the library is dropped")
    }

    func testGarbageInTheSavedDataIsIgnored() async throws {
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: Traffic()).send), blobs: MemoryBlobStore(data: Data("not json".utf8)))
        await store.load()
        let count = await store.all().count
        XCTAssertEqual(count, 0)
    }

    func testTwoChecksAtOnceDoNotDoubleTheWork() async throws {
        let traffic = Traffic()
        let store = ReleaseStore(client: SourceClient(token: "t", send: web(traffic: traffic, delay: true).send))
        async let a = store.check((1...6).map { target($0) }, manual: true)
        async let b = store.check((1...6).map { target($0) }, manual: true)
        let (x, y) = await (a, b)
        XCTAssertEqual(x.checked + y.checked, 6, "one of them did the work; the other did nothing")
    }
}
