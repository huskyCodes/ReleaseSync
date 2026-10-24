import CommonCrypto
import CryptoKit
import Foundation

/// Optional end-to-end encryption for the sync file: AES-256-GCM with a key made from the passphrase (PBKDF2-SHA256).
/// Wherever the file is stored, only someone with the passphrase can read it. See "Encryption" in docs/SYNC-FORMAT.md.
public enum SyncCrypto {
    static let marker = "releasesync-enc"
    static let iterations = 200_000

    /// Whether the text is a locked sync file: it starts with `{` and mentions the marker.
    public static func isEncrypted(_ text: String) -> Bool {
        guard let first = text.unicodeScalars.first(where: { !$0.properties.isWhitespace }), first == "{" else { return false }
        return text.contains("\"" + marker + "\"")
    }

    static func key(passphrase: String, salt: Data, iterations: Int) -> SymmetricKey? {
        let password = Array(passphrase.utf8)       // as typed: no trimming and no Unicode normalization
        var derived = [UInt8](repeating: 0, count: 32)
        let status = password.withUnsafeBufferPointer { pw in
            salt.withUnsafeBytes { s in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                     UnsafeRawPointer(pw.baseAddress).map { $0.assumingMemoryBound(to: Int8.self) }, password.count,
                                     s.bindMemory(to: UInt8.self).baseAddress, salt.count,
                                     CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations), &derived, derived.count)
            }
        }
        guard status == Int32(kCCSuccess) else { return nil }
        return SymmetricKey(data: derived)
    }

    static func randomBytes(_ count: Int) -> Data { Data((0..<count).map { _ in UInt8.random(in: 0...255) }) }

    public static func encrypt(_ plain: String, passphrase: String) throws -> String {
        let salt = randomBytes(16)
        return try encrypt(plain, passphrase: passphrase, salt: salt, nonce: randomBytes(12), iterations: iterations)
    }

    /// With a fixed salt and nonce, so tests can make the same envelope twice. Never call this with fixed values for real files.
    static func encrypt(_ plain: String, passphrase: String, salt: Data, nonce: Data, iterations: Int) throws -> String {
        guard let key = key(passphrase: passphrase, salt: salt, iterations: iterations) else { throw SyncError.general("The passphrase couldn't be used.") }
        let box = try AES.GCM.seal(Data(plain.utf8), using: key, nonce: AES.GCM.Nonce(data: nonce), authenticating: Data(marker.utf8))
        return JSONWriter.write(.object([
            JSONMember(marker, .int(1)), JSONMember("Kdf", .string("pbkdf2-sha256")), JSONMember("Iter", .int(iterations)),
            JSONMember("Salt", .string(salt.base64EncodedString())), JSONMember("Nonce", .string(nonce.base64EncodedString())),
            JSONMember("Tag", .string(box.tag.base64EncodedString())), JSONMember("Data", .string(box.ciphertext.base64EncodedString())),
        ]))
    }

    public static func decrypt(_ envelope: String, passphrase: String?) throws -> String {
        guard let passphrase, !passphrase.isEmpty else { throw SyncError.locked }
        let root: JSONValue
        do { root = try JSONParser.parse(envelope) } catch { throw SyncError.damaged }
        guard case .object(let members) = root else { throw SyncError.damaged }
        // the envelope's names are matched exactly, unlike the document's
        func field(_ name: String) -> JSONValue? { members.last { $0.key == name }?.value }
        func text(_ name: String) throws -> String {
            guard let v = field(name) else { return "" }
            if case .string(let s) = v { return s }
            if case .null = v { return "" }
            throw SyncError.damaged
        }
        func whole(_ name: String) throws -> Int {
            guard let v = field(name), v != .null else { return 0 }
            guard case .number(let n) = v, let i = Int(n) else { throw SyncError.damaged }
            return i
        }
        let version = try whole(marker), iter = try whole("Iter")
        guard version == 1, (1000...10_000_000).contains(iter) else { throw SyncError.newer }
        guard let salt = Data(base64Encoded: try text("Salt")), let nonce = Data(base64Encoded: try text("Nonce")),
              let tag = Data(base64Encoded: try text("Tag")), let cipher = Data(base64Encoded: try text("Data")) else { throw SyncError.damaged }
        guard let key = key(passphrase: passphrase, salt: salt, iterations: iter) else { throw SyncError.damaged }
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: cipher, tag: tag)
            let plain = try AES.GCM.open(box, using: key, authenticating: Data(marker.utf8))
            guard let s = String(data: plain, encoding: .utf8) else { throw SyncError.damaged }
            return s
        } catch CryptoKitError.authenticationFailure {
            throw SyncError.wrongPassphrase
        } catch let e as SyncError {
            throw e
        } catch {
            throw SyncError.damaged
        }
    }
}
