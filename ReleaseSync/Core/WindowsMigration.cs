using System;
using System.IO;
using Microsoft.Win32;

namespace ReleaseSync.Core;

/// <summary>The Windows-only part of bringing an old GitSync install across: Start menu folder, "start with Windows" entry and the old link handler.</summary>
public static class WindowsMigration
{
    /// <summary>Renames a "start with Windows" entry, pointing it at the new program. Returns true if there was an old entry.</summary>
    public static bool MoveRunValue(RegistryKey runKey, string oldName, string newName, string exePath)
    {
        if (runKey.GetValue(oldName) == null) return false;
        runKey.SetValue(newName, $"\"{exePath}\" --background");
        runKey.DeleteValue(oldName, false);
        return true;
    }

    /// <summary>Moves the Start menu folder and "start with Windows" entry to the new name, and removes the old link handler.</summary>
    public static void Run()
    {
        if (Legacy.IsTestMode) return;
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var oldMenu = Path.Combine(programs, Legacy.OldName); var newMenu = Path.Combine(programs, "ReleaseSync");
            if (Legacy.MoveDirectoryIfNeeded(oldMenu, newMenu))
            {
                try { File.Delete(Path.Combine(newMenu, Legacy.OldName + ".lnk")); } catch { }      // the old notification shortcut; a new one is made when needed
            }
        }
        catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run != null && Environment.ProcessPath is { Length: > 0 } exe) MoveRunValue(run, Legacy.OldName, "ReleaseSync", exe);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Legacy.OldName.ToLowerInvariant(), false); } catch { }
    }
}
