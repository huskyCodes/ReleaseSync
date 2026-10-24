import XCTest
@testable import ReleaseSyncKit

final class SourceClientTests: XCTestCase {
    let token = "fake-token-0123456789"

    func fixtureJSON(_ key: String) throws -> String { try XCTUnwrap(try Fixtures.json("releases/parsing.json").str(key)) }

    func testGitHubReleasesAreAskedForAndReadLikeTheEngineDoes() async throws {
        let web = FakeWeb()
        let body = try fixtureJSON("ReleasesJson")
        web.answer = { _ in reply(200, body, headers: ["etag": "W/\"abc\"", "x-ratelimit-limit": "5000", "x-ratelimit-remaining": "4990", "x-ratelimit-reset": "1767268800"]) }
        let client = SourceClient(token: token, send: web.send)
        let result = try await client.releases(source: "github", owner: "example-org", repo: "sample-app", etag: nil)
        XCTAssertEqual(result.etag, "W/\"abc\"")
        XCTAssertEqual(result.releases.map(\.tag), ["v1.2.0-beta.1", "v1.1.0", "v1.0.0", "untimed"], "drafts dropped, newest first")
        let r = web.requests[0]
        XCTAssertEqual(r.url?.absoluteString, "https://api.github.com/repos/example-org/sample-app/releases?per_page=20")
        XCTAssertEqual(r.value(forHTTPHeaderField: "Authorization"), "Bearer \(token)")
        XCTAssertEqual(r.value(forHTTPHeaderField: "Accept"), "application/vnd.github+json")
        XCTAssertNil(r.value(forHTTPHeaderField: "If-None-Match"))
        let rate = await client.rate
        XCTAssertEqual(rate, RateStatus(limit: 5000, remaining: 4990, reset: Date(timeIntervalSince1970: 1767268800)))
    }

    func testAnUnchangedProjectIsAskedAboutWithItsETagAndCostsNothing() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(304, "", headers: ["x-ratelimit-remaining": "4990"]) }
        let client = SourceClient(token: token, send: web.send)
        let result = try await client.releases(source: "github", owner: "example-org", repo: "sample-app", etag: "W/\"abc\"")
        XCTAssertTrue(result.notModified)
        XCTAssertEqual(result.etag, "W/\"abc\"")
        XCTAssertTrue(result.releases.isEmpty)
        XCTAssertEqual(web.requests[0].value(forHTTPHeaderField: "If-None-Match"), "W/\"abc\"")
    }

    func testTheTokenGoesOnlyToGitHub() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(200, "[]") }
        let client = SourceClient(token: token, send: web.send)
        _ = try await client.releases(source: "gitlab", owner: "example", repo: "sample-app", etag: nil)
        _ = try await client.releases(source: "codeberg", owner: "example", repo: "sample-app", etag: nil)
        _ = try await client.releases(source: "forgejo:git.example.test", owner: "example", repo: "sample-app", etag: nil)
        _ = try await client.releases(source: "gitlab:git.example.test", owner: "example", repo: "sample-app", etag: nil)
        XCTAssertEqual(web.requests.map { $0.url!.host }, ["gitlab.com", "codeberg.org", "git.example.test", "git.example.test"])
        for r in web.requests {
            XCTAssertNil(r.value(forHTTPHeaderField: "Authorization"), r.url!.absoluteString)
            XCTAssertNil(r.value(forHTTPHeaderField: "X-GitHub-Api-Version"))
            XCTAssertFalse((r.allHTTPHeaderFields ?? [:]).values.contains { $0.contains(token) })
        }
        XCTAssertEqual(web.requests[1].url?.absoluteString, "https://codeberg.org/api/v1/repos/example/sample-app/releases?limit=20")
        XCTAssertEqual(web.requests[0].url?.absoluteString, "https://gitlab.com/api/v4/projects/example%2Fsample-app/releases?per_page=20")
    }

    func testOddSiteNamesAreNotFollowed() async throws {
        let web = FakeWeb()
        let client = SourceClient(token: token, send: web.send)
        for source in ["forgejo:localhost", "forgejo:192.168.0.1", "forgejo:10.0.0.1:3000", "forgejo:intranet", "forgejo:evil.test/path", "forgejo:a b.test", "gitlab:", "forgejo:x@evil.test"] {
            do { _ = try await client.releases(source: source, owner: "o", repo: "r", etag: nil); XCTFail(source) } catch let e as SourceError { XCTAssertEqual(e.message, "That site's address isn't usable.", source) }
        }
        XCTAssertTrue(web.requests.isEmpty)
        XCTAssertTrue(SourceClient.isUsableHost("git.example.test"))
        XCTAssertTrue(SourceClient.isUsableHost("git.example.test:8443"))
    }

    func testGitHubProblemsAreExplained() async throws {
        func fail(_ status: Int, _ headers: [String: String] = [:], token: String? = "t") async -> SourceError {
            let web = FakeWeb(); web.answer = { _ in reply(status, "{}", headers: headers) }
            do { _ = try await SourceClient(token: token, send: web.send).releases(source: "github", owner: "a", repo: "b", etag: nil); XCTFail("\(status)"); return SourceError("") }
            catch { return error as! SourceError }
        }
        let notFound = await fail(404), bad = await fail(401), denied = await fail(403, ["x-ratelimit-remaining": "7"]), server = await fail(500)
        XCTAssertEqual(notFound.message, "a/b wasn't found. If it's private, add a token with access in Settings.")
        XCTAssertEqual(bad.message, "GitHub rejected your access token. Check it in Settings.")
        XCTAssertEqual(denied.message, "GitHub denied the request (403)."); XCTAssertFalse(denied.rateLimited)
        XCTAssertEqual(server.message, "GitHub returned an error (500).")
        let limited = await fail(403, ["x-ratelimit-remaining": "0", "x-ratelimit-reset": "1767268800"]), anon = await fail(429, ["retry-after": "30"], token: nil)
        XCTAssertTrue(limited.rateLimited); XCTAssertTrue(limited.message.hasPrefix("GitHub rate limit reached. It resets at "))
        XCTAssertFalse(limited.message.contains("Add a token"), "they already have one")
        XCTAssertTrue(anon.rateLimited); XCTAssertTrue(anon.message.contains("raise the limit to 5,000 per hour"))
    }

    func testSearchesHaveTheirOwnAllowanceAndDontChangeTheCounter() async throws {
        let web = FakeWeb()
        web.answer = { _ in reply(200, "{}", headers: ["x-ratelimit-resource": "search", "x-ratelimit-remaining": "3", "x-ratelimit-limit": "30"]) }
        let client = SourceClient(token: token, send: web.send)
        _ = try await client.repo(source: "github", owner: "a", repo: "b")
        let rate = await client.rate
        XCTAssertEqual(rate.remaining, -1)
    }

    func testNetworkTroubleIsExplainedWithoutTheToken() async throws {
        for (code, message) in [(URLError.timedOut, "GitHub took too long to respond."), (URLError.notConnectedToInternet, "Can't reach GitHub. Check your internet connection.")] {
            let web = FakeWeb(); web.answer = { _ in throw URLError(code) }
            do { _ = try await SourceClient(token: token, send: web.send).repo(source: "github", owner: "a", repo: "b"); XCTFail() }
            catch let e as SourceError { XCTAssertEqual(e.message, message); XCTAssertFalse(e.message.contains(token)) }
        }
        let web = FakeWeb(); web.answer = { _ in throw URLError(.timedOut) }
        do { _ = try await SourceClient(send: web.send).repo(source: "codeberg", owner: "a", repo: "b"); XCTFail() }
        catch let e as SourceError { XCTAssertEqual(e.message, "The server took too long to respond.") }
    }

    func testGitHubProjectDetails() async throws {
        let web = FakeWeb(); let body = try fixtureJSON("RepoJson"); web.answer = { _ in reply(200, body) }
        let info = try await SourceClient(token: token, send: web.send).repo(source: "github", owner: "example-org", repo: "sample-app")
        XCTAssertEqual(info.fullName, "example-org/sample-app"); XCTAssertEqual(info.stars, 8421); XCTAssertEqual(info.topics, ["sample", "desktop", "media-player"])
        XCTAssertEqual(web.requests[0].url?.absoluteString, "https://api.github.com/repos/example-org/sample-app")
    }

    func testGitLabProjectAndReleases() async throws {
        let web = FakeWeb()
        web.answer = { r in
            if r.url!.path.hasSuffix("/releases") {
                return reply(200, #"""
                [{"tag_name":"v2.0","name":"Two","description":"Notes","upcoming_release":false,"released_at":"2026-01-02T10:00:00.000Z","created_at":"2026-01-01T10:00:00.000Z","_links":{"self":"https://gitlab.example.test/o/p/-/releases/v2.0"},
                  "assets":{"links":[{"name":"App.dmg","url":"https://x.test/a","direct_asset_url":"https://x.test/direct/App.dmg"},{"name":"Docs","url":""},{"name":"","url":"https://x.test/n"},{"name":"Other.zip","url":"https://x.test/other.zip"}]}},
                 {"tag_name":"v1.0","name":null,"description":null,"upcoming_release":true,"created_at":"2025-12-01T10:00:00Z","assets":{"links":[]}},
                 {"tag_name":"","name":"no tag","assets":{"links":[]}}]
                """#)
            }
            return reply(200, #"{"path_with_namespace":"group/sub/proj","path":"proj","description":"A project","star_count":12,"forks_count":3,"open_issues_count":1,"web_url":"https://gitlab.com/group/sub/proj","archived":false,"created_at":"2024-01-01T00:00:00Z","last_activity_at":"2026-01-01T00:00:00Z","namespace":{"avatar_url":"https://gitlab.com/ns.png"},"topics":["a","","b"],"license":{"nickname":"MIT","name":"MIT License"}}"#)
        }
        let client = SourceClient(send: web.send)
        let info = try await client.repo(source: "gitlab", owner: "group/sub", repo: "proj")
        XCTAssertEqual(info.fullName, "group/sub/proj"); XCTAssertEqual(info.ownerLogin, "group/sub"); XCTAssertEqual(info.stars, 12); XCTAssertEqual(info.topics, ["a", "b"])
        XCTAssertEqual(info.avatarUrl, "https://gitlab.com/ns.png"); XCTAssertEqual(info.licenseName, "MIT")
        XCTAssertEqual(web.requests[0].url?.absoluteString, "https://gitlab.com/api/v4/projects/group%2Fsub%2Fproj?license=true")

        let rel = try await client.releases(source: "gitlab:gitlab.example.test", owner: "o", repo: "p", etag: nil)
        XCTAssertEqual(rel.releases.map(\.tag), ["v2.0", "v1.0"], "newest first; a release without a tag is skipped")
        XCTAssertEqual(rel.releases[0].assets.map(\.name), ["App.dmg", "Other.zip"], "links without a name or address aren't downloads")
        XCTAssertEqual(rel.releases[0].assets[0].url, "https://x.test/direct/App.dmg", "the direct link is preferred")
        XCTAssertEqual(rel.releases[0].publishedAt?.text, "2026-01-02T10:00:00Z")
        XCTAssertEqual(rel.releases[0].htmlUrl, "https://gitlab.example.test/o/p/-/releases/v2.0")
        XCTAssertTrue(rel.releases[1].prerelease, "an upcoming release counts as a pre-release")
        XCTAssertEqual(rel.releases[1].publishedAt?.text, "2025-12-01T10:00:00Z", "falls back to when it was created")
    }

    func testCodebergProjectAndReleases() async throws {
        let web = FakeWeb()
        web.answer = { r in
            if r.url!.path.hasSuffix("/releases") { return reply(200, try! self.fixtureJSON("ReleasesJson")) }
            return reply(200, #"{"full_name":"example/sample-app","name":"sample-app","description":"Hi","stars_count":5,"forks_count":1,"open_issues_count":2,"watchers_count":3,"html_url":"https://codeberg.org/example/sample-app","website":"https://x.test","language":"C","owner":{"login":"example","avatar_url":"https://codeberg.org/a.png"},"created_at":"2024-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}"#)
        }
        let client = SourceClient(send: web.send)
        let info = try await client.repo(source: "codeberg", owner: "example", repo: "sample-app")
        XCTAssertEqual(info.stars, 5); XCTAssertEqual(info.ownerLogin, "example"); XCTAssertEqual(info.source, "codeberg"); XCTAssertEqual(info.homepage, "https://x.test")
        let rel = try await client.releases(source: "codeberg", owner: "example", repo: "sample-app", etag: nil)
        XCTAssertEqual(rel.releases.map(\.tag), ["v1.2.0-beta.1", "v1.1.0", "v1.0.0", "untimed"])
        let missing = FakeWeb(); missing.answer = { _ in reply(404) }
        do { _ = try await SourceClient(send: missing.send).releases(source: "forgejo:git.example.test", owner: "a", repo: "b", etag: nil); XCTFail() }
        catch let e as SourceError { XCTAssertEqual(e.message, "a/b wasn't found.") }
        let broken = FakeWeb(); broken.answer = { _ in reply(503) }
        do { _ = try await SourceClient(send: broken.send).releases(source: "forgejo:git.example.test", owner: "a", repo: "b", etag: nil); XCTFail() }
        catch let e as SourceError { XCTAssertEqual(e.message, "git.example.test returned an error (503).") }
    }
}
