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
    private static func regex(_ pattern: String, _ options: NSRegularExpression.Options = []) -> NSRegularExpression { try! NSRegularExpression(pattern: pattern, options: options) }
    private static let firstNumber = regex(#"\d+(?:\.\d+)*"#)
    private static let anyNumber = regex(#"\d+"#)
    private static let preRelease = regex("(alpha|beta|rc|preview|pre|dev|nightly|canary|snapshot)", .caseInsensitive)
    private static let dotted = regex(#"^(\D*\d+(\.\d+)+|[vV]?\d+\b)"#)
    private static let mentioned = regex(#"(?<![\w.])v?\d+(?:\.\d+)+(?:-[\w.]+)?"#)
    private static let oneOff = regex(#"^(branch|pr|feature|test|scratch)[-_/]"#, .caseInsensitive)

    private static func whole(_ v: String) -> NSRange { NSRange(v.startIndex..., in: v) }
    private static func matches(_ r: NSRegularExpression, _ v: String) -> Bool { r.firstMatch(in: v, range: whole(v)) != nil }

    private static func parts(_ v: String) -> [Int64] {
        guard let m = firstNumber.firstMatch(in: v, range: whole(v)), let r = Range(m.range, in: v) else { return [] }
        return v[r].split(separator: ".", omittingEmptySubsequences: false).map { Int64($0) ?? 0 }
    }

    private static func isPre(_ v: String) -> Bool { matches(preRelease, v) }

    /// A real version number like v1.2.3 or launcher@0.2.45, as opposed to a build label like build-83.
    private static func isDotted(_ v: String) -> Bool { matches(dotted, v) }

    /// All the numbers in a tag, in order.
    private static func numbers(_ v: String) -> [Int64] {
        anyNumber.matches(in: v, range: whole(v)).map { m in Range(m.range, in: v).flatMap { Int64(v[$0]) } ?? 0 }
    }

    /// A tag with its numbers blanked out, so build-75 and build-83 match but nx2-build-34 doesn't.
    private static func style(_ v: String) -> String {
        let t = invariantLower(clean(v))
        return anyNumber.stringByReplacingMatches(in: t, range: whole(t), withTemplate: "#")
    }

    private static func compare(_ a: [Int64], _ b: [Int64]) -> Int {
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x < y ? -1 : 1 }
        }
        return 0
    }

    /// The name without spaces and a leading v: "v1.2.3" becomes "1.2.3".
    public static func clean(_ v: String) -> String {
        var scalars = Substring(trimmed(v)).unicodeScalars
        while let f = scalars.first, f == "v" || f == "V" { scalars.removeFirst() }
        return String(scalars)
    }

    /// Throwaway builds of a developer's work in progress, which aren't what most people mean by the latest version.
    public static func isOneOff(_ tag: String) -> Bool { matches(oneOff, tag) }

    /// The release tag a version number seen on the Mac belongs to, like "2.0.11" for tag v2.0.11, or nil. A release is preferred over its
    /// pre-releases, and a bare "1.0.0" only matches a release really called that.
    public static func matchTag(_ seen: String?, in tags: [String]) -> String? {
        guard let seen, !trimmed(seen).isEmpty else { return nil }
        func trim(_ p: [Int64]) -> [Int64] { var p = p; while p.count > 1 && p.last == 0 { p.removeLast() }; return p }
        let raw = parts(seen)
        guard raw.count >= 2 else { return nil }
        let want = trim(raw)
        let list = tags.filter { !$0.isEmpty }
        if let exact = list.first(where: { invariantLower(clean($0)) == invariantLower(clean(seen)) }) { return exact }
        let candidates = list.filter { isDotted($0) && trim(parts($0)) == want }
        return candidates.first { !(isPre($0) || $0.contains("-")) } ?? candidates.first
    }

    /// Every release tag mentioned in a piece of text, like an app's own version file ("version=v2.0.11").
    public static func tagsIn(_ text: String, tags: [String]) -> [String] {
        var found: [String] = []
        for m in mentioned.matches(in: text, range: whole(text)) {
            guard let r = Range(m.range, in: text), let tag = matchTag(String(text[r]), in: tags), !found.contains(tag) else { continue }
            found.append(tag)
        }
        return found
    }

    /// Whether `latest` counts as a newer release than the installed one. Numbers only mean something between tags written the same way:
    /// across styles (build-83, then nx2-build-90) the publish dates decide, and with no dates any difference counts as an update.
    public static func isNewer(_ latest: String, than installed: String, latestDate: Timestamp? = nil, installedDate: Timestamp? = nil) -> Bool {
        if clean(latest) == clean(installed) { return false }
        let order: Int
        if isDotted(latest) && isDotted(installed) { order = compare(parts(latest), parts(installed)) }
        else if style(latest) == style(installed) { order = compare(numbers(latest), numbers(installed)) }
        else if let a = latestDate, let b = installedDate { return a > b }
        else if isDotted(latest) != isDotted(installed) { return isDotted(latest) }       // a real version number outranks a label
        else { return true }
        return order != 0 ? order > 0 : isPre(installed) && !isPre(latest)      // numerically equal: a release replacing a pre-release is newer
    }
}
