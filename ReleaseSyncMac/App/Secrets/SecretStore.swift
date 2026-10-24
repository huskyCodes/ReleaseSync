import Foundation
import Security

/// Where the app keeps its secrets. The names mirror the Windows app's Credential Manager entries.
enum SecretTarget {
    static let gitHubToken = "ReleaseSync/GitHubToken"
    static let syncPassphrase = "ReleaseSync/SyncPassphrase"
    static let virusTotalKey = "ReleaseSync/VirusTotalKey"
}

protocol SecretStore: Sendable {
    /// Whether something is saved, without reading it (so macOS has nothing to ask permission for).
    func exists(_ target: String) throws -> Bool
    func read(_ target: String) throws -> String?
    func write(_ target: String, _ secret: String) throws
    func delete(_ target: String) throws
}

struct KeychainError: Error, LocalizedError, Equatable {
    let status: OSStatus

    /// macOS said no: the person declined the question, or this build isn't on the item's list of apps that may use it.
    var isAccessDenied: Bool { [errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed, errSecNoAccessForItem].contains(status) }

    var errorDescription: String? {
        if isAccessDenied {
            return "macOS didn't let ReleaseSync use what it saved. When it asks, choose “Always Allow”. If it keeps asking, open Keychain Access, delete the old “ReleaseSync/…” items and save them again in Settings."
        }
        let text = SecCopyErrorMessageString(status, nil) as String? ?? "error \(status)"
        return "The Keychain said: \(text)"
    }
}

/// The Mac Keychain (the login keychain). Secrets are stored as generic passwords, one item per target, and never leave it:
/// not in files, logs, backups or UserDefaults. `prefix` only exists so tests can use their own items.
struct KeychainSecretStore: SecretStore {
    static let account = "ReleaseSync"
    var prefix = ""

    private func query(_ target: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: prefix + target,
         kSecAttrAccount as String: KeychainSecretStore.account]
    }

    func exists(_ target: String) throws -> Bool {
        var q = query(target)
        q[kSecReturnAttributes as String] = true      // the attributes only: no password, so no permission question
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        let status = SecItemCopyMatching(q as CFDictionary, nil)
        if status == errSecItemNotFound { return false }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
        return true
    }

    func read(_ target: String) throws -> String? {
        var q = query(target)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
        guard let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    func write(_ target: String, _ secret: String) throws {
        let data = Data(secret.utf8)
        let status = SecItemAdd(query(target).merging([kSecValueData as String: data]) { $1 } as CFDictionary, nil)
        if status == errSecDuplicateItem {
            let update = SecItemUpdate(query(target) as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            guard update == errSecSuccess else { throw KeychainError(status: update) }
        } else if status != errSecSuccess {
            throw KeychainError(status: status)
        }
    }

    func delete(_ target: String) throws {
        let status = SecItemDelete(query(target) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError(status: status) }
    }
}

/// Secrets kept in memory only. Used when the app runs under tests, so a test run never touches (or waits on) the real Keychain.
final class InMemorySecrets: SecretStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: String] = [:]
    func exists(_ target: String) throws -> Bool { lock.lock(); defer { lock.unlock() }; return items[target] != nil }
    func read(_ target: String) throws -> String? { lock.lock(); defer { lock.unlock() }; return items[target] }
    func write(_ target: String, _ secret: String) throws { lock.lock(); items[target] = secret; lock.unlock() }
    func delete(_ target: String) throws { lock.lock(); items[target] = nil; lock.unlock() }
}
