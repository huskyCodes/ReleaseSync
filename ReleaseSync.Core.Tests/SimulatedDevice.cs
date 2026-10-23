using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReleaseSync.Core.Tests;

/// <summary>A pretend phone/PC/Mac: it owns a small library and syncs it through whatever transport it's given, the way the real apps do.</summary>
public sealed class SimulatedDevice
{
    public SyncDevice Info { get; }
    public ISyncTransport Transport { get; set; }
    public string? Passphrase { get; set; }
    public SyncDoc Library { get; private set; } = new();

    public SimulatedDevice(string name, string platform, ISyncTransport transport)
    {
        Info = new SyncDevice { Id = "dev-" + name.ToLowerInvariant(), Name = name, Platform = platform, AppVersion = "1.0" };
        Transport = transport;
    }

    public IEnumerable<string> AppKeys => Library.Apps.Select(a => a.Key).OrderBy(x => x);
    public IEnumerable<string> LaterKeys => Library.TryLater.Select(a => a.Key).OrderBy(x => x);
    public SyncApp? App(string owner, string repo) => Library.Apps.FirstOrDefault(a => a.Key == Key(owner, repo));
    public static string Key(string owner, string repo) => $"github:{owner}/{repo}".ToLowerInvariant();

    public void Add(string owner, string repo, DateTime at, string? group = null) =>
        Library.Apps.Add(new SyncApp { Key = Key(owner, repo), Owner = owner, Repo = repo, Group = group, AddedAt = at, ModifiedAt = at });

    public void Edit(string owner, string repo, DateTime at, Action<SyncApp> change)
    {
        var a = App(owner, repo) ?? throw new InvalidOperationException("no such app");
        change(a); a.ModifiedAt = at;
    }

    public void Remove(string owner, string repo, DateTime at)
    {
        Library.Apps.RemoveAll(a => a.Key == Key(owner, repo));
        Library.Tombstones[Key(owner, repo)] = at;
    }

    public void Save(string owner, string repo, DateTime at, string? note = null) =>
        Library.TryLater.Add(new TryLaterItem { Owner = owner, Repo = repo, SavedAt = at, Note = note, ModifiedAt = note == null ? null : at });

    public void Unsave(string owner, string repo, DateTime at)
    {
        var item = new TryLaterItem { Owner = owner, Repo = repo };
        Library.TryLater.RemoveAll(x => x.Key == item.Key);
        Library.Tombstones[SyncMerger.LaterKey(item)] = at;
    }

    public void AddGroup(string name, DateTime at)
    {
        if (!Library.CustomGroups.Contains(name, StringComparer.OrdinalIgnoreCase)) Library.CustomGroups.Add(name);
        Library.GroupStamps[name] = at;
        Library.Tombstones.Remove(SyncMerger.GroupKey(name));
    }

    public void RemoveGroup(string name, DateTime at)
    {
        Library.CustomGroups.RemoveAll(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
        Library.GroupStamps.Remove(name);
        Library.Tombstones[SyncMerger.GroupKey(name)] = at;
        foreach (var a in Library.Apps.Where(a => string.Equals(a.Group, name, StringComparison.OrdinalIgnoreCase))) { a.Group = null; a.ModifiedAt = at; }
    }

    public async Task<SyncResult> SyncAsync(DateTime now)
    {
        var result = await SyncService.RunAsync(Transport, Library, Passphrase, Info, now);
        Library = result.Doc;
        return result;
    }

    public string Signature => SyncMerger.Signature(Library);
}
