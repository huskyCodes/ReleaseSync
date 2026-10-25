import Foundation

/// Changes a person can make to their library, applied to the document the way the Windows app records them: the app's `modifiedAt` moves
/// forward (so the change wins when devices merge) and a deletion leaves a tombstone (so other devices don't bring the item back).
/// Every function returns whether anything changed, so nothing is written when there was nothing to do.
public enum LibraryEdits {
    /// A time that is later than the old one even when this device's clock is behind, so the edit always wins the merge.
    static func stamp(_ now: Timestamp, after old: Timestamp) -> Timestamp { max(now, Timestamp(ticks: old.ticks + 1)) }

    private static func edit(_ key: String, in doc: inout SyncDoc, now: Timestamp, _ change: (inout SyncApp) -> Void) -> Bool {
        guard let i = doc.apps.firstIndex(where: { $0.key == key }) else { return false }
        var app = doc.apps[i]
        change(&app)
        guard app != doc.apps[i] else { return false }
        app.modifiedAt = stamp(now, after: doc.apps[i].modifiedAt)
        doc.apps[i] = app
        return true
    }

    public static func setFavorite(_ key: String, _ on: Bool, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        edit(key, in: &doc, now: now) { $0.pinned = on }
    }

    public static func setHidden(_ key: String, _ on: Bool, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        edit(key, in: &doc, now: now) { $0.hidden = on }
    }

    /// A blank note removes the note.
    public static func setNote(_ key: String, _ text: String, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let note: String? = trimmed.isEmpty ? nil : trimmed
        return edit(key, in: &doc, now: now) { $0.note = note }
    }

    /// Puts the app in a group (made first if it doesn't exist yet); a blank name takes it out of its group.
    public static func setGroup(_ key: String, _ name: String?, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        let group = name?.trimmingCharacters(in: .whitespacesAndNewlines)
        guard doc.apps.contains(where: { $0.key == key }) else { return false }
        if let group, !group.isEmpty { _ = addGroup(group, in: &doc, now: now) }
        let existing = group.flatMap { g in doc.customGroups.first { $0.caseInsensitiveCompare(g) == .orderedSame } }
        return edit(key, in: &doc, now: now) { $0.group = (group?.isEmpty ?? true) ? nil : existing ?? group }
    }

    /// Makes a group (nothing happens when one with that name, ignoring case, is already there).
    public static func addGroup(_ name: String, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, !doc.customGroups.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else { return false }
        doc.customGroups.append(name)
        doc.groupStamps[name] = now
        doc.tombstones[SyncMerger.groupKey(name)] = nil
        return true
    }

    /// Deletes a group; its apps stay, without a group.
    public static func removeGroup(_ name: String, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        guard let existing = doc.customGroups.first(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else { return false }
        for app in doc.apps where app.group?.caseInsensitiveCompare(existing) == .orderedSame { _ = setGroup(app.key, nil, in: &doc, now: now) }
        doc.customGroups.removeAll { $0.caseInsensitiveCompare(existing) == .orderedSame }
        for k in doc.groupStamps.keys where k.caseInsensitiveCompare(existing) == .orderedSame { doc.groupStamps[k] = nil }
        doc.tombstones[SyncMerger.groupKey(existing)] = now
        return true
    }

    /// Takes an app out of the library for good; the tombstone stops other devices bringing it back.
    public static func removeApp(_ key: String, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        guard doc.apps.contains(where: { $0.key == key }) else { return false }
        doc.apps.removeAll { $0.key == key }
        doc.tombstones[key] = now
        return true
    }

    public static func removeTryLater(_ key: String, in doc: inout SyncDoc, now: Timestamp) -> Bool {
        guard let item = doc.tryLater.first(where: { $0.key == key }) else { return false }
        doc.tryLater.removeAll { $0.key == key }
        doc.tombstones[SyncMerger.laterKey(item)] = now
        return true
    }
}
