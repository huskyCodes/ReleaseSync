import XCTest
@testable import ReleaseSyncKit

final class LibraryEditsTests: XCTestCase {
    let t0 = Timestamp(text: "2026-01-01T12:00:00Z")!
    func at(_ minutes: Int64) -> Timestamp { t0 + minutes * 60 * Timestamp.ticksPerSecond }

    func app(_ repo: String, modified: Timestamp? = nil) -> SyncApp {
        var a = SyncApp(); a.owner = "fake"; a.repo = repo; a.key = "github:fake/\(repo)"; a.addedAt = t0; a.modifiedAt = modified ?? t0
        return a
    }
    func doc(_ apps: [SyncApp]) -> SyncDoc { var d = SyncDoc(); d.apps = apps; return d }

    /// What another device does with the edit: merge the old copy it still has with the edited one.
    func merged(old: SyncDoc, edited: SyncDoc) -> SyncDoc { SyncMerger.merge(old, edited, now: at(100)) }

    func testAnEditWinsTheMergeAndNothingChangedChangesNothing() {
        let old = doc([app("a")])
        var d = old
        XCTAssertTrue(LibraryEdits.setFavorite("github:fake/a", true, in: &d, now: at(5)))
        XCTAssertEqual(d.apps[0].modifiedAt, at(5))
        XCTAssertTrue(merged(old: old, edited: d).apps[0].pinned)
        XCTAssertFalse(LibraryEdits.setFavorite("github:fake/a", true, in: &d, now: at(6)), "already a favorite")
        XCTAssertEqual(d.apps[0].modifiedAt, at(5), "no change, no new time")
        XCTAssertFalse(LibraryEdits.setFavorite("github:fake/missing", true, in: &d, now: at(6)))
    }

    func testAClockBehindTheOtherDevicesStillWins() {
        let old = doc([app("a", modified: at(50))])
        var d = old
        XCTAssertTrue(LibraryEdits.setHidden("github:fake/a", true, in: &d, now: at(10)))
        XCTAssertGreaterThan(d.apps[0].modifiedAt, at(50))
        XCTAssertTrue(merged(old: old, edited: d).apps[0].hidden)
    }

    func testNotesAreTrimmedAndBlankRemovesThem() {
        var d = doc([app("a")])
        XCTAssertTrue(LibraryEdits.setNote("github:fake/a", "  hello \n", in: &d, now: at(1)))
        XCTAssertEqual(d.apps[0].note, "hello")
        XCTAssertTrue(LibraryEdits.setNote("github:fake/a", "   ", in: &d, now: at(2)))
        XCTAssertNil(d.apps[0].note)
    }

    func testGroupsAreMadeOnDemandAndMatchedIgnoringCase() {
        var d = doc([app("a"), app("b")])
        XCTAssertTrue(LibraryEdits.setGroup("github:fake/a", " Media ", in: &d, now: at(1)))
        XCTAssertEqual(d.customGroups, ["Media"])
        XCTAssertEqual(d.groupStamps["Media"], at(1))
        XCTAssertEqual(d.apps[0].group, "Media")
        XCTAssertTrue(LibraryEdits.setGroup("github:fake/b", "media", in: &d, now: at(2)))
        XCTAssertEqual(d.customGroups, ["Media"], "no second group that differs only in case")
        XCTAssertEqual(d.apps[1].group, "Media", "uses the spelling of the existing group")
        XCTAssertTrue(LibraryEdits.setGroup("github:fake/b", nil, in: &d, now: at(3)))
        XCTAssertNil(d.apps[1].group)
    }

    func testDeletingAGroupLeavesTheAppsUngroupedAndATombstone() {
        var d = doc([app("a"), app("b")])
        _ = LibraryEdits.setGroup("github:fake/a", "Games", in: &d, now: at(1))
        let old = d
        XCTAssertTrue(LibraryEdits.removeGroup("games", in: &d, now: at(9)))
        XCTAssertTrue(d.customGroups.isEmpty)
        XCTAssertNil(d.groupStamps["Games"])
        XCTAssertNil(d.apps[0].group)
        XCTAssertEqual(d.tombstones["group:games"], at(9))
        let other = merged(old: old, edited: d)
        XCTAssertTrue(other.customGroups.isEmpty, "the other device's copy doesn't bring the group back")
        XCTAssertNil(other.apps[0].group)
        XCTAssertFalse(LibraryEdits.removeGroup("games", in: &d, now: at(10)))
        _ = LibraryEdits.addGroup("Games", in: &d, now: at(20))
        XCTAssertNil(d.tombstones["group:games"], "making the group again forgets its deletion")
    }

    func testARemovedAppStaysRemovedWhenAnOldCopyMerges() {
        let old = doc([app("a"), app("b")])
        var d = old
        XCTAssertTrue(LibraryEdits.removeApp("github:fake/a", in: &d, now: at(5)))
        XCTAssertEqual(d.tombstones["github:fake/a"], at(5))
        XCTAssertEqual(merged(old: old, edited: d).apps.map(\.repo), ["b"])
        XCTAssertFalse(LibraryEdits.removeApp("github:fake/a", in: &d, now: at(6)))
    }

    func testRemovingATryLaterItemLeavesItsTombstone() {
        var item = TryLaterItem(); item.owner = "fake"; item.repo = "later-one"; item.savedAt = t0
        var old = SyncDoc(); old.tryLater = [item]
        var d = old
        XCTAssertTrue(LibraryEdits.removeTryLater(item.key, in: &d, now: at(5)))
        XCTAssertEqual(d.tombstones["later:" + item.key], at(5))
        XCTAssertTrue(merged(old: old, edited: d).tryLater.isEmpty)
    }
}
