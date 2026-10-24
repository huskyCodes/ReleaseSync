import Foundation
import ReleaseSyncKit

struct LibrarySection: Identifiable, Equatable {
    let id: String
    let title: String
    let apps: [SyncApp]
}

/// How the library is laid out on screen: favorites first, then the person's own groups, then everything else.
/// (The Windows app also sorts ungrouped apps into automatic groups by type; that needs project descriptions, which the sync file doesn't carry.)
enum LibraryLayout {
    static let favoritesID = "favorites", ungroupedID = "ungrouped"

    static func sections(apps: [SyncApp], customGroups: [String], query: String, showHidden: Bool) -> [LibrarySection] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let shown = apps.filter { (showHidden || !$0.hidden) && (q.isEmpty || matches($0, q)) }
        func sorted(_ list: [SyncApp]) -> [SyncApp] {
            list.sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        }

        var result: [LibrarySection] = []
        let favorites = shown.filter(\.pinned)
        if !favorites.isEmpty { result.append(LibrarySection(id: favoritesID, title: "Favorites", apps: sorted(favorites))) }

        let rest = shown.filter { !$0.pinned }
        let named = Dictionary(grouping: rest.filter { !isBlankText($0.group) }) { $0.group!.lowercased() }
        var seen = Set<String>()
        for name in customGroups + rest.compactMap(\.group) {
            let key = name.lowercased()
            guard !isBlankText(name), seen.insert(key).inserted, let members = named[key] else { continue }
            result.append(LibrarySection(id: "group:" + key, title: name, apps: sorted(members)))
        }
        let ungrouped = rest.filter { isBlankText($0.group) }
        if !ungrouped.isEmpty { result.append(LibrarySection(id: ungroupedID, title: result.isEmpty ? "Your apps" : "Everything else", apps: sorted(ungrouped))) }
        return result
    }

    static func matches(_ app: SyncApp, _ query: String) -> Bool {
        [app.title, app.fullName, app.group, app.tags, app.note].contains { $0?.localizedStandardContains(query) == true }
    }

    static func matches(_ item: TryLaterItem, _ query: String) -> Bool {
        let topics = (item.topics ?? []).joined(separator: " ")
        return [item.repo, item.fullName, item.description, item.language, item.note, topics].contains { $0?.localizedStandardContains(query) == true }
    }
}
