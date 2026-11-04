using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;

namespace ReleaseSync.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void NotifyAll() => Raise(string.Empty);
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    /// <summary>Sets the field and refreshes every binding (for properties many computed values depend on).</summary>
    protected bool SetAll<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(string.Empty); return true;
    }
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> run;
    readonly Predicate<object?>? can;
    public RelayCommand(Action<object?> run, Predicate<object?>? can = null) { this.run = run; this.can = can; }
    public RelayCommand(Action run) : this(_ => run()) { }
    /// <summary>Raised by the app whenever buttons should re-check whether they are enabled (WPF wires this to its own re-query signal).</summary>
    public static event EventHandler? Requery;
    public static void RaiseRequery() => Requery?.Invoke(null, EventArgs.Empty);
    public event EventHandler? CanExecuteChanged { add => Requery += value; remove => Requery -= value; }
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

public enum InstallMode { Auto, Installer, Portable }
public enum AssetKind { Unknown, Installer, Msi, Msix, Portable, Zip }

/// <summary>One published release of a project, kept so older versions can be browsed and installed.</summary>
public class ReleaseSummary
{
    public string Tag { get; set; } = "";
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public DateTime? Published { get; set; }
    public bool Prerelease { get; set; }
    public string? Url { get; set; }
    public List<AssetInfo> Assets { get; set; } = new();
    [JsonIgnore] public bool IsLatest { get; set; }
    [JsonIgnore] public string PublishedText => Published.HasValue ? Fmt.Ago(Published) : "";
    [JsonIgnore] public string Label => Tag + (Prerelease ? "   pre-release" : "") + (IsLatest ? "   latest" : "") + (Published.HasValue ? "   ·   " + PublishedText : "");
    [JsonIgnore] public string NotesText => string.IsNullOrWhiteSpace(Notes) ? "No release notes were provided." : Fmt.StripMarkdown(Notes);
    public override string ToString() => Label;
}

public class AssetInfo
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public int Downloads { get; set; }
    public string? Digest { get; set; }
    [JsonIgnore] public string SizeText => Fmt.Size(Size);
    [JsonIgnore] public AssetKind Kind => AssetPicker.KindOf(Name);
    [JsonIgnore] public bool IsAutomatic => Url.Length == 0;
    [JsonIgnore] public string Label => IsAutomatic ? Name : $"{Name}   ·   {SizeText}";
    public override string ToString() => Label;
}

/// <summary>An app/repository tracked in the library, including its persisted state and live (runtime-only) state.</summary>
public class TrackedApp : Observable
{
    // ---- persisted, rarely changing ----
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string? ETag { get; set; }
    public string? NotifiedVersion { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Topics { get; set; } = new();
    public string? InstalledAssetName { get; set; }
    public DateTime? InstalledAt { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? AssetPattern { get; set; }
    public bool MetaFetched { get; set; }
    public string? UninstallKey { get; set; }
    public string Source { get; set; } = "github";            // github, gitlab or codeberg
    /// <summary>Which platform the app is for. Only "Windows" exists today; the library already filters and groups by it so more can be added without redesigning anything.</summary>
    public string Platform { get; set; } = "Windows";
    string? _installDir; public string? CustomInstallDir { get => _installDir; set => SetAll(ref _installDir, value); }
    [JsonIgnore] public bool HasCustomInstallDir => !string.IsNullOrWhiteSpace(CustomInstallDir);
    [JsonIgnore] public string InstallDirText => HasCustomInstallDir ? CustomInstallDir! : "Default — portable apps go to ReleaseSync's apps folder; installers choose their own";
    /// <summary>False when ReleaseSync linked this program to a repository by guessing from its name; an update must be confirmed first.</summary>
    public bool LinkVerified { get; set; } = true;
    [JsonIgnore] public bool NeedsLinkConfirm => !LinkVerified;
    public bool TrustUnsigned { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    List<ReleaseSummary> _releases = new(); public List<ReleaseSummary> Releases { get => _releases; set => SetAll(ref _releases, value); }
    string? _skipped; public string? SkippedVersion { get => _skipped; set => SetAll(ref _skipped, value); }
    string? _note; public string? Note { get => _note; set => SetAll(ref _note, value); }
    string? _tags; public string? Tags { get => _tags; set => SetAll(ref _tags, value); }
    [JsonIgnore] public List<BackupInfo> Backups => AppBackups.List(this);
    [JsonIgnore] public bool HasBackups => Backups.Count > 0;
    [JsonIgnore] public string? PrefSignature { get; set; }
    [JsonIgnore] public string? VerifyNote { get; set; }

    // ---- persisted, observable ----
    string? _displayName; public string? DisplayName { get => _displayName; set => SetAll(ref _displayName, value); }
    string? _description; public string? Description { get => _description; set => SetAll(ref _description, value); }
    string? _language; public string? Language { get => _language; set => SetAll(ref _language, value); }
    int _stars; public int Stars { get => _stars; set => SetAll(ref _stars, value); }
    string _category = "Other"; public string Category { get => _category; set => SetAll(ref _category, value); }
    string? _group; public string? Group { get => _group; set => SetAll(ref _group, value); }
    string? _installed; public string? InstalledVersion { get => _installed; set => SetAll(ref _installed, value); }
    string? _latest; public string? LatestVersion { get => _latest; set => SetAll(ref _latest, value); }
    string? _latestName; public string? LatestName { get => _latestName; set => SetAll(ref _latestName, value); }
    string? _notes; public string? LatestNotes { get => _notes; set => SetAll(ref _notes, value); }
    DateTime? _published; public DateTime? LatestPublished { get => _published; set => SetAll(ref _published, value); }
    bool _latestIsPre; public bool LatestIsPrerelease { get => _latestIsPre; set => SetAll(ref _latestIsPre, value); }
    List<AssetInfo> _assets = new(); public List<AssetInfo> Assets { get => _assets; set { _choices = null; SetAll(ref _assets, value); } }
    List<AssetInfo>? _choices;
    bool _pre; public bool IncludePrereleases { get => _pre; set => SetAll(ref _pre, value); }
    bool _ignore; public bool IgnoreUpdates { get => _ignore; set => SetAll(ref _ignore, value); }
    bool _auto; public bool AutoUpdate { get => _auto; set => SetAll(ref _auto, value); }
    bool _pinned; public bool Pinned { get => _pinned; set => SetAll(ref _pinned, value); }
    bool _hidden; public bool Hidden { get => _hidden; set => SetAll(ref _hidden, value); }
    InstallMode _mode; public InstallMode Mode { get => _mode; set => SetAll(ref _mode, value); }
    string? _args; public string? CustomArgs { get => _args; set => SetAll(ref _args, value); }
    string? _path; public string? InstallPath { get => _path; set => SetAll(ref _path, value); }
    DateTime? _checked; public DateTime? LastChecked { get => _checked; set => SetAll(ref _checked, value); }
    string? _error; public string? LastError { get => _error; set => SetAll(ref _error, value); }

    // ---- runtime only ----
    bool _busy; [JsonIgnore] public bool IsBusy { get => _busy; set => SetAll(ref _busy, value); }
    double _progress; [JsonIgnore] public double Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(IsIndeterminate)); } }
    string _busyText = ""; [JsonIgnore] public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    [JsonIgnore] public bool IsIndeterminate => _busy && _progress <= 0;
    [JsonIgnore] public CancellationTokenSource? Cts { get; set; }

    // ---- computed ----
    [JsonIgnore] public string FullName => $"{Owner}/{Repo}";
    [JsonIgnore] public string Url => RepoRef.UrlFor(Source, Owner, Repo);
    [JsonIgnore] public string SourceLabel => Source switch
    {
        "gitlab" => "GitLab", "codeberg" => "Codeberg", "github" => "GitHub",
        _ when Source.Contains(':') => Source[(Source.IndexOf(':') + 1)..],
        _ => "GitHub"
    };
    [JsonIgnore] public bool IsGitHub => Source == "github";
    [JsonIgnore] public bool IsSkipped => !string.IsNullOrEmpty(SkippedVersion) && SkippedVersion == LatestVersion;
    [JsonIgnore] public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    [JsonIgnore] public List<string> TagList => (Tags ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    [JsonIgnore] public bool HasTags => TagList.Count > 0;
    [JsonIgnore] public string Key => $"{Source}:{Owner}/{Repo}".ToLowerInvariant();

    /// <summary>The newest release as a summary (older saved libraries only have the flat Latest* fields).</summary>
    [JsonIgnore] public ReleaseSummary? LatestRelease =>
        Releases.FirstOrDefault(r => r.Tag == LatestVersion) ??
        (HasRelease ? new ReleaseSummary { Tag = LatestVersion!, Name = LatestName, Notes = LatestNotes, Published = LatestPublished, Prerelease = LatestIsPrerelease, Url = ReleaseUrl, Assets = Assets } : null);

    /// <summary>When behind by several versions, the notes of every release in between, newest first.</summary>
    [JsonIgnore] public string CumulativeNotes
    {
        get
        {
            if (!IsInstalled || !HasUpdate || Releases.Count < 2) return NotesText;
            var newer = Releases.Where(r => (IncludePrereleases || !r.Prerelease) && VersionUtil.IsNewer(r.Tag, InstalledVersion!)).Take(12).ToList();
            if (newer.Count < 2) return NotesText;
            return string.Join("\n\n────────\n\n", newer.Select(r => $"{r.Tag}{(r.Published.HasValue ? "  ·  " + r.PublishedText : "")}\n{(string.IsNullOrWhiteSpace(r.Notes) ? "No release notes were provided." : Fmt.StripMarkdown(r.Notes, 1500))}"));
        }
    }
    [JsonIgnore] public string NotesHeader => IsInstalled && HasUpdate && Releases.Count(r => VersionUtil.IsNewer(r.Tag, InstalledVersion!)) > 1 ? "WHAT'S NEW SINCE YOUR VERSION" : "WHAT'S NEW";
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Repo : DisplayName!;
    [JsonIgnore] public string Initial => Title.Length > 0 ? char.ToUpperInvariant(Title[0]).ToString() : "?";
    public const string UngroupedName = "Ungrouped";
    [JsonIgnore] public string DisplayGroup => !string.IsNullOrWhiteSpace(Group) ? Group! : AppSettingsProvider.Current.AutoGroups ? Category : UngroupedName;
    [JsonIgnore] public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);
    [JsonIgnore] public bool HasUpdate => !IgnoreUpdates && !IsSkipped && IsInstalled && !string.IsNullOrEmpty(LatestVersion) && VersionUtil.IsNewer(LatestVersion!, InstalledVersion!);
    [JsonIgnore] public bool HasRelease => !string.IsNullOrEmpty(LatestVersion);
    [JsonIgnore] public AssetInfo? ChosenAsset => AssetPicker.Pick(Assets, AssetPattern);
    [JsonIgnore] public bool CanInstall => ChosenAsset != null && !IsBusy;
    [JsonIgnore] public bool CanReinstall => IsInstalled && !HasUpdate && !IsBusy && ChosenAsset != null;
    [JsonIgnore] public bool ShowAction => !IsBusy && (HasUpdate || !IsInstalled);
    [JsonIgnore] public bool CanLaunch => !IsBusy && !string.IsNullOrEmpty(InstallPath) && File.Exists(InstallPath);
    [JsonIgnore] public string ActionText => IsInstalled ? "Update" : "Install";
    [JsonIgnore] public string ActionGlyph => IsInstalled ? "" : "";
    [JsonIgnore] public string ChosenAssetText => ChosenAsset?.Name ?? "No compatible Windows file found";
    [JsonIgnore] public string SizeText => ChosenAsset?.SizeText ?? "";
    [JsonIgnore] public string StarsText => Fmt.Count(Stars);
    [JsonIgnore] public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    [JsonIgnore] public bool HasError => !string.IsNullOrEmpty(LastError);
    [JsonIgnore] public string LatestText => HasRelease ? LatestVersion! : "—";
    [JsonIgnore] public string InstalledText => IsInstalled ? InstalledVersion! : "Not installed";
    [JsonIgnore] public string VersionText => HasUpdate ? $"{InstalledVersion}  →  {LatestVersion}" : IsInstalled ? InstalledVersion! : (HasRelease ? $"{LatestVersion} available" : "No release found");
    [JsonIgnore] public string PublishedText => LatestPublished.HasValue ? Fmt.Ago(LatestPublished) : "—";
    [JsonIgnore] public string LastCheckedText => LastChecked.HasValue ? Fmt.Ago(LastChecked) : "Never";
    [JsonIgnore] public string NotesText => string.IsNullOrWhiteSpace(LatestNotes) ? "No release notes were provided." : Fmt.StripMarkdown(LatestNotes);
    [JsonIgnore] public string NotesPreview { get { var t = Fmt.StripMarkdown(LatestNotes); return t.Length > 220 ? t[..220].TrimEnd() + "…" : t; } }
    [JsonIgnore] public List<AssetInfo> AssetChoices
    {
        get
        {
            if (_choices != null) return _choices;
            var l = new List<AssetInfo> { new AssetInfo { Name = "Automatic (recommended)" } };
            l.AddRange(AssetPicker.Choices(Assets));
            return _choices = l;
        }
    }
    [JsonIgnore] public AssetInfo? SelectedChoice
    {
        get
        {
            var ch = AssetChoices;
            if (string.IsNullOrEmpty(AssetPattern)) return ch[0];
            var m = AssetPicker.Pick(Assets, AssetPattern);
            return ch.FirstOrDefault(a => m != null && a.Name == m.Name) ?? ch[0];
        }
    }

    /// <summary>One of: Busy, Error, Update, Ignored, UpToDate, NotInstalled, Unchecked.</summary>
    [JsonIgnore] public string StatusKind =>
        IsBusy ? "Busy" :
        HasError && !HasRelease ? "Error" :
        HasUpdate ? "Update" :
        IsInstalled && (IgnoreUpdates || IsSkipped) && !string.IsNullOrEmpty(LatestVersion) && VersionUtil.IsNewer(LatestVersion!, InstalledVersion!) ? "Ignored" :
        IsInstalled ? (HasError ? "Error" : "UpToDate") :
        HasRelease ? "NotInstalled" : "Unchecked";

    [JsonIgnore] public string GroupKey { get => Group ?? ""; set => Group = string.IsNullOrWhiteSpace(value) ? null : value; }

    [JsonIgnore] public string StatusText => StatusKind switch
    {
        "Busy" => string.IsNullOrEmpty(BusyText) ? "Working…" : BusyText,
        "Error" => LastError != null && LastError.StartsWith("No ") ? "No releases" : "Check failed",
        "Update" => "Update available",
        "Ignored" => IsSkipped ? $"Skipped {LatestVersion}" : "Update ignored",
        "UpToDate" => "Up to date",
        "NotInstalled" => "Not installed",
        _ => "Not checked yet"
    };

    public void RaiseBusy() { Raise(nameof(BusyText)); Raise(nameof(Progress)); Raise(nameof(IsIndeterminate)); }
}

public class AppSettings : Observable
{
    // ---- GitHub ----
    /// <summary>Legacy location (older builds). Read once to migrate into the secret store, then cleared.</summary>
    public string? ProtectedToken { get; set; }
    string? _token; bool _tokenLoaded;
    [JsonIgnore] public string GitHubToken
    {
        get
        {
            if (!_tokenLoaded)
            {
                _tokenLoaded = true;
                try { _token = Secrets.Store.Read(Secrets.GitHubTarget); } catch { _token = null; }
                if (string.IsNullOrEmpty(_token) && !string.IsNullOrEmpty(ProtectedToken))
                {
                    var legacy = Secrets.LegacyUnprotect?.Invoke(ProtectedToken);
                    if (!string.IsNullOrEmpty(legacy))
                    {
                        _token = legacy;
                        try { if (Secrets.Store.Write(Secrets.GitHubTarget, legacy)) ProtectedToken = null; } catch { }
                    }
                }
            }
            return _token ?? "";
        }
        set
        {
            var v = (value ?? "").Trim();
            _tokenLoaded = true; _token = v.Length == 0 ? null : v; ProtectedToken = null;
            try { if (v.Length == 0) Secrets.Store.Delete(Secrets.GitHubTarget); else Secrets.Store.Write(Secrets.GitHubTarget, v); } catch { }
            Raise(); Raise(nameof(HasToken));
        }
    }
    [JsonIgnore] public bool HasToken => GitHubToken.Length > 0;

    // ---- updates ----
    bool _checkOnStartup = true; public bool CheckOnStartup { get => _checkOnStartup; set => Set(ref _checkOnStartup, value); }
    int _interval = 360; public int CheckIntervalMinutes { get => _interval; set => Set(ref _interval, value); }
    bool _notify = true; public bool NotifyOnUpdates { get => _notify; set => Set(ref _notify, value); }
    bool _autoInstall; public bool AutoInstallUpdates { get => _autoInstall; set => Set(ref _autoInstall, value); }
    bool _defPre; public bool DefaultIncludePrereleases { get => _defPre; set => Set(ref _defPre, value); }
    int _concurrent = 4; public int MaxConcurrentChecks { get => _concurrent; set => Set(ref _concurrent, value); }

    // ---- installation ----
    bool _installAfterAdd = true; public bool InstallAfterAdd { get => _installAfterAdd; set => Set(ref _installAfterAdd, value); }
    string _installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ReleaseSync Apps");
    public string InstallRoot { get => _installRoot; set => Set(ref _installRoot, value); }
    string _downloads = Path.Combine(Paths.LocalDir, "Downloads");
    public string DownloadFolder { get => _downloads; set => Set(ref _downloads, value); }
    bool _keepDownloads; public bool KeepDownloads { get => _keepDownloads; set => Set(ref _keepDownloads, value); }
    // scheduled backups to a folder the user picks
    string? _backupFolder; public string? BackupFolder { get => _backupFolder; set => Set(ref _backupFolder, value); }
    int _backupHours; public int BackupIntervalHours { get => _backupHours; set => Set(ref _backupHours, value); }       // 0 = off
    bool _backupFull; public bool BackupIncludeApps { get => _backupFull; set => Set(ref _backupFull, value); }
    int _backupKeep = 10; public int BackupKeep { get => _backupKeep; set => Set(ref _backupKeep, value); }            // 0 = keep all
    public DateTime? LastScheduledBackup { get; set; }
    // Try later page
    string _trySort = "recent"; public string TrySort { get => _trySort; set => Set(ref _trySort, value); }
    string _tryView = "Grid"; public string TryViewMode { get => _tryView; set => Set(ref _tryView, value); }
    bool _tryGroup = true; public bool TryGroup { get => _tryGroup; set => Set(ref _tryGroup, value); }
    // Discover page
    bool _discIntro; public bool DiscoverIntroSeen { get => _discIntro; set => Set(ref _discIntro, value); }
    bool _discLight; public bool DiscoverLight { get => _discLight; set => Set(ref _discLight, value); }
    string _discMode = "Trending"; public string DiscoverMode { get => _discMode; set => Set(ref _discMode, value); }
    string _discCat = "all"; public string DiscoverCategory { get => _discCat; set => Set(ref _discCat, value); }
    string _discWindow = "Day"; public string DiscoverCompare { get => _discWindow; set => Set(ref _discWindow, value); }
    string? _starsUser; public string? StarsUser { get => _starsUser; set => Set(ref _starsUser, value); }
    bool _discWin = true; public bool DiscoverWindowsOnly { get => _discWin; set => Set(ref _discWin, value); }
    bool _backupApps = true; public bool BackupBeforeUpdate { get => _backupApps; set => Set(ref _backupApps, value); }
    bool _backupData = true; public bool AutoBackupData { get => _backupData; set => Set(ref _backupData, value); }
    bool _verify = true; public bool VerifyDownloads { get => _verify; set => Set(ref _verify, value); }
    bool _defender = true; public bool ScanWithDefender { get => _defender; set => Set(ref _defender, value); }
    bool _provenance = true; public bool CheckProvenance { get => _provenance; set => Set(ref _provenance, value); }
    bool _useVt; public bool UseVirusTotal { get => _useVt; set => Set(ref _useVt, value); }
    bool _hints = true; public bool ShowSafetyHints { get => _hints; set => Set(ref _hints, value); }
    string? _vtKey; bool _vtLoaded;
    /// <summary>The VirusTotal API key, kept in the platform's secret store (Windows Credential Manager on Windows) like the GitHub token.</summary>
    [JsonIgnore] public string VirusTotalKey
    {
        get
        {
            if (!_vtLoaded) { _vtLoaded = true; try { _vtKey = Secrets.Store.Read(Secrets.VirusTotalTarget); } catch { _vtKey = null; } }
            return _vtKey ?? "";
        }
        set
        {
            var v = (value ?? "").Trim();
            _vtLoaded = true; _vtKey = v.Length == 0 ? null : v;
            try { if (v.Length == 0) Secrets.Store.Delete(Secrets.VirusTotalTarget); else Secrets.Store.Write(Secrets.VirusTotalTarget, v); } catch { }
            Raise(); Raise(nameof(HasVirusTotalKey));
        }
    }
    [JsonIgnore] public bool HasVirusTotalKey => VirusTotalKey.Length > 0;
    bool _bgChecks; public bool BackgroundChecks { get => _bgChecks; set => Set(ref _bgChecks, value); }
    bool _toasts = true; public bool UseToasts { get => _toasts; set => Set(ref _toasts, value); }
    string? _syncFolder; public string? SyncFolder { get => _syncFolder; set => Set(ref _syncFolder, value); }
    string? _syncMethod;
    /// <summary>How the library is kept in step between devices: "off", "folder" (a shared folder) or "gist" (a private GitHub Gist). Libraries set up before this existed use their sync folder, if any.</summary>
    public string SyncMethod
    {
        get => _syncMethod ?? (string.IsNullOrWhiteSpace(SyncFolder) ? "off" : "folder");
        set { if (value != _syncMethod) { _syncMethod = value; Raise(); Raise(nameof(SyncUsesFolder)); Raise(nameof(SyncUsesGist)); Raise(nameof(SyncIsOn)); } }
    }
    [JsonIgnore] public bool SyncUsesFolder => SyncMethod == "folder";
    [JsonIgnore] public bool SyncUsesGist => SyncMethod == "gist";
    [JsonIgnore] public bool SyncIsOn => SyncMethod is "folder" or "gist";
    string? _gistId; public string? GistId { get => _gistId; set => Set(ref _gistId, string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }
    string? _deviceId; public string DeviceId { get => _deviceId ??= Guid.NewGuid().ToString("N"); set => _deviceId = value; }
    string? _deviceName; public string DeviceName { get => string.IsNullOrWhiteSpace(_deviceName) ? Environment.MachineName : _deviceName; set => Set(ref _deviceName, value); }
    /// <summary>When each of your own groups was created; lets a deleted-then-recreated group survive syncing.</summary>
    public Dictionary<string, DateTime> GroupStamps { get; set; } = new();

    string? _syncPass; bool _syncPassLoaded;
    /// <summary>Optional passphrase that locks the sync file. Kept in the system's secret store, never in files or backups.</summary>
    [JsonIgnore] public string SyncPassphrase
    {
        get
        {
            if (!_syncPassLoaded) { _syncPassLoaded = true; try { _syncPass = Secrets.Store.Read(Secrets.SyncPassphraseTarget); } catch { _syncPass = null; } }
            return _syncPass ?? "";
        }
        set
        {
            var v = value ?? "";
            _syncPassLoaded = true; _syncPass = v.Length == 0 ? null : v;
            try { if (v.Length == 0) Secrets.Store.Delete(Secrets.SyncPassphraseTarget); else Secrets.Store.Write(Secrets.SyncPassphraseTarget, v); } catch { }
            Raise(); Raise(nameof(HasSyncPassphrase));
        }
    }
    [JsonIgnore] public bool HasSyncPassphrase => SyncPassphrase.Length > 0;
    string? _selfRepo = "https://github.com/huskyCodes/ReleaseSync"; public string? SelfUpdateRepo { get => _selfRepo; set => Set(ref _selfRepo, value); }
    public Dictionary<string, DateTime> Tombstones { get; set; } = new();
    public List<string> ScanFolders { get; set; } = new();
    bool _silent; public bool SilentInstall { get => _silent; set => Set(ref _silent, value); }
    string _silentArgs = "/S"; public string SilentArgs { get => _silentArgs; set => Set(ref _silentArgs, value); }
    string _arch = "Auto"; public string PreferredArch { get => _arch; set => Set(ref _arch, value); }
    bool _preferInstaller = true; public bool PreferInstaller { get => _preferInstaller; set => Set(ref _preferInstaller, value); }
    bool _shortcuts = true; public bool CreateShortcuts { get => _shortcuts; set => Set(ref _shortcuts, value); }

    // ---- appearance ----
    string _theme = "System"; public string Theme { get => _theme; set => Set(ref _theme, value); }
    string _accent = "#6C8CFF"; public string AccentHex { get => _accent; set => Set(ref _accent, value); }
    double _scale = 1.0; public double UiScale { get => _scale; set => Set(ref _scale, value); }
    string _density = "Comfortable"; public string Density { get => _density; set => Set(ref _density, value); }
    string _view = "Grid"; public string ViewMode { get => _view; set => Set(ref _view, value); }
    string _sort = "Name"; public string SortMode { get => _sort; set => Set(ref _sort, value); }
    bool _autoGroups = true; public bool AutoGroups { get => _autoGroups; set => Set(ref _autoGroups, value); }
    bool _groupItems = true; public bool GroupItems { get => _groupItems; set => Set(ref _groupItems, value); }
    bool _reduceMotion; public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }
    bool _sidebarCollapsed; public bool SidebarCollapsed { get => _sidebarCollapsed; set => Set(ref _sidebarCollapsed, value); }

    // ---- behavior ----
    bool _startWithWindows; public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    bool _startMinimized; public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    bool _tray = true; public bool ShowTrayIcon { get => _tray; set => Set(ref _tray, value); }
    bool _closeToTray = true; public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    bool _confirmRemove = true; public bool ConfirmRemove { get => _confirmRemove; set => Set(ref _confirmRemove, value); }

    // ---- misc / window ----
    public List<string> CustomGroups { get; set; } = new();
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
    public DateTime? LastFullCheck { get; set; }
}

// ---------------- GitHub DTOs ----------------

public class OwnerDto
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
}

public class RepoInfo : Observable
{
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("owner")] public OwnerDto? OwnerInfo { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("stargazers_count")] public int Stars { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("topics")] public List<string>? Topics { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("fork")] public bool Fork { get; set; }
    [JsonPropertyName("forks_count")] public int Forks { get; set; }
    [JsonPropertyName("open_issues_count")] public int OpenIssues { get; set; }
    [JsonPropertyName("subscribers_count")] public int Watchers { get; set; }
    [JsonPropertyName("license")] public LicenseDto? License { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("pushed_at")] public DateTime? PushedAt { get; set; }

    [JsonIgnore] public string ForksText => Fmt.Count(Forks);
    [JsonIgnore] public string IssuesText => Fmt.Count(OpenIssues);
    [JsonIgnore] public string LicenseText => License == null ? "No license" : (!string.IsNullOrEmpty(License.SpdxId) && License.SpdxId != "NOASSERTION" ? License.SpdxId : License.Name ?? "Custom");
    [JsonIgnore] public string UpdatedText => PushedAt.HasValue ? Fmt.Ago(PushedAt) : "—";
    [JsonIgnore] public string CreatedText => CreatedAt.HasValue ? CreatedAt.Value.ToString("MMM yyyy") : "—";
    [JsonIgnore] public bool HasHomepage => !string.IsNullOrWhiteSpace(Homepage);
    [JsonIgnore] public bool HasTopics => Topics != null && Topics.Count > 0;

    [JsonIgnore] public string OwnerLogin => OwnerInfo?.Login ?? FullName.Split('/')[0];
    [JsonIgnore] public string AvatarUrl => OwnerInfo?.AvatarUrl ?? "";
    [JsonIgnore] public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    [JsonIgnore] public string StarsText => Fmt.Count(Stars);
    [JsonIgnore] public bool HasLanguage => !string.IsNullOrEmpty(Language);
    [JsonIgnore] public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    bool _tracked; [JsonIgnore] public bool IsTracked { get => _tracked; set => Set(ref _tracked, value); }
    bool _tryLater; [JsonIgnore] public bool IsTryLater { get => _tryLater; set => Set(ref _tryLater, value); }

    /// <summary>Where the project lives: "github", or e.g. "forgejo:git.eden-emu.dev". Only Discover's curated picks use anything but GitHub.</summary>
    [JsonPropertyName("gitsync_source")] public string Source { get; set; } = "github";

    // Discover rankings (not part of GitHub's data)
    int _rank; [JsonIgnore] public int Rank { get => _rank; set { _rank = value; Raise(nameof(HasRank)); Raise(nameof(RankText)); } }
    [JsonIgnore] public bool HasRank => _rank > 0;
    [JsonIgnore] public string RankText => "#" + _rank;
    string _moveKind = ""; [JsonIgnore] public string MoveKind { get => _moveKind; set { _moveKind = value ?? ""; Raise(nameof(MoveKind)); Raise(nameof(HasMove)); } }
    string _moveText = ""; [JsonIgnore] public string MoveText { get => _moveText; set { _moveText = value ?? ""; Raise(nameof(MoveText)); } }
    [JsonIgnore] public bool HasMove => _moveKind.Length > 0;
    string _gainShort = ""; [JsonIgnore] public string GainShort { get => _gainShort; set { _gainShort = value ?? ""; Raise(nameof(GainShort)); } }
    string _gainText = ""; [JsonIgnore] public string GainText { get => _gainText; set { _gainText = value ?? ""; Raise(nameof(GainText)); Raise(nameof(HasGain)); } }
    [JsonIgnore] public bool HasGain => _gainText.Length > 0;
    string _starredText = ""; [JsonIgnore] public string StarredText { get => _starredText; set { _starredText = value ?? ""; Raise(nameof(StarredText)); Raise(nameof(HasStarredText)); } }
    [JsonIgnore] public bool HasStarredText => _starredText.Length > 0;
    bool _windows; [JsonIgnore] public bool HasWindows { get => _windows; set => Set(ref _windows, value); }
}

/// <summary>A repository saved to look at later — not tracked or installed.</summary>
public class TryLaterItem : Observable
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    /// <summary>Where it lives: "github" (also what older saved items mean), "gitlab", "codeberg", or "forgejo:host".</summary>
    public string Source { get; set; } = "github";
    public string Platform { get; set; } = "Windows";
    public string? Description { get; set; }
    public string? Language { get; set; }
    public int Stars { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string>? Topics { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the note last changed (used when merging with your other devices).</summary>
    public DateTime? ModifiedAt { get; set; }
    string? _note; public string? Note { get => _note; set { if (Set(ref _note, value)) { ModifiedAt = DateTime.UtcNow; Raise(nameof(HasNote)); } } }

    [JsonIgnore] public string Key => $"{Source}:{Owner}/{Repo}".ToLowerInvariant();
    [JsonIgnore] public string FullName => $"{Owner}/{Repo}";
    [JsonIgnore] public string Url => RepoRef.UrlFor(Source, Owner, Repo);
    string? _category;
    /// <summary>What kind of project this is (Games, Media, Developer tools…), worked out the same way the library does it.</summary>
    [JsonIgnore] public string Category => _category ??= Categorizer.Categorize(Repo, Description, Topics, Language);
    [JsonIgnore] public string Initial => Repo.Length > 0 ? char.ToUpperInvariant(Repo[0]).ToString() : "?";
    [JsonIgnore] public string StarsText => Fmt.Count(Stars);
    [JsonIgnore] public bool HasStars => Stars > 0;
    [JsonIgnore] public bool HasLanguage => !string.IsNullOrEmpty(Language);
    [JsonIgnore] public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    [JsonIgnore] public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    [JsonIgnore] public string SavedText => "Saved " + Fmt.Ago(SavedAt);
}

public class LicenseDto
{
    [JsonPropertyName("spdx_id")] public string? SpdxId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>A GitHub user or organization.</summary>
public class UserInfo
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("bio")] public string? Bio { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("public_repos")] public int PublicRepos { get; set; }
    [JsonPropertyName("followers")] public int Followers { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("blog")] public string? Blog { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name!;
    [JsonIgnore] public string Initial => Login.Length > 0 ? char.ToUpperInvariant(Login[0]).ToString() : "?";
    [JsonIgnore] public bool HasBio => !string.IsNullOrWhiteSpace(Bio);
    [JsonIgnore] public string KindText => string.Equals(Type, "Organization", StringComparison.OrdinalIgnoreCase) ? "Organization" : "Developer";
    [JsonIgnore] public string StatsText => $"{PublicRepos:N0} public repos" + (Followers > 0 ? $"  ·  {Fmt.Count(Followers)} followers" : "") + (string.IsNullOrWhiteSpace(Location) ? "" : "  ·  " + Location);
}

public class AssetDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    [JsonPropertyName("download_count")] public int Downloads { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

public class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<AssetDto> Assets { get; set; } = new();
}

public class SearchResponse
{
    [JsonPropertyName("total_count")] public int Total { get; set; }
    [JsonPropertyName("items")] public List<RepoInfo> Items { get; set; } = new();
}

// ---------------- helpers ----------------

public static class Fmt
{
    public static string Size(long b)
    {
        if (b <= 0) return "—";
        string[] u = { "B", "KB", "MB", "GB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < 3) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string Count(int n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    public static string Ago(DateTime? t)
    {
        if (t == null) return "—";
        var local = t.Value.Kind == DateTimeKind.Unspecified ? t.Value : t.Value.ToLocalTime();
        var d = DateTime.Now - local;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)Math.Max(1, d.TotalMinutes)} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return local.ToString(local.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy");
    }

    public static string StripMarkdown(string? md, int max = 2500)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var s = md.Replace("\r", "");
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*[-*+]\s+", "•  ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*[-*_]{3,}\s*$", "", RegexOptions.Multiline);
        s = s.Replace("**", "").Replace("__", "").Replace("`", "").Replace("~~", "");
        s = Regex.Replace(s, @"(?<![\w*])[_*]([^_*\n]+)[_*](?![\w*])", "$1");
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    public static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}

public static class VersionUtil
{
    static readonly Regex Num = new(@"\d+", RegexOptions.Compiled);
    static readonly Regex Pre = new(@"(alpha|beta|rc|preview|pre|dev|nightly|canary|snapshot)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static List<long> Parts(string v)
    {
        var m = Regex.Match(v, @"\d+(?:\.\d+)*");
        var src = m.Success ? m.Value : "";
        return src.Length == 0 ? new() : src.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToList();
    }

    public static string Clean(string v) => v.Trim().TrimStart('v', 'V');

    public static bool IsNewer(string latest, string installed)
    {
        if (Clean(latest) == Clean(installed)) return false;
        var a = Parts(latest); var b = Parts(installed);
        if (a.Count == 0 || b.Count == 0) return true; // unparsable: any difference counts as an update
        int n = Math.Max(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            long x = i < a.Count ? a[i] : 0, y = i < b.Count ? b[i] : 0;
            if (x != y) return x > y;
        }
        // numerically equal: a release replacing a pre-release counts as newer
        return Pre.IsMatch(installed) && !Pre.IsMatch(latest);
    }
}
