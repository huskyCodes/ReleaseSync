using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ReleaseSync.UI;

namespace ReleaseSync;

public partial class App : Application
{
    Mutex? mutex;
    EventWaitHandle? showEvent;
    public static MainViewModel VM { get; private set; } = null!;
    MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // a custom data folder (testing) gets its own instance name so it never collides with a normal copy
        var tag = Environment.GetEnvironmentVariable("RELEASESYNC_DATA") is { Length: > 0 } d ? "." + d.ToLowerInvariant().Aggregate(17u, (h, ch) => h * 31 + ch) : "";
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler(CenterTip));
        mutex = new Mutex(true, @"Local\ReleaseSync.SingleInstance" + tag, out bool first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ReleaseSync.Show" + tag);
        System.Windows.Input.CommandManager.RequerySuggested += (_, _) => ReleaseSync.Core.RelayCommand.RaiseRequery();      // buttons re-check whether they are enabled
        ReleaseSync.Core.Secrets.Store = new ReleaseSync.Core.WindowsSecretStore();                 // tokens live in Windows Credential Manager
        ReleaseSync.Core.Secrets.LegacyUnprotect = ReleaseSync.Core.Secret.Unprotect;               // tokens saved by very old versions
        bool checkOnce = e.Args.Contains("--check-once");
        var uri = e.Args.FirstOrDefault(a => a.StartsWith("releasesync://", StringComparison.OrdinalIgnoreCase) || a.StartsWith(ReleaseSync.Core.Legacy.OldName + "://", StringComparison.OrdinalIgnoreCase));
        if (!first)
        {
            // another copy is already running: a background check has nothing to do, a releasesync:// link is handed over
            if (!checkOnce)
            {
                if (uri != null) { try { System.IO.File.WriteAllText(CommandFile, uri); } catch { } }
                AllowSetForegroundWindow(-1);       // this copy was just launched by the user, so it may hand the foreground to the running one
                showEvent.Set();
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            try { VM?.AddLog("Unexpected error: " + args.Exception.Message, "error"); VM?.Toast("Something went wrong: " + args.Exception.Message, "error"); } catch { }
            args.Handled = true;
        };

        try { VM = new MainViewModel(); }
        catch (Exception ex) { LogCrash(ex); MessageBox.Show("ReleaseSync couldn't start:\n\n" + ex.Message + "\n\nDetails were saved to " + System.IO.Path.Combine(ReleaseSync.Core.Paths.DataDir, "error.log"), "ReleaseSync", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); return; }
        ReleaseSync.Core.WindowsMigration.Run();      // first run after the rename: Start menu, start-with-Windows and link handler move to the new name
        VM.Settings.DownloadFolder = ReleaseSync.Core.Legacy.RemapLocalPath(VM.Settings.DownloadFolder);
        ThemeManager.Apply(VM.Settings);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (VM.Settings.Theme == "System") Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply(VM.Settings)));
        };

        if (checkOnce)
        {
            _ = RunCheckOnceAsync();       // no window: look for updates, tell the user, quit
            return;
        }

        try { window = new MainWindow(VM); }
        catch (Exception ex) { LogCrash(ex); MessageBox.Show("ReleaseSync couldn't open its window:\n\n" + ex.Message + "\n\nDetails were saved to " + System.IO.Path.Combine(ReleaseSync.Core.Paths.DataDir, "error.log"), "ReleaseSync", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); return; }
        // "start minimized" is for when Windows launches ReleaseSync at sign-in (--background); opening it yourself always shows the window
        if (VM.Settings.StartMinimized && uri == null && e.Args.Contains("--background"))
            window.StartHidden();
        else
            window.Show();

        Task.Run(() =>
        {
            while (showEvent.WaitOne())
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    window?.BringToFront();
                    var cmd = ReadCommand();
                    if (cmd != null) VM.HandleCommand(cmd);
                }));
        });
        _ = VM.StartAsync();
        ApplyLaunchArgs(e.Args);
        if (uri != null) VM.HandleCommand(uri);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Hover tips sit centred under whatever they describe.</summary>
    public static void CenterTip(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ToolTip tip && tip.PlacementTarget is FrameworkElement target)
        {
            tip.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            tip.HorizontalOffset = (target.ActualWidth - tip.ActualWidth) / 2;
        }
    }

    static string CommandFile => System.IO.Path.Combine(ReleaseSync.Core.Paths.DataDir, "command.txt");

    static string? ReadCommand()
    {
        try
        {
            if (!System.IO.File.Exists(CommandFile)) return null;
            var text = System.IO.File.ReadAllText(CommandFile).Trim();
            System.IO.File.Delete(CommandFile);
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    /// <summary>The scheduled task's run: check, notify, quit — without ever showing a window.</summary>
    async Task RunCheckOnceAsync()
    {
        System.Windows.Forms.NotifyIcon? balloon = null;
        bool balloonShown = false;
        VM.NotifyRequest = (title, text) =>
        {
            // used only when Windows toasts aren't available
            try
            {
                balloon = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true };
                balloon.BalloonTipClicked += (_, _) => { ReleaseSync.UI.Shell.Open(Environment.ProcessPath); };
                balloon.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info);
                balloonShown = true;
            }
            catch { }
        };
        try { await VM.RunBackgroundAsync(); }
        catch (Exception ex) { LogCrash(ex); }
        await Task.Delay(balloonShown ? 12000 : 1500);
        try { balloon?.Dispose(); } catch { }
        try { VM.SaveAll(); } catch { }
        Shutdown();
    }

    static void LogCrash(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ReleaseSync.Core.Paths.DataDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(ReleaseSync.Core.Paths.DataDir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { }
    }

    /// <summary>Developer shortcuts: --page=Settings, --section=GitHub, --open=owner/repo, --add=owner/repo, --search=text.</summary>
    static void ApplyLaunchArgs(string[] args)
    {
        string? Arg(string key) => args.FirstOrDefault(a => a.StartsWith("--" + key + "=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        if (Arg("section") is { } sec) VM.SettingsSection = sec;
        if (Arg("page") is { } page) VM.Navigate(page);
        if (Arg("open") is { } open && open.Contains('/')) { var p = open.Split('/'); var a = VM.Find(p[0], p[1]); if (a != null) VM.SelectedApp = a; }
        if (Arg("add") is { } add && ReleaseSync.Core.SourceClient.TryParse(add, out var addRef) && addRef != null) _ = VM.OpenAddPreviewAsync(addRef.Owner, addRef.Repo, null, addRef.Source, addRef.FullPath);
        if (Arg("relink") is { } rl && rl.Contains('|')) { var parts = rl.Split('|', 2); var ra = parts[0].Split('/'); var target = VM.Find(ra[0], ra[1]); if (target != null && ReleaseSync.Core.SourceClient.TryParse(parts[1], out var newRef) && newRef != null) _ = VM.RelinkAsync(target, newRef, null); }
        if (args.Contains("--check")) _ = VM.CheckAllAsync(true);
        if (Arg("install") is { } inst && inst.Contains('/')) { var p = inst.Split('/'); var a = VM.Find(p[0], p[1]); if (a != null) _ = Task.Delay(4000).ContinueWith(_ => Current.Dispatcher.Invoke(() => _ = VM.InstallAsync(a))); }
        if (args.Contains("--newgroup")) VM.NewGroupCommand.Execute(null);
        if (args.Contains("--scan")) VM.ScanCommand.Execute(null);
        if (Arg("owner") is { } own) _ = VM.ShowOwnerAsync(own, append: false);
        if (Arg("details") is { } det && det.Contains('/')) { var p = det.Split('/'); _ = VM.OpenDetailsAsync(p[0], p[1], null); }
        if (Arg("search") is { } q) { VM.Navigate("Discover"); _ = VM.RunSearchAsync(q, $"Results for “{q}”"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { VM?.SaveAll(); } catch { }
        try { VM?.SyncOnExit(); } catch { }
        try { mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
