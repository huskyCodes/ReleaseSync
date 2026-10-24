import Foundation
import ReleaseSyncKit

/// What the person's other devices say they have installed (each device reports about itself in the sync file; this app only reads it).
struct DeviceInstall: Identifiable, Equatable {
    let device: SyncDevice
    let install: SyncInstall
    var id: String { device.id }
}

enum DeviceInventory {
    /// A device's installed programs by app key (lower case, the way the library writes keys).
    static func installed(on device: SyncDevice) -> [String: SyncInstall] {
        var result: [String: SyncInstall] = [:]
        for i in device.inventory { result[i.key.lowercased()] = i }
        return result
    }

    /// Devices that report a list at all (an empty list counts: the device said it has nothing).
    static func reporting(_ devices: [SyncDevice]) -> [SyncDevice] { devices.filter { $0.installed != nil } }

    /// Where an app is installed, online devices first, then by name.
    static func installs(of key: String, on devices: [SyncDevice], now: Timestamp) -> [DeviceInstall] {
        let key = key.lowercased()
        return devices.compactMap { d in installed(on: d)[key].map { DeviceInstall(device: d, install: $0) } }
            .sorted { a, b in
                let (x, y) = (a.device.isOnline(at: now), b.device.isOnline(at: now))
                if x != y { return x }
                return a.device.name.localizedCaseInsensitiveCompare(b.device.name) == .orderedAscending
            }
    }

    /// Keys of the apps that are installed on the chosen device, or on any device when none is chosen.
    static func keys(on devices: [SyncDevice], only deviceId: String?) -> Set<String> {
        var keys = Set<String>()
        for d in devices where deviceId == nil || d.id == deviceId { keys.formUnion(installed(on: d).keys) }
        return keys
    }

    /// Keys of the apps with an update waiting on the chosen device, or on any device when none is chosen.
    static func updatesWaiting(on devices: [SyncDevice], only deviceId: String?) -> Set<String> {
        var keys = Set<String>()
        for d in devices where deviceId == nil || d.id == deviceId {
            for (key, install) in installed(on: d) where install.hasUpdate { keys.insert(key) }
        }
        return keys
    }

    static func summary(of device: SyncDevice) -> String? {
        guard device.installed != nil else { return nil }
        let list = device.inventory
        var s = "\(list.count) \(list.count == 1 ? "app" : "apps") installed"
        let updates = list.filter(\.hasUpdate).count
        if updates > 0 { s += "  ·  \(updates) \(updates == 1 ? "update" : "updates") waiting" }
        return s
    }
}
