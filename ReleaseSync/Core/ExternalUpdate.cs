using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReleaseSync.Core;

/// <summary>
/// Notices when an app was updated without ReleaseSync, for example by its own built-in updater, so an update isn't offered for a version that's
/// already installed. Every source must name one of the app's releases to count, so a guess never replaces what ReleaseSync recorded.
/// </summary>
public static class ExternalUpdate
{
    /// <summary>The release that's really installed, when the PC says so, or null.</summary>
    public static string? Detect(TrackedApp app)
    {
        var tags = app.Releases.Select(r => r.Tag).ToList();
        if (tags.Count == 0) return null;
        var found = new List<string>();

        // installers record their version with Windows, and self-updaters usually run the installer again
        if (VersionUtil.MatchTag(UninstallService.Get(app.UninstallKey)?.Version, tags) is { } listed) found.Add(listed);

        if (!string.IsNullOrEmpty(app.InstallPath) && File.Exists(app.InstallPath))
        {
            var since = app.InstalledAt ?? DateTime.MinValue;
            found.AddRange(FromVersionFiles(app, Path.GetDirectoryName(app.InstallPath)!, since, tags));
            var exe = new FileInfo(app.InstallPath);
            if (exe.LastWriteTimeUtc > since.AddMinutes(2))      // only a file that changed since ReleaseSync put it there is worth reading
            {
                if (MatchingRelease(app, exe.FullName) is { } same) found.Add(same);
                var info = FileVersionInfo.GetVersionInfo(exe.FullName);
                if ((VersionUtil.MatchTag(info.ProductVersion, tags) ?? VersionUtil.MatchTag(info.FileVersion, tags)) is { } stamped) found.Add(stamped);
            }
        }

        return found.Distinct().OrderByDescending(t => t, Comparer<string>.Create((a, b) => app.IsNewer(a, b) ? 1 : app.IsNewer(b, a) ? -1 : 0)).FirstOrDefault();
    }

    /// <summary>
    /// Version files an app writes for itself, like hookshot_version.txt. One in the app's own folder counts, and below it only when its name or folder
    /// mentions the app, because apps often share a folder and one mustn't take another's version.
    /// </summary>
    static IEnumerable<string> FromVersionFiles(TrackedApp app, string folder, DateTime since, List<string> tags)
    {
        var name = app.Repo.ToLowerInvariant();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*version*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).Take(50).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(folder, file).ToLowerInvariant();
            bool own = !relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(name);
            if (!own || info.Length > 64 * 1024 || info.LastWriteTimeUtc <= since || info.Extension.ToLowerInvariant() is not ("" or ".txt" or ".json" or ".ini" or ".cfg")) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var tag in VersionUtil.TagsIn(text, tags)) yield return tag;
        }
    }

    /// <summary>The release whose published checksum matches this exact file.</summary>
    static string? MatchingRelease(TrackedApp app, string file)
    {
        var fileName = Path.GetFileName(file);
        var withDigest = app.Releases.SelectMany(r => r.Assets.Where(a => a.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Select(a => (r.Tag, Sha: SelfUpdate.Sha256From(a.Digest))))
            .Where(x => x.Sha != null).ToList();
        if (withDigest.Count == 0) return null;
        string mine;
        try { using var stream = File.OpenRead(file); mine = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return withDigest.FirstOrDefault(x => x.Sha == mine).Tag;
    }
}
