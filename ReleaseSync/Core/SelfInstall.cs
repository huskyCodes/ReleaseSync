using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ReleaseSync.Core;

/// <summary>
/// ReleaseSync is one downloaded file. On first run it offers to put itself in a permanent folder, so nobody has to move the file or make a shortcut by hand
/// (Windows can block shortcuts made by hand from a downloaded file). It adds itself to Windows' list of installed apps, and can be uninstalled from there or from Settings.
/// </summary>
public static class SelfInstall
{
    /// <summary>Where it installs for this user. Needs no administrator rights. Tests use their own folder and never touch the real one.</summary>
    public static string Folder =>
        Legacy.IsTestMode ? Environment.GetEnvironmentVariable("RELEASESYNC_TEST_INSTALL_DIR") ?? ""
                          : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ReleaseSync");

    public static string Target => Path.Combine(Folder, "ReleaseSync.exe");

    /// <summary>True when this is a downloaded copy that could be installed: a one-file release, opened by the user, not already in place, and not one they chose to keep where it is.</summary>
    public static bool ShouldOffer(string? declinedPath)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(Folder)) return false;
        if (!string.IsNullOrEmpty(typeof(SelfInstall).Assembly.Location)) return false;      // a developer build is many files, not one
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--background") || args.Contains("--check-once")) return false;
        if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(exe, declinedPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies this program into place, unless a newer version is already there.</summary>
    public static void CopyIntoPlace()
    {
        Directory.CreateDirectory(Folder);
        var exe = Environment.ProcessPath!;
        if (!File.Exists(Target) || !IsNewer(Target, exe)) File.Copy(exe, Target, true);
    }

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ReleaseSync";

    public static bool RunningInstalled =>
        !string.IsNullOrEmpty(Folder) && string.Equals(Environment.ProcessPath, Target, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists the installed copy in Windows' Installed apps, with an Uninstall button that opens ReleaseSync's own uninstall dialog.</summary>
    public static void RegisterInWindows(string version)
    {
        if (Legacy.IsTestMode || !RunningInstalled) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "ReleaseSync");
            k.SetValue("DisplayVersion", version);
            k.SetValue("Publisher", "ReleaseSync");
            k.SetValue("DisplayIcon", Target);
            k.SetValue("InstallLocation", Folder);
            k.SetValue("UninstallString", $"\"{Target}\" releasesync://uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>Removes the shortcuts and Windows entries, then deletes the program a moment after it has closed (a running program can't delete itself). Apps installed through ReleaseSync are left alone.</summary>
    public static void Remove(bool deleteData)
    {
        if (!Legacy.IsTestMode)
        {
            var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ReleaseSync");
            try { File.Delete(Path.Combine(menu, "ReleaseSync.lnk")); } catch { }
            try { if (Directory.Exists(menu) && !Directory.EnumerateFileSystemEntries(menu).Any()) Directory.Delete(menu); } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ReleaseSync.lnk")); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\releasesync", false); } catch { }
        }
        var steps = new List<string> { "ping -n 4 127.0.0.1 >nul", $"del /f /q \"{Target}\"", $"rmdir \"{Folder}\"" };
        if (deleteData)
            foreach (var dir in new[] { Paths.DataDir, Paths.LocalDir })
                if (InstallService.IsSafeToDelete(dir)) steps.Add($"rmdir /s /q \"{dir}\"");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", steps)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
    }

    static bool IsNewer(string a, string b) =>
        Version.TryParse(FileVersionInfo.GetVersionInfo(a).FileVersion, out var va) && Version.TryParse(FileVersionInfo.GetVersionInfo(b).FileVersion, out var vb) && va > vb;

    public static void AddDesktopShortcut()
    {
        if (Legacy.IsTestMode) return;
        try
        {
            var exe = Environment.ProcessPath!;
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(t)!;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ReleaseSync.lnk");
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe; sc.WorkingDirectory = Path.GetDirectoryName(exe); sc.Description = "ReleaseSync";
            sc.Save();
            Platform.SetAppId(lnk, Platform.Aumid);
        }
        catch { }
    }
}
