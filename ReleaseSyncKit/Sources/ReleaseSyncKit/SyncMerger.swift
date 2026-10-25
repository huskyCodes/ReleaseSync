import Foundation

/// Combines two copies of the library document into one. It is symmetric (A+B gives the same result as B+A) and repeatable
/// (merging the result again changes nothing), so devices can sync in any order and any number of times and still agree.
/// The newest edit to each item wins; a deletion beats an older copy of the item but not a newer edit or re-add.
/// This follows `SyncMerger` in ReleaseSync.Core and the rules in docs/SYNC-FORMAT.md.
public enum SyncMerger {
    /// Deletions are forgotten after this long.
    public static let tombstoneLife = Timestamp.days(90)

    public static func laterKey(_ item: TryLaterItem) -> String { "later:" + item.key }
    public static func groupKey(_ name: String) -> String { "group:" + invariantLower(trimmed(name)) }

    /// The newer of two versions. When the times are exactly equal, the one whose JSON text is greater wins, so every device picks the same one.
    private static func newer<T>(_ a: T, _ b: T, stamp: (T) -> Timestamp, text: (T) -> String) -> T {
        let sa = stamp(a), sb = stamp(b)
        if sa != sb { return sa > sb ? a : b }
        return ordinalCompare(text(a), text(b)) >= 0 ? a : b
    }

    public static func merge(_ a: SyncDoc, _ b: SyncDoc, now: Timestamp) -> SyncDoc {
        var result = SyncDoc()
        result.version = max(a.version, b.version)
        result.updatedAt = now
        result.writtenBy = a.updatedAt >= b.updatedAt ? a.writtenBy : b.writtenBy

        // deletions: the newest wins
        var tombs: [String: Timestamp] = [:]
        for src in [a.tombstones, b.tombstones] { for (k, t) in src where t > (tombs[k] ?? .zero) || tombs[k] == nil { tombs[k] = t } }

        // apps
        var apps: [String: SyncApp] = [:]
        for app in a.apps + b.apps where !app.key.isEmpty {
            apps[app.key] = apps[app.key].map { newer($0, app, stamp: \.modifiedAt, text: \.tieBreakText) } ?? app
        }
        for (key, app) in apps {
            guard let del = tombs[key] else { continue }
            if del >= app.modifiedAt && del >= app.addedAt { apps[key] = nil }      // deleted after its last edit
            else { tombs[key] = nil }                                                 // re-added or edited after the deletion
        }

        // try later
        var later: [String: TryLaterItem] = [:]
        for item in a.tryLater + b.tryLater {
            later[item.key] = later[item.key].map { newer($0, item, stamp: \.stamp, text: \.tieBreakText) } ?? item
        }
        for (key, item) in later {
            let tk = laterKey(item)
            guard let del = tombs[tk] else { continue }
            if del >= item.stamp { later[key] = nil } else { tombs[tk] = nil }
        }

        // groups: names match ignoring case; the kept spelling is the greater one, the stamp keeps the first spelling it was seen with
        var stamps: [String: (spelling: String, time: Timestamp)] = [:]
        for src in [a.groupStamps, b.groupStamps] {
            for (k, t) in src.sortedOrdinal(by: { $0.key }) {
                let folded = invariantUpper(k)
                if let have = stamps[folded] { if have.time < t { stamps[folded]!.time = t } } else { stamps[folded] = (k, t) }
            }
        }
        var groups: [String: String] = [:]      // folded name -> kept spelling
        for g in a.customGroups + b.customGroups where !isBlank(g) {
            let folded = invariantUpper(g)
            if let spelled = groups[folded] { if ordinalCompare(g, spelled) > 0 { groups[folded] = g } } else { groups[folded] = g }
        }
        for (folded, name) in groups {
            let tk = groupKey(name)
            guard let del = tombs[tk] else { continue }
            if let made = stamps[folded]?.time, del < made { tombs[tk] = nil }
            else { groups[folded] = nil; stamps[folded] = nil }
        }

        // devices
        for d in (a.devices.values.sortedOrdinal { $0.id }) + (b.devices.values.sortedOrdinal { $0.id }) where !d.id.isEmpty {
            result.devices[d.id] = result.devices[d.id].map { newer($0, d, stamp: \.lastSeen, text: \.tieBreakText) } ?? d
        }

        let cutoff = now - tombstoneLife
        for (k, t) in tombs where t >= cutoff { result.tombstones[k] = t }
        result.apps = apps.values.sortedOrdinal { $0.key }
        result.tryLater = later.values.sortedOrdinal { $0.key }
        result.customGroups = groups.values.sorted { ordinalIgnoreCaseCompare($0, $1) < 0 }
        result.groupStamps = Dictionary(uniqueKeysWithValues: stamps.values.map { ($0.spelling, $0.time) })
        return result
    }

    /// What the document says, without bookkeeping that changes on every sync (time written, who wrote it, when each device last checked in).
    /// Only ever compared with another signature from this same code, to tell whether anything changed.
    public static func signature(_ d: SyncDoc) -> String {
        var c = SyncDoc()
        c.version = d.version
        c.apps = d.apps.sortedOrdinal { $0.key }
        c.tryLater = d.tryLater.sortedOrdinal { $0.key }
        c.customGroups = d.customGroups.sorted { ordinalIgnoreCaseCompare($0, $1) < 0 }
        for (k, t) in d.groupStamps { c.groupStamps[invariantLower(k)] = t }
        c.tombstones = d.tombstones
        for dev in d.devices.values { c.devices[dev.id] = SyncDevice(id: dev.id, name: dev.name, platform: dev.platform, appVersion: dev.appVersion) }
        return c.text()
    }
}
