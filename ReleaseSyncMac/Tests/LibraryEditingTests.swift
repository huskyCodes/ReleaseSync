import ReleaseSyncKit
import XCTest
@testable import ReleaseSync

/// A shared file that can be read but not written.
actor ReadOnlyTransport: SyncTransport {
    nonisolated var description: String { "read-only" }
    let text: String?
    init(text: String?) { self.text = text }
    func read() async throws -> String? { text }
    func write(_ t: String) async throws { throw SyncError(.general, "GitHub is not reachable.") }
}

@MainActor
final class LibraryEditingTests: XCTestCase {
    let mac = SyncDevice(id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name: "Example Mac", platform: "Mac", appVersion: "0.1")

    func library() -> SyncDoc {
        var d = LibraryModelTests.library()
        d.devices[mac.id] = SyncDevice(id: mac.id, name: mac.name, platform: "Mac", appVersion: "0.1", lastSeen: Timestamp.now(), online: true)     // already listed and fresh
        return d
    }

    func model(_ transport: SyncTransport, allowed: Bool) throws -> LibraryModel {
        let secrets = MemorySecrets(); try secrets.write(SecretTarget.gitHubToken, "fake-token")
        let m = LibraryModel(secrets: secrets, makeTransport: { _ in transport })
        m.thisDevice = mac
        m.editingAllowed = { allowed }
        return m
    }

    func testSwitchedOffNothingIsChangedOrWritten() async throws {
        let t = MemoryTransport(text: library().text())
        let m = try model(t, allowed: false)
        await m.refresh()
        XCTAssertFalse(m.canEdit)
        let problem = await m.edit { LibraryEdits.setFavorite("github:fake/one", true, in: &$0, now: $1) }
        XCTAssertNotNil(problem)
        XCTAssertFalse(m.apps[0].pinned)
        let writes = await t.writes
        XCTAssertEqual(writes, 0)
    }

    func testAnEditIsSavedAndShownAndThisMacIsListedOnline() async throws {
        var start = library(); start.devices[mac.id] = nil
        let t = MemoryTransport(text: start.text())
        let m = try model(t, allowed: true)
        await m.refresh()
        let afterRefreshWrites = await t.writes
        XCTAssertEqual(afterRefreshWrites, 1, "this Mac is listed as soon as changes are allowed")
        let problem = await m.edit { LibraryEdits.setFavorite("github:fake/one", true, in: &$0, now: $1) }
        XCTAssertNil(problem)
        XCTAssertTrue(m.apps[0].pinned)
        let text = await t.text
        let saved = try SyncDoc(text: try XCTUnwrap(text))
        XCTAssertTrue(saved.apps[0].pinned)
        XCTAssertEqual(saved.devices[mac.id]?.online, true)
        XCTAssertEqual(saved.devices[mac.id]?.platform, "Mac")
        XCTAssertEqual(saved.devices["d1"]?.name, "Example PC", "the other devices are kept")
        XCTAssertFalse(m.isSaving)
    }

    func testAnEditThatChangesNothingWritesNothing() async throws {
        let t = MemoryTransport(text: library().text())
        let m = try model(t, allowed: true)
        await m.refresh()
        let before = await t.writes
        let problem = await m.edit { LibraryEdits.setFavorite("github:fake/one", false, in: &$0, now: $1) }
        XCTAssertNil(problem)
        let after = await t.writes
        XCTAssertEqual(before, after)
    }

    func testAFreshEntryForThisMacIsNotRewrittenOnEveryRefresh() async throws {
        let t = MemoryTransport(text: library().text())
        let m = try model(t, allowed: true)
        await m.refresh(); await m.refresh()
        let writes = await t.writes
        XCTAssertEqual(writes, 0)
    }

    func testAFailedSaveGivesTheLibraryBackAsItWas() async throws {
        let m = try model(ReadOnlyTransport(text: library().text()), allowed: true)
        await m.refresh()
        let problem = await m.edit { LibraryEdits.setFavorite("github:fake/one", true, in: &$0, now: $1) }
        XCTAssertEqual(problem, "GitHub is not reachable.")
        XCTAssertFalse(m.apps[0].pinned, "the change did not stick")
        XCTAssertFalse(m.isSaving)
    }

    func testDemoNeverWrites() async {
        let m = LibraryModel(showing: LibraryModelTests.library(), secrets: MemorySecrets())
        m.editingAllowed = { true }
        XCTAssertFalse(m.canEdit)
    }

    func testTheDeviceIdIsMadeOnceAndKept() {
        let defaults = UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)!
        let a = DeviceIdentity.make(defaults: defaults, name: "  My Mac ")
        let b = DeviceIdentity.make(defaults: defaults, name: nil)
        XCTAssertEqual(a.id, b.id)
        XCTAssertEqual(a.id.count, 32)
        XCTAssertEqual(a.name, "My Mac")
        XCTAssertEqual(b.name, "This Mac")
        XCTAssertEqual(a.platform, "Mac")
    }

    func testChangesAreOffUntilThePersonTurnsThemOn() {
        let defaults = UserDefaults(suiteName: "ReleaseSyncTests-" + UUID().uuidString)!
        XCTAssertFalse(ViewPrefs(defaults: defaults).allowLibraryChanges)
        let p = ViewPrefs(defaults: defaults); p.allowLibraryChanges = true
        XCTAssertTrue(ViewPrefs(defaults: defaults).allowLibraryChanges)
    }
}
