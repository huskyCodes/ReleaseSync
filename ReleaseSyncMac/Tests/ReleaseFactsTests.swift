import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

final class ReleaseFactsTests: XCTestCase {
    func app(_ repo: String = "tool", source: String = "github", owner: String = "fake", note: String? = nil, tags: String? = nil, pattern: String? = nil) -> SyncApp {
        var a = SyncApp(); a.source = source; a.owner = owner; a.repo = repo; a.key = "\(source):\(owner)/\(repo)"; a.note = note; a.tags = tags; a.assetPattern = pattern
        return a
    }

    func info(tag: String? = "v1.0", assets: [String] = [], error: String? = nil, checked: Bool = true, description: String? = nil) -> AppRelease {
        var e = AppRelease()
        e.latestTag = tag; e.lastError = error; e.lastChecked = checked ? .now() : nil
        e.assets = assets.map { AssetInfo(name: $0, url: "https://example.test/" + $0) }
        if let description { var m = RepoMeta(); m.description = description; e.meta = m }
        return e
    }

    func testBeforeAnythingIsKnown() {
        for facts in [ReleaseFacts(app(), nil), ReleaseFacts(app(), info(tag: nil, checked: false))] {
            XCTAssertEqual(facts.statusText, "Not checked yet"); XCTAssertEqual(facts.versionText, "Not installed"); XCTAssertNil(facts.hasMacBuild)
        }
    }

    func testAMacBuildMeansItCouldBeInstalled() {
        let f = ReleaseFacts(app(), info(assets: ["Tool-1.0-universal.dmg", "Tool-1.0-setup.exe"]))
        XCTAssertEqual(f.statusText, "Not installed"); XCTAssertEqual(f.versionText, "v1.0 available"); XCTAssertEqual(f.hasMacBuild, true)
        XCTAssertEqual(f.macAsset?.name, "Tool-1.0-universal.dmg")
    }

    func testAWindowsOnlyProjectSaysSo() {
        let f = ReleaseFacts(app(), info(assets: ["Tool-1.0-setup.exe", "Tool-1.0-linux.AppImage"]))
        XCTAssertEqual(f.statusText, "No Mac build"); XCTAssertEqual(f.versionText, "v1.0"); XCTAssertEqual(f.hasMacBuild, false); XCTAssertNil(f.macAsset)
        if case .warn = f.statusKind {} else { XCTFail("a warning colour") }
    }

    func testProblemsAreNamedPlainly() {
        let none = ReleaseFacts(app(), info(tag: nil, error: "No releases have been published for this repository."))
        XCTAssertEqual(none.statusText, "No releases"); XCTAssertEqual(none.versionText, "No release found")
        let broken = ReleaseFacts(app(), info(tag: nil, error: "git.example.test returned an error (503)."))
        XCTAssertEqual(broken.statusText, "Check failed")
        if case .danger = broken.statusKind {} else { XCTFail("red") }
    }

    func testTheSavedFilePatternPicksTheDownload() {
        let list = ["Tool-1.0-arm64.dmg", "Tool-1.0-arm64.pkg"]
        XCTAssertEqual(ReleaseFacts(app(pattern: #"\.pkg$"#), info(assets: list)).macAsset?.name, "Tool-1.0-arm64.pkg")
    }

    func testTheLineUnderTheNameIsTheDescriptionThenYourNoteThenTagsThenTheSite() {
        XCTAssertEqual(ReleaseFacts(app(note: "mine"), info(description: "About it")).blurb, "About it")
        XCTAssertEqual(ReleaseFacts(app(note: "mine"), nil).blurb, "mine")
        XCTAssertEqual(ReleaseFacts(app(tags: "a, b"), nil).blurb, "a · b")
        XCTAssertEqual(ReleaseFacts(app(source: "codeberg"), nil).blurb, "Codeberg")
    }

    func testPicturesComeFromTheProjectOrGitHubAndNeverInDemo() {
        XCTAssertEqual(ReleaseFacts(app(), nil).avatarURL()?.absoluteString, "https://github.com/fake.png?size=96")
        XCTAssertNil(ReleaseFacts(app(source: "codeberg"), nil).avatarURL())
        XCTAssertNil(ReleaseFacts(app(), nil).avatarURL(allowed: false))
        var e = info(); var m = RepoMeta(); m.avatarUrl = "https://avatars.githubusercontent.com/u/1"; e.meta = m
        XCTAssertEqual(ReleaseFacts(app(), e).avatarURL()?.absoluteString, "https://avatars.githubusercontent.com/u/1")
    }

    @MainActor func testOnlyKnownPictureSitesAreFetched() {
        XCTAssertTrue(AvatarCache.isAllowed(URL(string: "https://avatars.githubusercontent.com/u/1")!))
        XCTAssertTrue(AvatarCache.isAllowed(URL(string: "https://github.com/x.png")!))
        XCTAssertTrue(AvatarCache.isAllowed(URL(string: "https://secure.gravatar.com/avatar/1")!))
        XCTAssertFalse(AvatarCache.isAllowed(URL(string: "http://github.com/x.png")!), "https only")
        XCTAssertFalse(AvatarCache.isAllowed(URL(string: "https://evil.example.test/x.png")!))
        XCTAssertFalse(AvatarCache.isAllowed(URL(string: "https://github.com.evil.test/x.png")!))
        XCTAssertFalse(AvatarCache.isAllowed(URL(string: "https://notgithub.com/x.png")!))
    }
}
