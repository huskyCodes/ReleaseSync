import XCTest
@testable import ReleaseSyncKit

final class MergeFixtureTests: XCTestCase {
    func testMergeGivesWhatTheEngineGave() throws {
        let cases = try Fixtures.folders("merge")
        XCTAssertGreaterThanOrEqual(cases.count, 13)
        for name in cases {
            let a = try Fixtures.doc("merge/\(name)/a.json"), b = try Fixtures.doc("merge/\(name)/b.json")
            let expected = try Fixtures.doc("merge/\(name)/expected.json")
            let now = try XCTUnwrap(Timestamp(text: try XCTUnwrap(try Fixtures.json("merge/\(name)/case.json").str("Now"))))
            XCTAssertEqual(canonical(SyncMerger.merge(a, b, now: now)), canonical(expected), name)
        }
    }

    func testMergeIsSymmetricAndRepeatable() throws {
        for name in try Fixtures.folders("merge") {
            let a = try Fixtures.doc("merge/\(name)/a.json"), b = try Fixtures.doc("merge/\(name)/b.json")
            let now = try XCTUnwrap(Timestamp(text: try XCTUnwrap(try Fixtures.json("merge/\(name)/case.json").str("Now"))))
            let ab = SyncMerger.merge(a, b, now: now), ba = SyncMerger.merge(b, a, now: now)
            XCTAssertEqual(canonical(ab, ignoringWriter: true), canonical(ba, ignoringWriter: true), "\(name): a+b vs b+a")
            XCTAssertEqual(canonical(SyncMerger.merge(ab, ab, now: now), ignoringWriter: true), canonical(ab, ignoringWriter: true), "\(name): result with itself")
            if !name.hasPrefix("12-") {      // a forgotten tombstone lets an old copy of a deleted item come back
                XCTAssertEqual(canonical(SyncMerger.merge(ab, a, now: now), ignoringWriter: true), canonical(ab, ignoringWriter: true), "\(name): result with a")
                XCTAssertEqual(canonical(SyncMerger.merge(ab, b, now: now), ignoringWriter: true), canonical(ab, ignoringWriter: true), "\(name): result with b")
            }
        }
    }

    func testTieBreaksFollowTheEscapedText() throws {
        // 05: the escaped text decides, so \u00E9 loses to "z" although é sorts after z when written plainly
        let merged = SyncMerger.merge(try Fixtures.doc("merge/05-tie-break-escaping/a.json"), try Fixtures.doc("merge/05-tie-break-escaping/b.json"), now: Timestamp(text: "2026-01-01T13:00:00Z")!)
        let notes = Dictionary(uniqueKeysWithValues: merged.apps.map { ($0.repo, $0.note ?? "") })
        XCTAssertEqual(notes["esc-accent"], "z")
        XCTAssertEqual(notes["esc-plus"], "+")
        XCTAssertEqual(notes["esc-emoji"], "z")
        XCTAssertEqual(notes["esc-slash"], "a0b")
    }

    func testOneTickDecidesAnEdit() throws {
        let merged = SyncMerger.merge(try Fixtures.doc("merge/03-newer-edit-wins/a.json"), try Fixtures.doc("merge/03-newer-edit-wins/b.json"), now: Timestamp(text: "2026-01-01T13:00:00Z")!)
        XCTAssertEqual(merged.apps.first { $0.repo == "one-tick" }?.note, "from a")
    }

    func testKeysAreOrderedByCharacterCode() throws {
        let merged = SyncMerger.merge(try Fixtures.doc("merge/02-union-and-ordering/a.json"), try Fixtures.doc("merge/02-union-and-ordering/b.json"), now: Timestamp(text: "2026-01-01T12:10:00Z")!)
        XCTAssertEqual(merged.apps.map(\.key), ["codeberg:sample/site-app", "forgejo:git.example.test:sample/forge-app", "github:fake-owner/zeta-app",
                                                "github:fake/a-b", "github:fake/a.b", "github:fake/a1", "github:fake/a_b", "github:fake/ab", "gitlab:example/tool"])
    }

    func testWrittenDocumentsReadBackTheSame() throws {
        for name in try Fixtures.folders("merge") {
            for file in ["a.json", "b.json", "expected.json"] {
                let d = try Fixtures.doc("merge/\(name)/\(file)")
                XCTAssertEqual(try SyncDoc(text: d.text()), d, "\(name)/\(file)")
            }
        }
    }
}
