import Foundation
import ReleaseSyncKit

/// What the local-network link has told us about one of the person's other devices. Held in memory only.
struct PeerState: Equatable {
    /// A live, authenticated link to that device exists right now (the green light).
    var online: Bool
    /// What it has installed; nil until it has said.
    var installed: [LanInstall]?
    /// When `installed` was last received (shown as "as of …" when the device is not online).
    var asOf: Timestamp?
}

/// A device and one program on it.
struct DeviceInstall: Identifiable, Equatable {
    let device: SyncDevice
    let online: Bool
    let install: LanInstall
    var id: String { device.id }
}

enum DeviceInventory {
    /// At most this many entries of one device's list are used (docs/LAN-PRESENCE.md, limits).
    static let maxInstalled = 1000

    /// A device's installed programs by app key (lower case, the way the library writes keys). Entries without a key are ignored,
    /// a repeated key is taken from its last entry, and only the first 1,000 in key order count.
    static func installed(_ state: PeerState?) -> [String: LanInstall] {
        var byKey: [String: LanInstall] = [:]
        for i in state?.installed ?? [] where !i.key.isEmpty { byKey[i.key.lowercased()] = i }
        let kept = byKey.keys.sorted { Array($0.utf16).lexicographicallyPrecedes(Array($1.utf16)) }.prefix(maxInstalled)
        return Dictionary(uniqueKeysWithValues: kept.map { ($0, byKey[$0]!) })
    }

    /// Devices that have said what they have installed (an empty list counts: the device said it has nothing).
    static func reporting(_ devices: [SyncDevice], peers: [String: PeerState]) -> [SyncDevice] {
        devices.filter { peers[$0.id]?.installed != nil }
    }

    static func isOnline(_ device: SyncDevice, peers: [String: PeerState]) -> Bool { peers[device.id]?.online ?? false }

    /// Where an app is installed, online devices first, then by name.
    static func installs(of key: String, on devices: [SyncDevice], peers: [String: PeerState]) -> [DeviceInstall] {
        let key = key.lowercased()
        return devices.compactMap { d in installed(peers[d.id])[key].map { DeviceInstall(device: d, online: isOnline(d, peers: peers), install: $0) } }
            .sorted { a, b in
                if a.online != b.online { return a.online }
                return a.device.name.localizedCaseInsensitiveCompare(b.device.name) == .orderedAscending
            }
    }

    /// Keys of the apps that are installed on the chosen device, or on any device when none is chosen.
    static func keys(on devices: [SyncDevice], peers: [String: PeerState], only deviceId: String?) -> Set<String> {
        var keys = Set<String>()
        for d in devices where deviceId == nil || d.id == deviceId { keys.formUnion(installed(peers[d.id]).keys) }
        return keys
    }

    /// Keys of the apps with an update waiting on the chosen device, or on any device when none is chosen.
    static func updatesWaiting(on devices: [SyncDevice], peers: [String: PeerState], only deviceId: String?) -> Set<String> {
        var keys = Set<String>()
        for d in devices where deviceId == nil || d.id == deviceId {
            for (key, install) in installed(peers[d.id]) where install.hasUpdate { keys.insert(key) }
        }
        return keys
    }

    static func summary(of state: PeerState?) -> String? {
        guard let state, state.installed != nil else { return nil }
        let list = Array(installed(state).values)
        var s = "\(list.count) \(list.count == 1 ? "app" : "apps") installed"
        let updates = list.filter(\.hasUpdate).count
        if updates > 0 { s += "  ·  \(updates) \(updates == 1 ? "update" : "updates") waiting" }
        return s
    }
}
