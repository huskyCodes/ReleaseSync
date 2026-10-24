using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReleaseSync.Core;

/// <summary>
/// Combines two copies of the library document into one. It is symmetric (A+B gives the same result as B+A) and repeatable
/// (merging the result again changes nothing), so devices can sync in any order and any number of times and still agree.
/// The newest edit to each item wins; a deletion beats an older copy of the item but not a newer edit or re-add.
/// </summary>
public static class SyncMerger
{
    public static readonly TimeSpan TombstoneLife = TimeSpan.FromDays(90);

    public static string LaterKey(TryLaterItem i) => "later:" + i.Key;
    public static string GroupKey(string name) => "group:" + name.Trim().ToLowerInvariant();

    static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
    static DateTime LaterStamp(TryLaterItem i) => Utc(i.ModifiedAt is { } m && m > i.SavedAt ? m : i.SavedAt);
    static string Json(object o) => JsonSerializer.Serialize(o, o.GetType(), Store.Options);

    /// <summary>Picks the newer of two versions; when the times are equal the choice is decided by content, so both devices pick the same one.</summary>
    static T Newer<T>(T a, T b, Func<T, DateTime> stamp)
    {
        var sa = stamp(a); var sb = stamp(b);
        if (sa != sb) return sa > sb ? a : b;
        return string.CompareOrdinal(Json(a!), Json(b!)) >= 0 ? a : b;
    }

    public static SyncDoc Merge(SyncDoc a, SyncDoc b, DateTime now)
    {
        now = Utc(now);
        var result = new SyncDoc { Version = Math.Max(a.Version, b.Version), UpdatedAt = now, WrittenBy = a.UpdatedAt >= b.UpdatedAt ? a.WrittenBy : b.WrittenBy };

        // deletions, newest wins, forgotten after 90 days
        var tombs = new Dictionary<string, DateTime>();
        foreach (var src in new[] { a.Tombstones, b.Tombstones })
            foreach (var (k, t) in src) { var u = Utc(t); if (!tombs.TryGetValue(k, out var m) || m < u) tombs[k] = u; }

        // apps
        var apps = new Dictionary<string, SyncApp>();
        foreach (var app in a.Apps.Concat(b.Apps))
        {
            if (string.IsNullOrEmpty(app.Key)) continue;
            apps[app.Key] = apps.TryGetValue(app.Key, out var have) ? Newer(have, app, x => Utc(x.ModifiedAt)) : app;
        }
        foreach (var (key, app) in apps.ToList())
        {
            if (!tombs.TryGetValue(key, out var del)) continue;
            if (del >= Utc(app.ModifiedAt) && del >= Utc(app.AddedAt)) apps.Remove(key);       // deleted after its last edit
            else tombs.Remove(key);                                                          // re-added or edited after the deletion
        }

        // try later
        var later = new Dictionary<string, TryLaterItem>();
        foreach (var item in a.TryLater.Concat(b.TryLater))
            later[item.Key] = later.TryGetValue(item.Key, out var have) ? Newer(have, item, LaterStamp) : item;
        foreach (var (key, item) in later.ToList())
        {
            var tk = LaterKey(item);
            if (!tombs.TryGetValue(tk, out var del)) continue;
            if (del >= LaterStamp(item)) later.Remove(key); else tombs.Remove(tk);
        }

        // groups
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { a.GroupStamps, b.GroupStamps })
            foreach (var (k, t) in src) { var u = Utc(t); if (!stamps.TryGetValue(k, out var m) || m < u) stamps[k] = u; }
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in a.CustomGroups.Concat(b.CustomGroups).Where(g => !string.IsNullOrWhiteSpace(g)))
        {
            if (groups.TryGetValue(g, out var spelled)) { if (string.CompareOrdinal(g, spelled) > 0) groups[g] = g; continue; }
            groups[g] = g;
        }
        foreach (var name in groups.Keys.ToList())
        {
            var tk = GroupKey(name);
            if (!tombs.TryGetValue(tk, out var del)) continue;
            if (!stamps.TryGetValue(name, out var made) || del >= made) { groups.Remove(name); stamps.Remove(name); }
            else tombs.Remove(tk);
        }

        // devices
        foreach (var d in a.Devices.Values.Concat(b.Devices.Values))
        {
            if (string.IsNullOrEmpty(d.Id)) continue;
            result.Devices[d.Id] = result.Devices.TryGetValue(d.Id, out var have) ? Newer(have, d, x => Utc(x.LastSeen)) : d;
        }

        foreach (var kv in tombs.Where(k => k.Value >= now - TombstoneLife)) result.Tombstones[kv.Key] = kv.Value;
        result.Apps = apps.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        result.TryLater = later.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        result.CustomGroups = groups.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        result.GroupStamps = stamps;
        return result;
    }

    /// <summary>What the document says, without bookkeeping that changes on every sync (time written, who wrote it, when each device last checked in).
    /// A device's online state and its list of installed programs do count: when they change, the file is written.</summary>
    public static string Signature(SyncDoc d)
    {
        var copy = new SyncDoc
        {
            Version = d.Version, UpdatedAt = default, WrittenBy = "",
            Apps = d.Apps.OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
            TryLater = d.TryLater.OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
            CustomGroups = d.CustomGroups.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            GroupStamps = d.GroupStamps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k.Key.ToLowerInvariant(), k => Utc(k.Value)),
            Tombstones = d.Tombstones.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => Utc(k.Value)),
        };
        foreach (var dev in d.Devices.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            copy.Devices[dev.Id] = new SyncDevice
            {
                Id = dev.Id, Name = dev.Name, Platform = dev.Platform, AppVersion = dev.AppVersion, Online = dev.Online,
                Installed = dev.Installed?.OrderBy(i => i.Key, StringComparer.Ordinal).ToList(),      // what a device has installed, and whether it is online, are part of what the file says
            };
        return Json(copy);
    }
}
