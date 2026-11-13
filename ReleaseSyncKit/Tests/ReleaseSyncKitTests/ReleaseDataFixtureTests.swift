import XCTest
@testable import ReleaseSyncKit

/// Release data read and compared the way the C# engine does it (files in Fixtures/releases).
final class ReleaseDataFixtureTests: XCTestCase {
    func testVersionComparisonMatchesTheEngineForEveryPair() throws {
        let v = try Fixtures.json("releases/versions.json")
        let versions = v.list("Versions").compactMap { if case .string(let s) = $0 { s } else { nil } }
        let clean = v.list("Clean").compactMap { if case .string(let s) = $0 { s } else { nil } }
        XCTAssertEqual(versions.count, 44)
        let matrix = v.list("Newer")
        var pairs = 0
        for (i, latest) in versions.enumerated() {
            XCTAssertEqual(VersionUtil.clean(latest), clean[i], latest.debugDescription)
            guard case .array(let row) = matrix[i] else { return XCTFail() }
            for (j, installed) in versions.enumerated() {
                guard case .bool(let expected) = row[j] else { return XCTFail() }
                XCTAssertEqual(VersionUtil.isNewer(latest, than: installed), expected, "\(latest.debugDescription) vs \(installed.debugDescription)")
                pairs += 1
            }
        }
        XCTAssertEqual(pairs, 44 * 44)
    }

    func testDatesDecideBetweenTagStylesAndTheOtherRulesMatchTheEngine() throws {
        let v = try Fixtures.json("releases/versions.json")
        let dated = v.list("Dated")
        XCTAssertEqual(dated.count, 16)
        for c in dated {
            let date = { (name: String) in c.str(name).flatMap { Timestamp(text: $0) } }
            XCTAssertEqual(VersionUtil.isNewer(c.str("Latest")!, than: c.str("Installed")!, latestDate: date("LatestDate"), installedDate: date("InstalledDate")), c.flag("Newer"),
                           "\(c.str("Latest")!) vs \(c.str("Installed")!)")
        }
        let tags = v.list("Tags").compactMap { $0.stringValue }
        XCTAssertEqual(tags.count, 9)
        for c in v.list("MatchTag") { XCTAssertEqual(VersionUtil.matchTag(c.str("Seen"), in: tags), c.str("Tag"), c.str("Seen")!.debugDescription) }
        XCTAssertEqual(v.list("MatchTag").count, 14)
        for c in v.list("TagsIn") { XCTAssertEqual(VersionUtil.tagsIn(c.str("Text")!, tags: tags), c.list("Found").compactMap { $0.stringValue }, c.str("Text")!.debugDescription) }
        for c in v.list("OneOff") { XCTAssertEqual(VersionUtil.isOneOff(c.str("Tag")!), c.flag("Value"), c.str("Tag")!.debugDescription) }
    }

    func testReleaseNotesBecomePlainTextTheSameWay() throws {
        for c in try Fixtures.json("releases/markdown.json").list("Cases") {
            let text = c.str("Text") ?? "", plain = c.str("Plain") ?? ""
            XCTAssertEqual(Fmt.stripMarkdown(text), plain, text.prefix(60).debugDescription)
        }
    }

    func testCountsAndSizes() throws {
        let f = try Fixtures.json("releases/format.json")
        for c in f.list("Counts") { XCTAssertEqual(Fmt.count(Int(c.whole("Value")!)), c.str("Text"), "\(c.whole("Value")!)") }
        for c in f.list("Sizes") { XCTAssertEqual(Fmt.size(c.whole("Value")!), c.str("Text"), "\(c.whole("Value")!)") }
    }

    func testPastedProjectLinks() throws {
        let d = try Fixtures.json("releases/repo-refs.json")
        XCTAssertGreaterThan(d.list("Cases").count, 40)
        for c in d.list("Cases") {
            let input = c.str("Input") ?? ""
            let got = RepoRef.parse(input)
            if c.flag("Ok") == true {
                XCTAssertEqual(got, RepoRef(source: c.str("Source")!, owner: c.str("Owner")!, repo: c.str("Repo")!, fullPath: c.str("FullPath")), input.debugDescription)
                XCTAssertEqual(got?.url, c.str("Url"), input.debugDescription)
            } else {
                XCTAssertNil(got, input.debugDescription)
            }
        }
        for u in d.list("Urls") {
            XCTAssertEqual(RepoRef.urlFor(source: u.str("Item1")!, owner: u.str("Item2")!, repo: u.str("Item3")!), u.str("Url"))
        }
    }

    func testGitHubAndForgejoJSONIsReadTheSameWay() throws {
        let d = try Fixtures.json("releases/parsing.json")
        let repo = RepoInfo(github: try JSONParser.parse(try XCTUnwrap(d.str("RepoJson"))))
        let e = try XCTUnwrap(d.member("Repo"))
        XCTAssertEqual(repo.fullName, e.str("FullName")); XCTAssertEqual(repo.name, e.str("Name")); XCTAssertEqual(repo.ownerLogin, e.str("OwnerLogin"))
        XCTAssertEqual(repo.avatarUrl, e.str("AvatarUrl")); XCTAssertEqual(repo.description, e.str("Description")); XCTAssertEqual(Int64(repo.stars), e.whole("Stars"))
        XCTAssertEqual(repo.language, e.str("Language")); XCTAssertEqual(repo.topics, e.list("Topics").compactMap { if case .string(let s) = $0 { s } else { nil } })
        XCTAssertEqual(repo.htmlUrl, e.str("HtmlUrl")); XCTAssertEqual(repo.archived, e.flag("Archived") ?? false); XCTAssertEqual(repo.fork, e.flag("Fork") ?? false)
        XCTAssertEqual(Int64(repo.forks), e.whole("Forks")); XCTAssertEqual(Int64(repo.openIssues), e.whole("OpenIssues")); XCTAssertEqual(Int64(repo.watchers), e.whole("Watchers"))
        XCTAssertEqual(repo.licenseSpdx, e.str("LicenseSpdx")); XCTAssertEqual(repo.licenseName, e.str("LicenseName")); XCTAssertEqual(repo.homepage, e.str("Homepage"))
        XCTAssertEqual(repo.createdAt?.text, e.str("CreatedAt")); XCTAssertEqual(repo.pushedAt?.text, e.str("PushedAt"))

        let releases = try ReleaseInfo.list(github: try XCTUnwrap(d.str("ReleasesJson")))
        let want = d.list("Releases")
        XCTAssertEqual(releases.count, want.count)
        for (r, w) in zip(releases, want) {
            XCTAssertEqual(r.tag, w.str("Tag")); XCTAssertEqual(r.name, w.str("Name")); XCTAssertEqual(r.body, w.str("Body")); XCTAssertEqual(r.prerelease, w.flag("Prerelease") ?? false)
            XCTAssertEqual(r.publishedAt?.text, w.str("PublishedAt"), r.tag); XCTAssertEqual(r.htmlUrl, w.str("HtmlUrl"))
            let assets = w.list("Assets")
            XCTAssertEqual(r.assets.count, assets.count, r.tag)
            for (a, x) in zip(r.assets, assets) {
                XCTAssertEqual(a.name, x.str("Name")); XCTAssertEqual(a.size, x.whole("Size")); XCTAssertEqual(a.url, x.str("Url"))
                XCTAssertEqual(Int64(a.downloads), x.whole("Downloads")); XCTAssertEqual(a.state, x.str("State")); XCTAssertEqual(a.digest, x.str("Digest"))
            }
        }
    }
}
