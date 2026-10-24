import Foundation
import Security

/// Where the app keeps its secrets. The names mirror the Windows app's Credential Manager entries.
enum SecretTarget {
    static let gitHubToken = "ReleaseSync/GitHubToken"
    static let syncPassphrase = "ReleaseSync/SyncPassphrase"
    static let virusTotalKey = "ReleaseSync/VirusTotalKey"
}

protocol SecretStore: Sendable {
    func read(_ target: String) throws -> String?
    func write(_ target: String, _ secret: String) throws
    func delete(_ target: String) throws
}

struct KeychainError: Error, LocalizedError, Equatable {
    let status: OSStatus
    var errorDescription: String? {
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
