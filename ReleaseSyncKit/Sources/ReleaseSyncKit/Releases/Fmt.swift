import Foundation

/// Text helpers shared with the Windows app (`Fmt` and `VersionUtil` in ReleaseSync.Core): counts, sizes, release notes as plain text,
/// and which of two version names is newer.
public enum Fmt {
    /// One decimal place, with a trailing ".0" left off (the .NET format "0.#"). Like .NET, it first keeps 15 significant digits and then
    /// rounds halves away from zero, so 1.25 gives 1.3 where plain printf would give 1.2.
    static func oneDecimal(_ v: Double) -> String {
        var d = Decimal(string: String(format: "%.15g", v)) ?? Decimal(v)
        var rounded = Decimal()
        NSDecimalRound(&rounded, &d, 1, .plain)
        return "\(rounded)"
    }

    /// 1200 becomes "1.2k", 3,400,000 becomes "3.4M".
    public static func count(_ n: Int) -> String {
        if n >= 1_000_000 { return oneDecimal(Double(n) / 1_000_000) + "M" }
        if n >= 1000 { return oneDecimal(Double(n) / 1000) + "k" }
        return String(n)
    }

    /// A file size such as "52.4 MB"; "—" when it isn't known.
    public static func size(_ bytes: Int64) -> String {
        if bytes <= 0 { return "—" }
        let units = ["B", "KB", "MB", "GB"]
        var v = Double(bytes), i = 0
        while v >= 1024 && i < 3 { v /= 1024; i += 1 }
        return oneDecimal(v) + " " + units[i]
    }

    private static let rules: [(NSRegularExpression, String)] = {
        func rx(_ p: String, _ o: NSRegularExpression.Options = []) -> NSRegularExpression { try! NSRegularExpression(pattern: p, options: o) }
        return [
            (rx(#"!\[[^\]]*\]\([^)]*\)"#), ""),
            (rx(#"\[([^\]]*)\]\([^)]*\)"#), "$1"),
            (rx(#"<[^>]+>"#), ""),
            (rx(#"^\s{0,3}#{1,6}\s*"#, .anchorsMatchLines), ""),
            (rx(#"^\s*[-*+]\s+"#, .anchorsMatchLines), "•  "),
            (rx(#"^\s*[-*_]{3,}\s*$"#, .anchorsMatchLines), ""),
        ]
    }()
    private static let emphasis = try! NSRegularExpression(pattern: #"(?<![\w*])[_*]([^_*\n]+)[_*](?![\w*])"#)
    private static let trailingSpace = try! NSRegularExpression(pattern: #"[ \t]+\n"#)
    private static let blankRuns = try! NSRegularExpression(pattern: #"\n{3,}"#)

    private static func apply(_ rx: NSRegularExpression, _ template: String, to s: String) -> String {
        rx.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: template)
    }

    /// Release notes written in Markdown, as plain text for display. Cut at `max` characters with an ellipsis.
    public static func stripMarkdown(_ md: String?, max: Int = 2500) -> String {
        guard let md, !isBlank(md) else { return "" }
        var s = md.replacingOccurrences(of: "\r", with: "")
        for (rx, template) in rules { s = apply(rx, template, to: s) }
        for token in ["**", "__", "`", "~~"] { s = s.replacingOccurrences(of: token, with: "") }
        s = apply(emphasis, "$1", to: s)
        s = apply(trailingSpace, "\n", to: s)
        s = apply(blankRuns, "\n\n", to: s)
        s = trimmed(s)
        // the cut is by UTF-16 units, as in the Windows app
        if s.utf16.count > max {
            let cut = String(decoding: Array(s.utf16.prefix(max)), as: UTF16.self)
            return trimmedEnd(cut) + "…"
        }
        return s
    }

    private static func trimmedEnd(_ s: String) -> String {
        var scalars = Substring(s).unicodeScalars
        while let l = scalars.last, l.properties.isWhitespace { scalars.removeLast() }
        return String(scalars)
    }
}

/// Which release name is newer. Same rules as `VersionUtil` in ReleaseSync.Core, so an update is flagged on every device or on none.
public enum VersionUtil {
    private static let firstNumber = try! NSRegularExpression(pattern: #"\d+(?:\.\d+)*"#)
    private static let preRelease = try! NSRegularExpression(pattern: "(alpha|beta|rc|preview|pre|dev|nightly|canary|snapshot)", options: .caseInsensitive)

    private static func parts(_ v: String) -> [Int64] {
        guard let m = firstNumber.firstMatch(in: v, range: NSRange(v.startIndex..., in: v)), let r = Range(m.range, in: v) else { return [] }
        return v[r].split(separator: ".", omittingEmptySubsequences: false).map { Int64($0) ?? 0 }
    }

    private static func isPre(_ v: String) -> Bool { preRelease.firstMatch(in: v, range: NSRange(v.startIndex..., in: v)) != nil }

    /// The name without spaces and a leading v: "v1.2.3" becomes "1.2.3".
    public static func clean(_ v: String) -> String {
        var scalars = Substring(trimmed(v)).unicodeScalars
        while let f = scalars.first, f == "v" || f == "V" { scalars.removeFirst() }
        return String(scalars)
    }

    /// Whether `latest` counts as a newer release than the installed one.
    public static func isNewer(_ latest: String, than installed: String) -> Bool {
        if clean(latest) == clean(installed) { return false }
        let a = parts(latest), b = parts(installed)
        if a.isEmpty || b.isEmpty { return true }      // unparsable: any difference counts as an update
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return isPre(installed) && !isPre(latest)      // numerically equal: a release replacing a pre-release is newer
    }
}
