import Foundation

public struct SyncResult: Sendable {
    /// Everything this device and the shared file know, combined. Apply it to the library when `changed` is true.
    public var doc: SyncDoc
    /// The shared file had something this device didn't.
    public var changed: Bool
    /// This device updated the shared file.
    public var pushed: Bool
    /// The shared file didn't exist yet.
    public var firstSync: Bool
}

/// One sync round: read the shared file, merge it with this device's library, write it back if it changed.
public enum SyncService {
    /// How often a device refreshes its "last seen" time in the file when nothing else changed (this keeps its light green: see `SyncPresence`).
    public static let heartbeat = Timestamp.hours(2)

    /// Reads the shared file and nothing else: no merge, nothing written. Nil when nothing is stored yet.
    /// `locked` says whether the file was locked with a passphrase.
    public static func fetch(transport: SyncTransport, passphrase: String?) async throws -> (doc: SyncDoc?, locked: Bool) {
        var text = try await transport.read()
        guard let t = text, !isBlank(t) else { return (nil, false) }
        let locked = SyncCrypto.isEncrypted(t)
        if locked { text = try SyncCrypto.decrypt(t, passphrase: passphrase) }
        let doc = try SyncDoc(text: text!)
        if doc.version > SyncDoc.supportedVersion { throw SyncError.newer }
        return (doc, locked)
    }

    public static func run(transport: SyncTransport, local: SyncDoc, passphrase: String?, device: SyncDevice, now: Timestamp) async throws -> SyncResult {
        let (remote, remoteEncrypted) = try await fetch(transport: transport, passphrase: passphrase)

        var me = device
        me.lastSeen = now
        var mine = local
        mine.devices[me.id] = me
        var merged = SyncMerger.merge(mine, remote ?? SyncDoc(), now: now)
        merged.writtenBy = me.name

        let mergedSignature = SyncMerger.signature(merged)
        let changed = remote != nil && mergedSignature != SyncMerger.signature(mine)
        let wantEncrypted = !(passphrase ?? "").isEmpty
        var push = remote == nil
        if let remote, !push {
            push = mergedSignature != SyncMerger.signature(remote)
                || remoteEncrypted != wantEncrypted
                || remote.devices[me.id].map { now.ticks - $0.lastSeen.ticks > heartbeat } ?? true
        }
        if push {
            let body = merged.text()
            try await transport.write(wantEncrypted ? try SyncCrypto.encrypt(body, passphrase: passphrase!) : body)
        }
        return SyncResult(doc: merged, changed: changed, pushed: push, firstSync: remote == nil)
    }
}
