import Foundation
import XCTest
@testable import ReleaseSyncKit

/// The golden files written by the C# engine (ReleaseSync.Core.Tests/Fixtures). Read straight from the repository.
enum Fixtures {
    static let root: URL = {
        var u = URL(fileURLWithPath: #filePath)
        for _ in 0..<4 { u.deleteLastPathComponent() }          // Fixtures.swift, ReleaseSyncKitTests, Tests, ReleaseSyncKit
        return u.appendingPathComponent("ReleaseSync.Core.Tests/Fixtures")
    }()

    static func text(_ path: String) throws -> String { try String(contentsOf: root.appendingPathComponent(path), encoding: .utf8) }
    static func json(_ path: String) throws -> JSONValue { try JSONParser.parse(try text(path)) }
    static func doc(_ path: String) throws -> SyncDoc { try SyncDoc(text: try text(path)) }

    static func folders(_ path: String) throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent(path).path)
            .filter { var isDir: ObjCBool = false; FileManager.default.fileExists(atPath: root.appendingPathComponent(path + "/" + $0).path, isDirectory: &isDir); return isDir.boolValue }
            .sorted()
    }
}

extension JSONValue {
    func str(_ name: String) -> String? { if case .string(let s)? = member(name) { return s }; return nil }
    func list(_ name: String) -> [JSONValue] { if case .array(let a)? = member(name) { return a }; return [] }
    func flag(_ name: String) -> Bool? { if case .bool(let b)? = member(name) { return b }; return nil }
    func whole(_ name: String) -> Int64? { if case .number(let n)? = member(name) { return Int64(n) }; return nil }
}

/// A document as data: group stamp names in lower case (the C# engine keeps whichever spelling it saw first).
func canonical(_ d: SyncDoc, ignoringWriter: Bool = false) -> SyncDoc {
    var c = d
    if ignoringWriter { c.writtenBy = "" }
    c.groupStamps = Dictionary(uniqueKeysWithValues: d.groupStamps.map { (invariantLower($0.key), $0.value) })
    return c
}
