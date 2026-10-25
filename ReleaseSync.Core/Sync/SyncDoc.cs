using System;
using System.Collections.Generic;

namespace ReleaseSync.Core;

// ---------------- library sync document ----------------
public sealed class SyncApp
{
    public string Key { get; set; } = "";
    public string Source { get; set; } = "github";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string? Group { get; set; }
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
    public bool IncludePrereleases { get; set; }
    public bool IgnoreUpdates { get; set; }
    public bool AutoUpdate { get; set; }
    public InstallMode Mode { get; set; }
    public string? CustomArgs { get; set; }
    public string? DisplayName { get; set; }
    public string? Note { get; set; }
    public string? Tags { get; set; }
    public string? SkippedVersion { get; set; }
    public string? AssetPattern { get; set; }
    public bool TrustUnsigned { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>One of your devices, as the other ones see it in the sync file.</summary>
public sealed class SyncDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"Windows", "Mac", "Android", "iPhone"…</summary>
    public string Platform { get; set; } = "Windows";
    public string AppVersion { get; set; } = "";
    public DateTime LastSeen { get; set; }
}

public sealed class SyncDoc
{
    public int Version { get; set; } = 2;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string WrittenBy { get; set; } = Environment.MachineName;
    public List<SyncApp> Apps { get; set; } = new();
    public List<TryLaterItem> TryLater { get; set; } = new();
    public List<string> CustomGroups { get; set; } = new();
    /// <summary>When each custom group was created, so a group that was deleted and later re-created isn't deleted again by an older deletion.</summary>
    public Dictionary<string, DateTime> GroupStamps { get; set; } = new();
    /// <summary>What was deleted and when: apps (<c>github:owner/repo</c>), try-later items (<c>later:github:owner/repo</c>) and groups (<c>group:name</c>).</summary>
    public Dictionary<string, DateTime> Tombstones { get; set; } = new();
    public Dictionary<string, SyncDevice> Devices { get; set; } = new();
}
