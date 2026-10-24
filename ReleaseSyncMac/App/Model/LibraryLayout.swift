import Foundation
import ReleaseSyncKit

/// How the library is laid out, following the Windows app's rules (MainViewModel: LibraryFilter, ApplyViewConfig, UpdateGroups):
/// hidden apps only show under the Hidden filter; sections are the person's own groups plus automatic ones by type, in name order;
/// inside a section favorites come first, then the chosen sort, then the name.
enum LibraryLayout {
    static let ungrouped = "Ungrouped"

    enum Status: Hashable { case all, favorites, noMacBuild, hidden }

    struct Section: Identifiable, Equatable {
        var id: String { title }
        let title: String
        let apps: [SyncApp]
    }

    struct GroupChip: Identifiable, Equatable {
        var id: String { name }
        let name: String
        let count: Int
        let custom: Bool
    }

    struct Result {
        var sections: [Section] = []
        var chips: [GroupChip] = []
        /// The person's own groups that have nothing in them yet (shown so they don't seem to vanish).
        var emptyGroups: [String] = []
        var total = 0, favorites = 0, hidden = 0, noMacBuild = 0
    }

    /// The group an app is shown under: its own group, else (when automatic groups are on) its type, else "Ungrouped".
    /// With the project's details (description, topics, language) the automatic type is worked out the same way the Windows app does it;
    /// until they are known it goes by the name alone.
    static func displayGroup(_ app: SyncApp, autoGroups: Bool, info: AppRelease? = nil) -> String {
        if !isBlankText(app.group) { return app.group! }
        guard autoGroups else { return ungrouped }
        return Categorizer.categorize(name: app.repo, description: info?.meta?.description, topics: info?.meta?.topics, language: info?.meta?.language)
    }

    static func layout(apps: [SyncApp], customGroups: [String], releases: [String: AppRelease] = [:], status: Status, groupFilter: String?, query: String, sort: String, groupItems: Bool, autoGroups: Bool) -> Result {
        func noMac(_ a: SyncApp) -> Bool { ReleaseFacts(a, releases[a.key]).hasMacBuild == false }
        var r = Result()
        r.total = apps.filter { !$0.hidden }.count
        r.favorites = apps.filter { !$0.hidden && $0.pinned }.count
        r.hidden = apps.filter(\.hidden).count
        r.noMacBuild = apps.filter { !$0.hidden && noMac($0) }.count

        // group chips count what the status filter shows
        let inScope = apps.filter { $0.hidden == (status == .hidden) }
        var counts: [String: Int] = [:]
        for a in inScope { counts[displayGroup(a, autoGroups: autoGroups, info: releases[a.key]), default: 0] += 1 }
        for g in customGroups where counts.first(where: { $0.key.caseInsensitiveCompare(g) == .orderedSame }) == nil { counts[g] = 0 }
        let isCustom = { (name: String) in customGroups.contains { $0.caseInsensitiveCompare(name) == .orderedSame } }
        r.chips = counts
            .filter { autoGroups || isCustom($0.key) || ($0.key == ungrouped && $0.value > 0) }
            .map { GroupChip(name: $0.key, count: $0.value, custom: isCustom($0.key)) }
            .sorted { a, b in
                if a.custom != b.custom { return a.custom }
                if (a.name == ungrouped) != (b.name == ungrouped) { return b.name == ungrouped }
                return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
            }

        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let shown = inScope.filter { a in
            if let groupFilter, displayGroup(a, autoGroups: autoGroups, info: releases[a.key]) != groupFilter { return false }
            if status == .favorites && !a.pinned { return false }
            if status == .noMacBuild && !noMac(a) { return false }
            return matches(a, q, autoGroups, releases[a.key])
        }

        func ordered(_ list: [SyncApp]) -> [SyncApp] {
            list.sorted { a, b in
                if a.pinned != b.pinned { return a.pinned }
                if sort == "Recently added", a.addedAt != b.addedAt { return a.addedAt > b.addedAt }
                if sort == "Recently updated" {
                    let (x, y) = (releases[a.key]?.latestPublished?.ticks ?? 0, releases[b.key]?.latestPublished?.ticks ?? 0)
                    if x != y { return x > y }
                }
                return a.title.localizedCaseInsensitiveCompare(b.title) == .orderedAscending
            }
        }
        if groupItems {
            let groups = Dictionary(grouping: shown) { displayGroup($0, autoGroups: autoGroups, info: releases[$0.key]) }
            r.sections = groups.map { Section(title: $0.key, apps: ordered($0.value)) }
                .sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        } else if !shown.isEmpty {
            r.sections = [Section(title: "", apps: ordered(shown))]
        }
        if groupItems && status == .all && q.isEmpty {
            r.emptyGroups = r.chips.filter { $0.custom && $0.count == 0 && (groupFilter == nil || groupFilter == $0.name) }.map(\.name)
        }
        return r
    }

    static func matches(_ app: SyncApp, _ query: String, _ autoGroups: Bool, _ info: AppRelease? = nil) -> Bool {
        guard !query.isEmpty else { return true }
        return [app.title, app.fullName, displayGroup(app, autoGroups: autoGroups, info: info), app.note, app.tags, info?.meta?.description].contains { $0?.localizedStandardContains(query) == true }
    }

    // MARK: Try later

    struct TryType: Identifiable, Equatable {
        var id: String { name }
        let name: String       // empty = everything
        let count: Int
    }

    struct TryResult {
        var sections: [(title: String, items: [TryLaterItem])] = []
        var types: [TryType] = []
        var shown = 0, total = 0
    }

    static func category(_ item: TryLaterItem) -> String {
        Categorizer.categorize(name: item.repo, description: item.description, topics: item.topics, language: item.language)
    }

    static func tryLater(_ items: [TryLaterItem], type: String, query: String, sort: String, group: Bool) -> TryResult {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        func textMatches(_ t: TryLaterItem) -> Bool {
            guard !q.isEmpty else { return true }
            let topics = (t.topics ?? []).joined(separator: " ")
            return [t.fullName, t.description, t.note, t.language, category(t), topics].contains { $0?.localizedStandardContains(q) == true }
        }
        var r = TryResult(total: items.count)
        let byType = Dictionary(grouping: items.filter(textMatches), by: category)
        r.types = [TryType(name: "", count: byType.values.reduce(0) { $0 + $1.count })]
            + byType.map { TryType(name: $0.key, count: $0.value.count) }.sorted {
                $0.count != $1.count ? $0.count > $1.count : $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
            }
        let shown = items.filter { (type.isEmpty || category($0) == type) && textMatches($0) }
        r.shown = shown.count
        func ordered(_ list: [TryLaterItem]) -> [TryLaterItem] {
            list.sorted { a, b in
                switch sort {
                case "oldest": return a.savedAt < b.savedAt
                case "name": return a.repo.localizedCaseInsensitiveCompare(b.repo) == .orderedAscending
                case "stars": return a.stars != b.stars ? a.stars > b.stars : a.repo.localizedCaseInsensitiveCompare(b.repo) == .orderedAscending
                case "type":
                    let (x, y) = (category(a), category(b))
                    return x != y ? x.localizedCaseInsensitiveCompare(y) == .orderedAscending : a.repo.localizedCaseInsensitiveCompare(b.repo) == .orderedAscending
                default: return a.savedAt > b.savedAt
                }
            }
        }
        if group {
            r.sections = Dictionary(grouping: shown, by: category).map { (title: $0.key, items: ordered($0.value)) }
                .sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        } else if !shown.isEmpty {
            r.sections = [(title: "", items: ordered(shown))]
        }
        return r
    }
}
