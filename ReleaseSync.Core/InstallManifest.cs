using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace ReleaseSync.Core;

/// <summary>
/// Which files an app's release put into a folder, so removing the app deletes only those and never the user's own files
/// that happen to share the folder. It stays on this device and is not part of the synced library.
/// </summary>
public static class InstallManifest
{
    public sealed class Data
    {
        public string Dir { get; set; } = "";
        public List<string> Files { get; set; } = new();
        public DateTime UpdatedAt { get; set; }
    }

    public sealed record RemoveResult(int Removed, int Failed, int LeftBehind);

    static string FileFor(string appId) => Path.Combine(Paths.DataDir, "installs", Fmt.SafeName(appId) + ".json");

    /// <summary>A path inside the app's folder, written with "/" so a list made on one system reads on another. Null for anything that isn't a plain relative path.</summary>
    static string? Clean(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var p = rel.Replace('\\', '/').TrimStart('/');
        if (p.Length == 0 || p.EndsWith('/') || p.Contains(':')) return null;
        var parts = p.Split('/');
        return parts.Any(s => s.Length == 0 || s == "." || s == "..") ? null : p;
    }

    /// <summary>Every file under a folder, relative to it.</summary>
    public static List<string> ListDirectory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Clean(Path.GetRelativePath(root, f))).Where(p => p != null).Select(p => p!).ToList();

    /// <summary>
    /// The files an archive unpacks to. A single top-level folder is dropped, the same way installing does, so the list matches what ended up on disk.
    /// </summary>
    public static List<string> ListZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => Clean(e.FullName)).Where(p => p != null).Select(p => p!).ToList();
        var topLevel = zip.Entries.Select(e => e.FullName.Replace('\\', '/').TrimStart('/')).Where(p => p.Length > 0).Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        if (topLevel.Count == 1 && names.All(n => n.StartsWith(topLevel[0] + "/", StringComparison.Ordinal)))
            names = names.Select(n => n[(topLevel[0].Length + 1)..]).ToList();
        return names;
    }

    /// <summary>Remembers the files installed into <paramref name="dir"/>. Files recorded by an earlier version of the same app are kept, so an old file that a newer release dropped can still be removed.</summary>
    public static void Save(string appId, string dir, IEnumerable<string> files)
    {
        try
        {
            var existing = Load(appId, dir);
            var all = (existing ?? new List<string>()).Concat(files.Select(Clean).Where(p => p != null).Select(p => p!)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var file = FileFor(appId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Data { Dir = Path.GetFullPath(dir), Files = all, UpdatedAt = DateTime.UtcNow }));
            File.Move(tmp, file, true);
        }
        catch { }
    }

    /// <summary>The recorded files for this app in this folder, or null if there is no record (or it was made for a different folder).</summary>
    public static List<string>? Load(string appId, string dir) => LoadData(appId, dir)?.Files;

    static Data? LoadData(string appId, string dir)
    {
        try
        {
            var file = FileFor(appId);
            if (!File.Exists(file)) return null;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(file));
            if (d == null || !string.Equals(d.Dir.TrimEnd('\\', '/'), Path.GetFullPath(dir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
            d.Files = d.Files.Select(Clean).Where(p => p != null).Select(p => p!).ToList();
            return d.Files.Count == 0 ? null : d;
        }
        catch { return null; }
    }

    public static void Forget(string appId)
    {
        try { File.Delete(FileFor(appId)); } catch { }
    }

    /// <summary>
    /// Deletes just the listed files from <paramref name="dir"/> (never anything outside it), then any folders that are left empty.
    /// Everything else in the folder stays.
    /// </summary>
    public static RemoveResult RemoveListed(string dir, IEnumerable<string> files)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/');
        int removed = 0, failed = 0;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in files.Select(Clean).Where(p => p != null))
        {
            var path = Path.GetFullPath(Path.Combine(root, rel!.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                removed++;
                for (var d = Path.GetDirectoryName(path); d != null && d.Length > root.Length; d = Path.GetDirectoryName(d)) folders.Add(d);
            }
            catch { failed++; }
        }
        foreach (var d in folders.OrderByDescending(f => f.Length))
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        int left = 0;
        try { if (Directory.Exists(root)) left = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(); } catch { }
        return new RemoveResult(removed, failed, left);
    }
}
