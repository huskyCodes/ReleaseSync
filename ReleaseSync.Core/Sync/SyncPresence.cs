using System;
using System.Collections.Generic;
using System.Linq;

namespace ReleaseSync.Core;

/// <summary>
/// Whether another device looks connected, and what it has installed. There is no direct link between devices (they meet in the shared file), so
/// "connected" means: the device said it is online, and it said so recently. A device writes <see cref="SyncDevice.Online"/> = true when ReleaseSync
/// opens, false when it closes normally, and refreshes <see cref="SyncDevice.LastSeen"/> every <see cref="SyncService.Heartbeat"/> while it runs.
/// If it stops without saying so (a crash, a lid closed), it turns grey after <see cref="OnlineWindow"/>.
/// </summary>
public static class SyncPresence
{
    /// <summary>A little longer than the heartbeat, so one late refresh doesn't flicker the light.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(150);
    /// <summary>A device clock this far ahead of ours is still believed (clocks differ a little).</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(10);
    /// <summary>Readers ignore anything past this many installed programs in one device's list.</summary>
    public const int MaxInstalled = 1000;

    static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;

    public static bool IsOnline(SyncDevice d, DateTime now)
    {
        if (d.Online != true) return false;
        var age = Utc(now) - Utc(d.LastSeen);
        return age <= OnlineWindow && age >= -ClockSlack;
    }

    /// <summary>The device's list of installed programs, made safe to read: no empty keys, each key once (the last entry wins), in key order, at most <see cref="MaxInstalled"/>.</summary>
    public static List<SyncInstall> Inventory(SyncDevice d)
    {
        var byKey = new Dictionary<string, SyncInstall>(StringComparer.Ordinal);
        foreach (var i in d.Installed ?? new()) if (!string.IsNullOrEmpty(i.Key)) byKey[i.Key] = i;
        return byKey.Values.OrderBy(i => i.Key, StringComparer.Ordinal).Take(MaxInstalled).ToList();
    }

    /// <summary>An update is waiting on that device: it knows of a newer version than the one it has.</summary>
    public static bool HasUpdate(SyncInstall i) =>
        !string.IsNullOrEmpty(i.Latest) && !string.IsNullOrEmpty(i.Version) && VersionUtil.IsNewer(i.Latest, i.Version);
}
