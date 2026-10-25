using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

public sealed class SyncResult
{
    /// <summary>Everything this device and the shared file know, combined. Apply it to the library when <see cref="Changed"/> is true.</summary>
    public SyncDoc Doc { get; init; } = new();
    /// <summary>The shared file had something this device didn't.</summary>
    public bool Changed { get; init; }
    /// <summary>This device updated the shared file.</summary>
    public bool Pushed { get; init; }
    /// <summary>The shared file didn't exist yet.</summary>
    public bool FirstSync { get; init; }
}

/// <summary>One sync round: read the shared file, merge it with this device's library, write it back if it changed.</summary>
public static class SyncService
{
    /// <summary>How often a device refreshes its "last seen" time in the file when nothing else changed.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromHours(6);

    public static async Task<SyncResult> RunAsync(ISyncTransport transport, SyncDoc local, string? passphrase, SyncDevice self, DateTime now, CancellationToken ct = default)
    {
        string? text = await transport.ReadAsync(ct).ConfigureAwait(false);
        SyncDoc? remote = null; bool remoteEncrypted = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            remoteEncrypted = SyncCrypto.IsEncrypted(text);
            if (remoteEncrypted) text = SyncCrypto.Decrypt(text, passphrase);
            try { remote = JsonSerializer.Deserialize<SyncDoc>(text, Store.Options); }
            catch (JsonException ex) { throw new SyncException("The sync file isn't readable, so nothing was changed.", ex); }
            if (remote == null) throw new SyncException("The sync file isn't readable, so nothing was changed.");
            if (remote.Version > 2) throw new SyncException("The sync file was made by a newer version of ReleaseSync. Update ReleaseSync and try again.");
        }

        self.LastSeen = now;
        local.Devices[self.Id] = self;
        var merged = remote == null ? SyncMerger.Merge(local, new SyncDoc(), now) : SyncMerger.Merge(local, remote, now);
        merged.WrittenBy = self.Name;

        bool changed = remote != null && SyncMerger.Signature(merged) != SyncMerger.Signature(local);
        bool wantEncrypted = !string.IsNullOrEmpty(passphrase);
        bool push = remote == null
                    || SyncMerger.Signature(merged) != SyncMerger.Signature(remote)
                    || remoteEncrypted != wantEncrypted
                    || !remote.Devices.TryGetValue(self.Id, out var seen) || now - seen.LastSeen > Heartbeat;
        if (push)
        {
            var json = JsonSerializer.Serialize(merged, Store.Options);
            await transport.WriteAsync(wantEncrypted ? SyncCrypto.Encrypt(json, passphrase!) : json, ct).ConfigureAwait(false);
        }
        return new SyncResult { Doc = merged, Changed = changed, Pushed = push, FirstSync = remote == null };
    }
}
