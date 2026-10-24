using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ReleaseSync.Core;
using Microsoft.Win32;

namespace ReleaseSync.UI;

public sealed record HintItem(string Level, string Text)
{
    public string Glyph => Level switch { "warn" => "", "good" => "", _ => "" };
}

public sealed record Choice(string Label, object Value)
{
    public override string ToString() => Label;
}
public sealed record Preset(string Title, string Query, string Glyph, string Blurb);

public sealed class GroupEntry : Observable
{
    string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    int _count; public int Count { get => _count; set => Set(ref _count, value); }
    bool _custom; public bool IsCustom { get => _custom; set => Set(ref _custom, value); }
    bool _sel; public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    public string Glyph => IsCustom ? "" : "";
}

public sealed class ToastItem
{
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "info";
    public string Glyph => Kind switch { "success" => "", "error" => "", "warn" => "", _ => "" };
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "info";
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Glyph => Kind switch { "success" => "", "error" => "", "warn" => "", _ => "" };
}

public sealed class PromptVM : Observable
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string OkText { get; set; } = "OK";
    public string Placeholder { get; set; } = "";
    public bool ShowInput { get; set; }
    public bool IsDanger { get; set; }
    public bool ShowCheck { get; set; }
    public string CheckText { get; set; } = "";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    string _text = ""; public string Text { get => _text; set { if (Set(ref _text, value)) Error = null; } }
    bool _checked; public bool Checked { get => _checked; set => Set(ref _checked, value); }
    string? _error; public string? Error { get => _error; set => Set(ref _error, value); }
    public Func<PromptVM, string?>? OnOk { get; set; }
    public Action? OnClose { get; set; }
}

/// <summary>The release dropdown: browse every release of a project and pick a version and file to install or download.</summary>
public sealed class ReleasePickerVM : Observable
{
    public TrackedApp App { get; }
    public bool ShowActions { get; }
    public List<ReleaseSummary> Releases { get; }
    public List<AssetInfo> AssetChoices { get; private set; } = new();

    ReleaseSummary? _sel;
    public ReleaseSummary? Selected { get => _sel; set { if (Set(ref _sel, value)) Rebuild(); } }
    AssetInfo? _asset; public AssetInfo? SelectedAsset { get => _asset; set { if (Set(ref _asset, value)) Raise(nameof(FileText)); } }
    public AssetInfo? SelectedAssetOrNull => _asset == null || _asset.IsAutomatic ? null : _asset;

    public ReleasePickerVM(TrackedApp app, bool showActions)
    {
        App = app; ShowActions = showActions;
        var list = app.Releases.Count > 0 ? app.Releases.ToList() : (app.LatestRelease != null ? new List<ReleaseSummary> { app.LatestRelease } : new());
        foreach (var r in list) r.IsLatest = r.Tag == app.LatestVersion;
        Releases = list;
        _sel = list.FirstOrDefault(r => r.IsLatest) ?? list.FirstOrDefault();
        Rebuild();
    }

    void Rebuild()
    {
        var choices = new List<AssetInfo> { new() { Name = "Automatic (best match for this PC)" } };
        if (_sel != null) choices.AddRange(AssetPicker.Choices(_sel.Assets));
        AssetChoices = choices;
        _asset = choices[0];
        Raise(string.Empty);
    }

    public void Refresh() => Raise(string.Empty);

    /// <summary>Several builds of one release (gcc-standard, clang-pgo…): prefer the one whose name shares the most words with the copy already on the PC.</summary>
    public void PreferFlavor(string? folderName)
    {
        if (_sel == null || string.IsNullOrWhiteSpace(folderName)) return;
        static HashSet<string> Words(string s) => System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 1 && !w.All(char.IsDigit) && !System.Text.RegularExpressions.Regex.IsMatch(w, @"^v\d+$")).ToHashSet();
        var have = Words(folderName);
        var best = AssetChoices.Where(a => !a.IsAutomatic).Select(a => (a, score: Words(a.Name).Count(have.Contains))).OrderByDescending(x => x.score).FirstOrDefault();
        var current = AssetPicker.Pick(_sel.Assets, App.AssetPattern);
        int currentScore = current == null ? 0 : Words(current.Name).Count(have.Contains);
        if (best.a != null && best.score > currentScore) { _asset = best.a; Raise(string.Empty); }
    }

    public bool HasReleases => Releases.Count > 0;
    public bool IsCurrent => _sel != null && App.InstalledVersion == _sel.Tag;
    public bool IsOlder => _sel != null && !string.IsNullOrEmpty(App.InstalledVersion) && VersionUtil.IsNewer(App.InstalledVersion!, _sel.Tag);
    public string InstallText => IsCurrent ? "Reinstall this version" : IsOlder ? "Roll back to this version" : App.IsInstalled ? "Install this version" : "Install this version";
    public string NotesText => _sel?.NotesText ?? "";
    public string FileText
    {
        get
        {
            if (_sel == null) return "";
            var f = SelectedAssetOrNull ?? AssetPicker.Pick(_sel.Assets, App.AssetPattern);
            return f == null ? "No Windows file found in this release — you can still open its page." : $"{f.Name}  ·  {f.SizeText}";
        }
    }
    public bool CanAct => _sel != null && !App.IsBusy;
}

public sealed class AddPreviewVM : Observable
{
    public string Source { get; set; } = "github";
    string? _installDir; public string? InstallDir { get => _installDir; set { if (Set(ref _installDir, value)) { Raise(nameof(HasInstallDir)); Raise(nameof(InstallDirText)); } } }
    public bool HasInstallDir => !string.IsNullOrEmpty(_installDir);
    public string InstallDirText => HasInstallDir ? _installDir! : "Default — portable apps go to ReleaseSync's apps folder; setup programs choose their own folder";
    List<HintItem> _hints = new(); public List<HintItem> Hints { get => _hints; set { Set(ref _hints, value); Raise(nameof(HasHints)); } }
    public bool HasHints => _hints.Count > 0;
    ReleasePickerVM? _picker; public ReleasePickerVM? Picker { get => _picker; set { Set(ref _picker, value); Raise(nameof(PrimaryText)); } }
    public string Owner { get; set; } = "";
    public string RepoName { get; set; } = "";
    public List<ReleaseInfo> Releases { get; set; } = new();
    public List<Choice> Groups { get; set; } = new();
    public string? ETagHolder { get; set; }

    bool _loading = true; public bool IsLoading { get => _loading; set { if (Set(ref _loading, value)) { Raise(nameof(CanConfirm)); Raise(nameof(HasDraft)); } } }
    string? _error; public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    RepoInfo? _info; public RepoInfo? Info { get => _info; set => Set(ref _info, value); }
    TrackedApp? _draft; public TrackedApp? Draft { get => _draft; set { if (Set(ref _draft, value)) { Raise(nameof(HasDraft)); Raise(nameof(CanConfirm)); Raise(nameof(Choices)); Raise(nameof(CanInstall)); Raise(nameof(CanChooseInstall)); Raise(nameof(PrimaryText)); Raise(nameof(NoAssetWarning)); } } }
    public bool HasDraft => _draft != null && !_loading;
    public List<AssetInfo> Choices => _draft?.AssetChoices ?? new();
    AssetInfo? _choice; public AssetInfo? SelectedChoice { get => _choice; set { if (Set(ref _choice, value)) { if (_draft != null) { _draft.AssetPattern = value == null || value.IsAutomatic ? null : AssetPicker.MakePattern(value.Name); } Raise(nameof(CanInstall)); Raise(nameof(PrimaryText)); Raise(nameof(NoAssetWarning)); } } }
    string _group = ""; public string GroupKey { get => _group; set { if (Set(ref _group, value ?? "") && _draft != null) _draft.GroupKey = value ?? ""; } }
    bool _pre; public bool IncludePre { get => _pre; set => Set(ref _pre, value); }
    bool _install = true; public bool InstallNow { get => _install; set { if (Set(ref _install, value)) Raise(nameof(PrimaryText)); } }
    public bool CanInstall => _draft?.ChosenAsset != null;
    public bool NoAssetWarning => _draft != null && _draft.HasRelease && !CanInstall;
    public bool CanConfirm => HasDraft;
    public string PrimaryText => !_have && InstallNow && CanInstall ? (_picker?.Selected is { IsLatest: false } s ? $"Add & install {s.Tag}" : "Add & install") : "Add to library";

    // "I already have this on my PC"
    public bool DetectionDone { get; set; }
    UninstallEntry? _detected; public UninstallEntry? Detected { get => _detected; set { if (Set(ref _detected, value)) { Raise(nameof(HasDetected)); Raise(nameof(DetectedText)); } } }
    bool _foundLocally; public bool FoundLocally { get => _foundLocally; set { if (Set(ref _foundLocally, value)) { Raise(nameof(HasDetected)); Raise(nameof(DetectedText)); } } }
    public bool HasDetected => _detected != null || _foundLocally;
    public string DetectedText => _detected != null
        ? $"Found “{_detected.DisplayName}”{(string.IsNullOrEmpty(_detected.Version) ? "" : " " + _detected.Version)} in Windows' installed programs."
        : _foundLocally ? $"Found a copy on this PC: {_exePath}" : "";
    bool _have; public bool AlreadyHave { get => _have; set { if (Set(ref _have, value)) { Raise(nameof(PrimaryText)); Raise(nameof(CanChooseInstall)); } } }
    public bool CanChooseInstall => CanInstall && !_have;
    string _haveVersion = ""; public string HaveVersion { get => _haveVersion; set => Set(ref _haveVersion, value ?? ""); }
    string? _exePath; public string? ExePath { get => _exePath; set { if (Set(ref _exePath, value)) { Raise(nameof(HasExePath)); Raise(nameof(ExePathText)); } } }
    public bool HasExePath => !string.IsNullOrEmpty(_exePath);
    public string ExePathText => HasExePath ? _exePath! : "Not linked — optional, lets ReleaseSync launch it for you";

    bool _tryLater; public bool IsTryLater { get => _tryLater; set { if (Set(ref _tryLater, value)) { Raise(nameof(TryLaterText)); Raise(nameof(TryLaterGlyph)); } } }
    public string TryLaterText => _tryLater ? "On your try-later list" : "Try later";
    public string TryLaterGlyph => _tryLater ? "" : "";
}

/// <summary>State of the "repository details" dialog.</summary>
public sealed class RepoDetailVM : Observable
{
    public string Source { get; set; } = "github";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    List<HintItem> _hints = new(); public List<HintItem> Hints { get => _hints; set { Set(ref _hints, value); Raise(nameof(HasHints)); } }
    public bool HasHints => _hints.Count > 0;
    bool _loading = true; public bool IsLoading { get => _loading; set => Set(ref _loading, value); }
    string? _error; public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    RepoInfo? _info; public RepoInfo? Info { get => _info; set { Set(ref _info, value); Raise(nameof(HasInfo)); } }
    public bool HasInfo => _info != null;
    string _readme = ""; public string Readme { get => _readme; set { Set(ref _readme, value); Raise(nameof(HasReadme)); Raise(nameof(NoReadme)); } }
    public bool HasReadme => _readme.Length > 0;
    bool _readmeLoading = true; public bool ReadmeLoading { get => _readmeLoading; set { Set(ref _readmeLoading, value); Raise(nameof(NoReadme)); } }
    public bool NoReadme => !_readmeLoading && _readme.Length == 0;
    bool _tracked; public bool IsTracked { get => _tracked; set { if (Set(ref _tracked, value)) { Raise(nameof(AddText)); Raise(nameof(AddGlyph)); } } }
    bool _tryLater; public bool IsTryLater { get => _tryLater; set { if (Set(ref _tryLater, value)) { Raise(nameof(TryLaterText)); Raise(nameof(TryLaterGlyph)); } } }
    public string AddText => _tracked ? "Open in library" : "Add to library";
    public string AddGlyph => _tracked ? "" : "";
    public string TryLaterText => _tryLater ? "On your try-later list" : "Try later";
    public string TryLaterGlyph => _tryLater ? "" : "";
    public string FullName => $"{Owner}/{Name}";
}

public sealed partial class MainViewModel : Observable
{
    public AppSettings Settings { get; }
    public GitHubService GitHub { get; } = new();
    public SourceClient Sources { get; }
    public InstallService Installer { get; } = new();
    readonly Store store = new();

    public ObservableCollection<TrackedApp> Apps { get; } = new();
    public ObservableCollection<TryLaterItem> TryLater { get; } = new();
    public int TryLaterCount => TryLater.Count;
    public bool HasTryLater => TryLater.Count > 0;
    public bool NoTryLater => TryLater.Count == 0;
    readonly DispatcherTimer saveWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    public ObservableCollection<GroupEntry> Groups { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();
    public ObservableCollection<RepoInfo> SearchResults { get; } = new();
    public ListCollectionView LibraryView { get; }
    public ListCollectionView UpdatesView { get; }

    public Action<string, string>? NotifyRequest;
    public Action? ExitRequest;

    readonly SemaphoreSlim installLock = new(1, 1);
    readonly DispatcherTimer saveAppsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer saveSettingsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly DispatcherTimer checkTimer = new();
    CancellationTokenSource? searchCts;
    int searchPage = 1;

    public static readonly List<Preset> Presets = new()
    {
        new("Popular for Windows", "topic:windows stars:>3000", "", "Most starred Windows projects"),
        new("Utilities", "topic:windows topic:utility stars:>300", "", "Handy tools and system helpers"),
        new("Emulators & games", "topic:emulator topic:windows stars:>500", "", "Retro consoles and PC games"),
        new("Media", "topic:media-player topic:windows stars:>300", "", "Video, audio and streaming"),
        new("Developer tools", "topic:developer-tools topic:windows stars:>500", "", "Editors, CLIs and build tools"),
        new("Privacy & security", "topic:privacy topic:windows stars:>300", "", "Encryption, passwords and VPNs"),
        new("Productivity", "topic:productivity topic:windows stars:>500", "", "Notes, tasks and organization"),
        new("Customization", "topic:customization topic:windows stars:>300", "", "Themes, taskbars and tweaks"),
    };

    public static readonly string[] ThemeChoices = { "System", "Dark", "Light" };
    public static readonly string[] DensityChoices = { "Comfortable", "Compact" };
    public static readonly string[] ArchChoices = { "Auto", "x64", "arm64", "x86" };
    public static readonly string[] SortChoices = { "Name", "Recently updated", "Recently added", "Status" };
    public static readonly List<Choice> IntervalChoices = new()
    {
        new("Every 15 minutes", 15), new("Every 30 minutes", 30), new("Every hour", 60), new("Every 3 hours", 180),
        new("Every 6 hours", 360), new("Every 12 hours", 720), new("Daily", 1440), new("Manually only", 0)
    };
    public static readonly List<Choice> ScaleChoices = new()
    {
        new("80%", 0.8), new("90%", 0.9), new("100%", 1.0), new("110%", 1.1), new("125%", 1.25), new("150%", 1.5)
    };
    public static readonly string[] AccentColors = { "#6C8CFF", "#8B5CF6", "#EC6BA8", "#F2685B", "#F59E42", "#3DC48C", "#22B8CF", "#94A3B8" };
    public static readonly string[] SettingsSections = { "General", "GitHub", "Updates", "Installation", "Security", "Appearance", "Data" };

    public MainViewModel()
    {
        Sources = new SourceClient(GitHub);
        Settings = store.LoadSettings();
        AppSettingsProvider.Current = Settings;
        bool hadLegacyToken = Settings.ProtectedToken != null;
        _ = Settings.GitHubToken;                       // moves an older file-stored token into Credential Manager
        if (hadLegacyToken && Settings.ProtectedToken == null) SaveSettingsNow();
        foreach (var a in store.LoadApps())
        {
            Apps.Add(a); a.PropertyChanged += AppChanged; a.IsBusy = false;
            a.PrefSignature = PrefSig(a);
            a.Category = Categorizer.Categorize(a.Repo, a.Description, a.Topics, a.Language);   // always re-sort with the latest rules; groups the user chose are separate and kept
        }
        Settings.PropertyChanged += SettingsChanged;
        foreach (var w in store.LoadWatch()) { TryLater.Add(w); w.PropertyChanged += (_, _) => ScheduleSaveWatch(); }
        TryLater.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (TryLaterItem w in e.NewItems) { w.PropertyChanged += (_, _) => { ScheduleSaveWatch(); if (!syncApplying) ScheduleSync(); }; Settings.Tombstones.Remove(SyncMerger.LaterKey(w)); }
            if (e.OldItems != null && !syncApplying) foreach (TryLaterItem w in e.OldItems) { Settings.Tombstones[SyncMerger.LaterKey(w)] = DateTime.UtcNow; ScheduleSaveSettings(); }
            if (!syncApplying) ScheduleSync();
            Raise(nameof(TryLaterCount)); Raise(nameof(HasTryLater)); Raise(nameof(NoTryLater));
            ScheduleSaveWatch();
        };
        saveWatchTimer.Tick += (_, _) => { saveWatchTimer.Stop(); SaveWatchNow(); };
        Apps.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (TrackedApp a in e.NewItems) { a.PropertyChanged += AppChanged; a.PrefSignature ??= PrefSig(a); Settings.Tombstones.Remove(a.Key); }
            if (!syncApplying) ScheduleSync();
            ScheduleRefresh(); ScheduleSaveApps();
        };

        LibraryView = new ListCollectionView(Apps) { Filter = LibraryFilter };
        UpdatesView = new ListCollectionView(Apps) { Filter = o => o is TrackedApp a && a.HasUpdate && !a.Hidden };
        UpdatesView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.Title), ListSortDirection.Ascending));

        saveAppsTimer.Tick += (_, _) => { saveAppsTimer.Stop(); SaveAppsNow(); };
        saveSettingsTimer.Tick += (_, _) => { saveSettingsTimer.Stop(); SaveSettingsNow(); };
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); RefreshViews(); };
        checkTimer.Tick += async (_, _) => await CheckAllAsync(false);
        GitHub.StatusChanged += () => Application.Current.Dispatcher.BeginInvoke(new Action(() => { UpdateRateText(); RaiseDiscoverRate(); }));

        InitCommands();
        InitScanCommands();
        InitRelink();
        InitInstallDir();
        InitBackups();
        InitDiscover();
        InitTryLater();
        SetPlatformCommand = new RelayCommand(p => { _platformFilter = string.IsNullOrEmpty(p as string) ? null : (string)p; RefreshViews(); });
        InitSync();
        ApplyViewConfig();
        UpdateGroups();
        ConfigureTimer();
        UpdateRateText();
        _isCompact = Settings.SidebarCollapsed;
        OptionsRefresh();
    }

    // ===================== state =====================
    string _page = "Library"; public string CurrentPage { get => _page; set { if (Set(ref _page, value)) { Raise(nameof(PageTitle)); } } }
    public string PageTitle => _page == "Import" ? "Import & Export" : _page == "TryLater" ? "Try later" : _page == "Scan" ? "Scan PC" : _page;
    string _settingsSection = "General"; public string SettingsSection { get => _settingsSection; set => Set(ref _settingsSection, value); }
    bool _isCompact; public bool IsCompact { get => _isCompact; set => Set(ref _isCompact, value); }
    bool _isNarrow; public bool IsNarrow { get => _isNarrow; set => Set(ref _isNarrow, value); }
    TrackedApp? _selected; public TrackedApp? SelectedApp { get => _selected; set { if (value != null && value != DrawerApp) { DrawerApp = value; DrawerPicker = new ReleasePickerVM(value, true); } if (Set(ref _selected, value)) Raise(nameof(IsDrawerOpen)); } }
    ReleasePickerVM? _drawerPicker; public ReleasePickerVM? DrawerPicker { get => _drawerPicker; private set => Set(ref _drawerPicker, value); }
    TrackedApp? _drawerApp; public TrackedApp? DrawerApp { get => _drawerApp; private set => Set(ref _drawerApp, value); }
    public static readonly List<Choice> ModeChoices = new()
    {
        new("Automatic", InstallMode.Auto), new("Run as installer", InstallMode.Installer), new("Unpack as portable app", InstallMode.Portable)
    };
    public bool IsDrawerOpen => _selected != null;
    AddPreviewVM? _add; public AddPreviewVM? AddPreview { get => _add; set { if (Set(ref _add, value)) { Raise(nameof(IsAddOpen)); Raise(nameof(IsModalOpen)); } } }
    public bool IsAddOpen => _add != null;
    PromptVM? _prompt; public PromptVM? Prompt { get => _prompt; set { var old = _prompt; if (Set(ref _prompt, value)) { old?.OnClose?.Invoke(); Raise(nameof(IsPromptOpen)); Raise(nameof(IsModalOpen)); } } }
    public bool IsPromptOpen => _prompt != null;
    RepoDetailVM? _repoDetail; public RepoDetailVM? RepoDetail { get => _repoDetail; set { if (Set(ref _repoDetail, value)) { Raise(nameof(IsRepoOpen)); Raise(nameof(IsModalOpen)); } } }
    public bool IsRepoOpen => _repoDetail != null;
    UserInfo? _ownerProfile; public UserInfo? OwnerProfile { get => _ownerProfile; set { Set(ref _ownerProfile, value); Raise(nameof(HasOwnerProfile)); } }
    public bool HasOwnerProfile => _ownerProfile != null;
    string? ownerLogin;
    public bool IsModalOpen => _add != null || _prompt != null || _repoDetail != null;

    string _omni = ""; public string OmniboxText { get => _omni; set { if (Set(ref _omni, value)) { Raise(nameof(OmniboxIsRepo)); Raise(nameof(OmniboxActionText)); Raise(nameof(OmniboxHasText)); } } }
    public bool OmniboxIsRepo => SourceClient.TryParse(_omni, out _);
    public bool OmniboxHasText => !string.IsNullOrWhiteSpace(_omni);
    public string OmniboxActionText => OmniboxIsRepo ? "Add" : "Search";

    string? _groupFilter; public string? GroupFilter { get => _groupFilter; set { if (Set(ref _groupFilter, value)) { foreach (var g in Groups) g.IsSelected = g.Name == value; Raise(nameof(HasGroupFilter)); Raise(nameof(NoGroupFilter)); RefreshViews(); } } }
    public bool HasGroupFilter => _groupFilter != null;
    public bool NoGroupFilter => _groupFilter == null;

    // platform: the top level of the library (Platform → group → app). Hidden while there is only one platform.
    public ObservableCollection<TryTypeChip> Platforms { get; } = new();
    public bool HasPlatforms => Platforms.Count > 2;          // "All" plus at least two real platforms
    string? _platformFilter;
    public ICommand SetPlatformCommand { get; private set; } = null!;
    void UpdatePlatforms()
    {
        var counts = Apps.Where(a => !a.Hidden).GroupBy(a => a.Platform).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var wanted = new List<(string Name, int Count)> { ("", counts.Sum(g => g.Count())) };
        wanted.AddRange(counts.Select(g => (g.Key, g.Count())));
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Platforms.Count && Platforms[i].Name == wanted[i].Name) Platforms[i].Count = wanted[i].Count;
            else Platforms.Insert(i, new TryTypeChip { Name = wanted[i].Name, Count = wanted[i].Count });
        }
        while (Platforms.Count > wanted.Count) Platforms.RemoveAt(Platforms.Count - 1);
        if (_platformFilter != null && !wanted.Any(w => w.Name == _platformFilter)) _platformFilter = null;
        foreach (var p in Platforms) p.IsSelected = p.Name == (_platformFilter ?? "");
        Raise(nameof(HasPlatforms));
    }
    string _status = "All"; public string StatusFilter { get => _status; set { if (Set(ref _status, value)) RefreshViews(); } }
    string _filterText = ""; public string LibraryFilterText { get => _filterText; set { if (Set(ref _filterText, value)) RefreshViews(); } }

    public int TotalCount => Apps.Count(a => !a.Hidden);
    public int HiddenCount => Apps.Count(a => a.Hidden);
    public int UpdateCount => Apps.Count(a => a.HasUpdate && !a.Hidden);
    public int FavoriteCount => Apps.Count(a => a.Pinned && !a.Hidden);
    public int InstalledCount => Apps.Count(a => a.IsInstalled && !a.Hidden);
    public int NotInstalledCount => Apps.Count(a => !a.IsInstalled && !a.Hidden);
    public int ShownCount => LibraryView.Count;
    public bool IsLibraryEmpty => Apps.Count == 0;
    public bool NoLibraryMatches => Apps.Count > 0 && LibraryView.Count == 0 && EmptyGroups.Count == 0;

    /// <summary>Your own groups that have nothing in them yet, so they still appear in the library instead of seeming to vanish.</summary>
    List<GroupEntry> _emptyGroups = new(); public List<GroupEntry> EmptyGroups { get => _emptyGroups; private set => Set(ref _emptyGroups, value); }
    public bool NoUpdates => UpdatesView.Count == 0;
    public string Subtitle => Apps.Count == 0 ? "Nothing tracked yet"
        : _status == "Hidden" ? $"{HiddenCount} hidden {(HiddenCount == 1 ? "app" : "apps")}  ·  not shown in your library or update alerts"
        : $"{TotalCount} {(TotalCount == 1 ? "app" : "apps")}  ·  {(UpdateCount == 0 ? "everything is up to date" : UpdateCount + (UpdateCount == 1 ? " update available" : " updates available"))}" + (HiddenCount > 0 ? $"  ·  {HiddenCount} hidden" : "");
    public string LastCheckText => Settings.LastFullCheck.HasValue ? "Last checked " + Fmt.Ago(Settings.LastFullCheck) : "Not checked yet";

    bool _checking; public bool IsChecking { get => _checking; set { if (Set(ref _checking, value)) Raise(nameof(CheckButtonText)); } }
    string _checkProgress = ""; public string CheckProgressText { get => _checkProgress; set { Set(ref _checkProgress, value); Raise(nameof(CheckButtonText)); } }
    public string CheckButtonText => _checking ? (string.IsNullOrEmpty(_checkProgress) ? "Checking…" : _checkProgress) : "Check for updates";
    bool _updatingAll; public bool IsUpdatingAll { get => _updatingAll; set => Set(ref _updatingAll, value); }

    string _rateText = ""; public string RateText { get => _rateText; set => Set(ref _rateText, value); }
    string _rateLevel = "ok"; public string RateLevel { get => _rateLevel; set => Set(ref _rateLevel, value); }
    string _rateHint = ""; public string RateHint { get => _rateHint; set => Set(ref _rateHint, value); }

    // discover
    string _searchQuery = ""; public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }
    string _searchTitle = "Discover"; public string SearchTitle { get => _searchTitle; set => Set(ref _searchTitle, value); }
    bool _searchBusy; public bool SearchBusy { get => _searchBusy; set { if (Set(ref _searchBusy, value)) Raise(nameof(ShowPresets)); } }
    string? _searchError; public string? SearchError { get => _searchError; set { Set(ref _searchError, value); Raise(nameof(HasSearchError)); } }
    public bool HasSearchError => !string.IsNullOrEmpty(_searchError);
    bool _canMore; public bool CanLoadMore { get => _canMore; set => Set(ref _canMore, value); }
    bool _hasSearched; public bool HasSearched { get => _hasSearched; set { if (Set(ref _hasSearched, value)) Raise(nameof(ShowPresets)); } }
    public bool ShowPresets => !_hasSearched;
    int _searchTotal; public int SearchTotal { get => _searchTotal; set { Set(ref _searchTotal, value); Raise(nameof(SearchSummary)); } }
    public string SearchSummary => _searchTotal > 0 ? $"{_searchTotal:N0} repositories" : "";

    // import
    string _importText = ""; public string ImportText { get => _importText; set => Set(ref _importText, value); }
    string _importStatus = ""; public string ImportStatus { get => _importStatus; set { Set(ref _importStatus, value); Raise(nameof(HasImportStatus)); } }
    public bool HasImportStatus => !string.IsNullOrEmpty(_importStatus);
    bool _importing; public bool IsImporting { get => _importing; set => Set(ref _importing, value); }
    bool _importMarkInstalled; public bool ImportMarkInstalled { get => _importMarkInstalled; set => Set(ref _importMarkInstalled, value); }
    bool _importInstall; public bool ImportInstall { get => _importInstall; set => Set(ref _importInstall, value); }
    string _importGroup = ""; public string ImportGroup { get => _importGroup; set => Set(ref _importGroup, value ?? ""); }

    List<Choice> _groupOptions = new(); public List<Choice> GroupOptions { get => _groupOptions; private set => Set(ref _groupOptions, value); }
    public const string NewGroupMarker = "\u0001new-group";
    List<Choice> _moveMenuOptions = new(); public List<Choice> MoveMenuOptions { get => _moveMenuOptions; private set => Set(ref _moveMenuOptions, value); }
    List<Choice> _importGroupOptions = new(); public List<Choice> ImportGroupOptions { get => _importGroupOptions; private set => Set(ref _importGroupOptions, value); }

    void OptionsRefresh()
    {
        var custom = Settings.CustomGroups.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).Select(x => new Choice(x, x));
        var cats = Settings.AutoGroups ? Categorizer.AllCategories.Where(c => !Settings.CustomGroups.Contains(c)).Select(c => new Choice(c, c)) : Enumerable.Empty<Choice>();
        GroupOptions = new[] { new Choice(Settings.AutoGroups ? "Automatic (by type)" : "No group", "") }.Concat(custom).Concat(cats).ToList();
        MoveMenuOptions = GroupOptions.Concat(new[] { new Choice("New group…", NewGroupMarker) }).ToList();
        ImportGroupOptions = new[] { new Choice("Automatic (by type)", "") }.Concat(custom).ToList();
    }

    // ===================== commands =====================
    public ICommand NavigateCommand { get; private set; } = null!;
    public ICommand ToggleSidebarCommand { get; private set; } = null!;
    public ICommand SubmitOmniboxCommand { get; private set; } = null!;
    public ICommand CheckAllCommand { get; private set; } = null!;
    public ICommand UpdateAllCommand { get; private set; } = null!;
    public ICommand InstallCommand { get; private set; } = null!;
    public ICommand CancelCommand { get; private set; } = null!;
    public ICommand CheckCommand { get; private set; } = null!;
    public ICommand OpenCommand { get; private set; } = null!;
    public ICommand CloseDrawerCommand { get; private set; } = null!;
    public ICommand RemoveCommand { get; private set; } = null!;
    public ICommand LaunchCommand { get; private set; } = null!;
    public ICommand MarkInstalledCommand { get; private set; } = null!;
    public ICommand ReinstallCommand { get; private set; } = null!;
    public ICommand UninstallCommand { get; private set; } = null!;
    public ICommand InstallPickedCommand { get; private set; } = null!;
    public ICommand DownloadPickedCommand { get; private set; } = null!;
    public ICommand SkipCommand { get; private set; } = null!;
    public ICommand DetailsCommand { get; private set; } = null!;
    public ICommand CloseDetailsCommand { get; private set; } = null!;
    public ICommand DetailAddCommand { get; private set; } = null!;
    public ICommand DetailTryLaterCommand { get; private set; } = null!;
    public ICommand OwnerCommand { get; private set; } = null!;
    public ICommand ToggleTryLaterCommand { get; private set; } = null!;
    public ICommand LocateExistingCommand { get; private set; } = null!;
    public ICommand RemoveTryLaterCommand { get; private set; } = null!;
    public ICommand AddFromTryLaterCommand { get; private set; } = null!;
    public ICommand PreviewTryLaterCommand { get; private set; } = null!;
    public ICommand ClearTryLaterNoteCommand { get; private set; } = null!;
    public ICommand GitHubCommand { get; private set; } = null!;
    public ICommand ReleasesCommand { get; private set; } = null!;
    public ICommand PinCommand { get; private set; } = null!;
    public ICommand HideCommand { get; private set; } = null!;
    public ICommand ToggleAutoGroupsCommand { get; private set; } = null!;
    public ICommand NewGroupCommand { get; private set; } = null!;
    public ICommand RenameGroupCommand { get; private set; } = null!;
    public ICommand DeleteGroupCommand { get; private set; } = null!;
    public ICommand SelectGroupCommand { get; private set; } = null!;
    public ICommand ClearGroupFilterCommand { get; private set; } = null!;
    public ICommand SetStatusFilterCommand { get; private set; } = null!;
    public ICommand SetViewModeCommand { get; private set; } = null!;
    public ICommand ToggleGroupingCommand { get; private set; } = null!;
    public ICommand PresetCommand { get; private set; } = null!;
    public ICommand LoadMoreCommand { get; private set; } = null!;
    public ICommand AddResultCommand { get; private set; } = null!;
    public ICommand ConfirmAddCommand { get; private set; } = null!;
    public ICommand CancelAddCommand { get; private set; } = null!;
    public ICommand PromptOkCommand { get; private set; } = null!;
    public ICommand PromptCancelCommand { get; private set; } = null!;
    public ICommand BrowseImportCommand { get; private set; } = null!;
    public ICommand ImportTextCommand { get; private set; } = null!;
    public ICommand ExportJsonCommand { get; private set; } = null!;
    public ICommand ExportLinksCommand { get; private set; } = null!;
    public ICommand CopyLinksCommand { get; private set; } = null!;
    public ICommand ClearLogCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;
    public ICommand ResetSettingsCommand { get; private set; } = null!;
    public ICommand ClearCacheCommand { get; private set; } = null!;
    public ICommand SetSectionCommand { get; private set; } = null!;
    public ICommand OpenUrlCommand { get; private set; } = null!;
    public ICommand DismissToastCommand { get; private set; } = null!;
    public ICommand SetAccentCommand { get; private set; } = null!;
    public ICommand OpenInstallDirCommand { get; private set; } = null!;

    void InitCommands()
    {
        NavigateCommand = new RelayCommand(p => Navigate(p?.ToString() ?? "Library"));
        ToggleSidebarCommand = new RelayCommand(() => { IsCompact = !IsCompact; Settings.SidebarCollapsed = IsCompact; });
        SubmitOmniboxCommand = new RelayCommand(SubmitOmnibox);
        CheckAllCommand = new RelayCommand(async () => await CheckAllAsync(true));
        UpdateAllCommand = new RelayCommand(async () => await UpdateAllAsync());
        InstallCommand = new RelayCommand(async p => { if (p is TrackedApp a) await InstallAsync(a); });
        CancelCommand = new RelayCommand(p => { if (p is TrackedApp a) a.Cts?.Cancel(); });
        CheckCommand = new RelayCommand(async p => { if (p is TrackedApp a) { var ok = await CheckAppAsync(a, true); RefreshViews(); ScheduleSaveApps(); if (ok && a.HasError == false) Toast(a.HasUpdate ? $"{a.Title} has an update: {a.LatestVersion}" : $"{a.Title} is up to date", a.HasUpdate ? "info" : "success"); else if (a.HasError) Toast(a.LastError!, "error"); } });
        OpenCommand = new RelayCommand(p => { if (p is TrackedApp a) SelectedApp = a; });
        CloseDrawerCommand = new RelayCommand(() => SelectedApp = null);
        RemoveCommand = new RelayCommand(p => { if (p is TrackedApp a) AskRemove(a); });
        LaunchCommand = new RelayCommand(p => { if (p is TrackedApp a && a.CanLaunch) Shell.Open(a.InstallPath); });
        MarkInstalledCommand = new RelayCommand(p =>
        {
            if (p is TrackedApp a && a.HasRelease) { a.InstalledVersion = a.LatestVersion; ScheduleSaveApps(); Toast($"{a.Title} marked as installed ({a.LatestVersion})", "success"); AddLog($"Marked {a.FullName} as installed at {a.LatestVersion}", "info"); }
        });
        ReinstallCommand = new RelayCommand(async p => { if (p is TrackedApp a) await InstallAsync(a); });
        UninstallCommand = new RelayCommand(p => { if (p is TrackedApp a) AskUninstall(a); });
        ToggleTryLaterCommand = new RelayCommand(p => { if (p is RepoInfo r) ToggleTryLater(r); });
        LocateExistingCommand = new RelayCommand(LocateExisting);
        InstallPickedCommand = new RelayCommand(async p => { if (p is ReleasePickerVM pk && pk.Selected != null) await InstallAsync(pk.App, pk.Selected, pk.SelectedAssetOrNull); });
        DownloadPickedCommand = new RelayCommand(async p => { if (p is ReleasePickerVM pk) await DownloadPickedAsync(pk); });
        SkipCommand = new RelayCommand(p =>
        {
            if (p is not TrackedApp a || !a.HasRelease) return;
            if (a.IsSkipped) { a.SkippedVersion = null; Toast($"{a.Title}: {a.LatestVersion} is no longer skipped", "info"); }
            else { a.SkippedVersion = a.LatestVersion; Toast($"Skipped {a.LatestVersion} for {a.Title}. You'll be told about the next release.", "info"); }
            ScheduleSaveApps();
        });
        DetailsCommand = new RelayCommand(p =>
        {
            switch (p)
            {
                case RepoInfo r: _ = OpenDetailsAsync(r.OwnerLogin, r.Name, r, r.Source); break;
                case TrackedApp a: _ = OpenDetailsAsync(a.Owner, a.Repo, null, a.Source); break;
                case TryLaterItem t: _ = OpenDetailsAsync(t.Owner, t.Repo, null, t.Source); break;
                case AddPreviewVM v: _ = OpenDetailsAsync(v.Owner, v.RepoName, v.Info, v.Source); break;
            }
        });
        CloseDetailsCommand = new RelayCommand(() => RepoDetail = null);
        DetailAddCommand = new RelayCommand(() =>
        {
            var d = RepoDetail; if (d == null) return;
            RepoDetail = null;
            var existing = Find(d.Owner, d.Name);
            if (existing != null) { Navigate("Library", keepFilter: true); SelectedApp = existing; }
            else _ = OpenAddPreviewAsync(d.Owner, d.Name, d.Info, d.Source);
        });
        DetailTryLaterCommand = new RelayCommand(() =>
        {
            var d = RepoDetail; if (d == null) return;
            if (d.IsTryLater) { RemoveWatch(d.Owner, d.Name, toast: true); d.IsTryLater = false; }
            else { AddWatch(d.Owner, d.Name, d.Info, d.Source); d.IsTryLater = true; Toast($"Saved {d.Name} to Try later", "success"); }
        });
        OwnerCommand = new RelayCommand(p =>
        {
            if ((p is TrackedApp ta && ta.Source != "github") || (p is AddPreviewVM av && av.Source != "github") || (p is RepoInfo ri && ri.Source != "github") || (p is TryLaterItem tl && tl.Source != "github"))
            { Toast("Developer pages are only available for GitHub projects.", "info"); return; }
            var login = p switch { RepoInfo r => r.OwnerLogin, TrackedApp a => a.Owner, TryLaterItem t => t.Owner, AddPreviewVM v => v.Owner, string s => s, _ => null };
            if (!string.IsNullOrEmpty(login)) _ = ShowOwnerAsync(login, append: false);
        });
        RemoveTryLaterCommand = new RelayCommand(p => { if (p is TryLaterItem t) RemoveWatch(t.Owner, t.Repo, toast: true); });
        AddFromTryLaterCommand = new RelayCommand(p => { if (p is TryLaterItem t) _ = OpenAddPreviewAsync(t.Owner, t.Repo, null, t.Source); });
        ClearTryLaterNoteCommand = new RelayCommand(p => { if (p is TryLaterItem t) t.Note = null; });
        PreviewTryLaterCommand = new RelayCommand(() =>
        {
            var vm = AddPreview; if (vm == null) return;
            if (vm.IsTryLater) { RemoveWatch(vm.Owner, vm.RepoName, toast: false); vm.IsTryLater = false; Toast("Removed from your try-later list", "info"); }
            else { AddWatch(vm.Owner, vm.RepoName, vm.Info, vm.Source); AddPreview = null; Toast($"Saved {vm.Owner}/{vm.RepoName} to Try later", "success"); }
        });
        GitHubCommand = new RelayCommand(p => { if (p is TrackedApp a) Shell.Open(a.Url); else if (p is RepoInfo r) Shell.Open(r.HtmlUrl ?? RepoRef.UrlFor(r.Source, r.OwnerLogin, r.Name)); else if (p is TryLaterItem t) Shell.Open(t.Url); });
        ReleasesCommand = new RelayCommand(p => { if (p is TrackedApp a) Shell.Open(a.ReleaseUrl ?? a.Url + "/releases"); });
        PinCommand = new RelayCommand(p => { if (p is TrackedApp a) { a.Pinned = !a.Pinned; ScheduleSaveApps(); } });
        HideCommand = new RelayCommand(p =>
        {
            if (p is not TrackedApp a) return;
            a.Hidden = !a.Hidden;
            if (a.Hidden && SelectedApp == a) SelectedApp = null;
            ScheduleSaveApps();
            Toast(a.Hidden ? $"Hid {a.Title}. Find it under Library → Hidden." : $"{a.Title} is back in your library", "info");
        });
        ToggleAutoGroupsCommand = new RelayCommand(() => Settings.AutoGroups = !Settings.AutoGroups);
        NewGroupCommand = new RelayCommand(p => AskNewGroup(p as TrackedApp));
        RenameGroupCommand = new RelayCommand(p => { if (p is GroupEntry g) AskRenameGroup(g); });
        DeleteGroupCommand = new RelayCommand(p => { if (p is GroupEntry g) AskDeleteGroup(g); });
        SelectGroupCommand = new RelayCommand(p => { if (p is GroupEntry g) { GroupFilter = g.Name; if (CurrentPage != "Library") Navigate("Library", keepFilter: true); } });
        ClearGroupFilterCommand = new RelayCommand(() => GroupFilter = null);
        SetStatusFilterCommand = new RelayCommand(p => StatusFilter = p?.ToString() ?? "All");
        SetViewModeCommand = new RelayCommand(p => Settings.ViewMode = p?.ToString() ?? "Grid");
        ToggleGroupingCommand = new RelayCommand(() => Settings.GroupItems = !Settings.GroupItems);
        PresetCommand = new RelayCommand(async p => { if (p is Preset pr) await RunSearchAsync(pr.Query, pr.Title); });
        LoadMoreCommand = new RelayCommand(async () =>
        {
            if (ownerLogin != null) await ShowOwnerAsync(ownerLogin, append: true);
            else if (!HasSearched) await LoadMoreBrowseAsync();
            else await RunSearchAsync(SearchQuery, SearchTitle, append: true);
        });
        AddResultCommand = new RelayCommand(p =>
        {
            if (p is not RepoInfo r) return;
            if (RelinkTarget != null)
            {
                var target = RelinkTarget; RelinkTarget = null;
                _ = RelinkAsync(target, new RepoRef(r.Source, r.OwnerLogin, r.Name), r);
                Navigate("Library", keepFilter: true); SelectedApp = target;
                return;
            }
            if (r.IsTracked) { var ex = Find(r.OwnerLogin, r.Name); if (ex != null) SelectedApp = ex; }
            else _ = OpenAddPreviewAsync(r.OwnerLogin, r.Name, r, r.Source);
        });
        ConfirmAddCommand = new RelayCommand(async () => await ConfirmAddAsync());
        CancelAddCommand = new RelayCommand(() => AddPreview = null);
        PromptOkCommand = new RelayCommand(() =>
        {
            if (Prompt == null) return;
            var err = Prompt.OnOk?.Invoke(Prompt);
            if (err != null) Prompt.Error = err; else Prompt = null;
        });
        PromptCancelCommand = new RelayCommand(() => Prompt = null);
        BrowseImportCommand = new RelayCommand(BrowseImport);
        ImportTextCommand = new RelayCommand(async () => await RunImportAsync(ImportText));
        ExportJsonCommand = new RelayCommand(ExportJson);
        ExportLinksCommand = new RelayCommand(ExportLinks);
        CopyLinksCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, Apps.Select(a => a.Url))); Toast($"Copied {Apps.Count} links to the clipboard", "success"); } catch { }
        });
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        OpenFolderCommand = new RelayCommand(p =>
        {
            var which = p?.ToString();
            Shell.Reveal(which switch { "downloads" => Settings.DownloadFolder, "apps" => Settings.InstallRoot, _ => Paths.DataDir });
        });
        ResetSettingsCommand = new RelayCommand(AskResetSettings);
        ClearCacheCommand = new RelayCommand(() =>
        {
            foreach (var a in Apps) a.ETag = null;
            ScheduleSaveApps(); Toast("Cache cleared — the next check will fetch everything fresh", "success");
        });
        SetSectionCommand = new RelayCommand(p => SettingsSection = p?.ToString() ?? "General");
        OpenUrlCommand = new RelayCommand(p => Shell.Open(p?.ToString()));
        DismissToastCommand = new RelayCommand(p => { if (p is ToastItem t) Toasts.Remove(t); });
        SetAccentCommand = new RelayCommand(p => Settings.AccentHex = p?.ToString() ?? "#6C8CFF");
        OpenInstallDirCommand = new RelayCommand(p => { if (p is TrackedApp a && !string.IsNullOrEmpty(a.InstallPath)) Shell.Reveal(a.InstallPath); });
    }

    // ===================== navigation / omnibox =====================
    public void Navigate(string page, bool keepFilter = false)
    {
        if (page == "Library" && !keepFilter) GroupFilter = null;
        CurrentPage = page;
        SelectedApp = null;
        if (page == "Discover") Application.Current?.Dispatcher.BeginInvoke(new Action(EnsureBrowse), DispatcherPriority.Background);
    }

    void SubmitOmnibox()
    {
        var text = OmniboxText.Trim();
        if (text.Length == 0) return;
        if (SourceClient.TryParse(text, out var rr) && rr != null)
        {
            _ = OpenAddPreviewAsync(rr.Owner, rr.Repo, null, rr.Source, rr.FullPath);
            OmniboxText = "";
        }
        else
        {
            Navigate("Discover");
            _ = RunSearchAsync(text, $"Results for “{text}”");
        }
    }

    // ===================== view refresh =====================
    bool LibraryFilter(object o)
    {
        if (o is not TrackedApp a) return false;
        if (a.Hidden != (_status == "Hidden")) return false;     // hidden apps only show under the "Hidden" filter
        if (_groupFilter != null && a.DisplayGroup != _groupFilter) return false;
        if (_platformFilter != null && a.Platform != _platformFilter) return false;
        switch (_status)
        {
            case "Favorites": if (!a.Pinned) return false; break;
            case "Updates": if (!a.HasUpdate) return false; break;
            case "Installed": if (!a.IsInstalled) return false; break;
            case "NotInstalled": if (a.IsInstalled) return false; break;
        }
        var t = _filterText.Trim();
        if (t.Length > 0 &&
            !(a.Title.Contains(t, StringComparison.CurrentCultureIgnoreCase) || a.FullName.Contains(t, StringComparison.CurrentCultureIgnoreCase)
              || (a.Description?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false) || a.DisplayGroup.Contains(t, StringComparison.CurrentCultureIgnoreCase)
              || (a.Note?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false) || (a.Tags?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            return false;
        return true;
    }

    void ApplyViewConfig()
    {
        using (LibraryView.DeferRefresh())
        {
            LibraryView.GroupDescriptions.Clear();
            LibraryView.SortDescriptions.Clear();
            if (Settings.GroupItems)
            {
                LibraryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TrackedApp.DisplayGroup)));
                LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.DisplayGroup), ListSortDirection.Ascending));
            }
            LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.Pinned), ListSortDirection.Descending));
            switch (Settings.SortMode)
            {
                case "Recently updated": LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.LatestPublished), ListSortDirection.Descending)); break;
                case "Recently added": LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.AddedAt), ListSortDirection.Descending)); break;
                case "Status": LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.HasUpdate), ListSortDirection.Descending)); break;
            }
            LibraryView.SortDescriptions.Add(new SortDescription(nameof(TrackedApp.Title), ListSortDirection.Ascending));
        }
        RaiseStats();
    }

    void ScheduleRefresh() { refreshTimer.Stop(); refreshTimer.Start(); }

    public void RefreshViews()
    {
        if (_status == "Hidden" && HiddenCount == 0) { _status = "All"; Raise(nameof(StatusFilter)); }
        UpdatePlatforms();
        UpdateGroups();
        LibraryView.Refresh();
        UpdatesView.Refresh();
        RaiseStats();
    }

    void RaiseStats()
    {
        foreach (var n in new[] { nameof(TotalCount), nameof(HiddenCount), nameof(UpdateCount), nameof(FavoriteCount), nameof(InstalledCount), nameof(NotInstalledCount), nameof(ShownCount), nameof(IsLibraryEmpty),
                     nameof(NoLibraryMatches), nameof(NoUpdates), nameof(Subtitle), nameof(EmptyGroups), nameof(LastCheckText) }) Raise(n);
    }

    static string PrefSig(TrackedApp a) => string.Join("|", a.Group, a.Pinned, a.Hidden, a.IncludePrereleases, a.IgnoreUpdates, a.AutoUpdate, a.Mode, a.CustomArgs, a.DisplayName, a.Note, a.Tags, a.SkippedVersion, a.AssetPattern, a.TrustUnsigned);

    void AppChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackedApp.Progress) or nameof(TrackedApp.BusyText) or nameof(TrackedApp.IsIndeterminate)) return;
        if (s is TrackedApp changed)
        {
            // remember when the user last changed their own preferences for this app (library sync uses it)
            var sig = PrefSig(changed);
            if (changed.PrefSignature != null && changed.PrefSignature != sig) changed.ModifiedAt = DateTime.UtcNow;
            changed.PrefSignature = sig;
            if (!syncApplying) ScheduleSync();
        }
        ScheduleRefresh();
        if (s is TrackedApp a && !a.IsBusy) ScheduleSaveApps();
    }

    void UpdateGroups()
    {
        var counts = Apps.Where(a => !a.Hidden == (_status != "Hidden")).GroupBy(a => a.DisplayGroup).ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in Settings.CustomGroups) counts.TryAdd(c, 0);
        var desired = counts.Select(kv => (Name: kv.Key, Count: kv.Value, Custom: Settings.CustomGroups.Contains(kv.Key)))
                            .Where(x => Settings.AutoGroups || x.Custom || (x.Name == TrackedApp.UngroupedName && x.Count > 0))
                            .OrderByDescending(x => x.Custom).ThenBy(x => x.Name == TrackedApp.UngroupedName).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // update in place to keep the sidebar stable
        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            if (i < Groups.Count && Groups[i].Name == d.Name) { Groups[i].Count = d.Count; Groups[i].IsCustom = d.Custom; }
            else Groups.Insert(i, new GroupEntry { Name = d.Name, Count = d.Count, IsCustom = d.Custom, IsSelected = d.Name == _groupFilter });
        }
        while (Groups.Count > desired.Count) Groups.RemoveAt(Groups.Count - 1);
        foreach (var g in Groups) g.IsSelected = g.Name == _groupFilter;
        if (_groupFilter != null && !desired.Any(d => d.Name == _groupFilter)) { _groupFilter = null; Raise(nameof(GroupFilter)); Raise(nameof(HasGroupFilter)); Raise(nameof(NoGroupFilter)); }
        OptionsRefresh();
        bool plain = Settings.GroupItems && _status == "All" && _filterText.Trim().Length == 0;
        EmptyGroups = !plain ? new() : Groups.Where(g => g.IsCustom && g.Count == 0 && (_groupFilter == null || _groupFilter == g.Name)).Select(g => new GroupEntry { Name = g.Name, IsCustom = true }).ToList();
    }

    // ===================== persistence / settings =====================
    public void ScheduleSaveApps() { saveAppsTimer.Stop(); saveAppsTimer.Start(); }
    public void SaveAppsNow() { try { store.SaveApps(Apps); } catch (Exception ex) { AddLog("Couldn't save your library: " + ex.Message, "error"); } }
    void SaveSettingsNow() { try { store.SaveSettings(Settings); } catch (Exception ex) { AddLog("Couldn't save your settings: " + ex.Message, "error"); } }
    public void ScheduleSaveWatch() { saveWatchTimer.Stop(); saveWatchTimer.Start(); }
    void SaveWatchNow() { try { store.SaveWatch(TryLater); } catch (Exception ex) { AddLog("Couldn't save your try-later list: " + ex.Message, "error"); } }
    public void SaveAll() { saveAppsTimer.Stop(); saveSettingsTimer.Stop(); saveWatchTimer.Stop(); SaveAppsNow(); SaveSettingsNow(); SaveWatchNow(); }
    public void ScheduleSaveSettings() { saveSettingsTimer.Stop(); saveSettingsTimer.Start(); }

    public event Action<string>? SettingChanged;

    void SettingsChanged(object? s, PropertyChangedEventArgs e)
    {
        ScheduleSaveSettings();
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme): case nameof(AppSettings.AccentHex): case nameof(AppSettings.Density): ThemeManager.Apply(Settings); break;
            case nameof(AppSettings.ViewMode): break;
            case nameof(AppSettings.TrySort): case nameof(AppSettings.TryGroup): ApplyTryOrder(); break;
            case nameof(AppSettings.SortMode): case nameof(AppSettings.GroupItems): ApplyViewConfig(); break;
            case nameof(AppSettings.AutoGroups):
                // automatic groups appear/disappear immediately; the user's own groups are never touched
                foreach (var a in Apps) a.NotifyAll();
                RefreshViews();
                Toast(Settings.AutoGroups ? "Automatic groups are back on" : "Automatic groups are off — only your own groups are shown", "info");
                break;
            case nameof(AppSettings.CheckIntervalMinutes): ConfigureTimer(); if (Settings.BackgroundChecks) _ = Platform.ApplyBackgroundTaskAsync(Settings); break;
            case nameof(AppSettings.BackgroundChecks):
                _ = Platform.ApplyBackgroundTaskAsync(Settings);
                Toast(Settings.BackgroundChecks ? "ReleaseSync will check for updates in the background, even when it's closed" : "Background checks turned off", "info");
                break;
            case nameof(AppSettings.SyncFolder): case nameof(AppSettings.SyncMethod): case nameof(AppSettings.GistId): case nameof(AppSettings.SyncPassphrase):
                if (syncQuiet) break;
                ConfigureSync(); if (SyncReady) SyncNow(); break;
            case nameof(AppSettings.DeviceName): if (SyncReady) ScheduleSync(); break;
            case nameof(AppSettings.BackupFolder): case nameof(AppSettings.BackupIntervalHours): case nameof(AppSettings.BackupIncludeApps): case nameof(AppSettings.BackupKeep):
                Raise(nameof(BackupSummary)); Raise(nameof(CanBackUpNow)); lastBackupAttempt = DateTime.MinValue;
                if (e.PropertyName is nameof(AppSettings.BackupIntervalHours) or nameof(AppSettings.BackupFolder)) _ = Task.Delay(1500).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(new Action(async () => await RunScheduledBackupIfDueAsync())));
                break;
            case nameof(AppSettings.StartWithWindows): Shell.SetStartWithWindows(Settings.StartWithWindows); break;
            case nameof(AppSettings.PreferredArch): case nameof(AppSettings.PreferInstaller): ScheduleRefresh(); foreach (var a in Apps) a.NotifyAll(); break;
        }
        if (e.PropertyName != null) SettingChanged?.Invoke(e.PropertyName);
    }

    void ConfigureTimer()
    {
        checkTimer.Stop();
        if (Settings.CheckIntervalMinutes > 0)
        {
            checkTimer.Interval = TimeSpan.FromMinutes(Settings.CheckIntervalMinutes);
            checkTimer.Start();
        }
    }

    void AskResetSettings()
    {
        Prompt = new PromptVM
        {
            Title = "Reset all settings?",
            Message = "Appearance, update and installation preferences return to their defaults. Your library and token are kept.",
            OkText = "Reset settings", IsDanger = true,
            OnOk = _ =>
            {
                var d = new AppSettings();
                foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0))
                {
                    if (p.Name is nameof(AppSettings.ProtectedToken) or nameof(AppSettings.CustomGroups) or nameof(AppSettings.LastFullCheck) or nameof(AppSettings.WindowLeft) or nameof(AppSettings.WindowTop)
                        or nameof(AppSettings.Tombstones) or nameof(AppSettings.GroupStamps) or nameof(AppSettings.DeviceId) or nameof(AppSettings.DeviceName)) continue;
                    p.SetValue(Settings, p.GetValue(d));
                }
                Toast("Settings reset to defaults", "success");
                return null;
            }
        };
    }

    // ===================== rate limit =====================
    void UpdateRateText()
    {
        bool authed = Settings.HasToken;
        if (GitHub.RateRemaining < 0)
        {
            RateText = authed ? (GitHub.Login != null ? "@" + GitHub.Login : "Token saved") : "Not signed in";
            RateHint = authed ? "" : "Add a token for 5,000 requests/hour";
            RateLevel = "ok";
            return;
        }
        var who = authed && GitHub.Login != null ? "@" + GitHub.Login : (authed ? "Token" : "Anonymous");
        RateText = $"{who}  ·  {GitHub.RateRemaining:N0}/{GitHub.RateLimit:N0}";
        RateLevel = GitHub.RateRemaining == 0 ? "none" : GitHub.RateRemaining < Math.Max(5, GitHub.RateLimit / 10) ? "low" : "ok";
        RateHint = authed ? "Requests left this hour" : "Add a token for 5,000 requests/hour";
    }

    public async Task SaveTokenAsync(string token)
    {
        token = token.Trim();
        if (token.Length == 0) { Toast("Paste a token first", "warn"); return; }
        try
        {
            var login = await GitHub.ValidateTokenAsync(token);
            Settings.GitHubToken = token;
            await GitHub.RefreshStatusAsync();
            UpdateRateText();
            Toast($"Signed in as @{login}. Rate limit raised to 5,000/hour", "success");
            AddLog($"GitHub token saved for @{login}", "success");
            Raise(nameof(Settings));
        }
        catch (Exception ex) { Toast(ex.Message, "error"); }
    }

    public void ClearToken()
    {
        Settings.GitHubToken = "";
        GitHub.ClearLogin();
        _ = GitHub.RefreshStatusAsync();
        UpdateRateText();
        Toast("GitHub token removed", "info");
    }

    // ===================== toasts / log =====================
    public void Toast(string message, string kind = "info")
    {
        var t = new ToastItem { Message = message, Kind = kind };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == "error" ? 7 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    public void AddLog(string message, string kind = "info")
    {
        Log.Insert(0, new LogEntry { Message = message, Kind = kind });
        while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
    }

    // ===================== lookup / build =====================
    public TrackedApp? Find(string owner, string repo) =>
        Apps.FirstOrDefault(a => a.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) && a.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));

    static ReleaseInfo? SelectRelease(IEnumerable<ReleaseInfo> rels, bool pre) => rels.FirstOrDefault(r => !r.Draft && (pre || !r.Prerelease));

    static void ApplyRelease(TrackedApp app, List<ReleaseInfo> rels)
    {
        var rel = SelectRelease(rels, app.IncludePrereleases);
        if (rel == null)
        {
            app.LastError = rels.Count > 0 ? "No stable releases yet. Turn on pre-releases to track the newest build." : "No releases have been published for this repository.";
            return;
        }
        app.LatestVersion = rel.Tag;
        app.LatestName = rel.Name;
        app.LatestNotes = rel.Body;
        app.LatestPublished = rel.PublishedAt;
        app.LatestIsPrerelease = rel.Prerelease;
        app.ReleaseUrl = rel.HtmlUrl;
        app.Assets = ToAssets(rel);
        // keep recent history so older versions can be browsed and installed without another request
        var summaries = rels.Where(r => !r.Draft && !string.IsNullOrEmpty(r.Tag)).Take(15).Select(ToSummary).ToList();
        var latest = summaries.FirstOrDefault(s => s.Tag == rel.Tag);
        if (latest != null) latest.IsLatest = true;
        app.Releases = summaries;
        app.LastError = null;
    }

    static List<AssetInfo> ToAssets(ReleaseInfo rel) =>
        rel.Assets.Where(a => a.State != "uploading").Select(a => new AssetInfo { Name = a.Name, Url = a.Url, Size = a.Size, Downloads = a.Downloads, Digest = a.Digest }).ToList();

    public static ReleaseSummary ToSummary(ReleaseInfo r) => new()
    {
        Tag = r.Tag, Name = r.Name, Published = r.PublishedAt, Prerelease = r.Prerelease, Url = r.HtmlUrl, Assets = ToAssets(r),
        Notes = r.Body is { Length: > 3000 } b ? b[..3000] : r.Body,
    };

    static TrackedApp BuildApp(string owner, string repo, RepoInfo? info, List<ReleaseInfo>? rels, bool pre, string source = "github")
    {
        var app = new TrackedApp
        {
            Source = source,
            Owner = info?.OwnerLogin ?? owner,
            Repo = info?.Name is { Length: > 0 } n ? n : repo,
            Description = info?.Description,
            Language = info?.Language,
            Stars = info?.Stars ?? 0,
            Topics = info?.Topics ?? new(),
            AvatarUrl = !string.IsNullOrEmpty(info?.AvatarUrl) ? info!.AvatarUrl : source == "github" ? $"https://github.com/{owner}.png?size=96" : null,
            IncludePrereleases = pre,
            MetaFetched = info != null,
        };
        app.Category = Categorizer.Categorize(app.Repo, app.Description, app.Topics, app.Language);
        if (rels != null) ApplyRelease(app, rels);
        return app;
    }

    // ===================== try later =====================
    public TryLaterItem? FindWatch(string owner, string repo) =>
        TryLater.FirstOrDefault(t => t.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) && t.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));

    void AddWatch(string owner, string repo, RepoInfo? info, string source = "github")
    {
        if (FindWatch(owner, repo) != null) return;
        TryLater.Insert(0, new TryLaterItem
        {
            Owner = info?.OwnerLogin ?? owner, Repo = info?.Name is { Length: > 0 } n ? n : repo, Source = source,
            Description = info?.Description, Language = info?.Language, Stars = info?.Stars ?? 0, Topics = info?.Topics,
            AvatarUrl = !string.IsNullOrEmpty(info?.AvatarUrl) ? info!.AvatarUrl : source == "github" ? $"https://github.com/{owner}.png?size=96" : null,
        });
        MarkResultTryLater(owner, repo, true);
        AddLog($"Saved {owner}/{repo} to Try later", "info");
    }

    void RemoveWatch(string owner, string repo, bool toast)
    {
        var w = FindWatch(owner, repo);
        if (w == null) return;
        TryLater.Remove(w);
        MarkResultTryLater(owner, repo, false);
        if (toast) Toast($"Removed {w.Repo} from Try later", "info");
    }

    void ToggleTryLater(RepoInfo r)
    {
        if (r.IsTryLater) { RemoveWatch(r.OwnerLogin, r.Name, toast: true); r.IsTryLater = false; }
        else { AddWatch(r.OwnerLogin, r.Name, r, r.Source); r.IsTryLater = true; Toast($"Saved {r.Name} to Try later", "success"); }
    }

    void MarkResultTryLater(string owner, string repo, bool on)
    {
        foreach (var r in SearchResults)
            if (r.OwnerLogin.Equals(owner, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(repo, StringComparison.OrdinalIgnoreCase)) r.IsTryLater = on;
    }

    // ===================== add flow =====================
    public async Task OpenAddPreviewAsync(string owner, string repo, RepoInfo? known, string source = "github", string? fullPath = null)
    {
        var existing = Find(owner, repo);
        if (existing != null)
        {
            Toast($"{existing.Title} is already in your library", "info");
            Navigate("Library", keepFilter: true);
            SelectedApp = existing;
            return;
        }

        var vm = new AddPreviewVM { Source = source, Owner = owner, RepoName = repo, Groups = GroupOptions, IncludePre = Settings.DefaultIncludePrereleases, InstallNow = Settings.InstallAfterAdd, IsTryLater = FindWatch(owner, repo) != null };
        vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(AddPreviewVM.IncludePre) && vm.Info != null) await RebuildPreview(vm);
        };
        AddPreview = vm;
        try
        {
            if (source.StartsWith("unknown:"))
            {
                // an address ReleaseSync hasn't seen before: find out what kind of site it is
                var resolved = await Sources.ResolveAsync(new RepoRef(source, owner, repo, fullPath));
                source = resolved.Source; owner = resolved.Owner; repo = resolved.Repo;
                vm.Source = source; vm.Owner = owner; vm.RepoName = repo;
                var dup = Find(owner, repo);
                if (dup != null) { AddPreview = null; Toast($"{dup.Title} is already in your library", "info"); Navigate("Library", keepFilter: true); SelectedApp = dup; return; }
            }
            var info = known ?? await Sources.GetRepoAsync(source, owner, repo);
            vm.Info = info;
            var rr = await Sources.GetReleasesAsync(source, info.OwnerLogin, info.Name, null);
            vm.Releases = rr.Releases;
            vm.Owner = info.OwnerLogin; vm.RepoName = info.Name;
            vm.ETagHolder = rr.ETag;
            await RebuildPreview(vm);
            vm.Hints = await BuildHintsAsync(info, rr.Releases, source);
        }
        catch (Exception ex)
        {
            vm.Error = ex.Message;
            vm.IsLoading = false;
        }
    }

    async Task RebuildPreview(AddPreviewVM vm)
    {
        var group = vm.GroupKey;
        var draft = BuildApp(vm.Owner, vm.RepoName, vm.Info, vm.Releases, vm.IncludePre, vm.Source);
        draft.ETag = vm.ETagHolder;
        draft.GroupKey = group;
        vm.Draft = draft;
        vm.SelectedChoice = draft.AssetChoices[0];
        vm.Picker = new ReleasePickerVM(draft, showActions: false);
        vm.IsLoading = false;
        if (!vm.DetectionDone)
        {
            // before offering to install, check whether this PC already has it
            vm.DetectionDone = true;
            try
            {
                var (entry, _) = await Task.Run(() => UninstallService.Resolve(draft));
                if (entry != null)
                {
                    vm.Detected = entry;
                    vm.HaveVersion = entry.Version ?? "";
                    vm.ExePath = UninstallService.FindExe(entry);
                    vm.AlreadyHave = true;
                }
                else
                {
                    // not in Windows' installed programs — it may be a portable copy with a shortcut, or sitting in a folder
                    var found = await Task.Run(() => UninstallService.FindLocalProgram(draft));
                    if (found is { } f)
                    {
                        vm.ExePath = f.Exe;
                        vm.HaveVersion = f.Version ?? (ExeVersion(f.Exe) is { } ev && !ev.StartsWith("0.0.0") ? ev : "");
                        vm.FoundLocally = true;
                        vm.AlreadyHave = true;
                        vm.Picker?.PreferFlavor(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f.Exe)));
                    }
                }
            }
            catch { }
        }
    }

    async Task ConfirmAddAsync()
    {
        var vm = AddPreview;
        if (vm?.Draft == null) return;
        var app = vm.Draft;
        bool install = vm.InstallNow && vm.CanInstall && !vm.AlreadyHave;
        if (Find(app.Owner, app.Repo) != null) { AddPreview = null; return; }
        // a file chosen in the dropdown also becomes the preference for future updates
        if (vm.Picker?.SelectedAssetOrNull is { } chosenFile) app.AssetPattern = AssetPicker.MakePattern(chosenFile.Name);
        // a chosen install folder is remembered, so updates go to the same place
        if (!vm.AlreadyHave && vm.HasInstallDir) app.CustomInstallDir = vm.InstallDir;
        if (vm.AlreadyHave)
        {
            // track what's already on the PC: record its version so only genuinely newer releases show as updates
            var have = vm.HaveVersion.Trim();
            if (have.Length == 0 && !string.IsNullOrEmpty(vm.ExePath)) have = ExeVersion(vm.ExePath!) ?? "";
            if (app.HasRelease)
                app.InstalledVersion = have.Length == 0 || !VersionUtil.IsNewer(app.LatestVersion!, have) ? app.LatestVersion : have;
            else if (have.Length > 0) app.InstalledVersion = have;
            app.InstalledAt = DateTime.UtcNow;
            app.NotifiedVersion = app.LatestVersion;
            if (vm.Detected != null) app.UninstallKey = vm.Detected.Id;
            if (!string.IsNullOrEmpty(vm.ExePath)) app.InstallPath = vm.ExePath;
        }
        Apps.Add(app);
        MarkResultTracked(app.Owner, app.Repo, true);
        RemoveWatch(app.Owner, app.Repo, toast: false);
        AddPreview = null;
        AddLog($"Added {app.FullName}", "success");
        // a version picked in the dropdown is installed instead of the latest
        var target = vm.Picker?.Selected;
        if (install) { Toast($"Added {app.Title} — installing {(target ?? app.LatestRelease)?.Tag}…", "info"); await InstallAsync(app, target != null && target.Tag != app.LatestVersion ? target : null, assetOverride: vm.Picker?.SelectedAssetOrNull); }
        else if (vm.AlreadyHave) Toast(app.HasUpdate ? $"Added {app.Title} — you have {app.InstalledVersion}, {app.LatestVersion} is available" : $"Added {app.Title} — tracking your existing install", app.HasUpdate ? "info" : "success");
        else Toast($"Added {app.Title} to your library", "success");
    }

    static string? ExeVersion(string path)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var s = string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Split('+')[0].Trim();
            return s;
        }
        catch { return null; }
    }

    void LocateExisting()
    {
        var vm = AddPreview; if (vm == null) return;
        var dlg = new OpenFileDialog { Title = $"Find {vm.RepoName} on this PC", Filter = "Programs (*.exe)|*.exe|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        vm.ExePath = dlg.FileName;
        vm.AlreadyHave = true;
        if (string.IsNullOrWhiteSpace(vm.HaveVersion)) vm.HaveVersion = ExeVersion(dlg.FileName) ?? "";
    }

    void MarkResultTracked(string owner, string repo, bool tracked)
    {
        foreach (var r in SearchResults)
            if (r.OwnerLogin.Equals(owner, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(repo, StringComparison.OrdinalIgnoreCase)) r.IsTracked = tracked;
    }

    // ===================== repo details / owner =====================
    public void ResetDiscover()
    {
        searchCts?.Cancel();
        RelinkTarget = null;
        ownerLogin = null; OwnerProfile = null;
        HasSearched = false; SearchResults.Clear(); SearchError = null; SearchTitle = "Discover"; SearchTotal = 0; CanLoadMore = false; SearchBusy = false;
        _ = LoadBrowseAsync(false);
    }

    public async Task OpenDetailsAsync(string owner, string repo, RepoInfo? known, string source = "github")
    {
        var d = new RepoDetailVM { Source = source, Owner = owner, Name = repo, IsTracked = Find(owner, repo) != null, IsTryLater = FindWatch(owner, repo) != null };
        RepoDetail = d;
        try
        {
            if (known != null) { d.Info = known; d.IsLoading = false; }
            var infoTask = known != null ? Task.FromResult(known) : Sources.GetRepoAsync(source, owner, repo);
            var readmeTask = source == "github" ? GitHub.GetReadmeAsync(owner, repo) : Task.FromResult<string?>(null);
            var info = await infoTask;
            d.Owner = info.OwnerLogin; d.Name = info.Name;
            d.Info = info; d.IsLoading = false;
            _ = BuildHintsAsync(info, null, source).ContinueWith(t => { if (t.IsCompletedSuccessfully) Application.Current.Dispatcher.BeginInvoke(new Action(() => d.Hints = t.Result)); });
            try { d.Readme = Fmt.StripMarkdown(await readmeTask, 6000); }
            catch (Exception) { d.Readme = ""; }
        }
        catch (Exception ex) { d.Error = ex.Message; d.IsLoading = false; }
        finally { d.ReadmeLoading = false; }
    }

    /// <summary>Shows every public repository from one developer or organization, so related projects can be found and added.</summary>
    public async Task ShowOwnerAsync(string login, bool append)
    {
        RepoDetail = null; AddPreview = null; SelectedApp = null;
        if (!append)
        {
            Navigate("Discover");
            searchCts?.Cancel();
            ownerLogin = login; searchPage = 1; OwnerProfile = null; SearchResults.Clear(); SearchTotal = 0;
        }
        else searchPage++;
        var cts = searchCts = new CancellationTokenSource();
        SearchTitle = $"More from {login}"; SearchError = null; HasSearched = true; SearchBusy = true; CanLoadMore = false;
        try
        {
            if (!append)
            {
                try { OwnerProfile = await GitHub.GetUserAsync(login, cts.Token); SearchTitle = $"More from {OwnerProfile.DisplayName}"; SearchTotal = OwnerProfile.PublicRepos; }
                catch (GitHubException ex) when (!ex.RateLimited) { }
            }
            var items = await GitHub.GetOwnerReposAsync(login, searchPage, cts.Token);
            foreach (var r in items.Where(i => !i.Fork))
            {
                r.IsTracked = Find(r.OwnerLogin, r.Name) != null;
                r.IsTryLater = FindWatch(r.OwnerLogin, r.Name) != null;
                SearchResults.Add(r);
            }
            CanLoadMore = items.Count >= 30;
            if (SearchResults.Count == 0 && !CanLoadMore) SearchError = $"{login} has no public projects to show.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) SearchError = ex.Message; }
        finally { if (searchCts == cts) SearchBusy = false; }
    }

    // ===================== discover =====================
    public async Task RunSearchAsync(string query, string title, bool append = false)
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        if (!append) { ownerLogin = null; OwnerProfile = null; searchPage = 1; SearchResults.Clear(); SearchTotal = 0; }
        else searchPage++;
        SearchQuery = query; SearchTitle = title; SearchError = null; HasSearched = true; SearchBusy = true; CanLoadMore = false;
        try
        {
            var (items, total) = await GitHub.SearchAsync(query, searchPage, cts.Token);
            foreach (var r in items.Where(i => !i.Archived || true))
            {
                r.IsTracked = Find(r.OwnerLogin, r.Name) != null;
                r.IsTryLater = FindWatch(r.OwnerLogin, r.Name) != null;
                SearchResults.Add(r);
            }
            SearchTotal = total;
            CanLoadMore = SearchResults.Count < Math.Min(total, 240) && items.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) SearchError = ex.Message; }
        finally { if (searchCts == cts) SearchBusy = false; }
    }

    // ===================== checking =====================
    /// <returns>false when GitHub's rate limit stopped the check.</returns>
    public async Task<bool> CheckAppAsync(TrackedApp app, bool force)
    {
        if (app.IsBusy) return true;
        try
        {
            if (!app.MetaFetched && (app.Source != "github" || Settings.HasToken || GitHub.RateRemaining < 0 || GitHub.RateRemaining > 25))
            {
                try
                {
                    var info = await Sources.GetRepoAsync(app.Source, app.Owner, app.Repo);
                    app.Description = info.Description; app.Language = info.Language; app.Stars = info.Stars;
                    app.Topics = info.Topics ?? new();
                    if (!string.IsNullOrEmpty(info.AvatarUrl)) app.AvatarUrl = info.AvatarUrl;
                    app.Category = Categorizer.Categorize(app.Repo, app.Description, app.Topics, app.Language);
                    app.MetaFetched = true;
                }
                catch (GitHubException ex) when (ex.RateLimited) { throw; }
                catch (GitHubException) { app.MetaFetched = true; }
            }
            var etag = force || !app.HasRelease ? null : app.ETag;
            var res = await Sources.GetReleasesAsync(app.Source, app.Owner, app.Repo, etag);
            if (!res.NotModified) { app.ETag = res.ETag; ApplyRelease(app, res.Releases); }
            else if (app.Releases.Count == 0 && app.HasRelease) { var again = await Sources.GetReleasesAsync(app.Source, app.Owner, app.Repo, null); app.ETag = again.ETag; ApplyRelease(app, again.Releases); }   // older saved library: fetch the history once
            if (app == DrawerApp) DrawerPicker = new ReleasePickerVM(app, true);
            else app.LastError = null;
            app.LastChecked = DateTime.UtcNow;
            return true;
        }
        catch (GitHubException ex)
        {
            app.LastError = ex.Message;
            app.LastChecked = DateTime.UtcNow;
            return !ex.RateLimited;
        }
        catch (Exception ex) { app.LastError = ex.Message; return true; }
    }

    public async Task CheckAllAsync(bool manual)
    {
        if (IsChecking) return;
        if (Apps.Count == 0) { if (manual) Toast("Add something to your library first", "info"); return; }
        IsChecking = true;
        var list = Apps.Where(a => !a.IsBusy && !a.Hidden).ToList();
        int done = 0; bool limited = false;
        CheckProgressText = $"Checking 0/{list.Count}";
        var sem = new SemaphoreSlim(Math.Clamp(Settings.MaxConcurrentChecks, 1, 8));
        try
        {
            await Task.WhenAll(list.Select(async a =>
            {
                await sem.WaitAsync();
                try { if (!limited && !await CheckAppAsync(a, manual)) limited = true; }
                finally { sem.Release(); done++; CheckProgressText = $"Checking {done}/{list.Count}"; }
            }));
        }
        finally
        {
            IsChecking = false; CheckProgressText = "";
        }
        Settings.LastFullCheck = DateTime.Now; ScheduleSaveSettings();
        RefreshViews(); ScheduleSaveApps();

        var withUpdates = Apps.Where(a => a.HasUpdate && !a.Hidden).ToList();
        var fresh = withUpdates.Where(a => a.NotifiedVersion != a.LatestVersion).ToList();
        if (limited) { Toast(Apps.FirstOrDefault(a => a.HasError)?.LastError ?? "GitHub rate limit reached.", "warn"); AddLog("Update check stopped: GitHub rate limit reached", "warn"); }
        else if (manual) Toast(withUpdates.Count == 0 ? "Everything is up to date" : $"{withUpdates.Count} update{(withUpdates.Count == 1 ? "" : "s")} available", withUpdates.Count == 0 ? "success" : "info");
        AddLog($"Checked {list.Count} apps · {withUpdates.Count} update{(withUpdates.Count == 1 ? "" : "s")} available", "info");

        if (fresh.Count > 0)
        {
            foreach (var a in fresh) a.NotifiedVersion = a.LatestVersion;
            if (Settings.NotifyOnUpdates && !Settings.AutoInstallUpdates)
            {
                var names = string.Join(", ", fresh.Take(4).Select(a => a.Title)) + (fresh.Count > 4 ? $" and {fresh.Count - 4} more" : "");
                Notify(fresh.Count == 1 ? "Update available" : $"{fresh.Count} updates available", names, ("Update all", "releasesync://update-all"), ("View", "releasesync://updates"));
            }
        }
        var auto = Apps.Where(a => a.HasUpdate && !a.Hidden && (Settings.AutoInstallUpdates || a.AutoUpdate) && a.CanInstall).ToList();
        if (auto.Count > 0)
        {
            AddLog($"Auto-updating {auto.Count} app(s)", "info");
            await Task.WhenAll(auto.Select(a => InstallAsync(a, null, null, unattended: true)));
            if (Settings.NotifyOnUpdates) Notify("Apps updated", string.Join(", ", auto.Take(5).Select(a => a.Title)));
        }
    }

    // ===================== installing =====================
    public async Task UpdateAllAsync()
    {
        var todo = Apps.Where(a => a.HasUpdate && a.CanInstall && !a.Hidden).ToList();
        if (todo.Count == 0) { Toast("No updates to install", "info"); return; }
        IsUpdatingAll = true;
        try { await Task.WhenAll(todo.Select(a => InstallAsync(a))); }
        finally { IsUpdatingAll = false; }
        var failed = todo.Count(a => a.HasUpdate);
        Toast(failed == 0 ? $"Updated {todo.Count} app{(todo.Count == 1 ? "" : "s")}" : $"{todo.Count - failed} updated, {failed} need attention", failed == 0 ? "success" : "warn");
    }

    // ---- prompts that can be awaited (one at a time) ----
    readonly SemaphoreSlim promptLock = new(1, 1);

    /// <summary>Shows a dialog and waits for the answer. True = confirmed.</summary>
    public async Task<bool> ShowPromptAsync(PromptVM p)
    {
        await promptLock.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                p.OnOk = _ => { tcs.TrySetResult(true); return null; };
                p.OnClose = () => tcs.TrySetResult(false);
                Prompt = p;
            });
            return await tcs.Task;
        }
        finally { promptLock.Release(); }
    }

    async Task<bool> ConfirmUnsignedAsync(TrackedApp app, VerifyResult v)
    {
        bool onlyUnsigned = v.UnsignedConcern && !v.VtFlagged && !v.ProvenanceMismatch;
        var p = new PromptVM
        {
            Title = v.VtFlagged ? "Some antivirus engines flag this download" : "Check this download before installing",
            Message = $"{app.Title}'s download needs your decision:\n\n• " + string.Join("\n• ", v.Findings) + "\n\nOnly continue if you trust where it came from.",
            OkText = "Install anyway", IsDanger = true,
            ShowCheck = onlyUnsigned, CheckText = $"Don't ask again for {app.Title}", Checked = false,
        };
        var ok = await ShowPromptAsync(p);
        if (ok && onlyUnsigned && p.Checked) app.TrustUnsigned = true;
        return ok;
    }

    /// <param name="target">A specific release to install (older versions for a rollback); null means the latest.</param>
    /// <param name="assetOverride">A specific file from that release; null lets ReleaseSync choose.</param>
    /// <param name="unattended">True for automatic updates: nothing may ask the user anything.</param>
    public async Task InstallAsync(TrackedApp app, ReleaseSummary? target = null, AssetInfo? assetOverride = null, bool unattended = false)
    {
        if (app.IsBusy) return;
        var release = target ?? app.LatestRelease;
        var asset = assetOverride ?? (release != null ? AssetPicker.Pick(release.Assets, app.AssetPattern) : null);
        if (release == null || asset == null)
        {
            if (!unattended) { Toast($"No Windows download found for {app.Title}. Pick one manually in its details.", "warn"); SelectedApp = app; }
            return;
        }
        var wasUpdate = app.IsInstalled;
        bool downgrade = wasUpdate && !string.IsNullOrEmpty(app.InstalledVersion) && VersionUtil.IsNewer(app.InstalledVersion!, release.Tag);
        var cts = new CancellationTokenSource();
        app.Cts = cts;
        app.Progress = 0; app.BusyText = "Waiting in queue…"; app.IsBusy = true;
        var progress = new Progress<InstallProgress>(p => { app.Progress = p.Percent; app.BusyText = p.Text; });
        await installLock.WaitAsync();
        bool relaunch = false; string? relaunchExe = null;
        try
        {
            if (cts.IsCancellationRequested) throw new OperationCanceledException();

            // a link ReleaseSync only guessed from the program's name must be confirmed before anything gets replaced
            if (!app.LinkVerified && wasUpdate)
            {
                if (unattended) throw new InvalidOperationException($"{app.Title} wasn't updated automatically: ReleaseSync still needs you to confirm it follows the right project ({app.Url}).");
                app.BusyText = "Waiting for your answer…";
                if (!await ShowPromptAsync(new PromptVM
                {
                    Title = $"Is {app.FullName} the right project?",
                    Message = $"ReleaseSync matched “{app.Title}” to {app.Url} by its name only. If that's a different project, installing its update could replace your program with something else entirely.\n\nOpen the page to check, then continue only if it's the right one. If it's wrong, cancel and use “Change repository”.",
                    OkText = "Yes, it's the right one",
                })) { Shell.Open(app.Url); throw new OperationCanceledException(); }
                app.LinkVerified = true;
            }

            if (downgrade && !unattended)
            {
                app.BusyText = "Waiting for your answer…";
                if (!await ShowPromptAsync(new PromptVM
                {
                    Title = $"Go back to {release.Tag}?", OkText = "Install the older version",
                    Message = $"You have {app.InstalledVersion}. ReleaseSync will install {release.Tag} over it. Some programs refuse to install over a newer version — if that happens, uninstall it first.",
                })) throw new OperationCanceledException();
            }

            // the app can't be replaced while it's running
            var running = RunningApps.Find(app);
            if (running.Count > 0)
            {
                if (unattended) throw new InvalidOperationException($"{app.Title} is running, so it wasn't updated. It will be updated after you close it.");
                try { relaunchExe = running[0].MainModule?.FileName; } catch { }
                relaunchExe ??= app.InstallPath;
                app.BusyText = "Waiting for your answer…";
                var rp = new PromptVM
                {
                    Title = $"{app.Title} is running", OkText = "Close it and update", ShowCheck = true, CheckText = "Open it again afterwards", Checked = true,
                    Message = "It has to be closed to be updated. Save anything you're working on first — ReleaseSync asks it to close politely, and only forces it if it doesn't.",
                };
                if (!await ShowPromptAsync(rp)) throw new OperationCanceledException();
                relaunch = rp.Checked;
                app.BusyText = "Closing the app…";
                if (!await RunningApps.CloseAsync(running, cts.Token)) throw new InvalidOperationException($"Couldn't close {app.Title}. Close it yourself and try again.");
            }

            // where an installer-based app lives matters: a NAS may be offline, or Windows may still list a folder that no longer exists
            string? installDir = app.HasCustomInstallDir ? app.CustomInstallDir : null;      // a folder the user chose always wins
            if (!string.IsNullOrEmpty(app.UninstallKey) && wasUpdate && installDir == null)
            {
                var entry = UninstallService.Get(app.UninstallKey);
                var loc = entry?.InstallLocation?.Trim().Trim('"').TrimEnd('\\');
                if (string.IsNullOrEmpty(loc) && entry?.UninstallString != null)
                {
                    var exe = entry.UninstallString.Trim().Trim('"'); var cut = exe.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (cut > 0) loc = System.IO.Path.GetDirectoryName(exe[..(cut + 4)]);
                }
                if (!string.IsNullOrEmpty(loc))
                {
                    var root = System.IO.Path.GetPathRoot(loc);
                    bool rootUp = string.IsNullOrEmpty(root) || System.IO.Directory.Exists(root);
                    bool folderUp = rootUp && System.IO.Directory.Exists(loc);
                    bool onNas = Platform.IsNetworkPath(loc);
                    if (!rootUp)
                    {
                        if (unattended) throw new InvalidOperationException($"{app.Title} wasn't updated: {loc} isn't reachable right now (is your NAS on?).");
                        app.BusyText = "Waiting for your answer…";
                        if (!await ShowPromptAsync(new PromptVM
                        {
                            Title = "Can't reach where it's installed",
                            Message = $"{app.Title} is installed in {loc}, but that location isn't available — it looks like a network drive that's offline. Turn on your NAS or reconnect the drive, then try again.",
                            OkText = "I've reconnected it — continue",
                        })) throw new OperationCanceledException();
                        if (!System.IO.Directory.Exists(root)) throw new InvalidOperationException($"{loc} still isn't reachable. Reconnect the drive and try again.");
                        folderUp = System.IO.Directory.Exists(loc);
                    }
                    if (!folderUp)
                    {
                        // Windows still lists it, but its folder is gone: install it again into a folder the user picks
                        if (unattended) throw new InvalidOperationException($"{app.Title} wasn't updated: the folder it was installed in ({loc}) no longer exists. Open ReleaseSync to choose where to install it.");
                        app.BusyText = "Waiting for your answer…";
                        var staleKeyIsMine = app.UninstallKey!.StartsWith("CurrentUser", StringComparison.Ordinal);
                        var stale = new PromptVM
                        {
                            Title = "Its install folder is missing",
                            Message = $"Windows says {app.Title} is installed in {loc}, but that folder doesn't exist any more (it may have been moved or deleted). ReleaseSync can install the new version into a folder you choose instead.",
                            OkText = "Choose a folder…",
                            ShowCheck = staleKeyIsMine, CheckText = "Also clear the outdated entry Windows keeps for it (recommended)", Checked = true,
                        };
                        if (!await ShowPromptAsync(stale)) throw new OperationCanceledException();
                        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = $"Choose where to install {app.Title}", UseDescriptionForTitle = true, SelectedPath = System.IO.Directory.Exists(root) ? root! : "" };
                        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) throw new OperationCanceledException();
                        installDir = dlg.SelectedPath;
                        if (staleKeyIsMine && stale.Checked && UninstallService.DeleteUserEntry(app.UninstallKey!)) { AddLog($"Cleared the outdated Windows entry for {app.Title}", "info"); app.UninstallKey = null; app.InstallPath = null; }
                    }
                    else if (onNas) installDir = loc;       // reinstall onto the NAS, not into a default folder on this PC
                }
            }

            AddLog($"{(downgrade ? "Rolling back" : wasUpdate ? "Updating" : "Installing")} {app.FullName} {release.Tag} ({asset.Name})", "info");
            await Installer.InstallAsync(app, release, asset, progress, cts.Token, v => ConfirmUnsignedAsync(app, v), unattended, installDir);
            app.InstalledVersion = release.Tag;
            app.InstalledAt = DateTime.UtcNow;
            app.NotifiedVersion = app.LatestVersion;
            app.LastError = null;
            if (!string.IsNullOrEmpty(app.VerifyNote)) AddLog($"{app.Title}: {app.VerifyNote}", app.VerifyNote.Contains("not digitally signed") ? "warn" : "success");
            AddLog($"{app.Title} is now at {release.Tag}", "success");
            Toast($"{app.Title} {(downgrade ? "rolled back to" : wasUpdate ? "updated to" : "installed:")} {release.Tag}" + (app.VerifyNote != null && app.VerifyNote.Contains("checksum verified") ? "  ·  verified" : ""), "success");
            if (relaunch && !string.IsNullOrEmpty(relaunchExe) && System.IO.File.Exists(relaunchExe)) Shell.Open(relaunchExe);
        }
        catch (OperationCanceledException) { AddLog($"Cancelled {app.Title}", "warn"); if (!unattended) Toast($"Cancelled {app.Title}", "info"); }
        catch (Exception ex)
        {
            var msg = ex is HttpRequestException ? "The download failed. Check your connection and try again." : ex.Message;
            app.LastError = msg;
            AddLog($"{app.Title}: {msg}", "error");
            if (!unattended || !msg.Contains("is running")) Toast($"{app.Title}: {msg}", "error");
        }
        finally
        {
            installLock.Release();
            app.Cts = null; cts.Dispose();
            app.IsBusy = false; app.Progress = 0; app.BusyText = "";
            app.NotifyAll();
            if (app == DrawerApp) DrawerPicker?.Refresh();
            ScheduleSaveApps(); ScheduleRefresh();
        }
    }

    /// <summary>Downloads one file from any release into the download folder and shows it in Explorer.</summary>
    async Task DownloadPickedAsync(ReleasePickerVM pk)
    {
        var app = pk.App; var rel = pk.Selected;
        if (rel == null || app.IsBusy) return;
        var asset = pk.SelectedAssetOrNull ?? AssetPicker.Pick(rel.Assets, app.AssetPattern) ?? rel.Assets.FirstOrDefault();
        if (asset == null) { Toast($"{rel.Tag} has no downloadable files.", "warn"); return; }
        var cts = new CancellationTokenSource(); app.Cts = cts;
        app.Progress = 0; app.BusyText = "Starting download…"; app.IsBusy = true;
        var progress = new Progress<InstallProgress>(p => { app.Progress = p.Percent; app.BusyText = p.Text; });
        try
        {
            var path = await Installer.DownloadOnlyAsync(app, rel, asset, progress, cts.Token);
            AddLog($"Downloaded {asset.Name} ({app.FullName} {rel.Tag})", "success");
            Toast($"Downloaded {asset.Name}", "success");
            Shell.Reveal(path);
        }
        catch (OperationCanceledException) { Toast("Download cancelled", "info"); }
        catch (Exception ex) { Toast("The download failed: " + ex.Message, "error"); }
        finally { app.Cts = null; cts.Dispose(); app.IsBusy = false; app.Progress = 0; app.BusyText = ""; app.NotifyAll(); }
    }

    // ===================== remove =====================
    /// <summary>Unpacked by ReleaseSync (no Windows uninstaller), either into its apps folder or into a folder the user chose for it.</summary>
    bool IsPortableInstall(TrackedApp app) =>
        string.IsNullOrEmpty(app.UninstallKey) && !string.IsNullOrEmpty(app.InstallPath)
        && (app.InstallPath!.StartsWith(Settings.InstallRoot, StringComparison.OrdinalIgnoreCase)
            || (app.HasCustomInstallDir && app.InstallPath.StartsWith(app.CustomInstallDir!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));

    /// <summary>In ReleaseSync's own folder the whole folder can go; in a folder the user chose, only when the list of files the release installed is known.</summary>
    bool CanDeleteFiles(TrackedApp app) => IsPortableInstall(app) && (!app.HasCustomInstallDir || InstallService.OwnedFiles(app) != null);

    void RemoveFromLibrary(TrackedApp app, bool deleteFiles)
    {
        if (deleteFiles) { InstallService.RemoveFiles(app); InstallService.ClearDownloads(app); }
        Settings.Tombstones[app.Key] = DateTime.UtcNow;          // so a synced PC doesn't bring it back
        ScheduleSaveSettings();
        Apps.Remove(app);
        if (SelectedApp == app) SelectedApp = null;
        MarkResultTracked(app.Owner, app.Repo, false);
        AddLog($"Stopped tracking {app.FullName}", "info");
        Toast($"Stopped tracking {app.Title}", "info");
    }

    void AskRemove(TrackedApp app)
    {
        bool hasFiles = CanDeleteFiles(app);
        var (entry, exact) = app.IsInstalled && !IsPortableInstall(app) ? UninstallService.Resolve(app) : (null, false);
        bool canUninstall = entry != null;
        if (!Settings.ConfirmRemove && !hasFiles && !canUninstall) { RemoveFromLibrary(app, false); return; }

        string message = app.IsInstalled || hasFiles
            ? "ReleaseSync will stop tracking it and stop checking for updates. The app itself stays on your PC, untouched."
            : "ReleaseSync will stop tracking it.";
        if (canUninstall && !exact) message += $" (If you tick the box below, ReleaseSync would uninstall “{entry!.DisplayName}” — check that's the right program first.)";

        Prompt = new PromptVM
        {
            Title = $"Stop tracking {app.Title}?", Message = message,
            OkText = "Stop tracking", IsDanger = false,
            ShowCheck = hasFiles || canUninstall,
            CheckText = hasFiles ? (app.HasCustomInstallDir ? "Also delete the files ReleaseSync installed (other files in that folder stay)" : "Also delete the files ReleaseSync installed") : $"Also uninstall “{entry?.DisplayName}” from this PC",
            Checked = false,
            OnOk = p =>
            {
                if (canUninstall && p.Checked) _ = RunUninstallAsync(app, entry!, thenRemove: true);
                else RemoveFromLibrary(app, hasFiles && p.Checked);
                return null;
            }
        };
    }

    // ===================== uninstall =====================
    void AskUninstall(TrackedApp app)
    {
        if (!app.IsInstalled && string.IsNullOrEmpty(app.InstallPath) && string.IsNullOrEmpty(app.UninstallKey)) { Toast($"{app.Title} isn't installed", "info"); return; }
        if (IsPortableInstall(app))
        {
            bool inOwnFolder = !app.HasCustomInstallDir;
            bool known = inOwnFolder || InstallService.OwnedFiles(app) != null;
            // installed into a folder of the user's choosing before the list of files was kept: the release it came from can be fetched again to read the list
            var again = known ? null : app.Releases.FirstOrDefault(r => r.Tag == app.InstalledVersion)?.Assets.FirstOrDefault(a => a.Name == app.InstalledAssetName && a.Kind == AssetKind.Zip);
            if (again != null)
            {
                var tag = app.InstalledVersion!;
                Prompt = new PromptVM
                {
                    Title = $"Uninstall {app.Title}?",
                    Message = $"{app.Title} is in {app.CustomInstallDir}, a folder you chose, and that folder may hold your own files. To delete only what ReleaseSync installed, it needs to download {again.Name} ({again.SizeText}) again to read which files belong to it. Everything else in the folder stays. The app stays in your library so you can reinstall it later.",
                    OkText = "Download and uninstall", IsDanger = true,
                    OnOk = pp => { _ = UninstallAfterLearningFilesAsync(app, tag, again); return null; }
                };
                return;
            }
            Prompt = new PromptVM
            {
                Title = known ? $"Uninstall {app.Title}?" : $"Mark {app.Title} as uninstalled?",
                Message = inOwnFolder ? "This deletes the unpacked app files and its Start menu shortcut. It stays in your library so you can reinstall it later."
                    : known ? $"This deletes only the files ReleaseSync installed in {app.CustomInstallDir} and its Start menu shortcut. Anything else in that folder stays. The app stays in your library so you can reinstall it later."
                    : $"ReleaseSync doesn't have a list of the files it put in {app.CustomInstallDir}, and that folder may hold your own files, so it won't delete anything. This only marks the app as not installed; you can delete its files yourself.",
                OkText = known ? "Uninstall" : "Mark as uninstalled", IsDanger = known,
                OnOk = _ => { FinishPortableUninstall(app, known ? InstallService.RemoveFiles(app) : null, known, inOwnFolder); return null; }
            };
            return;
        }
        var (entry, exact) = UninstallService.Resolve(app);
        if (entry == null)
        {
            Toast($"Couldn't find {app.Title} in Windows' installed programs. Use Settings → Apps → Installed apps to remove it.", "warn");
            return;
        }
        Prompt = new PromptVM
        {
            Title = $"Uninstall {entry.DisplayName}?",
            Message = (exact ? "ReleaseSync will run the program's own uninstaller." : "ReleaseSync found this in Windows' installed programs and will run its uninstaller. Make sure it's the right program.")
                      + " It stays in your library so you can reinstall it later.",
            OkText = "Uninstall", IsDanger = true,
            OnOk = pp => { _ = RunUninstallAsync(app, entry, thenRemove: false); return null; }
        };
    }

    void FinishPortableUninstall(TrackedApp app, InstallManifest.RemoveResult? removed, bool known, bool inOwnFolder)
    {
        app.InstalledVersion = null; app.InstallPath = null; app.NotifiedVersion = null;
        if (known) InstallService.ClearDownloads(app);
        if (!known) { AddLog($"Marked {app.Title} as uninstalled (files left in place)", "info"); Toast($"{app.Title} is marked as uninstalled. Its files were left in place.", "info"); }
        else if (removed is { LeftBehind: > 0 } r && !inOwnFolder) { AddLog($"Uninstalled {app.Title} ({r.Removed} files removed, {r.LeftBehind} other files left)", "success"); Toast($"{app.Title} was uninstalled. {r.LeftBehind} other file{(r.LeftBehind == 1 ? "" : "s")} in that folder stayed.", "success"); }
        else { AddLog($"Uninstalled {app.Title}", "success"); Toast($"{app.Title} was uninstalled", "success"); }
        if (removed is { Failed: > 0 } f) Toast($"{f.Failed} file{(f.Failed == 1 ? "" : "s")} couldn't be deleted. Close the app and try again.", "warn");
        app.NotifyAll(); ScheduleSaveApps(); ScheduleRefresh();
    }

    async Task UninstallAfterLearningFilesAsync(TrackedApp app, string tag, AssetInfo asset)
    {
        if (app.IsBusy) return;
        app.Progress = 0; app.BusyText = "Checking which files it installed…"; app.IsBusy = true;
        AddLog($"Reading the file list of {asset.Name} to uninstall {app.Title}", "info");
        try
        {
            var zip = await Installer.DownloadAsync(app, tag, asset, new Progress<InstallProgress>(p => { app.Progress = p.Percent; app.BusyText = p.Text; }), CancellationToken.None);
            try
            {
                var files = await Task.Run(() => InstallManifest.ListZip(zip));
                InstallManifest.Save(app.Id, InstallService.PortableTarget(app), files);
                FinishPortableUninstall(app, InstallService.RemoveFiles(app), true, false);
            }
            finally
            {
                // the zip was only fetched to read its file list; don't leave a copy behind (unless the user keeps downloads on purpose)
                if (!Settings.KeepDownloads)
                    try { File.Delete(zip); var d = Path.GetDirectoryName(zip)!; if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        catch (Exception ex) { AddLog($"{app.Title}: {ex.Message}", "error"); Toast("Couldn't read the file list, so nothing was deleted: " + ex.Message, "error"); }
        finally { app.IsBusy = false; app.BusyText = ""; app.NotifyAll(); }
    }

    async Task RunUninstallAsync(TrackedApp app, UninstallEntry entry, bool thenRemove)
    {
        if (app.IsBusy) return;
        app.Progress = 0; app.BusyText = "Running the uninstaller…"; app.IsBusy = true;
        AddLog($"Uninstalling {app.Title} ({entry.DisplayName})", "info");
        try
        {
            var (ok, msg) = await UninstallService.RunAsync(entry, Settings.SilentInstall && entry.CanSilent);
            if (ok)
            {
                app.InstalledVersion = null; app.InstallPath = null; app.UninstallKey = null; app.NotifiedVersion = null;
                InstallService.ClearDownloads(app);
                AddLog($"Uninstalled {app.Title}", "success");
                if (thenRemove) RemoveFromLibrary(app, false);
                else Toast($"{app.Title} was uninstalled", "success");
            }
            else { AddLog($"{app.Title}: {msg}", "warn"); Toast(msg, "warn"); }
        }
        catch (Exception ex) { AddLog($"{app.Title}: {ex.Message}", "error"); Toast(ex.Message, "error"); }
        finally { app.IsBusy = false; app.BusyText = ""; app.NotifyAll(); ScheduleSaveApps(); ScheduleRefresh(); }
    }

    // ===================== groups =====================
    bool GroupNameExists(string name) =>
        Groups.Any(g => g.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) || Categorizer.AllCategories.Any(c => c.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    void AskNewGroup(TrackedApp? assign)
    {
        Prompt = new PromptVM
        {
            Title = "New group", Message = assign == null ? "Groups let you organize your library your own way. Drag apps onto a group in the sidebar to fill it." : $"Create a group and move {assign.Title} into it.",
            ShowInput = true, Placeholder = "e.g. Work tools", OkText = "Create group",
            OnOk = p =>
            {
                var name = p.Text.Trim();
                if (name.Length == 0) return "Give the group a name.";
                if (GroupNameExists(name)) return "A group with that name already exists.";
                Settings.CustomGroups.Add(name); GroupAdded(name); ScheduleSaveSettings();
                if (assign != null) assign.Group = name;
                RefreshViews();
                Toast($"Created group “{name}”", "success");
                return null;
            }
        };
    }

    void AskRenameGroup(GroupEntry g)
    {
        if (!g.IsCustom) return;
        Prompt = new PromptVM
        {
            Title = "Rename group", ShowInput = true, Text = g.Name, OkText = "Rename",
            OnOk = p =>
            {
                var name = p.Text.Trim();
                if (name.Length == 0) return "Give the group a name.";
                if (!name.Equals(g.Name, StringComparison.CurrentCultureIgnoreCase) && GroupNameExists(name)) return "A group with that name already exists.";
                var old = g.Name;
                int i = Settings.CustomGroups.IndexOf(old);
                if (i >= 0) Settings.CustomGroups[i] = name;
                if (!name.Equals(old, StringComparison.OrdinalIgnoreCase)) { GroupRemoved(old); GroupAdded(name); }
                foreach (var a in Apps.Where(a => a.Group == old)) a.Group = name;
                if (GroupFilter == old) GroupFilter = name;
                ScheduleSaveSettings(); RefreshViews();
                return null;
            }
        };
    }

    void AskDeleteGroup(GroupEntry g)
    {
        if (!g.IsCustom) return;
        Prompt = new PromptVM
        {
            Title = $"Delete “{g.Name}”?", Message = g.Count > 0 ? $"The {g.Count} app{(g.Count == 1 ? "" : "s")} inside go back to their automatic groups. Nothing is uninstalled." : "This group is empty.",
            OkText = "Delete group", IsDanger = true,
            OnOk = _ =>
            {
                Settings.CustomGroups.Remove(g.Name); GroupRemoved(g.Name);
                foreach (var a in Apps.Where(a => a.Group == g.Name)) a.Group = null;
                if (GroupFilter == g.Name) GroupFilter = null;
                ScheduleSaveSettings(); RefreshViews();
                return null;
            }
        };
    }

    public void MoveToGroup(TrackedApp app, string groupName)
    {
        bool auto = string.IsNullOrEmpty(groupName) || groupName == TrackedApp.UngroupedName || (Settings.AutoGroups && groupName == app.Category);
        app.Group = auto ? null : groupName;
        RefreshViews(); ScheduleSaveApps();
        Toast(auto ? $"{app.Title} is back in {app.DisplayGroup}" : $"Moved {app.Title} to {groupName}", "success");
    }

    // ===================== import / export =====================
    void BrowseImport()
    {
        var dlg = new OpenFileDialog { Title = "Import apps", Filter = "ReleaseSync backups and link lists|*.json;*.txt;*.csv;*.md|All files|*.*" };
        if (dlg.ShowDialog() == true) _ = ImportFileAsync(dlg.FileName);
    }

    public async Task ImportFileAsync(string path)
    {
        try { await RunImportAsync(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) { ImportStatus = "Couldn't read that file: " + ex.Message; }
    }

    public async Task RunImportAsync(string text)
    {
        if (IsImporting) return;
        if (string.IsNullOrWhiteSpace(text)) { ImportStatus = "Paste some GitHub links or drop a file first."; return; }
        var parsed = Importer.Parse(text);
        var fresh = parsed.Entries.Where(e => Find(e.Owner, e.Repo) == null).ToList();
        int already = parsed.Entries.Count - fresh.Count;
        if (fresh.Count == 0)
        {
            ImportStatus = parsed.Entries.Count == 0 ? "No GitHub links were found in that." : $"All {already} repositories are already in your library.";
            return;
        }

        IsImporting = true;
        bool metadata = Settings.HasToken || fresh.Count <= 25;
        int ok = 0, failed = 0; var added = new List<TrackedApp>(); string? stopReason = null;
        try
        {
            for (int i = 0; i < fresh.Count; i++)
            {
                var e = fresh[i];
                ImportStatus = $"Importing {i + 1} of {fresh.Count}  ·  {e.Owner}/{e.Repo}";
                try
                {
                    RepoInfo? info = metadata ? await GitHub.GetRepoAsync(e.Owner, e.Repo) : null;
                    var owner = info?.OwnerLogin ?? e.Owner; var repo = info?.Name ?? e.Repo;
                    var rr = await GitHub.GetReleasesAsync(owner, repo, null);
                    var app = BuildApp(owner, repo, info, rr.Releases, e.Prereleases || Settings.DefaultIncludePrereleases);
                    app.ETag = rr.ETag;
                    app.LastChecked = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(e.Name) && !e.Name!.Equals(app.Repo, StringComparison.OrdinalIgnoreCase)) app.DisplayName = e.Name;
                    var group = !string.IsNullOrEmpty(ImportGroup) ? ImportGroup : e.Group;
                    if (!string.IsNullOrWhiteSpace(group))
                    {
                        group = group!.Trim();
                        if (!Categorizer.AllCategories.Contains(group) && !Settings.CustomGroups.Contains(group)) { Settings.CustomGroups.Add(group); GroupAdded(group); }
                        app.Group = group == app.Category ? null : group;
                    }
                    if (ImportMarkInstalled && app.HasRelease) app.InstalledVersion = app.LatestVersion;
                    Apps.Add(app); added.Add(app); ok++;
                    RemoveWatch(app.Owner, app.Repo, toast: false);
                }
                catch (GitHubException ex) when (ex.RateLimited) { stopReason = ex.Message; break; }
                catch (Exception ex) { failed++; AddLog($"Import: {e.Owner}/{e.Repo}: {ex.Message}", "warn"); }
            }
        }
        finally { IsImporting = false; }

        ScheduleSaveSettings(); RefreshViews();
        var parts = new List<string> { $"Imported {ok} app{(ok == 1 ? "" : "s")}" };
        if (already > 0) parts.Add($"{already} already tracked");
        if (failed > 0) parts.Add($"{failed} couldn't be added");
        if (parsed.Skipped > 0) parts.Add($"{parsed.Skipped} skipped (not on GitHub)");
        var remaining = fresh.Count - ok - failed;
        if (stopReason != null) parts.Add($"stopped early: {stopReason}");
        ImportStatus = string.Join("  ·  ", parts);
        AddLog(ImportStatus, ok > 0 ? "success" : "warn");
        if (ok > 0) { Toast($"Imported {ok} app{(ok == 1 ? "" : "s")}", "success"); ImportText = ""; }
        if (ImportInstall && !ImportMarkInstalled)
        {
            var go = added.Where(a => a.CanInstall).ToList();
            if (go.Count > 0) await Task.WhenAll(go.Select(a => InstallAsync(a)));
        }
    }

    void ExportJson()
    {
        if (Apps.Count == 0) { Toast("Your library is empty", "info"); return; }
        var dlg = new SaveFileDialog { Title = "Export library", FileName = "releasesync-library.json", Filter = "ReleaseSync library (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        var payload = new
        {
            format = "releasesync", version = 1, exported = DateTime.UtcNow,
            apps = Apps.Select(a => new { url = a.Url, name = a.Title, group = a.Group, includePrereleases = a.IncludePrereleases }).ToList()
        };
        try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })); Toast($"Exported {Apps.Count} apps", "success"); }
        catch (Exception ex) { Toast("Export failed: " + ex.Message, "error"); }
    }

    void ExportLinks()
    {
        if (Apps.Count == 0) { Toast("Your library is empty", "info"); return; }
        var dlg = new SaveFileDialog { Title = "Export links", FileName = "releasesync-links.txt", Filter = "Text file (*.txt)|*.txt" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, Apps.Select(a => a.Url)), Encoding.UTF8); Toast($"Exported {Apps.Count} links", "success"); }
        catch (Exception ex) { Toast("Export failed: " + ex.Message, "error"); }
    }

    // ===================== lifecycle =====================
    public async Task StartAsync()
    {
        RefreshViews();
        StartExtras();
        _ = GitHub.RefreshStatusAsync();
        if (Settings.CheckOnStartup && Apps.Count > 0)
        {
            await Task.Delay(1800);
            await CheckAllAsync(false);
        }
    }
}

