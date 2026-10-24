import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class LibraryLayoutTests: XCTestCase {
    func app(_ repo: String, name: String? = nil, group: String? = nil, pinned: Bool = false, hidden: Bool = false, tags: String? = nil, note: String? = nil) -> SyncApp {
        var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo)"
        a.displayName = name; a.group = group; a.pinned = pinned; a.hidden = hidden; a.tags = tags; a.note = note
        return a
    }

    func layout(_ apps: [SyncApp], groups: [String] = [], query: String = "", hidden: Bool = false) -> [(String, [String])] {
        LibraryLayout.sections(apps: apps, customGroups: groups, query: query, showHidden: hidden).map { ($0.title, $0.apps.map(\.repo)) }
    }

    func testFavoritesFirstThenGroupsThenTheRest() {
        let apps = [app("zeta"), app("alpha", pinned: true), app("mix", group: "Media"), app("game", group: "Games"), app("beta")]
        let result = layout(apps, groups: ["Games", "Media"])
        XCTAssertEqual(result.map(\.0), ["Favorites", "Games", "Media", "Everything else"])
        XCTAssertEqual(result[3].1, ["beta", "zeta"])
    }

    func testAnAppAppearsOnce() {
        let all = layout([app("a", group: "Games", pinned: true), app("b", group: "Games")], groups: ["Games"]).flatMap(\.1)
        XCTAssertEqual(all.sorted(), ["a", "b"])
    }

    func testOnlyTheRestIsCalledYourApps() {
        XCTAssertEqual(layout([app("a"), app("b")]).map(\.0), ["Your apps"])
    }

    func testGroupNamesMatchIgnoringCaseAndUnknownGroupsStillShow() {
        let result = layout([app("a", group: "games"), app("b", group: "Games"), app("c", group: "Elsewhere")], groups: ["Games"])
        XCTAssertEqual(result.map(\.0), ["Games", "Elsewhere"])
        XCTAssertEqual(result[0].1, ["a", "b"])
    }

    func testHiddenAppsAreLeftOutUnlessAsked() {
        let apps = [app("a"), app("b", hidden: true)]
        XCTAssertEqual(layout(apps).flatMap(\.1), ["a"])
        XCTAssertEqual(layout(apps, hidden: true).flatMap(\.1), ["a", "b"])
    }

    func testSearchLooksAtNamesGroupsTagsAndNotes() {
        let apps = [app("player", name: "Pixel Player", tags: "video, audio"), app("notes", note: "Remember the café"), app("game", group: "Retro")]
        XCTAssertEqual(layout(apps, query: "pixel").flatMap(\.1), ["player"])
        XCTAssertEqual(layout(apps, query: "AUDIO").flatMap(\.1), ["player"])
        XCTAssertEqual(layout(apps, query: "cafe").flatMap(\.1), ["notes"], "ignores accents and case")
        XCTAssertEqual(layout(apps, query: "retro").flatMap(\.1), ["game"])
        XCTAssertEqual(layout(apps, query: "fake/").flatMap(\.1).count, 3, "owner/repo")
        XCTAssertTrue(layout(apps, query: "nothing like this").isEmpty)
    }

    func testTitlesPreferTheDisplayName() {
        XCTAssertEqual(app("repo", name: "Nice Name").title, "Nice Name")
        XCTAssertEqual(app("repo", name: "  ").title, "repo")
        XCTAssertEqual(app("repo").title, "repo")
    }
}

final class RepoLinksTests: XCTestCase {
    func testKnownSites() {
        XCTAssertEqual(RepoLinks.url(source: "github", owner: "fake", repo: "app")?.absoluteString, "https://github.com/fake/app")
        XCTAssertEqual(RepoLinks.url(source: "gitlab", owner: "fake/sub", repo: "app")?.absoluteString, "https://gitlab.com/fake/sub/app")
        XCTAssertEqual(RepoLinks.url(source: "codeberg", owner: "fake", repo: "app")?.absoluteString, "https://codeberg.org/fake/app")
        XCTAssertEqual(RepoLinks.url(source: "forgejo:git.example.test", owner: "fake", repo: "app")?.absoluteString, "https://git.example.test/fake/app")
    }

    func testOddValuesFromTheFileAreNotTurnedIntoLinks() {
        XCTAssertNil(RepoLinks.url(source: "forgejo:evil.test/x?y", owner: "fake", repo: "app"))
        XCTAssertNil(RepoLinks.url(source: "forgejo:", owner: "fake", repo: "app"))
        XCTAssertNil(RepoLinks.url(source: "ftp", owner: "fake", repo: "app"))
        XCTAssertNil(RepoLinks.url(source: "github", owner: "../x", repo: "app"))
        XCTAssertNil(RepoLinks.url(source: "github", owner: "fake", repo: "a b"))
        XCTAssertNil(RepoLinks.url(source: "github", owner: "fake", repo: "app?x=1"))
        XCTAssertNil(RepoLinks.url(source: "github", owner: "", repo: "app"))
    }

    func testNames() {
        XCTAssertEqual(RepoLinks.sourceName("github"), "GitHub")
        XCTAssertEqual(RepoLinks.sourceName("forgejo:git.example.test"), "git.example.test")
    }
}
