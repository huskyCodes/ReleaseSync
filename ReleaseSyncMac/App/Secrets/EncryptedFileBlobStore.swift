import CryptoKit
import Foundation
import ReleaseSyncKit

/// Keeps a small file between runs, encrypted with AES-256-GCM. The key is made once and lives in the Keychain, so the file is
/// unreadable to anything else: what ReleaseSync remembers about your apps' releases would otherwise show which projects you follow,
/// even when your library is locked with a passphrase. If the key can't be used, nothing is kept (everything is just fetched again).
final class EncryptedFileBlobStore: BlobStore, @unchecked Sendable {
    static let keyTarget = "ReleaseSync/CacheKey"

    private let url: URL
    private let secrets: SecretStore
    private let lock = NSLock()
    private var key: SymmetricKey?

    init(url: URL, secrets: SecretStore) { self.url = url; self.secrets = secrets }

    /// `~/Library/Application Support/ReleaseSync/<name>`
    static func defaultURL(_ name: String) -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ReleaseSync", isDirectory: true).appendingPathComponent(name)
    }

    private func existingKey() throws -> SymmetricKey? {
        lock.lock(); defer { lock.unlock() }
        if let key { return key }
        guard let text = try secrets.read(Self.keyTarget), let data = Data(base64Encoded: text), data.count == 32 else { return nil }
        key = SymmetricKey(data: data)
        return key
    }

    private func newKey() throws -> SymmetricKey {
        lock.lock(); defer { lock.unlock() }
        if let key { return key }
        let fresh = SymmetricKey(size: .bits256)
        try secrets.write(Self.keyTarget, fresh.withUnsafeBytes { Data($0).base64EncodedString() })
        key = fresh
        return fresh
    }

    func load() async throws -> Data? {
        guard FileManager.default.fileExists(atPath: url.path), let key = try existingKey() else { return nil }
        guard let sealed = try? AES.GCM.SealedBox(combined: Data(contentsOf: url)), let plain = try? AES.GCM.open(sealed, using: key) else { return nil }
        return plain
    }

    func save(_ data: Data) async throws {
        let key = try existingKey() ?? newKey()
        guard let combined = try AES.GCM.seal(data, using: key).combined else { return }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try combined.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        var values = URLResourceValues(); values.isExcludedFromBackup = true
        var u = url; try? u.setResourceValues(values)
    }
}
