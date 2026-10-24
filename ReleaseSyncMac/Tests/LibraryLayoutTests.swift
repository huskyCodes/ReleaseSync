import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class LibraryLayoutTests: XCTestCase {
    func app(_ repo: String, name: String? = nil, group: String? = nil, pinned: Bool = false, hidden: Bool = false, tags: String? = nil, note: String? = nil, added: Int64 = 0) -> SyncApp {
        var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo)"
        a.displayName = name; a.group = group; a.pinned = pinned; a.hidden = hidden; a.tags = tags; a.note = note; a.addedAt = Timestamp(ticks: added)
        return a
    }

    func layout(_ apps: [SyncApp], groups: [String] = [], status: LibraryLayout.Status = .all, groupFilter: String? = nil, query: String = "", sort: String = "Name",
                grouped: Bool = true, auto: Bool = true) -> LibraryLayout.Result {
        LibraryLayout.layout(apps: apps, customGroups: groups, status: status, groupFilter: groupFilter, query: query, sort: sort, groupItems: grouped, autoGroups: auto)
    }

    func titles(_ r: LibraryLayout.Result) -> [String] { r.sections.map(\.title) }
    func repos(_ r: LibraryLayout.Result) -> [[String]] { r.sections.map { $0.apps.map(\.repo) } }

    func testSectionsAreYourGroupsAndAutomaticTypesInNameOrder() {
        let apps = [app("zeta-tool"), app("mix", group: "Media"), app("game", group: "Games"), app("pdf-reader")]
        let r = layout(apps, groups: ["Games", "Media"])
        XCTAssertEqual(titles(r), ["Games", "Media", "Productivity", "System & Utilities"])
        XCTAssertEqual(r.chips.map(\.name), ["Games", "Media", "Productivity", "System & Utilities"], "your own groups come first in the chips")
    }

    func testAutomaticGroupsCanBeTurnedOff() {
        let r = layout([app("pdf-reader"), app("mix", group: "Media")], groups: ["Media"], auto: false)
        XCTAssertEqual(titles(r), ["Media", "Ungrouped"])
        XCTAssertEqual(r.chips.map(\.name), ["Media", "Ungrouped"])
    }

    func testFavoritesComeFirstInsideTheirGroup() {
        let r = layout([app("b"), app("a"), app("c", pinned: true)], grouped: false)
        XCTAssertEqual(repos(r), [["c", "a", "b"]])
        XCTAssertEqual(titles(r), [""], "one section without a heading when not grouped")
    }

    func testAnAppAppearsOnce() {
        let all = layout([app("a", group: "Games", pinned: true), app("b", group: "Games")], groups: ["Games"]).sections.flatMap(\.apps).map(\.repo)
        XCTAssertEqual(all.sorted(), ["a", "b"])
    }

    func testHiddenAppsOnlyShowUnderHidden() {
        let apps = [app("a"), app("b", hidden: true)]
        let all = layout(apps), hidden = layout(apps, status: .hidden)
        XCTAssertEqual(repos(all).flatMap { $0 }, ["a"])
        XCTAssertEqual(repos(hidden).flatMap { $0 }, ["b"])
        XCTAssertEqual([all.total, all.hidden, all.favorites], [1, 1, 0])
    }

    func testFavoritesFilterAndGroupFilter() {
        let apps = [app("a", group: "Games", pinned: true), app("b", group: "Games"), app("c", group: "Media", pinned: true)]
        XCTAssertEqual(repos(layout(apps, status: .favorites)).flatMap { $0 }, ["a", "c"])
        XCTAssertEqual(repos(layout(apps, groupFilter: "Games")).flatMap { $0 }, ["a", "b"])
        XCTAssertEqual(repos(layout(apps, status: .favorites, groupFilter: "Media")).flatMap { $0 }, ["c"])
    }

    func testGroupNamesMatchIgnoringCase() {
        let r = layout([app("a", group: "games"), app("b", group: "Games")], groups: ["Games"])
        XCTAssertEqual(r.chips.count, 2, "the Windows app compares exact names for sections, so a different spelling is its own group")
    }

    func testEmptyGroupsStillShowWhenNothingIsFiltered() {
        let apps = [app("a", group: "Games")]
        XCTAssertEqual(layout(apps, groups: ["Games", "Retro"]).emptyGroups, ["Retro"])
        XCTAssertEqual(layout(apps, groups: ["Games", "Retro"], query: "a").emptyGroups, [], "not while searching")
        XCTAssertEqual(layout(apps, groups: ["Games", "Retro"], status: .favorites).emptyGroups, [])
    }

    func testSortByRecentlyAdded() {
        let apps = [app("old", added: 1), app("new", added: 9), app("mid", added: 5)]
        XCTAssertEqual(repos(layout(apps, sort: "Recently added", grouped: false)), [["new", "mid", "old"]])
        XCTAssertEqual(repos(layout(apps, sort: "Name", grouped: false)), [["mid", "new", "old"]])
    }

    func testSearchLooksAtNamesGroupsTagsAndNotes() {
        let apps = [app("player", name: "Pixel Player", tags: "video, audio"), app("notes", note: "Remember the café"), app("game", group: "Retro")]
        func found(_ q: String) -> [String] { repos(layout(apps, query: q)).flatMap { $0 }.sorted() }
        XCTAssertEqual(found("pixel"), ["player"])
        XCTAssertEqual(found("AUDIO"), ["player"])
        XCTAssertEqual(found("cafe"), ["notes"], "ignores accents and case")
        XCTAssertEqual(found("retro"), ["game"])
        XCTAssertEqual(found("fake/").count, 3, "owner/repo")
        XCTAssertTrue(found("nothing like this").isEmpty)
    }

    func testTitlesPreferTheDisplayName() {
        XCTAssertEqual(app("repo", name: "Nice Name").title, "Nice Name")
        XCTAssertEqual(app("repo", name: "  ").title, "repo")
        XCTAssertEqual(app("repo").title, "repo")
    }

    // MARK: Try later

    func item(_ repo: String, stars: Int = 0, saved: Int64 = 0, description: String? = nil, topics: [String]? = nil, note: String? = nil) -> TryLaterItem {
        var i = TryLaterItem(); i.owner = "fake"; i.repo = repo; i.stars = stars; i.savedAt = Timestamp(ticks: saved); i.description = description; i.topics = topics; i.note = note
        return i
    }

    func testTryLaterTypesAreCountedAndSortedByCount() {
        let items = [item("a", description: "A pdf reader for documents"), item("b", description: "Take notes and keep a to-do list"), item("c", description: "A video player for movies"), item("d")]
        let r = LibraryLayout.tryLater(items, type: "", query: "", sort: "recent", group: true)
        XCTAssertEqual(r.types.first, LibraryLayout.TryType(name: "", count: 4))
        XCTAssertEqual(r.types.dropFirst().first?.name, "Productivity", "the type with the most projects is first")
        XCTAssertEqual(r.total, 4); XCTAssertEqual(r.shown, 4)
    }

    func testTryLaterSorts() {
        let items = [item("b", stars: 5, saved: 2), item("a", stars: 50, saved: 1), item("c", stars: 5, saved: 3)]
        func order(_ sort: String) -> [String] { LibraryLayout.tryLater(items, type: "", query: "", sort: sort, group: false).sections.flatMap { $0.items }.map(\.repo) }
        XCTAssertEqual(order("recent"), ["c", "b", "a"])
        XCTAssertEqual(order("oldest"), ["a", "b", "c"])
        XCTAssertEqual(order("name"), ["a", "b", "c"])
        XCTAssertEqual(order("stars"), ["a", "b", "c"])
    }

    func testTryLaterSearchAndTypeFilter() {
        let items = [item("alpha", description: "A pdf reader"), item("beta", description: "A video player", topics: ["media"])]
        let all = LibraryLayout.tryLater(items, type: "", query: "", sort: "name", group: true)
        let type = all.types.last!.name
        XCTAssertEqual(LibraryLayout.tryLater(items, type: type, query: "", sort: "name", group: true).shown, 1)
        XCTAssertEqual(LibraryLayout.tryLater(items, type: "", query: "media", sort: "name", group: true).shown, 1)
        XCTAssertEqual(LibraryLayout.tryLater(items, type: "", query: "zzz", sort: "name", group: true).sections.count, 0)
    }
}

@MainActor
final class ViewPrefsTests: XCTestCase {
    func testDefaultsMatchTheWindowsApp() {
        let p = ViewPrefs(defaults: UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)!)
        XCTAssertEqual(p.viewMode, .grid); XCTAssertEqual(p.sort, "Name"); XCTAssertTrue(p.groupItems); XCTAssertTrue(p.autoGroups)
        XCTAssertEqual(p.tryViewMode, .grid); XCTAssertEqual(p.trySort, "recent"); XCTAssertTrue(p.tryGroup); XCTAssertEqual(p.appearance, .system)
    }

    func testChoicesAreKept() {
        let name = "ReleaseSyncTests-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let a = ViewPrefs(defaults: defaults)
        a.viewMode = .list; a.sort = "Recently added"; a.autoGroups = false; a.trySort = "stars"; a.appearance = .dark
        let b = ViewPrefs(defaults: defaults)
        XCTAssertEqual(b.viewMode, .list); XCTAssertEqual(b.sort, "Recently added"); XCTAssertFalse(b.autoGroups); XCTAssertEqual(b.trySort, "stars"); XCTAssertEqual(b.appearance, .dark)
    }

    func testUnknownStoredValuesFallBack() {
        let name = "ReleaseSyncTests-" + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set("Sideways", forKey: "ViewMode"); defaults.set("Recently updated", forKey: "SortMode"); defaults.set("whatever", forKey: "TrySort")
        let p = ViewPrefs(defaults: defaults)
        XCTAssertEqual(p.viewMode, .grid); XCTAssertEqual(p.sort, "Name"); XCTAssertEqual(p.trySort, "recent")
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
