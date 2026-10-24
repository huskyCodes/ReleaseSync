import Foundation
import ReleaseSyncKit

/// What can be said about an app on this Mac from its releases: the version line, the status pill and which download would be used.
/// Nothing is installed by this version of the Mac app, so there is no "installed" or "update available" yet.
struct ReleaseFacts {
    let app: SyncApp
    let info: AppRelease?

    init(_ app: SyncApp, _ info: AppRelease?) { self.app = app; self.info = info }

    var checked: Bool { info?.lastChecked != nil }
    var hasRelease: Bool { info?.hasRelease ?? false }
    var latestTag: String { info?.latestTag ?? "" }

    /// The download this Mac would use, honouring the app's saved file pattern.
    var macAsset: AssetInfo? { MacAssets.pick(info?.assets, pattern: app.assetPattern) }
    /// Whether the latest release has anything that installs on this Mac. Nil until the releases are known.
    var hasMacBuild: Bool? { hasRelease ? MacAssets.looksLikeMac(info?.assets) : nil }

    var statusText: String {
        if !checked { return "Not checked yet" }
        if !hasRelease { return (info?.lastError ?? "").hasPrefix("No ") ? "No releases" : "Check failed" }
        return hasMacBuild == false ? "No Mac build" : "Not installed"
    }

    var statusKind: Pill.Kind {
        if !checked { return .neutral }
        if !hasRelease { return (info?.lastError ?? "").hasPrefix("No ") ? .neutral : .danger }
        return hasMacBuild == false ? .warn : .neutral
    }

    /// The line next to the little box icon on a card.
    var versionText: String {
        if !hasRelease { return checked ? "No release found" : "Not installed" }
        return hasMacBuild == false ? latestTag : "\(latestTag) available"
    }

    var description: String? { isBlankText(info?.meta?.description) ? nil : info?.meta?.description }
    var stars: Int { info?.meta?.stars ?? 0 }
    /// The project's picture. (Made-up demo data never fetches pictures.)
    func avatarURL(allowed: Bool = true) -> URL? {
        guard allowed else { return nil }
        if let s = info?.meta?.avatarUrl, let u = URL(string: s) { return u }
        return app.source == "github" ? URL(string: "https://github.com/\(app.owner).png?size=96") : nil
    }

    var releasedText: String? { info?.latestPublished.map { "Released " + $0.friendlyAgo } }
    var error: String? { info?.lastError }

    /// A one-line summary for a card when the project has no description: your note, your tags, or the site it comes from.
    var blurb: String {
        if let d = description { return d }
        if !isBlankText(app.note) { return app.note! }
        if !app.tagList.isEmpty { return app.tagList.joined(separator: " · ") }
        return RepoLinks.sourceName(app.source)
    }
}
