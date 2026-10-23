using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReleaseSync.Core;

public sealed class BackupManifest
{
    public string Format { get; set; } = "releasesync-backup";
    public int Version { get; set; } = 1;
    public DateTime Created { get; set; } = DateTime.Now;
    public string Machine { get; set; } = Environment.MachineName;
    public string ReleaseSyncVersion { get; set; } = "";
    public int AppCount { get; set; }
    public bool IncludesAppFiles { get; set; }
    public List<ManifestApp> Apps { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
}

public sealed class ManifestApp
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";       // folder inside the zip under apps/
    public string TargetDir { get; set; } = "";    // where the files go when restored
    public int Files { get; set; }
}

/// <summary>Backs up the whole library to a folder the user chose (a NAS, OneDrive, a USB drive…): library data, and optionally portable apps' files.</summary>
public static class FullBackup
{
    public sealed record Result(bool Ok, string? Path, string Message, long Size);

    static readonly Regex NameRx = new(@"^(?:ReleaseSync|GitSync)-(library|full)-\d{8}-\d{6}\.zip$", RegexOptions.Compiled);
    static readonly string[] DataFiles = { "settings.json", "apps.json", "trylater.json" };

    /// <summary>The folder holding a portable app's files, if ReleaseSync can safely treat it as the app's own.</summary>
    public static string? AppFolder(TrackedApp app)
    {
        try
        {
            var own = InstallService.PortableTarget(app);
            if (Directory.Exists(own) && InstallService.IsSafeToDelete(own)) return own;
            if (string.IsNullOrEmpty(app.UninstallKey) && !string.IsNullOrEmpty(app.InstallPath) && File.Exists(app.InstallPath))
            {
                var dir = Path.GetDirectoryName(app.InstallPath)!;
                var loose = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
                if (InstallService.IsSafeToDelete(dir) && !loose.Any(l => dir.TrimEnd('\\').Equals(l, StringComparison.OrdinalIgnoreCase))) return dir;
            }
        }
        catch { }
        return null;
    }

    static void AddFile(ZipArchive za, string source, string entryName)
    {
        var e = za.CreateEntry(entryName, CompressionLevel.Optimal);
        using var inp = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var outp = e.Open();
        inp.CopyTo(outp);
    }

    public static Result Run(IEnumerable<TrackedApp> apps, bool includeAppFiles, string folder, int keep, CancellationToken ct, Action<string>? status = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return new(false, null, "No backup location is set.", 0);
            var root = Path.GetPathRoot(folder);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return new(false, null, $"Can't reach {folder} (is the drive or NAS available?). ReleaseSync will try again later.", 0);
            Directory.CreateDirectory(folder);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var finalPath = Path.Combine(folder, $"ReleaseSync-{(includeAppFiles ? "full" : "library")}-{stamp}.zip");
            var tmp = finalPath + ".part";
            var manifest = new BackupManifest { ReleaseSyncVersion = Environment.GetEnvironmentVariable("RELEASESYNC_VERSION") ?? "", IncludesAppFiles = includeAppFiles, AppCount = apps.Count() };

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                status?.Invoke("Saving your library and settings…");
                foreach (var f in DataFiles)
                {
                    var src = Path.Combine(Paths.DataDir, f);
                    if (File.Exists(src)) AddFile(za, src, "library/" + f);
                }

                if (includeAppFiles)
                {
                    foreach (var app in apps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var dir = AppFolder(app);
                        if (dir == null) continue;
                        status?.Invoke($"Saving {app.Title}'s files…");
                        long size = 0;
                        try { size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => { return x.Length; }); } catch { }
                        if (size > AppBackups.MaxBytes) { manifest.Skipped.Add($"{app.Title} (over {Fmt.Size(AppBackups.MaxBytes)})"); continue; }
                        var entryRoot = "apps/" + Fmt.SafeName(app.Owner.Replace('/', '-') + "-" + app.Repo) + "/";
                        int count = 0;
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            ct.ThrowIfCancellationRequested();
                            try { AddFile(za, f, entryRoot + Path.GetRelativePath(dir, f).Replace('\\', '/')); count++; }
                            catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                        manifest.Apps.Add(new ManifestApp { Name = app.Title, Folder = entryRoot.TrimEnd('/'), TargetDir = dir, Files = count });
                    }
                }
                var m = za.CreateEntry("manifest.json");
                using (var w = new StreamWriter(m.Open())) w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            File.Move(tmp, finalPath, true);

            if (keep > 0)
                foreach (var old in new DirectoryInfo(folder).GetFiles("*.zip").Where(f => NameRx.IsMatch(f.Name)).OrderByDescending(f => f.Name).Skip(keep))
                { try { old.Delete(); } catch { } }

            var size2 = new FileInfo(finalPath).Length;
            var extra = manifest.Skipped.Count > 0 ? $" Skipped: {string.Join(", ", manifest.Skipped)}." : "";
            return new(true, finalPath, $"Backed up to {finalPath} ({Fmt.Size(size2)}{(includeAppFiles ? $", {manifest.Apps.Count} apps' files" : "")}).{extra}", size2);
        }
        catch (OperationCanceledException) { return new(false, null, "Backup cancelled.", 0); }
        catch (Exception ex) { return new(false, null, "Backup failed: " + ex.Message, 0); }
    }

    public static BackupManifest? ReadManifest(string zipPath)
    {
        try
        {
            using var za = ZipFile.OpenRead(zipPath);
            var e = za.GetEntry("manifest.json");
            if (e == null) return null;
            using var r = new StreamReader(e.Open());
            return JsonSerializer.Deserialize<BackupManifest>(r.ReadToEnd());
        }
        catch { return null; }
    }

    /// <summary>Puts the library files back (and optionally the apps' files). The current library is saved first.</summary>
    public static string Restore(string zipPath, bool restoreAppFiles)
    {
        DataBackups.Create();
        var manifest = ReadManifest(zipPath) ?? throw new InvalidOperationException("That doesn't look like a ReleaseSync backup.");
        int filesBack = 0;
        using var za = ZipFile.OpenRead(zipPath);
        foreach (var f in DataFiles)
        {
            var e = za.GetEntry("library/" + f);
            if (e == null) continue;
            e.ExtractToFile(Path.Combine(Paths.DataDir, f), true);
        }
        if (restoreAppFiles)
        {
            foreach (var app in manifest.Apps)
            {
                var target = Path.GetFullPath(app.TargetDir);
                if (!InstallService.IsSafeToDelete(target)) continue;
                foreach (var e in za.Entries.Where(x => x.FullName.StartsWith(app.Folder + "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                {
                    var dest = Path.GetFullPath(Path.Combine(target, e.FullName[(app.Folder.Length + 1)..]));
                    if (!dest.StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;      // never write outside the app's folder
                    try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); e.ExtractToFile(dest, true); filesBack++; } catch { }
                }
            }
        }
        return restoreAppFiles ? $"Restored your library and {filesBack} app files." : "Restored your library and settings.";
    }
}
