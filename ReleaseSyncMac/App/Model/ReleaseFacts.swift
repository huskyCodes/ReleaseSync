import Foundation
import ReleaseSyncKit

/// What can be said about an app on this Mac from its releases and from what is installed here: the version line, the status pill, whether an
/// update is waiting, and which download would be used. (The status words follow the Windows app.)
struct ReleaseFacts {
    let app: SyncApp
    let info: AppRelease?
    /// The release tag installed on this Mac, if it is installed.
    let installed: String?
    /// Something is being installed or removed right now (its words, e.g. "Downloading 3 MB of 12 MB").
    let busy: String?

    init(_ app: SyncApp, _ info: AppRelease?, installed: String? = nil, busy: String? = nil) { self.app = app; self.info = info; self.installed = installed; self.busy = busy }

    var isInstalled: Bool { !(installed ?? "").isEmpty }
    var isSkipped: Bool { !isBlankText(app.skippedVersion) && app.skippedVersion == latestTag }

    /// When a version was published. A tag missing from the saved list (an old release, or one an app updated itself past) counts as older than all of them.
    func publishedOf(_ tag: String) -> Timestamp? {
        if let listed = info?.releases.first(where: { $0.tag == tag })?.published { return listed }
        if tag == latestTag, let latest = info?.latestPublished { return latest }
        guard let oldest = info?.releases.compactMap(\.published).min() else { return nil }
        return Timestamp(ticks: oldest.ticks - Timestamp.ticksPerSecond)
    }

    /// Compares two of this app's versions, using their publish dates when the tags aren't written the same way.
    func isNewer(_ a: String, than b: String) -> Bool { VersionUtil.isNewer(a, than: b, latestDate: publishedOf(a), installedDate: publishedOf(b)) }

    /// A newer release exists than the one installed here, and the person hasn't turned updates off or skipped it.
    var hasUpdate: Bool { isInstalled && hasRelease && !app.ignoreUpdates && !isSkipped && isNewer(latestTag, than: installed!) }
    /// A newer release exists but the person chose to ignore or skip it.
    var updateIgnored: Bool { isInstalled && hasRelease && (app.ignoreUpdates || isSkipped) && isNewer(latestTag, than: installed!) }

    /// The installed version isn't one of this app's recent releases, so the app may have updated itself past what ReleaseSync knows.
    var versionUnrecognized: Bool {
        guard isInstalled, let releases = info?.releases, !releases.isEmpty, installed != latestTag else { return false }
        return releases.allSatisfy { $0.tag != installed }
    }

    var checked: Bool { info?.lastChecked != nil }
    var hasRelease: Bool { info?.hasRelease ?? false }
    var latestTag: String { info?.latestTag ?? "" }

    /// The download this Mac would use, honouring the app's saved file pattern.
    var macAsset: AssetInfo? { MacAssets.pick(info?.assets, pattern: app.assetPattern) }
    /// Whether the latest release has anything that installs on this Mac. Nil until the releases are known.
    var hasMacBuild: Bool? { hasRelease ? MacAssets.looksLikeMac(info?.assets) : nil }

    var statusText: String {
        if busy != nil { return "Working…" }
        if hasUpdate { return "Update available" }
        if updateIgnored { return isSkipped ? "Skipped \(latestTag)" : "Update ignored" }
        if isInstalled { return hasRelease ? "Up to date" : "Installed" }
        if !checked { return "Not checked yet" }
        if !hasRelease { return (info?.lastError ?? "").hasPrefix("No ") ? "No releases" : "Check failed" }
        return hasMacBuild == false ? "No Mac build" : "Not installed"
    }

    var statusKind: Pill.Kind {
        if busy != nil { return .accent }
        if hasUpdate { return .accent }
        if updateIgnored { return .warn }
        if isInstalled { return .success }
        if !checked { return .neutral }
        if !hasRelease { return (info?.lastError ?? "").hasPrefix("No ") ? .neutral : .danger }
        return hasMacBuild == false ? .warn : .neutral
    }

    /// The line next to the little box icon on a card.
    var versionText: String {
        if hasUpdate { return "\(installed!)  →  \(latestTag)" }
        if isInstalled { return installed! }
        if !hasRelease { return checked ? "No release found" : "Not installed" }
        return hasMacBuild == false ? latestTag : "\(latestTag) available"
    }

    /// What the main button on a card says, or nil when there is nothing to press (no download for this Mac, or nothing newer).
    var actionText: String? {
        if busy != nil { return nil }
        if hasUpdate { return macAsset == nil ? nil : "Update" }
        if !isInstalled && hasRelease { return macAsset == nil ? nil : "Install" }
        return nil
    }

    /// Why Install or Update can't be pressed, when a release exists but nothing in it fits this Mac.
    var noInstallableFileText: String? {
        guard hasRelease, (hasUpdate || !isInstalled), macAsset == nil else { return nil }
        guard let assets = info?.assets, !assets.isEmpty else { return "\(latestTag) has no files to download yet, so there's nothing to install." }
        return "\(latestTag) has no file ReleaseSync can install on a Mac. Its files are \(assets.map(\.name).joined(separator: ", "))."
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
