using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ReleaseSync.Core;

public sealed class BackupInfo
{
    public string ZipPath { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string? Version { get; set; }
    public string Reason { get; set; } = "update";
    public DateTime Created { get; set; }
    public long SizeBytes { get; set; }
    public bool IsFile { get; set; }                 // a single program file rather than a folder
    [System.Text.Json.Serialization.JsonIgnore] public string Title => (Reason == "removal" ? "Before it was removed" : "Before an update") + (string.IsNullOrEmpty(Version) ? "" : $"  ·  {Version}");
    [System.Text.Json.Serialization.JsonIgnore] public string Detail => $"{Fmt.Ago(Created.ToUniversalTime())}  ·  {Fmt.Size(SizeBytes)}";
}

/// <summary>Copies of an app's folder taken just before ReleaseSync changes it, so a failed update can be undone and settings are never at risk.</summary>
public static class AppBackups
{
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;      // bigger folders (games…) are skipped
    const int KeepPerApp = 3;

    public static string Root => Path.Combine(Paths.LocalDir, "Backups");
    public static string FolderFor(TrackedApp app) => Path.Combine(Root, Fmt.SafeName(app.Owner.Replace('/', '-') + "-" + app.Repo));

    static long DirSize(string dir, long cap)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += f.Length;
                if (total > cap) return total;
            }
        }
        catch { }
        return total;
    }

    /// <summary>Backs up a folder (or a single file). Returns the backup, or null when backups are off, the folder is too big, or nothing was readable.</summary>
    public static BackupInfo? Create(TrackedApp app, string source, string reason, CancellationToken ct = default)
    {
        var s = AppSettingsProvider.Current;
        if (!s.BackupBeforeUpdate) return null;
        bool isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) return null;
        try
        {
            if (!isFile && DirSize(source, MaxBytes) > MaxBytes) return null;
            Directory.CreateDirectory(FolderFor(app));
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zip = Path.Combine(FolderFor(app), $"{stamp}-{reason}.zip");
            var tmp = zip + ".part";
            int added = 0;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                IEnumerable<string> files = isFile ? new[] { source } : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
                var baseDir = isFile ? Path.GetDirectoryName(source)! : source;
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var entry = za.CreateEntry(Path.GetRelativePath(baseDir, f).Replace('\\', '/'), CompressionLevel.Fastest);
                        using var inp = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var outp = entry.Open();
                        inp.CopyTo(outp);
                        added++;
                    }
                    catch (IOException) { /* a file held open by something else: leave it out rather than fail the whole backup */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (added == 0) { File.Delete(tmp); return null; }
            File.Move(tmp, zip, true);
            var info = new BackupInfo { ZipPath = zip, TargetDir = isFile ? Path.GetDirectoryName(source)! : source, Version = app.InstalledVersion, Reason = reason, Created = DateTime.Now, SizeBytes = new FileInfo(zip).Length, IsFile = isFile };
            File.WriteAllText(zip + ".json", JsonSerializer.Serialize(info));
            Prune(app);
            return info;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }          // a backup that can't be made must never be the reason an update fails
    }

    public static List<BackupInfo> List(TrackedApp app)
    {
        var result = new List<BackupInfo>();
        try
        {
            var dir = FolderFor(app);
            if (!Directory.Exists(dir)) return result;
            foreach (var meta in Directory.GetFiles(dir, "*.zip.json"))
            {
                try
                {
                    var b = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(meta));
                    if (b != null && File.Exists(b.ZipPath)) result.Add(b);
                }
                catch { }
            }
        }
        catch { }
        return result.OrderByDescending(b => b.Created).ToList();
    }

    static void Prune(TrackedApp app)
    {
        foreach (var old in List(app).Skip(KeepPerApp))
        {
            try { File.Delete(old.ZipPath); File.Delete(old.ZipPath + ".json"); } catch { }
        }
    }

    /// <summary>Puts a backup's files back where they were (overwriting what's there; other files are left alone).</summary>
    public static void Restore(BackupInfo b)
    {
        Directory.CreateDirectory(b.TargetDir);
        using var za = ZipFile.OpenRead(b.ZipPath);
        foreach (var e in za.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;                                   // a folder entry
            var dest = Path.GetFullPath(Path.Combine(b.TargetDir, e.FullName));
            if (!dest.StartsWith(Path.GetFullPath(b.TargetDir), StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the target
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }

    public static long TotalSize()
    {
        try { return Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; } catch { return 0; }
    }
}

/// <summary>Dated copies of ReleaseSync's own data (library, preferences, try-later list), so a damaged file or a mistake is never final.</summary>
public static class DataBackups
{
    const int Keep = 10;
    static readonly string[] Files = { "settings.json", "apps.json", "trylater.json" };
    public static string Root => Path.Combine(Paths.DataDir, "Backups");

    public sealed class Entry
    {
        public string Folder { get; init; } = "";
        public DateTime Created { get; init; }
        public int Apps { get; init; }
        public string Label => $"{Created:MMM d, yyyy  h:mm tt}  ·  {Apps} app{(Apps == 1 ? "" : "s")}";
    }

    public static string? Create()
    {
        try
        {
            var dest = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dest);
            int copied = 0;
            foreach (var f in Files)
            {
                var src = Path.Combine(Paths.DataDir, f);
                if (File.Exists(src)) { File.Copy(src, Path.Combine(dest, f), true); copied++; }
            }
            if (copied == 0) { Directory.Delete(dest); return null; }
            foreach (var old in Directory.GetDirectories(Root).OrderByDescending(d => d).Skip(Keep)) { try { Directory.Delete(old, true); } catch { } }
            return dest;
        }
        catch { return null; }
    }

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (var d in Directory.GetDirectories(Root).OrderByDescending(x => x))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(d), "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)) continue;
                int apps = 0;
                try { var f = Path.Combine(d, "apps.json"); if (File.Exists(f)) using (var doc = JsonDocument.Parse(File.ReadAllText(f))) apps = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0; } catch { }
                list.Add(new Entry { Folder = d, Created = t, Apps = apps });
            }
        }
        catch { }
        return list;
    }

    /// <summary>A backup is made automatically at most every 12 hours.</summary>
    public static void CreateIfDue()
    {
        var last = List().FirstOrDefault();
        if (last == null || (DateTime.Now - last.Created).TotalHours >= 12) Create();
    }

    /// <summary>Copies a backup's files over the live ones. The caller must restart ReleaseSync without saving afterwards.</summary>
    public static void RestoreFiles(Entry e)
    {
        Create();      // keep the current state too, in case the restore was a mistake
        foreach (var f in Files)
        {
            var src = Path.Combine(e.Folder, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(Paths.DataDir, f), true);
        }
    }

    /// <summary>Used when a data file can't be read: the newest backup that holds a readable copy, or null.</summary>
    public static string? NewestReadable(string fileName)
    {
        foreach (var e in List())
        {
            var p = Path.Combine(e.Folder, fileName);
            if (!File.Exists(p)) continue;
            try { using var _ = JsonDocument.Parse(File.ReadAllText(p)); return p; } catch { }
        }
        return null;
    }
}
