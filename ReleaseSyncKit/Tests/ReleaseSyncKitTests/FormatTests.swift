import XCTest
@testable import ReleaseSyncKit

final class FormatTests: XCTestCase {
    func testEscapingMatchesTheEngineExactly() throws {
        let v = try Fixtures.json("escapes.json")
        let strings = v.list("Strings")
        XCTAssertFalse(strings.isEmpty)
        for s in strings {
            let text = try XCTUnwrap(s.str("Text")), expected = try XCTUnwrap(s.str("Json"))
            XCTAssertEqual(JSONWriter.quote(text), expected, "for \(text.debugDescription)")
        }
    }

    func testStringsReadBackFromTheirEscapes() throws {
        for s in try Fixtures.json("escapes.json").list("Strings") {
            let text = try XCTUnwrap(s.str("Text")), json = try XCTUnwrap(s.str("Json"))
            XCTAssertEqual(try JSONParser.parse(json), .string(text))
        }
    }

    func testTimestampsAreWrittenTheSameWay() throws {
        let v = try Fixtures.json("timestamps.json")
        for w in v.list("Write") {
            let t = Timestamp(unixSeconds: try XCTUnwrap(w.whole("UnixSeconds")), subTicks: try XCTUnwrap(w.whole("Ticks")))
            XCTAssertEqual(t.text, w.str("Text"))
        }
    }

    func testTimestampsAreReadTheSameWay() throws {
        let v = try Fixtures.json("timestamps.json")
        for r in v.list("Read") {
            let text = try XCTUnwrap(r.str("Text"))
            let t = try XCTUnwrap(Timestamp(text: text), text)
            XCTAssertEqual(t, Timestamp(unixSeconds: try XCTUnwrap(r.whole("UnixSeconds")), subTicks: try XCTUnwrap(r.whole("Ticks"))), text)
            XCTAssertEqual(t.text, r.str("Canonical"), text)
        }
    }

    func testTimestampsKeepEveryTick() {
        let a = Timestamp(text: "2026-01-01T12:00:00.0000001Z")!, b = Timestamp(text: "2026-01-01T12:00:00Z")!
        XCTAssertEqual(a.ticks - b.ticks, 1)
        XCTAssertTrue(a > b)
        XCTAssertEqual(Timestamp(text: "0001-01-01T00:00:00Z"), Timestamp.zero)
    }

    func testBadTimestampsAreRefused() {
        for bad in ["", "2026-01-01", "2026-13-01T00:00:00Z", "2026-02-30T00:00:00Z", "2026-01-01T24:00:00Z", "2026-01-01T12:00:00.Z", "2026-01-01T12:00:00Zjunk"] {
            XCTAssertNil(Timestamp(text: bad), bad)
        }
    }

    func testEveryItemInTheFixturesIsWrittenExactlyAsTheEngineWroteIt() throws {
        // each app, Try later item and device, parsed and written again, gives the same text (the merge's tie-break compares this text)
        var checked = 0
        for dir in try Fixtures.folders("merge") {
            for file in ["a.json", "b.json", "expected.json"] {
                let root = try Fixtures.json("merge/\(dir)/\(file)")
                for m in root.list("Apps") { XCTAssertEqual(JSONWriter.write(try SyncApp(json: m).json), JSONWriter.write(m), dir); checked += 1 }
                for m in root.list("TryLater") { XCTAssertEqual(JSONWriter.write(try TryLaterItem(json: m).json), JSONWriter.write(m), dir); checked += 1 }
                if case .object(let devices)? = root.member("Devices") {
                    for d in devices { XCTAssertEqual(JSONWriter.write(try SyncDevice(json: d.value).json), JSONWriter.write(d.value), dir); checked += 1 }
                }
            }
        }
        XCTAssertGreaterThan(checked, 100)
    }

    func testTheParserIsStrict() {
        for bad in ["", "{", "{\"a\":}", "[1,]", "{\"a\" 1}", "\"unterminated", "nul", "{\"a\":1} x", "\"bad \\q escape\""] {
            XCTAssertThrowsError(try JSONParser.parse(bad), bad)
        }
        XCTAssertNoThrow(try JSONParser.parse("\u{FEFF}{\"a\": [1, 2.5e3, -4, true, false, null, \"\\u00e9\"]}"))
    }

    func testRefusesToReadADeeplyNestedFile() {
        XCTAssertThrowsError(try JSONParser.parse(String(repeating: "[", count: 200) + String(repeating: "]", count: 200)))
    }

    func testDocumentsReadLeniently() throws {
        // names in any case, unknown properties ignored, numbers accepted for the mode, missing things get defaults
        let doc = try SyncDoc(text: #"{"version":2,"apps":[{"KEY":"k","mode":1,"addedat":"2026-01-01T14:00:00+02:00","future":5}],"extra":{}}"#)
        XCTAssertEqual(doc.apps.first?.mode, .installer)
        XCTAssertEqual(doc.apps.first?.addedAt, Timestamp(text: "2026-01-01T12:00:00Z"))
        XCTAssertEqual(doc.apps.first?.source, "github")
    }

    func testUnreadableDocumentsGiveTheFriendlyMessage() {
        for bad in ["not json", "[]", #"{"Apps": 5}"#, #"{"Apps":[{"Pinned":"yes"}]}"#, #"{"Tombstones":{"k":"not a time"}}"#] {
            XCTAssertThrowsError(try SyncDoc(text: bad), bad) { XCTAssertEqual($0 as? SyncError, SyncError.unreadable) }
        }
    }
}
