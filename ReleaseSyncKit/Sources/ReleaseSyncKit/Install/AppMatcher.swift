import Foundation

/// An app found on this Mac, as far as matching it to a project needs.
public struct ScannedApp: Equatable, Sendable {
    public var name: String
    public var bundleId: String?
    public var version: String?
    public var path: String
    /// The address the app checks for updates (Sparkle's SUFeedURL), when it has one: it often names the project's GitHub page.
    public var updateFeed: String?

    public init(name: String, bundleId: String? = nil, version: String? = nil, path: String, updateFeed: String? = nil) {
        self.name = name; self.bundleId = bundleId; self.version = version; self.path = path; self.updateFeed = updateFeed
    }
}

/// Working out which project an app on this Mac comes from, without asking any website (the Mac counterpart of the Windows "Scan this PC" matching).
public enum AppMatcher {
    public enum Match: Equatable, Sendable {
        /// The app's own update address points at this project.
        case feed(RepoRef)
        /// The app's name is the name of an app already in the library.
        case library(key: String)
    }

    private static func norm(_ s: String) -> String { invariantLower(s).filter { $0.isLetter || $0.isNumber } }

    /// The project named in an update address like `https://github.com/owner/repo/releases.atom`, or nil.
    public static func repo(inFeed feed: String?) -> RepoRef? {
        guard let feed, let url = URL(string: feed), url.scheme == "https", let host = url.host?.lowercased() else { return nil }
        let parts = url.path.split(separator: "/").map(String.init)
        if host == "github.com" || host == "www.github.com", parts.count >= 2 { return RepoRef.parse("https://github.com/\(parts[0])/\(parts[1])") }
        if host.hasSuffix(".github.io"), let owner = host.split(separator: ".").first, let repo = parts.first { return RepoRef.parse("https://github.com/\(owner)/\(repo)") }
        if host == "raw.githubusercontent.com" || host == "github.com", parts.count >= 2 { return RepoRef.parse("https://github.com/\(parts[0])/\(parts[1])") }
        return nil
    }

    /// The best match for an app among the library's apps (`library`: key, repo name, display name), or nil.
    public static func match(_ app: ScannedApp, library: [(key: String, repo: String, displayName: String?)]) -> Match? {
        if let ref = repo(inFeed: app.updateFeed) {
            let key = SyncApp.makeKey(source: ref.source, owner: ref.owner, repo: ref.repo)
            if library.contains(where: { $0.key == key }) { return .library(key: key) }
            return .feed(ref)
        }
        let name = norm(app.name.hasSuffix(".app") ? String(app.name.dropLast(4)) : app.name)
        guard !name.isEmpty else { return nil }
        if let hit = library.first(where: { norm($0.repo) == name || ($0.displayName.map(norm) ?? "") == name }) { return .library(key: hit.key) }
        return nil
    }
}
