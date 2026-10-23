using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

/// <summary>Reads the packages winget has installed and finds the project page each one points to.</summary>
public static class WingetService
{
    static string Run(string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
            return output.Wait(3000) ? output.Result : "";
        }
        catch { return ""; }
    }

    public static bool IsAvailable()
    {
        var t = Run("--version", 8000);
        return t.Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Packages installed from the winget source: display name, package id and installed version.</summary>
    public static List<(string Name, string Id, string Version)> List()
    {
        var result = new List<(string, string, string)>();
        var text = Run("list --source winget --accept-source-agreements --disable-interactivity", 120000);
        var lines = text.Replace("\r", "").Split('\n');
        int h = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Name") && l.Contains(" Id") && l.Contains("Version"));
        if (h < 0 || h + 2 >= lines.Length) return result;
        var header = lines[h];
        int iId = header.IndexOf(" Id", StringComparison.Ordinal) + 1, iVer = header.IndexOf("Version", StringComparison.Ordinal);
        int iAvail = header.IndexOf("Available", StringComparison.Ordinal), iSrc = header.IndexOf("Source", StringComparison.Ordinal);
        int verEnd = iAvail > 0 ? iAvail : iSrc > 0 ? iSrc : -1;
        for (int i = h + 2; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length <= iVer) continue;
            var name = l[..Math.Min(iId, l.Length)].Trim();
            var id = l[iId..iVer].Trim();
            var ver = verEnd > iVer && l.Length >= verEnd ? l[iVer..verEnd].Trim() : l[iVer..].Trim();
            if (id.Length > 0 && id.Contains('.') && name.Length > 0) result.Add((name, id, ver.Split(' ')[0]));
        }
        return result;
    }

    /// <summary>The project's GitHub repository, taken from the package's homepage / publisher links.</summary>
    public static (string Owner, string Repo)? FindRepo(string id)
    {
        var text = Run($"show --id {id} --exact --source winget --accept-source-agreements --disable-interactivity", 40000);
        foreach (var key in new[] { "Homepage", "Publisher Url", "Package Url", "Publisher Support Url", "Release Notes Url" })
            foreach (var line in text.Replace("\r", "").Split('\n'))
                if (line.TrimStart().StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) && UninstallService.ParseGitHubUrl(line) is { } hit) return hit;
        return null;
    }
}

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

public sealed class SyncDoc
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string WrittenBy { get; set; } = Environment.MachineName;
    public List<SyncApp> Apps { get; set; } = new();
    public List<TryLaterItem> TryLater { get; set; } = new();
    public List<string> CustomGroups { get; set; } = new();
    public Dictionary<string, DateTime> Tombstones { get; set; } = new();
}
