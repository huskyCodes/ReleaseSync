import Foundation

/// Sorts a project into a kind (Games & Emulation, Media & Entertainment…) from its name, description, topics and language, by scoring
/// words. Same words and same scoring as `Categorizer` in ReleaseSync.Core, so an app lands in the same group on every device.
public enum Categorizer {
    struct Rule {
        let name: String
        let strong: [String]
        let weak: [String]
        let nameBits: [String]
        var weight = 1.0
    }

    public static var allCategories: [String] { rules.map(\.name) + ["Other"] }

    public static func categorize(name: String, description: String? = nil, topics: [String]? = nil, language: String? = nil) -> String {
        let topicList = (topics ?? []).map(invariantLower)
        var topicSet = Set(topicList)
        for t in topicList { for part in t.split(separator: "-", omittingEmptySubsequences: false) { topicSet.insert(String(part)) } }
        let nameTokens = Set(tokens(camelSplit(name).map(invariantLowerChar), keeping: { $0.isASCIILowerOrDigit }))
        let nameNorm = String(String.UnicodeScalarView(invariantLower(name).unicodeScalars.filter(isLetterOrDigit)))
        let descLower = invariantLower(description ?? "")
        let descTokens = Set(tokens(Array(descLower), keeping: { $0.isASCIILowerOrDigit || $0 == "-" || $0 == "+" }))
        func phrase(_ term: String) -> Bool { (term.contains(" ") || term.contains("-")) && descLower.contains(term) }

        var best = "Other", bestScore = 0.0
        for r in rules {
            var s = 0.0
            for t in r.strong {
                if topicSet.contains(t) { s += 4 }
                if nameTokens.contains(t) { s += 3 }
                if descTokens.contains(t) || phrase(t) { s += 2 }
            }
            for t in r.weak {
                if topicSet.contains(t) { s += 2 }
                if nameTokens.contains(t) { s += 1.5 }
                if descTokens.contains(t) { s += 0.75 }
            }
            for b in r.nameBits where nameNorm.contains(b.replacingOccurrences(of: "-", with: "")) { s += 4 }
            s *= r.weight
            if s > bestScore + 0.01 { best = r.name; bestScore = s }
        }
        return bestScore >= 2 ? best : "Other"
    }

    // MARK: text helpers (plain ASCII rules, like the regular expressions in the C# version)

    /// Puts a space between a lower-case letter or digit and a capital that follows it: "RetroLauncher" becomes "Retro Launcher".
    private static func camelSplit(_ s: String) -> [Character] {
        var out: [Character] = []
        var previous: Character?
        for c in s {
            if let p = previous, p.isASCIILowerOrDigit, c.isASCIIUpper { out.append(" ") }
            out.append(c); previous = c
        }
        return out
    }

    private static func invariantLowerChar(_ c: Character) -> Character { Character(invariantLower(String(c))) }

    /// The runs of characters that pass `keeping`, as words.
    private static func tokens(_ chars: [Character], keeping: (Character) -> Bool) -> [String] {
        var words: [String] = [], current = ""
        for c in chars {
            if keeping(c) { current.append(c) } else if !current.isEmpty { words.append(current); current = "" }
        }
        if !current.isEmpty { words.append(current) }
        return words
    }

    private static func isLetterOrDigit(_ u: Unicode.Scalar) -> Bool {
        switch u.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter, .decimalNumber: return true
        default: return false
        }
    }
}

private extension Character {
    var isASCIIUpper: Bool { ("A"..."Z").contains(self) }
    var isASCIILowerOrDigit: Bool { ("a"..."z").contains(self) || ("0"..."9").contains(self) }
}
