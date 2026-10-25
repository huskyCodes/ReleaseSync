import Foundation
import ReleaseSyncKit

enum LanSecretError: Error, LocalizedError, Equatable {
    case badPairId, badSecret
    var errorDescription: String? {
        switch self {
        case .badPairId: return "That isn't a valid pairing id."
        case .badSecret: return "A pairing secret must be exactly 32 bytes."
        }
    }
}

extension SecretTarget {
    /// The Keychain item for one pairing's shared secret (docs/LAN-PRESENCE.md, 5.5). Nil unless `pairId` is 32 lower-case hex digits, so a
    /// pair id can never smuggle other characters into an item name.
    static func lanPair(_ pairId: String) -> String? { LanPairRecord.isValidPairId(pairId) ? "ReleaseSync/LanPair/" + pairId : nil }
}

/// The pairing secrets, one Keychain item per pairing, stored as Base64 text. They live only in the secret store: not in files, logs,
/// backups or preferences, and they stay on this Mac (the login Keychain is not the synced one: these items are never marked synchronizable).
struct LanPairSecrets: Sendable {
    let store: SecretStore

    private func target(_ pairId: String) throws -> String {
        guard let t = SecretTarget.lanPair(pairId) else { throw LanSecretError.badPairId }
        return t
    }

    func write(_ psk: Data, for pairId: String) throws {
        guard psk.count == 32 else { throw LanSecretError.badSecret }
        try store.write(try target(pairId), psk.base64EncodedString())
    }

    func read(_ pairId: String) throws -> Data? {
        guard let text = try store.read(try target(pairId)) else { return nil }
        guard let data = Data(base64Encoded: text), data.count == 32 else { throw LanSecretError.badSecret }
        return data
    }

    func exists(_ pairId: String) throws -> Bool { try store.exists(try target(pairId)) }
    func delete(_ pairId: String) throws { try store.delete(try target(pairId)) }
}
