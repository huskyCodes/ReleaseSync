using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ReleaseSync.Core;

namespace ReleaseSync.UI;

public sealed partial class MainViewModel
{
    public string AppVersionLabel => "ReleaseSync " + AppVersion;
    public string AppVersionShort => "Version " + AppVersion;
    public static string AppVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    // ===================== notifications =====================
    /// <summary>Shows a Windows toast (with buttons) when possible, else the tray balloon.</summary>
    public void Notify(string title, string body, params (string Label, string Uri)[] buttons)
    {
        if (Settings.UseToasts && Platform.ShowToast(title, body, buttons)) return;
        NotifyRequest?.Invoke(title, body);
    }

    // ===================== safety hints on the Add / details screens =====================
    /// <summary>Plain-language signals about a project, so a newcomer can judge it at a glance. Informational — never blocks anything.</summary>
    public async Task<List<HintItem>> BuildHintsAsync(RepoInfo info, List<ReleaseInfo>? releases, string source)
    {
        var hints = new List<HintItem>();
        if (!Settings.ShowSafetyHints) return hints;
        var now = DateTime.UtcNow;
        string Span(TimeSpan t) => t.TotalDays < 60 ? $"{(int)t.TotalDays} days" : t.TotalDays < 700 ? $"{(int)(t.TotalDays / 30)} months" : $"{t.TotalDays / 365:0.#} years";

        if (info.CreatedAt is { } created)
        {
            var age = now - created.ToUniversalTime();
            if (age.TotalDays < 30) hints.Add(new("warn", $"This repository is only {Span(age)} old."));
            else if (age.TotalDays >= 730) hints.Add(new("good", $"Active for {Span(age)}."));
        }
        if (info.Archived) hints.Add(new("warn", "It's archived — no longer maintained."));
        else if (info.PushedAt is { } pushed && (now - pushed.ToUniversalTime()).TotalDays > 730) hints.Add(new("warn", $"Not updated since {pushed:MMM yyyy}."));
        if (info.Stars < 5 && source == "github") hints.Add(new("warn", $"Very few people have starred it ({info.Stars})."));
        else if (info.Stars >= 1000) hints.Add(new("good", $"Widely used ({Fmt.Count(info.Stars)} stars)."));
        if (info.License == null) hints.Add(new("warn", "No license is stated."));
        if (info.Fork) hints.Add(new("info", "This is a fork of another project."));
        if (releases != null && releases.FirstOrDefault(r => !r.Draft) is { } latest && latest.Assets.Any(a => !string.IsNullOrEmpty(a.Digest)))
            hints.Add(new("good", "Its downloads come with checksums ReleaseSync can verify."));

        if (source == "github")
        {
            try
            {
                var owner = await GitHub.GetUserAsync(info.OwnerLogin);
                if (owner.CreatedAt is { } oc && (now - oc.ToUniversalTime()).TotalDays < 60) hints.Add(new("warn", $"The publisher's GitHub account is only {Span(now - oc.ToUniversalTime())} old."));
                else if (string.Equals(owner.Type, "Organization", StringComparison.OrdinalIgnoreCase)) hints.Add(new("good", $"Published by an organization ({owner.DisplayName})."));
            }
            catch { }
        }
        return hints.OrderBy(h => h.Level == "warn" ? 0 : h.Level == "info" ? 1 : 2).Take(6).ToList();
    }

    // ===================== backups =====================
    List<DataBackups.Entry> _dataBackups = new();
    public List<DataBackups.Entry> DataBackupList { get => _dataBackups; private set { Set(ref _dataBackups, value); Raise(nameof(HasDataBackups)); Raise(nameof(DataBackupSummary)); } }
    public bool HasDataBackups => _dataBackups.Count > 0;
    DataBackups.Entry? _selectedDataBackup; public DataBackups.Entry? SelectedDataBackup { get => _selectedDataBackup; set => Set(ref _selectedDataBackup, value); }
    public string DataBackupSummary => _dataBackups.Count == 0 ? "No backups yet." : $"{_dataBackups.Count} backup{(_dataBackups.Count == 1 ? "" : "s")}, newest from {_dataBackups[0].Created:MMM d, h:mm tt}. App folders backed up before updates use {Fmt.Size(AppBackups.TotalSize())}.";

    public ICommand BackupDataNowCommand { get; private set; } = null!;
    public ICommand RestoreDataCommand { get; private set; } = null!;
    public ICommand OpenBackupsFolderCommand { get; private set; } = null!;
    public ICommand RestoreBackupCommand { get; private set; } = null!;

    void RefreshDataBackups()
    {
        DataBackupList = DataBackups.List();
        SelectedDataBackup ??= DataBackupList.FirstOrDefault();
        if (SelectedDataBackup != null && !DataBackupList.Any(b => b.Folder == SelectedDataBackup.Folder)) SelectedDataBackup = DataBackupList.FirstOrDefault();
    }

    void InitBackups()
    {
        BackupDataNowCommand = new RelayCommand(() =>
        {
            SaveAll();                                            // make sure the copy reflects what's on screen
            var path = DataBackups.Create();
            RefreshDataBackups();
            Toast(path != null ? "Backed up your library and settings" : "Nothing to back up yet", path != null ? "success" : "info");
        });
        OpenBackupsFolderCommand = new RelayCommand(p => Shell.Reveal(p?.ToString() == "apps" ? AppBackups.Root : DataBackups.Root));
        RestoreDataCommand = new RelayCommand(p => { if ((p as DataBackups.Entry ?? SelectedDataBackup) is { } e) AskRestoreData(e); });
        RestoreBackupCommand = new RelayCommand(async p => { if (p is BackupInfo b && DrawerApp != null) await RestoreAppBackupAsync(DrawerApp, b); });
        InitScheduledBackups();
    }

    // ---- scheduled backups to a folder the user chose ----
    public static readonly List<Choice> BackupIntervalChoices = new()
    {
        new("Off", 0), new("Every 6 hours", 6), new("Every 12 hours", 12), new("Daily", 24), new("Every 3 days", 72), new("Weekly", 168), new("Every 2 weeks", 336), new("Monthly", 720),
    };
    public static readonly List<Choice> BackupKeepChoices = new() { new("Newest 5", 5), new("Newest 10", 10), new("Newest 30", 30), new("Newest 100", 100), new("Keep all", 0) };
    public static readonly List<Choice> BackupScopeChoices = new()
    {
        new("Library and settings (small)", false),
        new("Library, settings and portable apps' files (can be large)", true),
    };

    readonly DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    DateTime lastBackupAttempt = DateTime.MinValue;
    bool backupFailureToasted;
    bool _backingUp; public bool IsBackingUp { get => _backingUp; private set { Set(ref _backingUp, value); Raise(nameof(CanBackUpNow)); } }
    public bool CanBackUpNow => !_backingUp && !string.IsNullOrWhiteSpace(Settings.BackupFolder);
    string _backupStatus = ""; public string BackupStatus { get => _backupStatus; private set { Set(ref _backupStatus, value); Raise(nameof(BackupSummary)); } }
    public string BackupSummary
    {
        get
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(Settings.BackupFolder)) lines.Add("Choose a folder to save backups into — a NAS, a cloud-synced folder like OneDrive, or an external drive.");
            else if (Settings.BackupIntervalHours <= 0) lines.Add("Scheduled backups are off. You can still use “Back up now”.");
            else
            {
                var next = (Settings.LastScheduledBackup ?? DateTime.Now).AddHours(Settings.BackupIntervalHours);
                lines.Add(Settings.LastScheduledBackup == null ? "The first backup runs shortly." : $"Next backup: {(next <= DateTime.Now ? "due now" : next.ToString("ddd MMM d, h:mm tt"))}.");
            }
            if (Settings.LastScheduledBackup is { } last) lines.Add($"Last successful backup: {last:ddd MMM d, h:mm tt}.");
            if (!string.IsNullOrEmpty(_backupStatus)) lines.Add(_backupStatus);
            return string.Join("  ", lines);
        }
    }
    public ICommand ChooseBackupFolderCommand { get; private set; } = null!;
    public ICommand ClearBackupFolderCommand { get; private set; } = null!;
    public ICommand BackupToFolderNowCommand { get; private set; } = null!;
    public ICommand RestoreFromFileCommand { get; private set; } = null!;

    bool BackupDue() =>
        !string.IsNullOrWhiteSpace(Settings.BackupFolder) && Settings.BackupIntervalHours > 0
        && (Settings.LastScheduledBackup == null || (DateTime.Now - Settings.LastScheduledBackup.Value).TotalHours >= Settings.BackupIntervalHours)
        && (DateTime.Now - lastBackupAttempt).TotalMinutes >= 60;       // after a failure, wait an hour before trying again

    public async Task RunScheduledBackupIfDueAsync() { if (BackupDue()) await RunBackupAsync(false); }

    public async Task RunBackupAsync(bool manual)
    {
        if (IsBackingUp) return;
        var folder = Settings.BackupFolder;
        if (string.IsNullOrWhiteSpace(folder)) { if (manual) Toast("Choose a backup folder first", "info"); return; }
        IsBackingUp = true; lastBackupAttempt = DateTime.Now;
        BackupStatus = "Backing up…";
        try
        {
            SaveAll();                                                  // so the copy matches what's on screen
            var apps = Apps.ToList(); bool full = Settings.BackupIncludeApps; int keep = Settings.BackupKeep;
            var result = await Task.Run(() => FullBackup.Run(apps, full, folder!, keep, CancellationToken.None,
                msg => Application.Current?.Dispatcher.BeginInvoke(new Action(() => BackupStatus = msg))));
            if (result.Ok)
            {
                Settings.LastScheduledBackup = DateTime.Now; ScheduleSaveSettings();
                BackupStatus = result.Message; backupFailureToasted = false;
                AddLog(result.Message, "success");
                if (manual) Toast("Backup saved", "success");
            }
            else
            {
                BackupStatus = result.Message;
                AddLog(result.Message, "warn");
                if (manual || !backupFailureToasted) { Toast(result.Message, "warn"); backupFailureToasted = true; }
            }
        }
        catch (Exception ex) { BackupStatus = "Backup failed: " + ex.Message; AddLog(BackupStatus, "warn"); }
        finally { IsBackingUp = false; Raise(nameof(BackupSummary)); }
    }

    void InitScheduledBackups()
    {
        ChooseBackupFolderCommand = new RelayCommand(() =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where ReleaseSync should save backups (a NAS, a OneDrive/Dropbox folder, or an external drive)", UseDescriptionForTitle = true, ShowNewFolderButton = true, SelectedPath = Settings.BackupFolder ?? "" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) Settings.BackupFolder = dlg.SelectedPath;
        });
        ClearBackupFolderCommand = new RelayCommand(() => Settings.BackupFolder = null);
        BackupToFolderNowCommand = new RelayCommand(async () => await RunBackupAsync(true));
        RestoreFromFileCommand = new RelayCommand(AskRestoreFromFile);
        backupTimer.Tick += async (_, _) => await RunScheduledBackupIfDueAsync();
        backupTimer.Start();
    }

    void AskRestoreFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a ReleaseSync backup", Filter = "ReleaseSync backups (ReleaseSync-*.zip, GitSync-*.zip)|ReleaseSync-*.zip;GitSync-*.zip|All zip files|*.zip", InitialDirectory = Settings.BackupFolder is { } f && System.IO.Directory.Exists(f) ? f : "" };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FileName;
        var m = FullBackup.ReadManifest(path);
        if (m == null) { Toast("That file isn't a ReleaseSync backup.", "error"); return; }
        bool hasFiles = m.IncludesAppFiles && m.Apps.Count > 0;
        Prompt = new PromptVM
        {
            Title = "Restore this backup?",
            Message = $"Made {m.Created:MMMM d, yyyy 'at' h:mm tt} on {m.Machine}: {m.AppCount} app{(m.AppCount == 1 ? "" : "s")} in the library" + (hasFiles ? $", plus the files of {m.Apps.Count} portable app{(m.Apps.Count == 1 ? "" : "s")}" : "") +
                      ". What you have now is backed up first, and ReleaseSync restarts to finish.",
            OkText = "Restore and restart", IsDanger = true,
            ShowCheck = hasFiles, CheckText = hasFiles ? $"Also put back the apps' files ({m.Apps.Count})" : "", Checked = false,
            OnOk = p =>
            {
                try { var msg = FullBackup.Restore(path, p.Checked); AddLog(msg, "success"); RestartWithoutSaving(); }
                catch (Exception ex) { return "Couldn't restore: " + ex.Message; }
                return null;
            }
        };
    }

    /// <summary>Starts a fresh copy a moment from now and leaves without saving (saving would overwrite what was just restored).</summary>
    static void RestartWithoutSaving()
    {
        var exe = Environment.ProcessPath!;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & start \"\" \"{exe}\"") { CreateNoWindow = true, UseShellExecute = false });
        Environment.Exit(0);
    }

    void AskRestoreData(DataBackups.Entry e)
    {
        Prompt = new PromptVM
        {
            Title = "Restore this backup?",
            Message = $"ReleaseSync will go back to how your library and settings were on {e.Created:MMMM d, yyyy 'at' h:mm tt} ({e.Apps} app{(e.Apps == 1 ? "" : "s")}). What you have now is backed up first, so you can undo this. ReleaseSync restarts to finish.",
            OkText = "Restore and restart", IsDanger = true,
            OnOk = _ =>
            {
                try
                {
                    DataBackups.RestoreFiles(e);
                    RestartWithoutSaving();
                }
                catch (Exception ex) { return "Couldn't restore: " + ex.Message; }
                return null;
            }
        };
    }

    async Task RestoreAppBackupAsync(TrackedApp app, BackupInfo b)
    {
        var ok = await ShowPromptAsync(new PromptVM
        {
            Title = $"Restore {app.Title} from this backup?",
            Message = $"{b.Title}  ({b.Detail}). The saved files go back into {b.TargetDir}, replacing the current versions of those files. Anything else in the folder is left alone.",
            OkText = "Restore it", IsDanger = true,
        });
        if (!ok) return;
        var running = RunningApps.Find(app);
        if (running.Count > 0)
        {
            if (!await ShowPromptAsync(new PromptVM { Title = $"{app.Title} is running", Message = "It has to be closed to put the old files back.", OkText = "Close it and restore" })) return;
            if (!await RunningApps.CloseAsync(running)) { Toast($"Couldn't close {app.Title}. Close it yourself and try again.", "error"); return; }
        }
        try
        {
            await Task.Run(() => AppBackups.Restore(b));
            if (!string.IsNullOrEmpty(b.Version)) app.InstalledVersion = b.Version;
            AddLog($"Restored {app.Title} from a backup ({b.Version})", "success");
            Toast($"{app.Title} was restored{(string.IsNullOrEmpty(b.Version) ? "" : " to " + b.Version)}", "success");
        }
        catch (Exception ex) { Toast("Couldn't restore: " + ex.Message, "error"); }
        app.NotifyAll(); ScheduleSaveApps();
    }

    // ===================== choosing where an app is installed =====================
    public ICommand ChooseInstallDirCommand { get; private set; } = null!;
    public ICommand ResetInstallDirCommand { get; private set; } = null!;

    /// <summary>Asks for a folder. If the folder already holds other things (or is a broad location like Documents), the app gets its own sub-folder inside it.</summary>
    string? PickInstallFolder(string appTitle, string repo, string? start)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Choose where {appTitle} should be installed. If the folder isn't empty, ReleaseSync puts the app in its own sub-folder.",
            UseDescriptionForTitle = true, ShowNewFolderButton = true,
            SelectedPath = !string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start) ? start : "",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        var chosen = dlg.SelectedPath.TrimEnd('\\');
        bool crowded = System.IO.Directory.Exists(chosen) && System.IO.Directory.EnumerateFileSystemEntries(chosen).Any();
        bool alreadyOwn = System.IO.Path.GetFileName(chosen).Equals(Fmt.SafeName(repo), StringComparison.OrdinalIgnoreCase);
        return (crowded && !alreadyOwn) || !InstallService.IsSafeToDelete(chosen) ? System.IO.Path.Combine(chosen, Fmt.SafeName(repo)) : chosen;
    }

    void InitInstallDir()
    {
        ChooseInstallDirCommand = new RelayCommand(p =>
        {
            switch (p)
            {
                case AddPreviewVM v:
                    if (PickInstallFolder(v.RepoName, v.RepoName, v.InstallDir) is { } d1) { v.InstallDir = d1; if (v.Draft != null) v.Draft.CustomInstallDir = d1; }
                    break;
                case TrackedApp a:
                    if (PickInstallFolder(a.Title, a.Repo, a.CustomInstallDir ?? a.InstallPath) is { } d2)
                    {
                        a.CustomInstallDir = d2; ScheduleSaveApps();
                        Toast($"{a.Title} will go to {d2} the next time it's installed or updated. The current copy isn't moved.", "info");
                    }
                    break;
            }
        });
        ResetInstallDirCommand = new RelayCommand(p =>
        {
            switch (p)
            {
                case AddPreviewVM v: v.InstallDir = null; if (v.Draft != null) v.Draft.CustomInstallDir = null; break;
                case TrackedApp a: a.CustomInstallDir = null; ScheduleSaveApps(); Toast($"{a.Title} goes back to the default install location.", "info"); break;
            }
        });
    }

    // ===================== pointing an app at a different repository =====================
    TrackedApp? _relinkTarget;
    public TrackedApp? RelinkTarget { get => _relinkTarget; set { if (Set(ref _relinkTarget, value)) { Raise(nameof(HasRelink)); Raise(nameof(RelinkText)); } } }
    public bool HasRelink => _relinkTarget != null;
    public string RelinkText => _relinkTarget == null ? "" : $"Choosing the right project for “{_relinkTarget.Title}”. Pick the correct one below and press “Use this one”.";

    public ICommand ConfirmLinkCommand { get; private set; } = null!;
    public ICommand RelinkCommand { get; private set; } = null!;
    public ICommand FindBetterCommand { get; private set; } = null!;
    public ICommand CancelRelinkCommand { get; private set; } = null!;

    void InitRelink()
    {
        ConfirmLinkCommand = new RelayCommand(p => { if (p is TrackedApp a) { a.LinkVerified = true; a.NotifyAll(); ScheduleSaveApps(); Toast($"Confirmed: {a.Title} follows {a.FullName}", "success"); } });
        RelinkCommand = new RelayCommand(p => { if (p is TrackedApp a) AskRelink(a); });
        FindBetterCommand = new RelayCommand(p =>
        {
            if (p is not TrackedApp a) return;
            RelinkTarget = a;
            Navigate("Discover");
            _ = RunSearchAsync(UninstallService.CleanName(a.Title) is { Length: > 1 } q ? q : a.Title, $"Pick the right project for {a.Title}");
        });
        CancelRelinkCommand = new RelayCommand(() => RelinkTarget = null);
    }

    void AskRelink(TrackedApp app)
    {
        Prompt = new PromptVM
        {
            Title = $"Change {app.Title}'s repository",
            Message = $"It's currently linked to {app.Url}. Paste the address of the right project — GitHub, GitLab, Codeberg, or a self-hosted site. ReleaseSync keeps your notes, group and installed version, and checks the new repository for updates.",
            ShowInput = true, Placeholder = "https://github.com/owner/project", OkText = "Switch",
            OnOk = p =>
            {
                if (!SourceClient.TryParse(p.Text, out var rf) || rf == null) return "That doesn't look like a repository address.";
                _ = RelinkAsync(app, rf, null);
                return null;
            }
        };
    }

    public async Task RelinkAsync(TrackedApp app, RepoRef rf, RepoInfo? known)
    {
        if (app.IsBusy) return;
        app.Progress = 0; app.BusyText = "Switching repository…"; app.IsBusy = true;
        try
        {
            rf = await Sources.ResolveAsync(rf);
            var info = known ?? await Sources.GetRepoAsync(rf.Source, rf.Owner, rf.Repo);
            var rr = await Sources.GetReleasesAsync(rf.Source, info.OwnerLogin, info.Name, null);
            var newKey = $"{rf.Source}:{info.OwnerLogin}/{info.Name}".ToLowerInvariant();
            if (Apps.Any(a => a != app && a.Key == newKey)) { Toast($"{info.FullName} is already in your library as another entry.", "warn"); return; }

            Settings.Tombstones[app.Key] = DateTime.UtcNow; ScheduleSaveSettings();      // the old repository entry is gone for synced PCs
            app.Source = rf.Source; app.Owner = info.OwnerLogin; app.Repo = info.Name; app.LinkVerified = true;     // the user chose this one
            app.ETag = null; app.AssetPattern = null; app.SkippedVersion = null; app.NotifiedVersion = null; app.LastError = null;
            app.LatestVersion = null; app.LatestNotes = null; app.LatestName = null; app.LatestPublished = null; app.Assets = new(); app.Releases = new(); app.ReleaseUrl = null;
            app.Description = info.Description; app.Language = info.Language; app.Stars = info.Stars; app.Topics = info.Topics ?? new();
            app.AvatarUrl = !string.IsNullOrEmpty(info.AvatarUrl) ? info.AvatarUrl : rf.Source == "github" ? $"https://github.com/{info.OwnerLogin}.png?size=96" : null;
            app.MetaFetched = true;
            app.Category = Categorizer.Categorize(app.Repo, app.Description, app.Topics, app.Language);
            ApplyRelease(app, rr.Releases);
            app.LastChecked = DateTime.UtcNow;
            app.ModifiedAt = DateTime.UtcNow;
            AddLog($"{app.Title} is now linked to {app.FullName}", "success");
            Toast(app.HasUpdate ? $"{app.Title} now follows {info.FullName} — {app.LatestVersion} is newer than your {app.InstalledVersion}" : $"{app.Title} now follows {info.FullName}", "success");
        }
        catch (Exception ex) { Toast("Couldn't switch: " + ex.Message, "error"); }
        finally
        {
            app.IsBusy = false; app.Progress = 0; app.BusyText = "";
            app.NotifyAll();
            if (app == DrawerApp) DrawerPicker = new ReleasePickerVM(app, true);
            ScheduleSaveApps(); RefreshViews();
        }
    }

    // ===================== links like releasesync://update-all =====================
    public void HandleCommand(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var u) || !(u.Scheme.Equals("releasesync", StringComparison.OrdinalIgnoreCase) || u.Scheme.Equals(Legacy.OldName, StringComparison.OrdinalIgnoreCase))) return;
        switch (u.Host.ToLowerInvariant())
        {
            case "update-all": Navigate("Updates"); _ = UpdateAllAsync(); break;
            case "updates": Navigate("Updates"); break;
            case "selfupdate": _ = AskSelfUpdateAsync(); break;
            case "add":
                var repo = System.Web.HttpUtility.ParseQueryString(u.Query)["repo"];
                if (SourceClient.TryParse(repo, out var rr) && rr != null) _ = OpenAddPreviewAsync(rr.Owner, rr.Repo, null, rr.Source, rr.FullPath);
                break;
            default: Navigate("Library", keepFilter: true); break;
        }
    }

    // ===================== headless "--check-once" (used by the scheduled task) =====================
    public async Task RunBackgroundAsync()
    {
        await CheckAllAsync(false);
        await RunScheduledBackupIfDueAsync();
        if (!string.IsNullOrWhiteSpace(Settings.SelfUpdateRepo)) await CheckSelfUpdateAsync(false);
    }

    // ===================== library sync (a shared folder or a private GitHub Gist) =====================
    bool syncApplying, syncBusy, syncAgain, syncQuiet;
    readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer syncPollTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    FileSystemWatcher? syncWatcher;
    DateTime lastSyncWrite = DateTime.MinValue;
    string? SyncFile
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Settings.SyncFolder)) return null;
            var file = Path.Combine(Settings.SyncFolder!, "ReleaseSync", "library.json");
            var old = Path.Combine(Settings.SyncFolder!, Legacy.OldName, "library.json");
            try { if (!File.Exists(file) && File.Exists(old)) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(old, file); } } catch { }      // a library synced before the rename
            return file;
        }
    }

    /// <summary>Sync is switched on and has what it needs (a folder, or a GitHub token for the Gist).</summary>
    public bool SyncReady => Settings.SyncMethod switch { "folder" => !string.IsNullOrWhiteSpace(Settings.SyncFolder), "gist" => Settings.HasToken, _ => false };
    public bool SyncNeedsToken => Settings.SyncUsesGist && !Settings.HasToken;
    public System.Collections.ObjectModel.ObservableCollection<SyncDeviceRow> SyncDevices { get; } = new();
    public bool HasSyncDevices => SyncDevices.Count > 0;

    string _syncStatus = ""; public string SyncStatus { get => _syncStatus; set { Set(ref _syncStatus, value); Raise(nameof(HasSyncStatus)); } }
    public bool HasSyncStatus => !string.IsNullOrEmpty(_syncStatus);

    public ICommand CheckSelfCommand { get; private set; } = null!;
    public ICommand UpdateSelfCommand { get; private set; } = null!;
    public ICommand SyncNowCommand { get; private set; } = null!;
    public ICommand ClearSyncFolderCommand { get; private set; } = null!;

    void InitSync()
    {
        CheckSelfCommand = new RelayCommand(async () => await CheckSelfUpdateAsync(true));
        UpdateSelfCommand = new RelayCommand(async () => await AskSelfUpdateAsync());
        AskSelfUpdateCommand = UpdateSelfCommand;
        SyncNowCommand = new RelayCommand(() =>
        {
            if (!Settings.SyncIsOn) Toast("Choose how to sync first", "info");
            else if (SyncNeedsToken) Toast("Add your GitHub token in Settings first — the sync gist is stored in your GitHub account", "info");
            else if (!SyncReady) Toast("Choose a sync folder first", "info");
            else SyncNow();
        });
        ClearSyncFolderCommand = new RelayCommand(() => Settings.SyncFolder = null);
        syncTimer.Tick += (_, _) => { syncTimer.Stop(); SyncNow(); };
        syncPollTimer.Tick += (_, _) => { if (SyncReady && Settings.SyncUsesGist) SyncNow(); };
        Settings.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppSettings.GitHubToken) or nameof(AppSettings.HasToken)) { Raise(nameof(SyncReady)); Raise(nameof(SyncNeedsToken)); } };
        ConfigureSync();
    }

    public void ScheduleSync()
    {
        if (!SyncReady) return;
        syncTimer.Interval = TimeSpan.FromSeconds(Settings.SyncUsesGist ? 20 : 2.5);        // a Gist is edited over the internet, so batch changes together
        syncTimer.Stop(); syncTimer.Start();
    }

    void ConfigureSync()
    {
        syncWatcher?.Dispose(); syncWatcher = null; syncPollTimer.Stop();
        Raise(nameof(SyncReady)); Raise(nameof(SyncNeedsToken));
        if (!SyncReady) { SyncStatus = SyncNeedsToken ? "Add your GitHub token in Settings to sync through a private Gist." : ""; return; }
        if (Settings.SyncUsesGist) { syncPollTimer.Start(); return; }
        var file = SyncFile;
        if (file == null) { SyncStatus = ""; return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            syncWatcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, "library.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) =>
            {
                if ((DateTime.UtcNow - lastSyncWrite).TotalSeconds < 5) return;      // our own write
                Application.Current?.Dispatcher.BeginInvoke(new Action(ScheduleSync));
            };
            syncWatcher.Changed += h; syncWatcher.Created += h;
        }
        catch (Exception ex) { SyncStatus = "Can't use that folder: " + ex.Message; }
    }

    public void SyncNow() => _ = SyncNowAsync();

    public async Task SyncNowAsync()
    {
        if (!SyncReady) return;
        if (syncBusy) { syncAgain = true; return; }
        syncBusy = true; syncTimer.Stop();
        System.Net.Http.HttpClient? http = null;
        try
        {
            SyncStatus = "Syncing…";
            var sent = BuildLocalDoc();
            ISyncTransport transport;
            if (Settings.SyncUsesGist) { http = GistTransport.CreateClient(Settings.GitHubToken); transport = new GistTransport(http, Settings.GistId); }
            else { transport = new FolderTransport(SyncFile!); lastSyncWrite = DateTime.UtcNow; }
            var self = new SyncDevice { Id = Settings.DeviceId, Name = Settings.DeviceName, Platform = "Windows", AppVersion = AppVersion };
            var result = await SyncService.RunAsync(transport, sent, Settings.HasSyncPassphrase ? Settings.SyncPassphrase : null, self, DateTime.UtcNow);
            lastSyncWrite = DateTime.UtcNow;
            if (transport is GistTransport g && g.GistId != Settings.GistId) { syncQuiet = true; try { Settings.GistId = g.GistId; } finally { syncQuiet = false; } ScheduleSaveSettings(); }

            int before = Apps.Count;
            if (result.Changed) ApplyDoc(result.Doc, sent);
            ShowSyncDevices(result.Doc);
            var others = result.Doc.Devices.Count - 1;
            SyncStatus = $"Synced at {DateTime.Now:t}  ·  {Apps.Count} apps, {TryLater.Count} on your try-later list" + (others > 0 ? $"  ·  {others} other device{(others == 1 ? "" : "s")}" : "");
            if (Apps.Count > before) { Toast($"Sync added {Apps.Count - before} app{(Apps.Count - before == 1 ? "" : "s")} from your other devices", "success"); _ = CheckAllAsync(false); }
        }
        catch (SyncPassphraseException ex) { SyncStatus = ex.Message; }
        catch (SyncException ex) { SyncStatus = "Sync problem: " + ex.Message; }
        catch (Exception ex) { SyncStatus = "Sync problem: " + ex.Message; }
        finally
        {
            http?.Dispose(); syncBusy = false;
            if (syncAgain) { syncAgain = false; ScheduleSync(); }
        }
    }

    void ShowSyncDevices(SyncDoc doc)
    {
        SyncDevices.Clear();
        foreach (var d in doc.Devices.Values.OrderByDescending(d => d.Id == Settings.DeviceId).ThenByDescending(d => d.LastSeen))
        {
            bool me = d.Id == Settings.DeviceId;
            SyncDevices.Add(new SyncDeviceRow { Name = me ? d.Name + " (this device)" : d.Name, Detail = $"{d.Platform}  ·  ReleaseSync {d.AppVersion}  ·  " + (me ? "just now" : "last seen " + Fmt.Ago(d.LastSeen)) });
        }
        Raise(nameof(HasSyncDevices));
    }

    /// <summary>A group the user created or renamed to; recorded so other devices (and a deleted group of the same name) are handled correctly.</summary>
    void GroupAdded(string name) { Settings.GroupStamps[name] = DateTime.UtcNow; Settings.Tombstones.Remove(SyncMerger.GroupKey(name)); ScheduleSync(); }
    void GroupRemoved(string name) { Settings.GroupStamps.Remove(name); Settings.Tombstones[SyncMerger.GroupKey(name)] = DateTime.UtcNow; ScheduleSync(); }

    static void ApplyPrefs(TrackedApp a, SyncApp d)
    {
        a.Group = d.Group; a.Pinned = d.Pinned; a.Hidden = d.Hidden; a.IncludePrereleases = d.IncludePrereleases; a.IgnoreUpdates = d.IgnoreUpdates; a.AutoUpdate = d.AutoUpdate;
        a.Mode = d.Mode; a.CustomArgs = d.CustomArgs; a.DisplayName = d.DisplayName; a.Note = d.Note; a.Tags = d.Tags; a.SkippedVersion = d.SkippedVersion;
        a.AssetPattern = d.AssetPattern; a.TrustUnsigned = d.TrustUnsigned;
    }

    /// <summary>Brings the library in line with the merged document. Only things that were part of what was sent can be removed, so anything the user did while the sync was running is kept.</summary>
    void ApplyDoc(SyncDoc doc, SyncDoc sent)
    {
        syncApplying = true;
        try
        {
            foreach (var (k, t) in doc.Tombstones) if (!Settings.Tombstones.TryGetValue(k, out var mine) || mine < t) Settings.Tombstones[k] = t;
            foreach (var k in sent.Tombstones.Keys.Where(k => !doc.Tombstones.ContainsKey(k)).ToList()) Settings.Tombstones.Remove(k);
            foreach (var (k, t) in doc.GroupStamps) if (!Settings.GroupStamps.TryGetValue(k, out var mine) || mine < t) Settings.GroupStamps[k] = t;

            var wanted = doc.Apps.ToDictionary(a => a.Key);
            var sentApps = sent.Apps.Select(a => a.Key).ToHashSet();
            foreach (var local in Apps.Where(a => sentApps.Contains(a.Key) && !wanted.ContainsKey(a.Key)).ToList())
            {
                Apps.Remove(local); if (SelectedApp == local) SelectedApp = null;
            }
            foreach (var d in doc.Apps)
            {
                var local = Apps.FirstOrDefault(a => a.Key == d.Key);
                if (local == null)
                {
                    var app = new TrackedApp { Source = d.Source, Owner = d.Owner, Repo = d.Repo, AddedAt = d.AddedAt, ModifiedAt = d.ModifiedAt };
                    ApplyPrefs(app, d);
                    app.Category = Categorizer.Categorize(app.Repo, null, null);
                    Apps.Add(app);
                    app.PrefSignature = PrefSig(app);
                }
                else if (d.ModifiedAt > local.ModifiedAt.AddSeconds(1))
                {
                    ApplyPrefs(local, d); local.ModifiedAt = d.ModifiedAt; local.PrefSignature = PrefSig(local);
                }
            }

            foreach (var g in sent.CustomGroups.Where(g => !doc.CustomGroups.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList()) Settings.CustomGroups.RemoveAll(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));
            foreach (var g in doc.CustomGroups) if (!Settings.CustomGroups.Contains(g, StringComparer.OrdinalIgnoreCase)) Settings.CustomGroups.Add(g);

            var wantedLater = doc.TryLater.ToDictionary(w => w.Key);
            var sentLater = sent.TryLater.Select(w => w.Key).ToHashSet();
            foreach (var w in TryLater.Where(w => sentLater.Contains(w.Key) && !wantedLater.ContainsKey(w.Key)).ToList()) TryLater.Remove(w);
            foreach (var w in doc.TryLater)
            {
                var mine = TryLater.FirstOrDefault(x => x.Key == w.Key);
                if (mine == null) { if (Find(w.Owner, w.Repo) == null) TryLater.Add(w); }
                else if (!ReferenceEquals(mine, w) && (w.ModifiedAt ?? default) > (mine.ModifiedAt ?? default)) mine.Note = w.Note;
            }
            ScheduleSaveSettings(); ScheduleSaveApps(); ScheduleSaveWatch();
        }
        finally { syncApplying = false; RefreshViews(); OptionsRefresh(); }
    }

    SyncDoc BuildLocalDoc() => new()
    {
        CustomGroups = Settings.CustomGroups.ToList(), TryLater = TryLater.ToList(), Tombstones = new(Settings.Tombstones), GroupStamps = new(Settings.GroupStamps),
        Apps = Apps.Select(a => new SyncApp
        {
            Key = a.Key, Source = a.Source, Owner = a.Owner, Repo = a.Repo, Group = a.Group, Pinned = a.Pinned, Hidden = a.Hidden, IncludePrereleases = a.IncludePrereleases,
            IgnoreUpdates = a.IgnoreUpdates, AutoUpdate = a.AutoUpdate, Mode = a.Mode, CustomArgs = a.CustomArgs, DisplayName = a.DisplayName, Note = a.Note, Tags = a.Tags,
            SkippedVersion = a.SkippedVersion, AssetPattern = a.AssetPattern, TrustUnsigned = a.TrustUnsigned, AddedAt = a.AddedAt, ModifiedAt = a.ModifiedAt,
        }).ToList(),
    };

    // ===================== ReleaseSync updating itself =====================
    ReleaseSummary? selfRelease; AssetInfo? selfAsset; string selfChanges = "";
    public bool HasSelfUpdate => selfRelease != null;
    public string SelfUpdateText => selfRelease == null ? "" : $"ReleaseSync {SelfVersion} is available";
    string SelfVersion => (selfRelease?.Tag ?? "").TrimStart('v', 'V');
    string _selfStatus = ""; public string SelfStatus { get => _selfStatus; set { Set(ref _selfStatus, value); Raise(nameof(HasSelfStatus)); } }
    public bool HasSelfStatus => !string.IsNullOrEmpty(_selfStatus);
    bool _selfUpdating; public bool SelfUpdating { get => _selfUpdating; private set { if (Set(ref _selfUpdating, value)) Raise(nameof(SelfButtonText)); } }
    double _selfPercent;
    public string SelfButtonText => !SelfUpdating ? "Update available" : _selfPercent >= 1 ? $"Updating…  {_selfPercent:0}%" : "Updating…";
    public ICommand AskSelfUpdateCommand { get; private set; } = null!;

    void RaiseSelfUpdate() { Raise(nameof(HasSelfUpdate)); Raise(nameof(SelfUpdateText)); Raise(nameof(SelfButtonText)); }

    public async Task CheckSelfUpdateAsync(bool manual)
    {
        var text = Settings.SelfUpdateRepo;
        if (string.IsNullOrWhiteSpace(text) || !SourceClient.TryParse(text, out var rf) || rf == null)
        {
            if (manual) SelfStatus = "Enter the address of ReleaseSync's repository first.";
            return;
        }
        if (SelfUpdating) return;
        try
        {
            rf = await Sources.ResolveAsync(rf);
            var testReleases = Legacy.IsTestMode ? Environment.GetEnvironmentVariable("RELEASESYNC_TEST_RELEASES") : null;      // tests can't read a private repository
            var releases = !string.IsNullOrEmpty(testReleases) ? JsonSerializer.Deserialize<List<ReleaseInfo>>(File.ReadAllText(testReleases))! : (await Sources.GetReleasesAsync(rf.Source, rf.Owner, rf.Repo, null)).Releases;
            var rel = releases.FirstOrDefault(r => !r.Prerelease);
            if (rel == null) { SelfStatus = "That repository has no releases yet."; return; }
            var sum = ToSummary(rel);
            var asset = sum.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("releasesync", StringComparison.OrdinalIgnoreCase))
                        ?? sum.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("releasesync", StringComparison.OrdinalIgnoreCase))
                        ?? sum.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (VersionUtil.IsNewer(rel.Tag, AppVersion) && asset != null)
            {
                selfRelease = sum; selfAsset = asset;
                selfChanges = SelfUpdate.ChangesSince(releases.Where(r => !r.Prerelease && VersionUtil.IsNewer(r.Tag, AppVersion)).Select(r => (r.Tag, ToSummary(r).Notes)));
                SelfStatus = $"{rel.Tag} is available (you have {AppVersion}).";
                RaiseSelfUpdate();
                if (!manual && Settings.SelfUpdateNotified != rel.Tag)      // once per version; the button stays in the sidebar after that
                {
                    Settings.SelfUpdateNotified = rel.Tag; ScheduleSaveSettings();
                    Notify("ReleaseSync update available", $"Version {SelfVersion} is ready to install.", ("See what's new", "releasesync://selfupdate"));
                }
            }
            else { selfRelease = null; selfAsset = null; selfChanges = ""; SelfStatus = $"You're on the latest version ({AppVersion})."; RaiseSelfUpdate(); }
        }
        catch (Exception ex) { SelfStatus = ex.Message; }
    }

    /// <summary>The update dialog: which version you have, which you'd get, what changed. Nothing happens until the user says yes.</summary>
    public async Task AskSelfUpdateAsync()
    {
        if (SelfUpdating) return;
        if (selfRelease == null) { await CheckSelfUpdateAsync(true); if (selfRelease == null) { Toast(SelfStatus, "info"); return; } }
        Prompt = new PromptVM
        {
            Title = "Update ReleaseSync?",
            Message = $"You have version {AppVersion}. This updates it to version {SelfVersion}.",
            DetailsTitle = "What's new", Details = selfChanges,
            Footnote = "ReleaseSync will close and reopen by itself. Your library and settings stay as they are.",
            OkText = "Download and update", CancelText = "Not now",
            OnOk = p => { _ = UpdateSelfAsync(); return null; },
        };
    }

    /// <summary>Downloads the new version, checks it, puts it in place of the running program and reopens. If anything fails the current version keeps working.</summary>
    public async Task UpdateSelfAsync()
    {
        if (SelfUpdating) return;
        if (selfRelease == null || selfAsset == null) { await CheckSelfUpdateAsync(true); if (selfRelease == null) return; }
        if (Apps.Any(a => a.IsBusy)) { Toast("An app is installing or updating. Update ReleaseSync when that's finished.", "info"); return; }

        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var release = selfRelease!; var asset = selfAsset!;
        var tmpDir = Path.Combine(Path.GetTempPath(), "releasesync-self-" + Guid.NewGuid().ToString("N")[..8]);
        var staged = Path.Combine(dir, "ReleaseSync.new.exe");
        var fake = new TrackedApp { Owner = "releasesync", Repo = "self" };
        _selfPercent = 0; SelfUpdating = true;
        try
        {
            Directory.CreateDirectory(tmpDir);
            SelfStatus = "Downloading the update…";
            var progress = new Progress<InstallProgress>(p => { SelfStatus = p.Text; _selfPercent = p.Percent; Raise(nameof(SelfButtonText)); });
            var testFile = Legacy.IsTestMode ? Environment.GetEnvironmentVariable("RELEASESYNC_TEST_UPDATE") : null;      // tests can't download from a private repository
            var downloaded = !string.IsNullOrEmpty(testFile) ? testFile : await Installer.DownloadAsync(fake, release.Tag, asset, progress, CancellationToken.None);

            SelfStatus = "Checking the download…";
            var expected = SelfUpdate.Sha256From(asset.Digest);
            if (expected == null && release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase)) is { } sum)
                expected = SelfUpdate.Sha256From(await File.ReadAllTextAsync(await Installer.DownloadAsync(fake, release.Tag, sum, new Progress<InstallProgress>(), CancellationToken.None)));
            if (expected == null) throw new InvalidOperationException("the download has no checksum to compare it with, so it wasn't used.");
            if (!await Task.Run(() => SelfUpdate.FileMatches(downloaded, expected))) throw new InvalidOperationException("the download doesn't match its checksum, so it wasn't used.");

            string newExe = downloaded;
            if (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(downloaded, tmpDir, true);
                newExe = Directory.GetFiles(tmpDir, "ReleaseSync.exe", SearchOption.AllDirectories).FirstOrDefault() ?? throw new InvalidOperationException("the download doesn't contain ReleaseSync.exe.");
            }

            File.Copy(newExe, staged, true);          // beside the program first: if this folder can't be written to, nothing has changed yet
            var old = Path.Combine(dir, "ReleaseSync.old.exe");
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old, true);                // a running program can be renamed, not overwritten
            try { File.Move(staged, exe); }
            catch { File.Move(old, exe, true); throw; }

            InstallService.ClearDownloads(fake);
            SelfStatus = "Updated. Restarting…";
            AddLog($"ReleaseSync updated to {release.Tag}", "success");
            SaveAll();
            ((App)Application.Current).ReleaseSingleInstance();      // otherwise the new version would see this one and close itself
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir });
            if (ExitRequest != null) ExitRequest(); else Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch { }
            SelfUpdating = false;
            SelfStatus = "Couldn't update: " + ex.Message;
            Toast("ReleaseSync couldn't update: " + ex.Message + " Your current version still works.", "error");
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    // ===================== winget packages in Scan PC =====================
    async Task ScanWingetAsync()
    {
        if (IsScanning) return;
        IsScanning = true; HasScanned = true;
        var cts = scanCts = new CancellationTokenSource(); var ct = cts.Token;
        try
        {
            ScanStatus = "Looking for winget…";
            if (!await Task.Run(WingetService.IsAvailable, ct)) { ScanStatus = "winget isn't available on this PC."; return; }
            ScanStatus = "Reading your winget packages (this takes a moment)…";
            var rows = await Task.Run(WingetService.List, ct);
            if (rows.Count == 0) { ScanStatus = "winget didn't report any packages."; return; }

            int done = 0, found = 0;
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    ScanStatus = $"Checking {rows.Count} winget packages for project pages…  {done}/{rows.Count}";
                    var link = await Task.Run(() => WingetService.FindRepo(row.Id), ct);
                    if (link is not { } l) return;
                    if (Find(l.Owner, l.Repo) != null || ScanLinked.Any(x => x.Owner?.Equals(l.Owner, StringComparison.OrdinalIgnoreCase) == true && x.RepoName?.Equals(l.Repo, StringComparison.OrdinalIgnoreCase) == true)) return;
                    var entry = new UninstallEntry { Id = "winget|" + row.Id, DisplayName = row.Name, Version = row.Version };
                    var it = new ScanItem(entry) { Source = "github", Owner = l.Owner, RepoName = l.Repo, Kind = "Linked" };
                    AttachScanItem(it);
                    try
                    {
                        var info = await Sources.GetRepoAsync("github", l.Owner, l.Repo, ct);
                        it.Owner = info.OwnerLogin; it.RepoName = info.Name; it.Repo = info;
                        var rr = await Sources.GetReleasesAsync("github", it.Owner, it.RepoName, null, ct);
                        it.Releases = rr.Releases; it.ReleasesLoaded = true; it.IsSelected = true;
                    }
                    catch (GitHubException) { it.ReleasesLoaded = true; }
                    // don't list a program twice if the Windows scan already found it under another name
                    if (Find(it.Owner!, it.RepoName!) != null || ScanLinked.Any(x => x.Owner == it.Owner && x.RepoName == it.RepoName)) return;
                    ScanLinked.Add(it); found++;
                    RaiseScanCounts();
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { done++; gate.Release(); }
            }));
            ScanStatus = $"winget: {found} more program{(found == 1 ? "" : "s")} linked to a project page.";
        }
        catch (OperationCanceledException) { ScanStatus = "Cancelled."; }
        finally { IsScanning = false; RaiseScanCounts(); }
    }

    // ===================== start-up work shared by the window and background modes =====================
    void StartExtras()
    {
        if (Store.RecoveryNote != null) { AddLog(Store.RecoveryNote, "warn"); Toast(Store.RecoveryNote, "warn"); }
        _ = Task.Delay(8000).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(new Action(async () => await RunScheduledBackupIfDueAsync())));
        if (Settings.AutoBackupData) _ = Task.Run(() => { DataBackups.CreateIfDue(); Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshDataBackups)); });
        else RefreshDataBackups();
        var replaced = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "")!, "ReleaseSync.old.exe");
        _ = Task.Run(async () => { for (int i = 0; i < 5 && File.Exists(replaced); i++) { try { File.Delete(replaced); } catch { await Task.Delay(3000); } } });      // the old version may still be closing
        if (Settings.StartWithWindows) Shell.SetStartWithWindows(true);      // keeps the entry right if the program was moved
        if (Settings.BackgroundChecks) _ = Platform.ApplyBackgroundTaskAsync(Settings);
        if (SyncReady) SyncNow();
        if (!string.IsNullOrWhiteSpace(Settings.SelfUpdateRepo)) _ = CheckSelfUpdateAsync(false);
    }
}

/// <summary>One line in the list of devices that sync with this library.</summary>
public sealed class SyncDeviceRow
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
}
