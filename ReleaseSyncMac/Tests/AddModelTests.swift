import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

@MainActor
final class AddModelTests: XCTestCase {
    func send(asked: Asked = Asked()) -> HTTPSend {
        { r in
            asked.add(r.url!.path)
            let headers = ["x-ratelimit-remaining": "4000", "x-ratelimit-limit": "5000"]
            switch r.url!.path {
            case "/repos/fake/cool": return reply(200, #"{"full_name":"fake/cool","name":"cool","owner":{"login":"fake"},"description":"A cool tool","stargazers_count":500,"language":"Swift"}"#, headers: headers)
            case "/repos/fake/cool/releases":
                return reply(200, #"[{"tag_name":"v1.0","published_at":"2026-01-01T00:00:00Z","assets":[{"name":"Cool-1.0-universal.dmg","size":1000,"browser_download_url":"https://e.test/Cool-1.0-universal.dmg"},{"name":"Cool-1.0-setup.exe","browser_download_url":"https://e.test/Cool-1.0-setup.exe"}]}]"#, headers: headers)
            case "/repos/fake/winonly": return reply(200, #"{"full_name":"fake/winonly","name":"winonly","owner":{"login":"fake"},"stargazers_count":3}"#, headers: headers)
            case "/repos/fake/winonly/releases": return reply(200, #"[{"tag_name":"v1","assets":[{"name":"W.exe","browser_download_url":"https://e.test/W.exe"}]}]"#, headers: headers)
            case "/search/repositories": return reply(200, #"{"items":[{"full_name":"fake/cool","name":"cool","owner":{"login":"fake"},"stargazers_count":500}]}"#, headers: ["x-ratelimit-resource": "search"])
            default: return reply(404)
            }
        }
    }

    struct Rig { let add: AddModel; let library: LibraryModel; let transport: MemoryTransport; let allowed: Allowed }
    final class Allowed: @unchecked Sendable { var value = true }

    func rig(allowed: Bool = true, asked: Asked = Asked()) async throws -> Rig {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        var doc = LibraryModelTests.library(); doc.apps = []; doc.tryLater = []
        let transport = MemoryTransport(text: doc.text())
        let client = SourceClient(token: "fake-token", send: send(asked: asked), sleep: { _ in })
        let library = LibraryModel(secrets: secrets, makeTransport: { _ in transport }, client: client)
        library.thisDevice = SyncDevice(id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name: "Example Mac", platform: "Mac", appVersion: "0.1")
        let gate = Allowed(); gate.value = allowed
        library.editingAllowed = { gate.value }
        await library.refresh()
        let log = ActivityLog(), toasts = Toasts()
        let install = InstallModel(showing: [:], log: log, toasts: toasts)
        let add = AddModel(library: library, install: install, toasts: toasts, log: log, allowChanges: { gate.value = true })
        return Rig(add: add, library: library, transport: transport, allowed: gate)
    }

    func testALinkShowsWhatTheProjectOffersForTheMac() async throws {
        let r = try await rig()
        await r.add.open("https://github.com/fake/cool")
        guard case .preview(let p) = r.add.stage else { return XCTFail("\(r.add.stage)") }
        XCTAssertEqual(p.info.fullName, "fake/cool"); XCTAssertEqual(p.latest?.tag, "v1.0")
        XCTAssertEqual(p.chosen?.name, "Cool-1.0-universal.dmg"); XCTAssertEqual(p.choices.map(\.name), ["Cool-1.0-universal.dmg"])
        XCTAssertFalse(p.inLibrary); XCTAssertTrue(r.add.installNow)
    }

    func testAProjectWithNothingForTheMacCanStillBeAddedButNotInstalled() async throws {
        let r = try await rig()
        await r.add.open("fake/winonly")
        guard case .preview(let p) = r.add.stage else { return XCTFail() }
        XCTAssertNil(p.chosen); XCTAssertFalse(r.add.installNow)
    }

    func testAddingSavesItToTheLibraryAndTheFile() async throws {
        let r = try await rig()
        await r.add.open("fake/cool")
        r.add.installNow = false
        await r.add.add()
        XCTAssertEqual(r.add.stage, .closed)
        XCTAssertEqual(r.library.apps.map(\.key), ["github:fake/cool"])
        let text = await r.transport.text
        XCTAssertTrue(try SyncDoc(text: try XCTUnwrap(text)).apps.contains { $0.key == "github:fake/cool" })
        XCTAssertEqual(r.library.releases["github:fake/cool"]?.latestTag, "v1.0", "its releases were looked at straight away")
    }

    func testChangesOffAsksFirstAndThenGoesOn() async throws {
        let r = try await rig(allowed: false)
        await r.add.open("fake/cool")
        r.add.installNow = false
        await r.add.add()
        XCTAssertEqual(r.add.stage, .needsPermission)
        XCTAssertTrue(r.library.apps.isEmpty, "nothing changed before the answer")
        await r.add.allowChangesAndContinue()
        XCTAssertTrue(r.allowed.value)
        XCTAssertEqual(r.library.apps.map(\.key), ["github:fake/cool"])
    }

    func testWordsSearchAndAResultOpensItsPreview() async throws {
        let r = try await rig()
        await r.add.open("cool tool")
        guard case .results(let q, let found) = r.add.stage else { return XCTFail("\(r.add.stage)") }
        XCTAssertEqual(q, "cool tool"); XCTAssertEqual(found.map(\.fullName), ["fake/cool"])
        await r.add.open(repo: found[0])
        guard case .preview = r.add.stage else { return XCTFail() }
    }

    func testAProjectThatIsAlreadyInTheLibraryIsSaidSoAndNotAddedTwice() async throws {
        let r = try await rig()
        await r.add.open("fake/cool"); r.add.installNow = false; await r.add.add()
        await r.add.open("fake/cool")
        guard case .preview(let p) = r.add.stage else { return XCTFail() }
        XCTAssertTrue(p.inLibrary)
        await r.add.add()
        XCTAssertEqual(r.library.apps.count, 1)
    }

    func testAMissingProjectGivesAPlainMessage() async throws {
        let r = try await rig()
        await r.add.open("fake/nothing-here")
        guard case .failed(let m) = r.add.stage else { return XCTFail() }
        XCTAssertFalse(m.isEmpty)
    }

    func testSavingForLaterNeedsTheSameYes() async throws {
        let r = try await rig(allowed: false)
        var info = RepoInfo(); info.source = "github"; info.ownerLogin = "fake"; info.name = "cool"
        await r.add.saveForLater(info)
        XCTAssertEqual(r.add.stage, .needsPermission)
        await r.add.allowChangesAndContinue()
        XCTAssertEqual(r.library.tryLater.map(\.repo), ["cool"])
    }

    func testTheChosenFileIsRememberedAsAPatternThatSurvivesNewVersions() {
        let p = AddModel.assetPattern(for: "Tool-1.2.3-arm64.dmg")
        let rx = try! NSRegularExpression(pattern: p, options: .caseInsensitive)
        func hit(_ s: String) -> Bool { rx.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)) != nil }
        XCTAssertTrue(hit("Tool-1.2.3-arm64.dmg")); XCTAssertTrue(hit("Tool-2.0-arm64.dmg")); XCTAssertTrue(hit("Tool-10.11.12-arm64.dmg"))
        XCTAssertFalse(hit("Tool-2.0-x64.dmg")); XCTAssertFalse(hit("Tool-2.0-arm64.pkg")); XCTAssertFalse(hit("MyTool-2.0-arm64.dmg"))
    }
}
