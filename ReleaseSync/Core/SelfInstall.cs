using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReleaseSync.Core;

/// <summary>
/// ReleaseSync is one downloaded file. On first run it offers to put itself in a permanent folder, so nobody has to move the file or make a shortcut by hand
/// (Windows can block shortcuts and copies that still carry the "downloaded from the web" mark).
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

    /// <summary>Copies this program into place, unless a newer version is already there, and clears the "downloaded from the web" mark on the copy. The user has just chosen to install it.</summary>
    public static void CopyIntoPlace()
    {
        Directory.CreateDirectory(Folder);
        var exe = Environment.ProcessPath!;
        if (!File.Exists(Target) || !IsNewer(Target, exe)) File.Copy(exe, Target, true);
        try { File.Delete(Target + ":Zone.Identifier"); } catch { }
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
            dynamic sc = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ReleaseSync.lnk"));
            sc.TargetPath = exe; sc.WorkingDirectory = Path.GetDirectoryName(exe); sc.Description = "ReleaseSync";
            sc.Save();
        }
        catch { }
    }
}
