import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

@MainActor
final class DiscoverModelTests: XCTestCase {
    func defaults() -> UserDefaults { UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)! }

    /// A GitHub with `count` projects per search, where every project whose name ends in "-mac" ships a .dmg.
    func send(count: Int = 8, remaining: Int = 4000, asked: Asked = Asked()) -> HTTPSend {
        return { r in
            asked.add(r.url!.path)
            let headers = ["x-ratelimit-remaining": String(remaining), "x-ratelimit-limit": "5000"]
            if r.url!.path == "/search/repositories" {
                let items = (0..<count).map { i -> String in
                    let name = "proj\(i)" + (i % 2 == 0 ? "-mac" : "-win")
                    return #"{"full_name":"o/\#(name)","name":"\#(name)","owner":{"login":"o"},"description":"d","stargazers_count":\#(1000 - i)}"#
                }
                return reply(200, #"{"items":[\#(items.joined(separator: ","))]}"#, headers: ["x-ratelimit-resource": "search"])
            }
            if r.url!.path.hasSuffix("/releases") {
                let mac = r.url!.path.contains("-mac")
                return reply(200, mac ? #"[{"tag_name":"v1","assets":[{"name":"A.dmg","browser_download_url":"https://e.test/A.dmg"}]}]"# : #"[{"tag_name":"v1","assets":[{"name":"A.exe","browser_download_url":"https://e.test/A.exe"}]}]"#, headers: headers)
            }
            return reply(404)
        }
    }

    func make(token: String? = "t", count: Int = 8, remaining: Int = 4000, prefs: ViewPrefs? = nil, asked: Asked = Asked()) async -> (DiscoverModel, ViewPrefs, RateMeter) {
        let client = SourceClient(token: token, send: send(count: count, remaining: remaining, asked: asked), sleep: { _ in })
        let prefs = prefs ?? ViewPrefs(defaults: defaults())
        let meter = RateMeter()
        if remaining < 150 { await client.setToken(token); _ = try? await client.search("warm", sort: "stars", perPage: 1); _ = try? await client.releases(source: "github", owner: "o", repo: "x", etag: nil); await meter.update(from: client) }
        return (DiscoverModel(client: client, prefs: prefs, meter: meter), prefs, meter)
    }

    func testOnlyProjectsWithMacDownloadsAreShownAndRanked() async throws {
        let (m, _, _) = await make()
        m.load(); await m.waitUntilIdle()
        XCTAssertNil(m.error)
        XCTAssertFalse(m.items.isEmpty)
        XCTAssertTrue(m.items.allSatisfy { $0.repo.name.hasSuffix("-mac") }, "the Windows-only ones are hidden")
        XCTAssertTrue(m.items.allSatisfy { $0.hasMacBuild == true })
        XCTAssertEqual(m.items.map(\.rank), Array(1...m.items.count))
        XCTAssertFalse(m.isBusy)
        XCTAssertTrue(m.status.contains("movement arrows appear on your next visit"), m.status)
    }

    func testTurningTheFilterOffShowsEverythingWithoutAskingAboutMacDownloads() async throws {
        let asked = Asked()
        let (m, prefs, _) = await make(asked: asked)
        prefs.discoverMacOnly = false
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 8)
        XCTAssertFalse(asked.paths.contains { $0.hasSuffix("/releases") })
    }

    func testWithoutATokenThereIsNoMacCheck() async throws {
        let (m, prefs, _) = await make(token: nil)
        prefs.discoverCategory = "media"          // few enough searches to go out at once without a token
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 8, "everything is shown rather than spending 60 requests an hour on checks")
    }

    func testRunningLowPausesTheMacCheck() async throws {
        let (m, _, meter) = await make(remaining: 100)
        XCTAssertTrue(meter.isTight)
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 8)
        XCTAssertTrue(m.status.contains("Mac check paused"), m.status)
    }

    func testHandPickedListsAreNeverFiltered() async throws {
        let (m, prefs, _) = await make()
        prefs.discoverMode = .featured
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 8)
        XCTAssertEqual(m.items.filter { $0.hasMacBuild == true }.count, 4, "but the ones with Mac downloads are marked")
        XCTAssertEqual(m.items.filter { $0.hasMacBuild == false }.count, 4)
    }

    func testLightModeShowsFewerAndLoadMoreAddsTheRest() async throws {
        let (m, prefs, _) = await make(count: 40)
        prefs.discoverMacOnly = false
        prefs.discoverLight = true
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 15)
        XCTAssertTrue(m.canLoadMore)
        m.loadMore(); await m.waitUntilIdle()
        XCTAssertEqual(m.items.count, 30)
        prefs.discoverLight = false
        let (full, fullPrefs, _) = await make(count: 40)
        fullPrefs.discoverMacOnly = false
        full.load(); await full.waitUntilIdle()
        XCTAssertEqual(full.items.count, 36)
    }

    func testChoicesAreRememberedAndSwitchingReloads() async throws {
        let (m, prefs, _) = await make()
        m.setMode(.popular); await m.waitUntilIdle()
        m.setCategory("media"); await m.waitUntilIdle()
        XCTAssertEqual(prefs.discoverMode, .popular); XCTAssertEqual(prefs.discoverCategory, "media")
        XCTAssertFalse(m.items.isEmpty)
        let again = ViewPrefs(defaults: UserDefaults(suiteName: "ReleaseSyncTests-x-" + UUID().uuidString)!)
        XCTAssertEqual(again.discoverMode, .trending, "a fresh start")
    }

    func testAProblemIsShownInPlainWords() async throws {
        let client = SourceClient(token: "t", send: { _ in reply(500) }, sleep: { _ in })
        let m = DiscoverModel(client: client, prefs: ViewPrefs(defaults: defaults()), meter: RateMeter())
        m.load(); await m.waitUntilIdle()
        XCTAssertEqual(m.error, "GitHub returned an error (500).")
        XCTAssertTrue(m.items.isEmpty); XCTAssertFalse(m.isBusy)
    }

    func testDemoNeverAsksAnything() async throws {
        let asked = Asked()
        let client = SourceClient(send: { asked.add($0.url!.path); return reply(500) })
        let m = DiscoverModel(showing: [DiscoverItem(RepoInfo())], client: client, prefs: ViewPrefs(defaults: defaults()), meter: RateMeter())
        m.load(force: true); m.appeared(); m.loadMore()
        await m.waitUntilIdle()
        XCTAssertTrue(asked.paths.isEmpty)
        XCTAssertEqual(m.items.count, 1)
    }

    func testPreferencesFallBackOnUnknownValues() {
        let d = defaults()
        d.set("Sideways", forKey: "DiscoverMode"); d.set("nope", forKey: "DiscoverCategory")
        let p = ViewPrefs(defaults: d)
        XCTAssertEqual(p.discoverMode, .trending); XCTAssertEqual(p.discoverCategory, "all"); XCTAssertTrue(p.discoverMacOnly); XCTAssertFalse(p.discoverLight)
    }
}
