import Foundation
import ReleaseSyncKit

/// How this Mac is listed among the person's devices: a random id made once (not a hardware id), the Mac's own name, and the app version.
enum DeviceIdentity {
    static func make(defaults: UserDefaults = .standard, name: String? = Host.current().localizedName, bundle: Bundle = .main) -> SyncDevice {
        let id: String
        if let saved = defaults.string(forKey: "DeviceId"), saved.count == 32 { id = saved }
        else {
            id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            defaults.set(id, forKey: "DeviceId")
        }
        let clean = (name ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return SyncDevice(id: id, name: clean.isEmpty ? "This Mac" : clean, platform: "Mac", appVersion: (bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "")
    }
}
