import Foundation

/// A project on one of the places ReleaseSync reads from.
/// `source` is `github`, `gitlab`, `codeberg`, `forgejo:host`, `gitlab:host`, or `unknown:host` (a site that still has to be identified).
public struct RepoRef: Equatable, Sendable {
    public var source: String
    public var owner: String
    public var repo: String
    /// The whole project path, for GitLab-style nested groups.
    public var fullPath: String?

    public init(source: String, owner: String, repo: String, fullPath: String? = nil) {
        self.source = source; self.owner = owner; self.repo = repo; self.fullPath = fullPath
    }

    public var url: String { RepoRef.urlFor(source: source, owner: owner, repo: repo) }

    public static func urlFor(source: String, owner: String, repo: String) -> String {
        switch source {
        case "github": return "https://github.com/\(owner)/\(repo)"
        case "gitlab": return "https://gitlab.com/\(owner)/\(repo)"
        case "codeberg": return "https://codeberg.org/\(owner)/\(repo)"
        default:
            if let colon = source.firstIndex(of: ":") { return "https://\(source[source.index(after: colon)...])/\(owner)/\(repo)" }
            return "https://github.com/\(owner)/\(repo)"
        }
    }

    // MARK: reading what a person pastes (the same rules as SourceClient.TryParse in ReleaseSync.Core)

    private static func rx(_ p: String) -> NSRegularExpression { try! NSRegularExpression(pattern: p, options: .caseInsensitive) }
    private static let gitLab = rx(#"^(?:https?://)?(?:www\.)?gitlab\.com/(?<p>[^?#]+?)(?:/-/.*)?(?:\.git)?/?(?:[?#].*)?$"#)
    private static let codeberg = rx(#"^(?:https?://)?(?:www\.)?codeberg\.org/(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#].*)?$"#)
    private static let gitHub = rx(#"^(?:(?:https?://)?(?:www\.)?github\.com[/:]|git@github\.com:)?(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#].*)?$"#)
    private static let stopWords: Set<String> = ["-", "releases", "src", "tree", "blob", "issues", "pulls", "pull", "commit", "commits", "wiki", "actions", "tags", "branches", "raw", "archive", "packages", "projects", "compare"]

    private static func group(_ name: String, in m: NSTextCheckingResult, of s: String) -> String {
        let r = m.range(withName: name)
        return r.location == NSNotFound ? "" : String(s[Range(r, in: s)!])
    }

    private static func trimQuotes(_ s: String) -> String {
        var t = Substring(trimmed(s))
        let junk: Set<Character> = ["\"", "'", "<", ">"]
        while let f = t.first, junk.contains(f) { t.removeFirst() }
        while let l = t.last, junk.contains(l) { t.removeLast() }
        return String(t)
    }

    /// Understands GitHub, GitLab and Codeberg links (and plain `owner/repo`, which means GitHub). Any other web address with an
    /// owner and a project in its path comes back as `unknown:host`, to be identified with `SourceClient.resolve`.
    public static func parse(_ input: String?) -> RepoRef? {
        guard let input, !isBlank(input) else { return nil }
        let t = trimQuotes(input)
        if t.contains(" ") { return nil }
        let whole = NSRange(t.startIndex..., in: t)

        if let m = gitLab.firstMatch(in: t, range: whole) {
            let segs = group("p", in: m, of: t).split(separator: "/", omittingEmptySubsequences: true).map(String.init)
            // the C# version trims slashes first, then splits
            guard segs.count >= 2 else { return nil }
            return RepoRef(source: "gitlab", owner: segs.dropLast().joined(separator: "/"), repo: segs.last!)
        }
        if let m = codeberg.firstMatch(in: t, range: whole) {
            return RepoRef(source: "codeberg", owner: group("o", in: m, of: t), repo: group("r", in: m, of: t))
        }
        if let (o, r) = parseGitHub(t) { return RepoRef(source: "github", owner: o, repo: r) }

        // any other web address: could be a self-hosted Forgejo/Gitea or GitLab site
        if let comps = URLComponents(string: t), let scheme = comps.scheme?.lowercased(), scheme == "https" || scheme == "http",
           let host = comps.host, host.contains(".") {
            var segs: [String] = []
            for s in comps.percentEncodedPath.split(separator: "/", omittingEmptySubsequences: true).map(String.init) {
                if stopWords.contains(s.lowercased()) { break }
                segs.append(s)
            }
            if segs.count >= 2 {
                if segs[segs.count - 1].lowercased().hasSuffix(".git") { segs[segs.count - 1] = String(segs[segs.count - 1].dropLast(4)) }
                return RepoRef(source: "unknown:" + host.lowercased(), owner: segs[0], repo: segs[1], fullPath: segs.joined(separator: "/"))
            }
        }
        return nil
    }

    /// GitHub's own rules: `owner/repo`, a github.com link, or `git@github.com:owner/repo.git`.
    private static func parseGitHub(_ t: String) -> (String, String)? {
        guard let m = gitHub.firstMatch(in: t, range: NSRange(t.startIndex..., in: t)) else { return nil }
        let owner = group("o", in: m, of: t), repo = group("r", in: m, of: t)
        if owner.lowercased() == "orgs" || owner.lowercased() == "topics" { return nil }
        // a bare "a/b" should not look like a file path or link fragment
        if !t.lowercased().contains("github.com") && (t.contains("://") || t.contains("\\") || t.filter({ $0 == "/" }).count != 1) { return nil }
        return (owner, repo)
    }
}
