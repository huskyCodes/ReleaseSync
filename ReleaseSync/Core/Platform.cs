using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ReleaseSync.Core;

/// <summary>Windows integration: the background-check scheduled task, the releasesync:// link handler and toast notifications.</summary>
public static class Platform
{
    public const string TaskName = "ReleaseSync background update check";
    public const string Aumid = "ReleaseSync.App";
    static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Test builds (custom data folder) must not register themselves with Windows — that would point real shortcuts and links at a debug exe.</summary>
    static bool Skip => Environment.GetEnvironmentVariable("RELEASESYNC_DATA") is { Length: > 0 } && Environment.GetEnvironmentVariable("RELEASESYNC_ALLOW_PLATFORM") != "1";

    // ------------------------------------------------------------ scheduled task
    /// <summary>Creates (or removes) the Windows scheduled task that checks for updates while ReleaseSync is closed.</summary>
    public static Task ApplyBackgroundTaskAsync(AppSettings s) => Task.Run(() =>
    {
        try
        {
            if (Skip) return;
            RunHidden("schtasks.exe", $"/Delete /TN \"{Legacy.OldName} background update check\" /F");      // the task from before the rename
            if (!s.BackgroundChecks) { RunHidden("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F"); return; }
            int mins = s.CheckIntervalMinutes <= 0 ? 360 : s.CheckIntervalMinutes;
            string sched = mins % 1440 == 0 ? $"DAILY /MO {mins / 1440}" : mins % 60 == 0 ? $"HOURLY /MO {mins / 60}" : $"MINUTE /MO {mins}";
            RunHidden("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /SC {sched} /TR \"\\\"{ExePath}\\\" --check-once\"");
        }
        catch { }
    });

    public static bool BackgroundTaskExists()
    {
        try { return RunHidden("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0; } catch { return false; }
    }

    static int RunHidden(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        if (p == null) return -1;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }

    // ------------------------------------------------------------ network drives
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>
    /// A drive letter that points at a NAS (Y:) exists only for the user's own session; programs started as administrator can't see it.
    /// The network path (\\server\share) works for both, so use that when installing onto a NAS.
    /// </summary>
    public static string ToNetworkPath(string path)
    {
        try
        {
            if (path.Length >= 2 && path[1] == ':')
            {
                var sb = new StringBuilder(512); int len = sb.Capacity;
                if (WNetGetConnection(path[..2], sb, ref len) == 0 && sb.Length > 0) return sb.ToString().TrimEnd('\\') + path[2..];
            }
        }
        catch { }
        return path;
    }

    public static bool IsNetworkPath(string? path) => !string.IsNullOrEmpty(path) && ToNetworkPath(path).StartsWith(@"\\", StringComparison.Ordinal);

    // ------------------------------------------------------------ keeping the Windows shell alive
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);

    /// <summary>True while the taskbar exists.</summary>
    public static bool HasShell() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    /// <summary>
    /// Some installers (7-Zip's, for one) ask Windows to shut Explorer down so they can replace a file it has loaded, and Explorer
    /// doesn't always come back, leaving a bare desktop with no taskbar. If that happened, bring Explorer back.
    /// </summary>
    public static async Task RestoreShellIfMissingAsync()
    {
        try
        {
            for (int i = 0; i < 12; i++)                 // give the installer a chance to restart it itself
            {
                if (HasShell()) return;
                await Task.Delay(1000).ConfigureAwait(false);
            }
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } }
            await Task.Delay(1500).ConfigureAwait(false);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------ releasesync:// links
    public static void RegisterProtocol()
    {
        if (Skip) return;
        try
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Legacy.OldName.ToLowerInvariant(), false); } catch { }
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\releasesync");
            k.SetValue("", "URL:ReleaseSync");
            k.SetValue("URL Protocol", "");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch { }
    }

    // ------------------------------------------------------------ toast notifications
    static bool aumidReady;

    /// <summary>Toasts from a desktop app need a Start menu shortcut that carries the app's AppUserModelID.</summary>
    static bool EnsureShortcut()
    {
        if (aumidReady) return true;
        if (Skip) return false;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ReleaseSync");
            Directory.CreateDirectory(dir);
            var lnk = Path.Combine(dir, "ReleaseSync.lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = ExePath; sc.WorkingDirectory = Path.GetDirectoryName(ExePath); sc.Description = "ReleaseSync";
            sc.Save();
            SetAppId(lnk, Aumid);
            return aumidReady = true;
        }
        catch { return false; }
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint c); void GetAt(uint i, out PropertyKey k);
        void GetValue(ref PropertyKey k, out PropVariant v);
        void SetValue(ref PropertyKey k, ref PropVariant v);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    static void SetAppId(string lnkPath, string appId)
    {
        var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 2 /* read-write */, ref iid, out var store);
        var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };   // System.AppUserModel.ID
        var v = new PropVariant { vt = 31 /* VT_LPWSTR */, ptr = Marshal.StringToCoTaskMemUni(appId) };
        try { store.SetValue(ref key, ref v); store.Commit(); }
        finally { Marshal.FreeCoTaskMem(v.ptr); Marshal.ReleaseComObject(store); }
    }

    /// <summary>Shows a Windows toast. Buttons open releasesync:// links, which ReleaseSync handles. Returns false if toasts aren't available.</summary>
    public static bool ShowToast(string title, string body, params (string Label, string Uri)[] buttons)
    {
        try
        {
            if (!EnsureShortcut()) return false;
            RegisterProtocol();
            var xml = new StringBuilder();
            xml.Append("<toast launch=\"releasesync://open\" activationType=\"protocol\"><visual><binding template=\"ToastGeneric\">");
            xml.Append($"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text></binding></visual>");
            if (buttons.Length > 0)
            {
                xml.Append("<actions>");
                foreach (var (label, uri) in buttons) xml.Append($"<action content=\"{SecurityElement.Escape(label)}\" arguments=\"{SecurityElement.Escape(uri)}\" activationType=\"protocol\"/>");
                xml.Append("</actions>");
            }
            xml.Append("</toast>");
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml.ToString());
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid).Show(new Windows.UI.Notifications.ToastNotification(doc));
            return true;
        }
        catch { return false; }
    }
}

