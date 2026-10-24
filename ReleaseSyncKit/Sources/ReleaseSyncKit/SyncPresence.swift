import Foundation

/// Whether another device looks connected, and what it has installed. There is no direct link between devices (they meet in the shared file),
/// so "connected" means: the device said it is online, and it said so recently. A device writes `online = true` when ReleaseSync opens, `false`
/// when it closes normally, and refreshes `lastSeen` every `SyncService.heartbeat` while it runs. If it stops without saying so (a crash, a lid
/// closed), it turns grey after `onlineWindow`.
public enum SyncPresence {
    private static func minutes(_ n: Int64) -> Int64 { n * 60 * Timestamp.ticksPerSecond }

    /// A little longer than the heartbeat, so one late refresh doesn't flicker the light.
    public static let onlineWindow = minutes(150)
    /// A device clock this far ahead of ours is still believed (clocks differ a little).
    public static let clockSlack = minutes(10)
    /// Readers ignore anything past this many installed programs in one device's list.
    public static let maxInstalled = 1000

    public static func isOnline(_ d: SyncDevice, at now: Timestamp) -> Bool {
        guard d.online == true else { return false }
        let age = now.ticks - d.lastSeen.ticks
        return age <= onlineWindow && age >= -clockSlack
    }

    /// The device's list of installed programs, made safe to read: no empty keys, each key once (the last entry wins), in key order, at most `maxInstalled`.
    public static func inventory(_ d: SyncDevice) -> [SyncInstall] {
        var byKey: [String: SyncInstall] = [:]
        for i in d.installed ?? [] where !i.key.isEmpty { byKey[i.key] = i }
        return Array(byKey.values.sortedOrdinal { $0.key }.prefix(maxInstalled))
    }
}

public extension SyncDevice {
    func isOnline(at now: Timestamp) -> Bool { SyncPresence.isOnline(self, at: now) }
    var inventory: [SyncInstall] { SyncPresence.inventory(self) }
}
