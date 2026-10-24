import Foundation

// String rules the sync format depends on. Swift's own `<` and `==` on String follow Unicode rules; the format
// (like the C# engine) compares plain UTF-16 code units, so the comparisons here are spelled out.

/// Plain character-by-character comparison of UTF-16 code units (.NET's `StringComparison.Ordinal`).
func ordinalCompare(_ a: String, _ b: String) -> Int {
    var i = a.utf16.makeIterator(), j = b.utf16.makeIterator()
    while true {
        switch (i.next(), j.next()) {
        case (nil, nil): return 0
        case (nil, _): return -1
        case (_, nil): return 1
        case let (x?, y?): if x != y { return x < y ? -1 : 1 }
        }
    }
}

/// One character mapped to one character (like .NET's invariant culture); characters that would grow are left alone.
private func mapScalars(_ s: String, _ map: (Unicode.Scalar) -> String) -> String {
    var out = String.UnicodeScalarView()
    for u in s.unicodeScalars {
        let m = map(u).unicodeScalars
        if m.count == 1 { out.append(m.first!) } else { out.append(u) }
    }
    return String(out)
}

func invariantLower(_ s: String) -> String { mapScalars(s) { $0.properties.lowercaseMapping } }
func invariantUpper(_ s: String) -> String { mapScalars(s) { $0.properties.uppercaseMapping } }

/// Ordering used for group names: ordinal, ignoring case (.NET's `OrdinalIgnoreCase`, which compares upper-cased text).
func ordinalIgnoreCaseCompare(_ a: String, _ b: String) -> Int { ordinalCompare(invariantUpper(a), invariantUpper(b)) }

func isBlank(_ s: String?) -> Bool { s == nil || s!.unicodeScalars.allSatisfy { $0.properties.isWhitespace } }

func trimmed(_ s: String) -> String {
    var scalars = Substring(s).unicodeScalars
    while let f = scalars.first, f.properties.isWhitespace { scalars.removeFirst() }
    while let l = scalars.last, l.properties.isWhitespace { scalars.removeLast() }
    return String(scalars)
}

extension Sequence {
    func sortedOrdinal(by key: (Element) -> String) -> [Element] {
        sorted { ordinalCompare(key($0), key($1)) < 0 }
    }
}
