import Foundation
import Observation

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
        loading = false
    }

    private func save(_ key: String, _ value: Any) { if !loading { defaults.set(value, forKey: key) } }
}
