import Foundation

/// A parsed JSON value that keeps the order of an object's members, which the sync format's text rules need.
public enum JSONValue: Equatable, Sendable {
    case null
    case bool(Bool)
    case number(String)          // the number as written
    case string(String)
    case array([JSONValue])
    case object([JSONMember])

    public static func int(_ v: Int) -> JSONValue { .number(String(v)) }
}

public struct JSONMember: Equatable, Sendable {
    public var key: String
    public var value: JSONValue
    public init(_ key: String, _ value: JSONValue) { self.key = key; self.value = value }
}

public struct JSONError: Error, Equatable, Sendable {
    public var message: String
    init(_ message: String) { self.message = message }
}

// MARK: reading

public enum JSONParser {
    static let maxDepth = 64

    public static func parse(_ text: String) throws -> JSONValue {
        var p = Parser(bytes: Array(text.utf8))
        p.skipSpace()
        let v = try p.value(depth: 0)
        p.skipSpace()
        guard p.i == p.bytes.count else { throw JSONError("Unexpected text after the value.") }
        return v
    }

    private struct Parser {
        let bytes: [UInt8]
        var i = 0
        init(bytes: [UInt8]) {
            // a byte-order mark in front is ignored
            self.bytes = bytes
            if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { i = 3 }
        }

        mutating func skipSpace() { while i < bytes.count, [32, 9, 10, 13].contains(bytes[i]) { i += 1 } }

        mutating func value(depth: Int) throws -> JSONValue {
            guard depth <= JSONParser.maxDepth else { throw JSONError("Too deeply nested.") }
            guard i < bytes.count else { throw JSONError("Unexpected end.") }
            switch bytes[i] {
            case UInt8(ascii: "{"): return try object(depth: depth)
            case UInt8(ascii: "["): return try array(depth: depth)
            case UInt8(ascii: "\""): return .string(try string())
            case UInt8(ascii: "t"): try word("true"); return .bool(true)
            case UInt8(ascii: "f"): try word("false"); return .bool(false)
            case UInt8(ascii: "n"): try word("null"); return .null
            default: return try number()
            }
        }

        mutating func word(_ w: String) throws {
            let u = Array(w.utf8)
            guard i + u.count <= bytes.count, Array(bytes[i..<(i + u.count)]) == u else { throw JSONError("Unexpected text.") }
            i += u.count
        }

        mutating func number() throws -> JSONValue {
            let start = i
            if i < bytes.count, bytes[i] == UInt8(ascii: "-") { i += 1 }
            let digitsStart = i
            while i < bytes.count, (48...57).contains(bytes[i]) { i += 1 }
            guard i > digitsStart else { throw JSONError("Unexpected text.") }
            if i < bytes.count, bytes[i] == UInt8(ascii: ".") {
                i += 1
                let f = i
                while i < bytes.count, (48...57).contains(bytes[i]) { i += 1 }
                guard i > f else { throw JSONError("Bad number.") }
            }
            if i < bytes.count, bytes[i] == UInt8(ascii: "e") || bytes[i] == UInt8(ascii: "E") {
                i += 1
                if i < bytes.count, bytes[i] == UInt8(ascii: "+") || bytes[i] == UInt8(ascii: "-") { i += 1 }
                let e = i
                while i < bytes.count, (48...57).contains(bytes[i]) { i += 1 }
                guard i > e else { throw JSONError("Bad number.") }
            }
            return .number(String(decoding: bytes[start..<i], as: UTF8.self))
        }

        mutating func hex4() throws -> UInt16 {
            guard i + 4 <= bytes.count else { throw JSONError("Bad escape.") }
            var v: UInt16 = 0
            for k in 0..<4 {
                let c = bytes[i + k]
                let d: UInt16
                switch c {
                case 48...57: d = UInt16(c - 48)
                case 65...70: d = UInt16(c - 55)
                case 97...102: d = UInt16(c - 87)
                default: throw JSONError("Bad escape.")
                }
                v = v * 16 + d
            }
            i += 4
            return v
        }

        mutating func string() throws -> String {
            i += 1   // opening quote
            var units: [UInt16] = []
            var run: [UInt8] = []
            func flush() { if !run.isEmpty { units.append(contentsOf: String(decoding: run, as: UTF8.self).utf16); run.removeAll() } }
            while true {
                guard i < bytes.count else { throw JSONError("Unterminated string.") }
                let c = bytes[i]
                if c == UInt8(ascii: "\"") { i += 1; break }
                if c < 0x20 { throw JSONError("Control character in string.") }
                if c == UInt8(ascii: "\\") {
                    flush()
                    i += 1
                    guard i < bytes.count else { throw JSONError("Bad escape.") }
                    let e = bytes[i]; i += 1
                    switch e {
                    case UInt8(ascii: "\""): units.append(0x22)
                    case UInt8(ascii: "\\"): units.append(0x5C)
                    case UInt8(ascii: "/"): units.append(0x2F)
                    case UInt8(ascii: "b"): units.append(8)
                    case UInt8(ascii: "f"): units.append(12)
                    case UInt8(ascii: "n"): units.append(10)
                    case UInt8(ascii: "r"): units.append(13)
                    case UInt8(ascii: "t"): units.append(9)
                    case UInt8(ascii: "u"): units.append(try hex4())
                    default: throw JSONError("Bad escape.")
                    }
                } else { run.append(c); i += 1 }
            }
            flush()
            return String(decoding: units, as: UTF16.self)
        }

        mutating func array(depth: Int) throws -> JSONValue {
            i += 1
            var items: [JSONValue] = []
            skipSpace()
            if i < bytes.count, bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
            while true {
                skipSpace()
                items.append(try value(depth: depth + 1))
                skipSpace()
                guard i < bytes.count else { throw JSONError("Unexpected end.") }
                if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                if bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
                throw JSONError("Expected , or ].")
            }
        }

        mutating func object(depth: Int) throws -> JSONValue {
            i += 1
            var members: [JSONMember] = []
            skipSpace()
            if i < bytes.count, bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
            while true {
                skipSpace()
                guard i < bytes.count, bytes[i] == UInt8(ascii: "\"") else { throw JSONError("Expected a name.") }
                let key = try string()
                skipSpace()
                guard i < bytes.count, bytes[i] == UInt8(ascii: ":") else { throw JSONError("Expected :.") }
                i += 1
                skipSpace()
                members.append(JSONMember(key, try value(depth: depth + 1)))
                skipSpace()
                guard i < bytes.count else { throw JSONError("Unexpected end.") }
                if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                if bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
                throw JSONError("Expected , or }.")
            }
        }
    }
}

// MARK: writing

/// Writes JSON text the way the C# engine does: members in the order given, two-space indentation, and every character
/// that .NET's default encoder escapes written as a `\uXXXX` escape with upper-case hex. The merge's tie-break compares
/// this text, so it has to match exactly.
public enum JSONWriter {
    public static func write(_ v: JSONValue, indented: Bool = true) -> String {
        var out = ""
        emit(v, level: 0, indented: indented, into: &out)
        return out
    }

    public static func quote(_ s: String) -> String {
        var out = "\""
        for u in s.utf16 {
            switch u {
            case 0x22, 0x26, 0x27, 0x2B, 0x3C, 0x3E, 0x60: out += escape(u)       //  "  &  '  +  <  >  `
            case 0x5C: out += "\\\\"
            case 0x08: out += "\\b"
            case 0x09: out += "\\t"
            case 0x0A: out += "\\n"
            case 0x0C: out += "\\f"
            case 0x0D: out += "\\r"
            case 0x20...0x7E: out.unicodeScalars.append(Unicode.Scalar(UInt8(u)))
            default: out += escape(u)                                              // other control characters, DEL, everything non-ASCII
            }
        }
        return out + "\""
    }

    private static func escape(_ u: UInt16) -> String {
        let h = String(u, radix: 16, uppercase: true)
        return "\\u" + String(repeating: "0", count: 4 - h.count) + h
    }

    private static func emit(_ v: JSONValue, level: Int, indented: Bool, into out: inout String) {
        switch v {
        case .null: out += "null"
        case .bool(let b): out += b ? "true" : "false"
        case .number(let n): out += n
        case .string(let s): out += quote(s)
        case .array(let items):
            if items.isEmpty { out += "[]"; return }
            out += "["
            for (n, item) in items.enumerated() {
                if n > 0 { out += "," }
                if indented { out += "\n" + String(repeating: "  ", count: level + 1) }
                emit(item, level: level + 1, indented: indented, into: &out)
            }
            if indented { out += "\n" + String(repeating: "  ", count: level) }
            out += "]"
        case .object(let members):
            if members.isEmpty { out += "{}"; return }
            out += "{"
            for (n, m) in members.enumerated() {
                if n > 0 { out += "," }
                if indented { out += "\n" + String(repeating: "  ", count: level + 1) }
                out += quote(m.key) + (indented ? ": " : ":")
                emit(m.value, level: level + 1, indented: indented, into: &out)
            }
            if indented { out += "\n" + String(repeating: "  ", count: level) }
            out += "}"
        }
    }
}

// MARK: reading helpers

extension JSONValue {
    /// The member with this name; names match in any case, like the C# reader.
    func member(_ name: String) -> JSONValue? {
        guard case .object(let members) = self else { return nil }
        let want = invariantLower(name)
        return members.last { invariantLower($0.key) == want }?.value
    }

    /// Present and not null, or nil.
    func present(_ name: String) -> JSONValue? {
        guard let v = member(name), v != .null else { return nil }
        return v
    }

    func string(_ name: String) throws -> String? {
        guard let v = present(name) else { return nil }
        guard case .string(let s) = v else { throw JSONError("\(name) should be text.") }
        return s
    }

    func bool(_ name: String) throws -> Bool? {
        guard let v = present(name) else { return nil }
        guard case .bool(let b) = v else { throw JSONError("\(name) should be true or false.") }
        return b
    }

    func int(_ name: String) throws -> Int? {
        guard let v = present(name) else { return nil }
        guard case .number(let n) = v, let i = Int(n) else { throw JSONError("\(name) should be a whole number.") }
        return i
    }

    func timestamp(_ name: String) throws -> Timestamp? {
        guard let s = try string(name) else { return nil }
        guard let t = Timestamp(text: s) else { throw JSONError("\(name) should be a date and time.") }
        return t
    }

    func array(_ name: String) throws -> [JSONValue]? {
        guard let v = present(name) else { return nil }
        guard case .array(let a) = v else { throw JSONError("\(name) should be a list.") }
        return a
    }

    func object(_ name: String) throws -> [JSONMember]? {
        guard let v = present(name) else { return nil }
        guard case .object(let m) = v else { throw JSONError("\(name) should be an object.") }
        return m
    }

    func stringList(_ name: String) throws -> [String]? {
        guard let a = try array(name) else { return nil }
        return try a.map { item in
            guard case .string(let s) = item else { throw JSONError("\(name) should be a list of text.") }
            return s
        }
    }
}

extension Timestamp {
    var json: JSONValue { .string(text) }
}
