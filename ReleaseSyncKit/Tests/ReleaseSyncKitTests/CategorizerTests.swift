import XCTest
@testable import ReleaseSyncKit

final class CategorizerTests: XCTestCase {
    func testEveryProjectLandsWhereTheEngineSaidItShould() throws {
        let cases = try Fixtures.json("categorize.json").list("Cases")
        XCTAssertGreaterThan(cases.count, 150)
        var kinds = Set<String>()
        for c in cases {
            let name = c.str("Name") ?? "", expected = try XCTUnwrap(c.str("Category"))
            let topics: [String]? = c.present("Topics").map { _ in c.list("Topics").compactMap { if case .string(let s) = $0 { return s } else { return nil } } }
            let got = Categorizer.categorize(name: name, description: c.str("Description"), topics: topics, language: c.str("Language"))
            XCTAssertEqual(got, expected, "\(name.debugDescription) / \(c.str("Description") ?? "-") / \(topics ?? [])")
            kinds.insert(expected)
        }
        XCTAssertEqual(kinds.count, Categorizer.allCategories.count, "the cases reach every category")
    }

    func testCategoryNamesAndOrder() {
        XCTAssertEqual(Categorizer.allCategories.last, "Other")
        XCTAssertEqual(Categorizer.allCategories.first, "Games & Emulation")
        XCTAssertEqual(Categorizer.allCategories.count, 11)
    }
}
