using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ReleaseSync.Core;

/// <summary>
/// Moves an app's files to another folder without ever risking them: everything is copied and compared first, and the originals are only removed once all of it is safely
/// in place. If anything goes wrong before that, the copies are cleaned up and the app is exactly where it was.
/// </summary>
public static class FolderMove
{
    public sealed record Result(int Moved, int LeftBehind);

    static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
    static bool Inside(string path, string folder) => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>A path (or an icon location like "C:\app\a.exe,0") in its new place after a move, or null if it isn't inside the old folder.</summary>
    public static string? Rebase(string? path, string oldDir, string newDir)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var from = Full(oldDir);
        if (string.Equals(path.TrimEnd('\\', '/'), from, StringComparison.OrdinalIgnoreCase)) return Full(newDir);
        return Inside(path, from) ? Full(newDir) + path[from.Length..] : null;
    }

    /// <summary>Why this move can't be done, in plain words, or null if it can.</summary>
    public static string? Problem(string source, string destination, long bytesNeeded = 0)
    {
        var from = Full(source); var to = Full(destination);
        if (!Directory.Exists(from)) return "Its current folder can't be found.";
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return "It's already in that folder.";
        if (Inside(to, from)) return "That folder is inside its current folder. Choose a folder somewhere else.";
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return "That folder already has files in it. Choose an empty folder.";
        try
        {
            var root = Path.GetPathRoot(to);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "That drive isn't available.";
            if (bytesNeeded > 0 && !string.Equals(root, Path.GetPathRoot(from), StringComparison.OrdinalIgnoreCase) && new DriveInfo(root).AvailableFreeSpace < bytesNeeded)
                return "There isn't enough free space on that drive.";
        }
        catch (ArgumentException) { }      // a network path: the copy itself will say if there's no room
        return null;
    }

    /// <summary>The size of what would be moved: the listed files, or the whole folder when no list is given.</summary>
    public static long Size(string source, IReadOnlyList<string>? onlyThese) =>
        Files(Full(source), onlyThese).Sum(rel => new FileInfo(Path.Combine(Full(source), rel)).Length);

    /// <summary>Relative paths of what to move: the listed files that exist, or everything in the folder.</summary>
    static List<string> Files(string from, IReadOnlyList<string>? onlyThese) =>
        onlyThese != null
            ? onlyThese.Select(f => f.Replace('/', Path.DirectorySeparatorChar)).Where(f => File.Exists(Path.Combine(from, f))).ToList()
            : Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(from, f)).ToList();

    /// <summary>
    /// Moves the listed files (other things in the folder stay), or the whole folder when no list is given. Throws if the files couldn't be copied; the originals are then untouched.
    /// LeftBehind counts originals that couldn't be removed afterwards: the app is safely in its new place, and only leftovers remain in the old one.
    /// </summary>
    public static Result Move(string source, string destination, IReadOnlyList<string>? onlyThese, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var from = Full(source); var to = Full(destination);
        if (Problem(from, to) is { } problem) throw new IOException(problem);

        var files = Files(from, onlyThese);
        bool madeFolder = !Directory.Exists(to);
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(to);
            if (onlyThese == null)
                foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(from, files[i]); var dst = Path.Combine(to, files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                copied.Add(dst);
                File.Copy(src, dst, false);
                if (!Same(src, dst)) throw new IOException($"{files[i]} didn't copy correctly.");
                progress?.Report((i + 1) * 100.0 / files.Count);
            }
        }
        catch
        {
            foreach (var f in copied) { try { File.Delete(f); } catch { } }
            try { if (Directory.Exists(to)) RemoveEmptyFolders(to, madeFolder); } catch { }
            throw;
        }

        // everything is safely in the new place: only now do the originals go
        int left = 0;
        foreach (var rel in files)
        {
            try { var f = Path.Combine(from, rel); File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            catch { left++; }
        }
        try { RemoveEmptyFolders(from, onlyThese == null); } catch { }
        return new Result(files.Count, left);
    }

    /// <summary>Removes the empty folders under a folder, and the folder itself if asked and it ended up empty.</summary>
    static void RemoveEmptyFolders(string folder, bool includingItself)
    {
        foreach (var d in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        if (includingItself && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    static bool Same(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
