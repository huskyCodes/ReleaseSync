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
    /// How often a device refreshes its "last seen" time in the file when nothing else changed.
    public static let heartbeat = Timestamp.hours(6)

    public static func run(transport: SyncTransport, local: SyncDoc, passphrase: String?, device: SyncDevice, now: Timestamp) async throws -> SyncResult {
        var text = try await transport.read()
        var remote: SyncDoc?
        var remoteEncrypted = false
        if let t = text, !isBlank(t) {
            remoteEncrypted = SyncCrypto.isEncrypted(t)
            if remoteEncrypted { text = try SyncCrypto.decrypt(t, passphrase: passphrase) }
            let doc = try SyncDoc(text: text!)
            if doc.version > SyncDoc.supportedVersion { throw SyncError.newer }
            remote = doc
        }

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
