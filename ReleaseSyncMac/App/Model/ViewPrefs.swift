import Foundation
import Observation
import ReleaseSyncKit

/// How the person likes the pages laid out: grid or list, sorting, grouping, light or dark. These are ordinary preferences, kept in the
/// app's preferences. (Secrets are never kept here: they live in the Keychain.) The names and defaults follow the Windows app's settings.
@MainActor
@Observable
final class ViewPrefs {
    enum Layout: String { case grid = "Grid", list = "List" }
    enum Appearance: String, CaseIterable { case system = "System", dark = "Dark", light = "Light" }

    static let librarySorts = ["Name", "Recently updated", "Recently added"]
    static let trySorts: [(name: String, key: String)] = [("Recently saved", "recent"), ("Oldest first", "oldest"), ("Name A–Z", "name"), ("Most stars", "stars"), ("Type", "type")]

    private let defaults: UserDefaults
    @ObservationIgnored private var loading = true

    var viewMode = Layout.grid { didSet { save("ViewMode", viewMode.rawValue) } }
    var sort = "Name" { didSet { save("SortMode", sort) } }
    var groupItems = true { didSet { save("GroupItems", groupItems) } }
    var autoGroups = true { didSet { save("AutoGroups", autoGroups) } }
    var tryViewMode = Layout.grid { didSet { save("TryViewMode", tryViewMode.rawValue) } }
    var trySort = "recent" { didSet { save("TrySort", trySort) } }
    var tryGroup = true { didSet { save("TryGroup", tryGroup) } }
    var appearance = Appearance.system { didSet { save("Theme", appearance.rawValue) } }

    var discoverMode = DiscoverMode.trending { didSet { save("DiscoverMode", discoverMode.rawValue) } }
    var discoverCategory = "all" { didSet { save("DiscoverCategory", discoverCategory) } }
    /// Compare rankings with last week's instead of yesterday's.
    var discoverCompareWeek = false { didSet { save("DiscoverCompareWeek", discoverCompareWeek) } }
    /// Hide projects with nothing for the Mac (each one checked once, about one request).
    var discoverMacOnly = true { didSet { save("DiscoverMacOnly", discoverMacOnly) } }
    /// Light mode asks GitHub for about a third as much.
    var discoverLight = false { didSet { save("DiscoverLight", discoverLight) } }
    /// Whether this Mac may change the library in the sync file. Off until the person turns it on: older versions of ReleaseSync on other
    /// devices stop syncing once a device writes the newer file format.
    var allowLibraryChanges = false { didSet { save("AllowLibraryChanges", allowLibraryChanges) } }
    /// How often to look for new versions while the app is open; 0 is never.
    var checkEveryHours = 6 { didSet { save("CheckEveryHours", checkEveryHours) } }
    /// Tell the person once about each new version.
    var notifyOfUpdates = true { didSet { save("NotifyOfUpdates", notifyOfUpdates) } }
    var discoverIntroSeen = false { didSet { save("DiscoverIntroSeen", discoverIntroSeen) } }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        viewMode = Layout(rawValue: defaults.string(forKey: "ViewMode") ?? "") ?? .grid
        sort = defaults.string(forKey: "SortMode").flatMap { Self.librarySorts.contains($0) ? $0 : nil } ?? "Name"
        groupItems = defaults.object(forKey: "GroupItems") as? Bool ?? true
        autoGroups = defaults.object(forKey: "AutoGroups") as? Bool ?? true
        tryViewMode = Layout(rawValue: defaults.string(forKey: "TryViewMode") ?? "") ?? .grid
        trySort = defaults.string(forKey: "TrySort").flatMap { s in Self.trySorts.contains { $0.key == s } ? s : nil } ?? "recent"
        tryGroup = defaults.object(forKey: "TryGroup") as? Bool ?? true
        appearance = Appearance(rawValue: defaults.string(forKey: "Theme") ?? "") ?? .system
        discoverMode = DiscoverMode(rawValue: defaults.string(forKey: "DiscoverMode") ?? "") ?? .trending
        discoverCategory = defaults.string(forKey: "DiscoverCategory").flatMap { k in DiscoverCatalog.categories.contains { $0.key == k } ? k : nil } ?? "all"
        discoverCompareWeek = defaults.object(forKey: "DiscoverCompareWeek") as? Bool ?? false
        discoverMacOnly = defaults.object(forKey: "DiscoverMacOnly") as? Bool ?? true
        discoverLight = defaults.object(forKey: "DiscoverLight") as? Bool ?? false
        discoverIntroSeen = defaults.object(forKey: "DiscoverIntroSeen") as? Bool ?? false
        checkEveryHours = [0, 1, 3, 6, 12, 24].contains(defaults.object(forKey: "CheckEveryHours") as? Int ?? 6) ? (defaults.object(forKey: "CheckEveryHours") as? Int ?? 6) : 6
        notifyOfUpdates = defaults.object(forKey: "NotifyOfUpdates") as? Bool ?? true
        allowLibraryChanges = defaults.object(forKey: "AllowLibraryChanges") as? Bool ?? false
        loading = false
    }

    private func save(_ key: String, _ value: Any) { if !loading { defaults.set(value, forKey: key) } }
}
