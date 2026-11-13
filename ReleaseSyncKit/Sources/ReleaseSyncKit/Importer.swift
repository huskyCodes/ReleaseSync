import Foundation

/// Reading a list of projects out of whatever the person gives: a ReleaseSync export, an Obtainium export, or any text with GitHub links or
/// owner/repo lines. The same rules as `Importer` in ReleaseSync.Core.
public enum Importer {
    public struct Entry: Equatable, Sendable {
        public var owner: String
        public var repo: String
        public var name: String?
        public var group: String?
        public var prereleases: Bool
    }

    public struct Result: Equatable, Sendable {
        public var entries: [Entry] = []
        /// Entries in a file that are not on GitHub.
        public var skipped = 0
    }

    private static let urlRegex = try! NSRegularExpression(pattern: #"github\.com[/:](?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+)"#, options: .caseInsensitive)

    private static func cleanRepo(_ r: String) -> String { r.lowercased().hasSuffix(".git") ? String(r.dropLast(4)) : r }

    private static func github(_ text: String) -> (owner: String, repo: String)? {
        guard let ref = RepoRef.parse(text), ref.source == "github" else { return nil }
        return (ref.owner, ref.repo)
    }

    public static func parse(_ text: String) -> Result {
        var result = Result()
        var seen = Set<String>()
        func add(_ o: String, _ r: String, name: String? = nil, group: String? = nil, pre: Bool = false) {
            let r = cleanRepo(r)
            guard seen.insert(invariantLower("\(o)/\(r)")).inserted else { return }
            result.entries.append(Entry(owner: o, repo: r, name: name, group: group, prereleases: pre))
        }

        let trimmed = text.drop { $0.isWhitespace }
        if trimmed.hasPrefix("{") || trimmed.hasPrefix("["), let data = text.data(using: .utf8), let root = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) {
            var apps: [Any]?
            if let array = root as? [Any] { apps = array }
            else if let object = root as? [String: Any], let list = object["apps"] as? [Any] { apps = list }
            if let apps {
                for case let el as [String: Any] in apps {
                    guard let url = el["url"] as? String, url.lowercased().contains("github.com"), let (o, r) = github(url) else { result.skipped += 1; continue }
                    var pre = (el["includePrereleases"] as? Bool) == true
                    var group = el["group"] as? String
                    if group == nil, let cats = el["categories"] as? [Any], let first = cats.first as? String { group = first }
                    if let extra = el["additionalSettings"] {
                        let inner: [String: Any]? = (extra as? [String: Any]) ?? (extra as? String).flatMap { $0.data(using: .utf8) }.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                        if (inner?["includePrereleases"] as? Bool) == true { pre = true }
                    }
                    add(o, r, name: el["name"] as? String, group: group, pre: pre)
                }
                return result
            }
        }

        for m in urlRegex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let o = Range(m.range(withName: "o"), in: text), let r = Range(m.range(withName: "r"), in: text) else { continue }
            add(String(text[o]), String(text[r]))
        }
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let l = line.trimmingCharacters(in: .whitespacesAndNewlines)
            if l.isEmpty || l.lowercased().contains("github.com") { continue }
            if let (o, r) = github(l) { add(o, r) }
        }
        return result
    }

    /// An export of the library the Windows app can read back: `{ format, version, exported, apps: [{ url, name, group, includePrereleases }] }`.
    public static func exportJSON(_ apps: [SyncApp], exported: Timestamp = .now()) -> String {
        var list: [JSONValue] = []
        for a in apps {
            var m = [JSONMember("url", .string(RepoRef.urlFor(source: a.source, owner: a.owner, repo: a.repo))), JSONMember("name", .string(isBlank(a.displayName) ? a.repo : a.displayName!))]
            if let g = a.group, !g.isEmpty { m.append(JSONMember("group", .string(g))) }
            m.append(JSONMember("includePrereleases", .bool(a.includePrereleases)))
            list.append(.object(m))
        }
        return JSONWriter.write(.object([JSONMember("format", .string("releasesync")), JSONMember("version", .int(1)), JSONMember("exported", exported.json), JSONMember("apps", .array(list))]))
    }

    public static func exportLinks(_ apps: [SyncApp]) -> String { apps.map { RepoRef.urlFor(source: $0.source, owner: $0.owner, repo: $0.repo) }.joined(separator: "\n") }
}
