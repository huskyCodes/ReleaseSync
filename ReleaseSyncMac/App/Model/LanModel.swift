import Foundation
import Observation
import ReleaseSyncKit

/// What the "Find my devices on this network" screen shows and does (docs/LAN-PRESENCE.md). This version holds the settings, the list of
/// paired devices and the counters in memory and in preferences; the networking itself (Bonjour, sockets, the handshake) is not built yet,
/// so `isAvailable` is false in the real app and the screen says so. Nothing here listens, announces or browses.
@MainActor
@Observable
final class LanModel {
    /// Flips to true when the platform networking exists.
    static let networkingBuilt = false

    struct BlockedAttempt: Identifiable, Equatable {
        let id = UUID()
        let at: Date
        let address: String
    }

    let isAvailable: Bool
    /// The master switch "Find my devices on this network". Off means no listening, announcing or browsing at all.
    private(set) var enabled: Bool
    private(set) var pairs: [LanPairRecord]
    /// Why nothing is happening on this network, when the platform says so (for example a network marked Public on Windows). Nil otherwise.
    private(set) var networkNote: String?
    /// Connections that did not authenticate and were turned away. Held in memory only and cleared when the app quits.
    private(set) var blocked: [BlockedAttempt] = []
    /// A pairing in progress (the Add a device window). Shapes only for now.
    var pairingStep: LanPairingStep?
    var pairingRole = LanPairRole.host

    private let defaults: UserDefaults
    private let secrets: LanPairSecrets

    init(defaults: UserDefaults = .standard, secrets: LanPairSecrets, available: Bool = LanModel.networkingBuilt) {
        self.defaults = defaults; self.secrets = secrets; self.isAvailable = available
        self.enabled = available && (defaults.object(forKey: "FindMyDevices") as? Bool ?? false)
        if let data = defaults.data(forKey: "LanPairs"), let saved = try? JSONDecoder().decode([LanPairRecord].self, from: data) {
            self.pairs = saved.filter { LanPairRecord.isValidPairId($0.pairId) }.prefix(Lan.maxPaired).map { $0 }
        } else { self.pairs = [] }
    }

    /// Made-up pairings and blocked attempts to look at in the demo.
    init(showing pairs: [LanPairRecord], blocked: [BlockedAttempt], secrets: LanPairSecrets) {
        self.defaults = UserDefaults(suiteName: "ReleaseSyncDemo-" + UUID().uuidString)!
        self.secrets = secrets; self.isAvailable = true; self.enabled = true
        self.pairs = pairs; self.blocked = blocked
    }

    private func save() {
        if let data = try? JSONEncoder().encode(pairs) { defaults.set(data, forKey: "LanPairs") }
    }

    func setEnabled(_ on: Bool) {
        guard isAvailable else { return }
        enabled = on
        defaults.set(on, forKey: "FindMyDevices")
    }

    func setPaused(_ pairId: String, _ paused: Bool) {
        guard let i = pairs.firstIndex(where: { $0.pairId == pairId }), pairs[i].paused != paused else { return }
        pairs[i].paused = paused
        save()
    }

    /// Forgets one pairing: its secret and its record. (Once the link exists this also tells the other device first.)
    func remove(_ pairId: String) throws {
        try secrets.delete(pairId)
        pairs.removeAll { $0.pairId == pairId }
        save()
    }

    func removeAll() throws {
        for p in pairs { try secrets.delete(p.pairId) }
        pairs = []
        save()
    }

    /// Keeps a finished pairing: the record in preferences, the secret in the secret store. At most `Lan.maxPaired`.
    func add(_ record: LanPairRecord, secret: Data) throws {
        guard pairs.count < Lan.maxPaired || pairs.contains(where: { $0.pairId == record.pairId }) else { throw LanModelError.tooManyDevices }
        try secrets.write(secret, for: record.pairId)
        pairs.removeAll { $0.pairId == record.pairId }
        pairs.append(record)
        save()
    }

    // MARK: blocked attempts (memory only)

    func noteBlockedAttempt(address: String, at: Date = Date()) {
        blocked.append(BlockedAttempt(at: at, address: address))
        if blocked.count > 200 { blocked.removeFirst(blocked.count - 200) }
    }

    func blockedInLastHour(now: Date = Date()) -> [BlockedAttempt] { blocked.filter { now.timeIntervalSince($0.at) <= 3600 } }

    func clearOnQuit() { blocked = [] }
}

enum LanModelError: Error, LocalizedError {
    case tooManyDevices
    var errorDescription: String? { "You can pair up to \(Lan.maxPaired) devices. Remove one first." }
}

extension LanPairingStep {
    /// "482 913" for "482913".
    static func spaced(_ sas: String) -> String { sas.count == 6 ? sas.prefix(3) + " " + sas.suffix(3) : sas }
}
