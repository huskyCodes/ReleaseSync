using System;
using System.IO;

namespace ReleaseSync.Core;

/// <summary>
/// ReleaseSync was called GitSync before it was renamed. This brings an existing install across the first time the new version runs:
/// the library and settings are copied (the old folder is left alone), the saved token is copied, and the "start with Windows",
/// background-check and Start menu entries are moved to the new name. Nothing of the old install is deleted.
/// </summary>
public static class Legacy
{
    /// <summary>The old name, kept in one place (and spelled out in two parts so a global rename never touches it).</summary>
    public const string OldName = "Git" + "Sync";

    /// <summary>Test copies (a custom data folder) must never touch the real user's folders, registry or credentials.</summary>
    public static bool IsTestMode => Environment.GetEnvironmentVariable("RELEASESYNC_DATA") is { Length: > 0 };

    // ---------- helpers that work on any folders (so they can be tested without touching real ones) ----------
    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false); } catch (IOException) { }      // never overwrite, never fail the whole copy for one locked file
        }
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Copies the old data folder to the new one when the new one doesn't exist yet. The old folder stays as it was.</summary>
    public static bool CopyDataIfNeeded(string oldDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return false;
        try { CopyDirectory(oldDir, newDir); return true; }
        catch { return Directory.Exists(newDir); }
    }

    /// <summary>Moves a folder to its new name. If it can't be moved (the old copy of the app may still be using it) the old folder simply stays in use.</summary>
    public static bool MoveDirectoryIfNeeded(string oldDir, string newDir)
    {
        if (Directory.Exists(newDir)) return true;
        if (!Directory.Exists(oldDir)) return false;
        try { Directory.Move(oldDir, newDir); return true; }
        catch { return false; }
    }

    // ---------- the real thing, run once at start-up ----------
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The per-user local folder: ReleaseSync's own, or the old one when it could not be moved yet.</summary>
    public static string ResolveLocalDir()
    {
        var neu = Path.Combine(Local, "ReleaseSync");
        if (IsTestMode) return neu;
        var old = Path.Combine(Local, OldName);
        return MoveDirectoryIfNeeded(old, neu) || !Directory.Exists(old) ? neu : old;
    }

    /// <summary>If a saved path points into the old local folder, point it at the folder now in use.</summary>
    public static string RemapLocalPath(string path)
    {
        var oldRoot = Path.Combine(Local, OldName);
        if (path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(Paths.LocalDir, oldRoot, StringComparison.OrdinalIgnoreCase))
            return Paths.LocalDir + path[oldRoot.Length..];
        return path;
    }

}
