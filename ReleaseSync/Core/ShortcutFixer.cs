using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReleaseSync.Core;

/// <summary>Finds the user's own shortcuts to an app (desktop, Start menu, taskbar) and points them at its new folder after a move, so nothing stops working.</summary>
public static class ShortcutFixer
{
    /// <summary>Where shortcuts live. Tests use their own folder and never touch the real ones.</summary>
    static IEnumerable<string> Folders()
    {
        if (Legacy.IsTestMode)
        {
            var test = Environment.GetEnvironmentVariable("RELEASESYNC_TEST_SHORTCUTS");
            return string.IsNullOrEmpty(test) ? Array.Empty<string>() : new[] { test };
        }
        var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), pinned,
        };
    }

    static dynamic? Shell()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        return t == null ? null : Activator.CreateInstance(t);
    }

    /// <summary>Shortcuts that open something being moved: anything in the folder, or just the listed files when the folder is shared.</summary>
    public static List<string> Find(string oldDir, IReadOnlyCollection<string>? onlyFiles)
    {
        var found = new List<string>();
        try
        {
            var shell = Shell();
            if (shell == null) return found;
            var listed = onlyFiles?.Select(f => f.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Folders().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(3000))
                {
                    try
                    {
                        string target = shell.CreateShortcut(lnk).TargetPath;
                        if (FolderMove.Rebase(target, oldDir, oldDir) == null) continue;
                        if (listed != null && !listed.Contains(Path.GetRelativePath(oldDir, target))) continue;
                        found.Add(lnk);
                    }
                    catch { }
                }
        }
        catch { }
        return found;
    }

    /// <summary>Points the shortcuts at the new folder. Returns how many couldn't be changed (shortcuts shared by everyone on the PC need administrator rights).</summary>
    public static int Repoint(IEnumerable<string> shortcuts, string oldDir, string newDir)
    {
        int failed = 0;
        var shell = Shell();
        foreach (var lnk in shortcuts)
        {
            try
            {
                var s = shell!.CreateShortcut(lnk);
                string target = s.TargetPath, work = s.WorkingDirectory, icon = s.IconLocation;
                s.TargetPath = FolderMove.Rebase(target, oldDir, newDir) ?? target;
                if (FolderMove.Rebase(work, oldDir, newDir) is { } w) s.WorkingDirectory = w;
                if (FolderMove.Rebase(icon, oldDir, newDir) is { } i) s.IconLocation = i;
                s.Save();
            }
            catch { failed++; }
        }
        return failed;
    }
}
