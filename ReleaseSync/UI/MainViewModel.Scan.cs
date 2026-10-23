using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ReleaseSync.Core;

namespace ReleaseSync.UI;

/// <summary>One program found on this PC, and what ReleaseSync knows about its GitHub repository.</summary>
public sealed class ScanItem : Observable
{
    public UninstallEntry Entry { get; }
    public ScanItem(UninstallEntry e) { Entry = e; }

    public string Source { get; set; } = "github";
    public string? FullPath { get; set; }
    public string? Owner { get; set; }
    public string? RepoName { get; set; }
    string _kind = "None"; public string Kind { get => _kind; set { _kind = value; NotifyAll(); } }   // Linked, Likely, Possible, None
    RepoInfo? _repo; public RepoInfo? Repo { get => _repo; set { _repo = value; NotifyAll(); } }
    public List<ReleaseInfo> Releases { get; set; } = new();
    bool _releasesLoaded; public bool ReleasesLoaded { get => _releasesLoaded; set { _releasesLoaded = value; NotifyAll(); } }
    bool _selected; public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    bool _busy; public bool IsBusy { get => _busy; set { _busy = value; NotifyAll(); } }
    string? _note; public string? Note { get => _note; set { _note = value; NotifyAll(); } }
    bool _tracked; public bool IsTracked { get => _tracked; set { _tracked = value; NotifyAll(); } }

    public string Name => Entry.DisplayName;
    public string? Version => string.IsNullOrWhiteSpace(Entry.Version) ? null : Entry.Version;
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    public bool HasRepo => Owner != null && RepoName != null;
    public string FullName => HasRepo ? (Repo?.FullName ?? $"{Owner}/{RepoName}") : "";
    public string? Description => Repo?.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string? AvatarUrl => !string.IsNullOrEmpty(Repo?.AvatarUrl) ? Repo!.AvatarUrl : Owner != null && Source == "github" ? $"https://github.com/{Owner}.png?size=96" : null;
    public bool IsPortable => Entry.Id.StartsWith("path|") || Entry.Id.StartsWith("winget|");
    public string StarsText => Repo != null ? Fmt.Count(Repo.Stars) : "";
    public bool HasStars => Repo != null && Repo.Stars > 0;
    public string PublisherText => string.IsNullOrWhiteSpace(Entry.Publisher) ? "" : Entry.Publisher!;
    public string VersionText => Version ?? "version unknown";
    public bool IsLinkedKind => Kind != "None";
    public bool CanSelect => HasRepo && !IsTracked;
    public string KindText => Kind switch { "Linked" => "Linked by the program", "Manual" => "Linked by you", "Likely" => "Likely match", "Possible" => "Possible match", _ => "" };
    public string SourceText => Source == "github" ? "" : Source switch { "gitlab" => "GitLab", "codeberg" => "Codeberg", _ => Source.Contains(':') ? Source[(Source.IndexOf(':') + 1)..] : "" };
    public bool HasSourceText => SourceText.Length > 0;
    public string WhereText => Entry.Id.StartsWith("path|") ? (Entry.InstallLocation ?? "") : "";

    public ReleaseInfo? Latest => Releases.FirstOrDefault(r => !r.Draft && !r.Prerelease) ?? Releases.FirstOrDefault(r => !r.Draft);
    public bool HasUpdate => Latest != null && Version != null && VersionUtil.IsNewer(Latest.Tag, Version);
    public string StatusText =>
        IsTracked ? "Already in your library" :
        !HasRepo ? (Note ?? "") :
        !ReleasesLoaded ? "Checking releases…" :
        Latest == null ? "No releases published" :
        Version == null ? $"Latest release {Latest.Tag}" :
        HasUpdate ? $"Update available: {Version} → {Latest.Tag}" : $"Up to date ({Latest.Tag})";
}

public sealed partial class MainViewModel
{
    public ObservableCollection<ScanItem> ScanLinked { get; } = new();
    public ObservableCollection<ScanItem> ScanOther { get; } = new();
    readonly List<ScanItem> allOther = new();
    CancellationTokenSource? scanCts;
    DateTime nextSearchAt = DateTime.MinValue;

    bool _scanning; public bool IsScanning { get => _scanning; set { if (Set(ref _scanning, value)) { Raise(nameof(CanScan)); Raise(nameof(CanAddScanned)); } } }
    public bool CanScan => !_scanning;
    string _scanStatus = ""; public string ScanStatus { get => _scanStatus; set { Set(ref _scanStatus, value); Raise(nameof(HasScanStatus)); } }
    public bool HasScanStatus => !string.IsNullOrEmpty(_scanStatus);
    bool _hasScanned; public bool HasScanned { get => _hasScanned; set { if (Set(ref _hasScanned, value)) RaiseScanCounts(); } }
    bool _hideSystem = true; public bool HideSystem { get => _hideSystem; set { if (Set(ref _hideSystem, value)) RebuildOther(); } }
    bool _lookingUp; public bool IsLookingUp { get => _lookingUp; set { if (Set(ref _lookingUp, value)) Raise(nameof(CanLookUpAll)); } }
    public bool CanLookUpAll => !_lookingUp && !_scanning && ScanOther.Count > 0;

    public int ScanSelectedCount => ScanLinked.Count(i => i.IsSelected && i.CanSelect);
    public bool CanAddScanned => !_scanning && ScanSelectedCount > 0;
    public string AddScannedText => ScanSelectedCount == 1 ? "Add 1 app to library" : $"Add {ScanSelectedCount} apps to library";
    public bool HasLinked => ScanLinked.Count > 0;
    public string LinkedHeader => $"Linked to GitHub  ·  {ScanLinked.Count}";
    public string OtherHeader => $"Other programs  ·  {ScanOther.Count}";
    public bool HasOther => ScanOther.Count > 0;
    public bool ScanEmptyHint => HasScanned && ScanLinked.Count == 0;
    public string LookUpAllText
    {
        get
        {
            int secs = (int)Math.Ceiling(ScanOther.Count * SearchGap().TotalSeconds);
            return secs >= 90 ? $"Find all on GitHub (~{(secs + 30) / 60} min)" : $"Find all on GitHub (~{secs} s)";
        }
    }

    public ICommand ScanCommand { get; private set; } = null!;
    public ICommand CancelScanCommand { get; private set; } = null!;
    public ICommand LookUpCommand { get; private set; } = null!;
    public ICommand LookUpAllCommand { get; private set; } = null!;
    public ICommand AddScannedCommand { get; private set; } = null!;
    public ICommand SelectAllScannedCommand { get; private set; } = null!;
    public ICommand LinkCommand { get; private set; } = null!;
    public ICommand AddScanFolderCommand { get; private set; } = null!;
    public ICommand RemoveScanFolderCommand { get; private set; } = null!;
    public ICommand ScanWingetCommand { get; private set; } = null!;
    public bool HasScanFolders => Settings.ScanFolders.Count > 0;
    public System.Collections.ObjectModel.ObservableCollection<string> ScanFolderList { get; } = new();

    void InitScanCommands()
    {
        ScanCommand = new RelayCommand(async () => await ScanAsync());
        CancelScanCommand = new RelayCommand(() => scanCts?.Cancel());
        LookUpCommand = new RelayCommand(async p => { if (p is ScanItem i) await LookUpAsync(i, scanCts?.Token ?? CancellationToken.None); });
        LookUpAllCommand = new RelayCommand(async () => await LookUpAllAsync());
        LinkCommand = new RelayCommand(p => { if (p is ScanItem i) AskLink(i); });
        foreach (var f in Settings.ScanFolders) ScanFolderList.Add(f);
        AddScanFolderCommand = new RelayCommand(() =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder that holds your portable programs (each program in its own sub-folder)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || Settings.ScanFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
            Settings.ScanFolders.Add(dlg.SelectedPath); ScanFolderList.Add(dlg.SelectedPath); ScheduleSaveSettings(); Raise(nameof(HasScanFolders));
        });
        RemoveScanFolderCommand = new RelayCommand(p =>
        {
            if (p is not string f) return;
            Settings.ScanFolders.RemoveAll(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)); ScanFolderList.Remove(f); ScheduleSaveSettings(); Raise(nameof(HasScanFolders));
        });
        ScanWingetCommand = new RelayCommand(async () => await ScanWingetAsync());
        AddScannedCommand = new RelayCommand(async () => await AddScannedAsync());
        SelectAllScannedCommand = new RelayCommand(p =>
        {
            bool on = p?.ToString() == "all";
            foreach (var i in ScanLinked.Where(i => i.CanSelect)) i.IsSelected = on;
        });
    }

    void RaiseScanCounts()
    {
        foreach (var n in new[] { nameof(ScanSelectedCount), nameof(CanAddScanned), nameof(AddScannedText), nameof(HasLinked), nameof(LinkedHeader), nameof(OtherHeader), nameof(HasOther), nameof(ScanEmptyHint), nameof(CanLookUpAll), nameof(LookUpAllText) })
            Raise(n);
    }

    void AttachScanItem(ScanItem i) => i.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ScanItem.IsSelected) or "") RaiseScanCounts(); };

    void RebuildOther()
    {
        ScanOther.Clear();
        foreach (var i in allOther.Where(i => !_hideSystem || !UninstallService.IsSystemLike(i.Entry))) ScanOther.Add(i);
        RaiseScanCounts();
    }

    TimeSpan SearchGap() => TimeSpan.FromSeconds(Settings.HasToken ? 2.3 : 6.6);   // GitHub allows 30 searches/min with a token, 10 without

    async Task ScanAsync()
    {
        if (IsScanning) return;
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource(); var ct = cts.Token;
        IsScanning = true; HasScanned = false; ScanLinked.Clear(); ScanOther.Clear(); allOther.Clear();
        ScanStatus = "Reading the programs installed on this PC…";
        try
        {
            var trackedKeys = Apps.Where(a => !string.IsNullOrEmpty(a.UninstallKey)).Select(a => a.UninstallKey!).ToHashSet();
            var trackedExes = Apps.Where(a => !string.IsNullOrEmpty(a.InstallPath)).Select(a => a.InstallPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folders = Settings.ScanFolders.ToList();
            ScanStatus = "Reading installed programs, shortcuts and your folders…";
            var found = await Task.Run(() =>
            {
                var registry = UninstallService.Snapshot().Values
                    .GroupBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .Where(e => !trackedKeys.Contains(e.Id)).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
                var portable = UninstallService.PortableCandidates(registry, folders)
                    .Where(e => e.DisplayIcon == null || !trackedExes.Contains(e.DisplayIcon)).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
                return registry.Concat(portable).Select(e => (Entry: e, Link: UninstallService.FindRepoLink(e))).ToList();
            }, ct);

            foreach (var (entry, link) in found)
            {
                var it = new ScanItem(entry);
                AttachScanItem(it);
                if (link is { } l) { it.Source = l.Source; it.FullPath = l.FullPath; it.Owner = l.Owner; it.RepoName = l.Repo; it.Kind = "Linked"; it.IsTracked = Find(l.Owner, l.Repo) != null; ScanLinked.Add(it); }
                else allOther.Add(it);
            }
            RebuildOther();
            HasScanned = true;

            // fill in repository details and the latest release for the linked ones
            var linked = ScanLinked.Where(i => !i.IsTracked).ToList();
            int done = 0; bool limited = false;
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(linked.Select(async i =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (limited) return;
                    ScanStatus = $"Checking {linked.Count} linked program{(linked.Count == 1 ? "" : "s")} on GitHub…  {done}/{linked.Count}";
                    try
                    {
                        if (i.Source.StartsWith("unknown:"))
                        {
                            var rf = await Sources.ResolveAsync(new RepoRef(i.Source, i.Owner!, i.RepoName!, i.FullPath), ct);
                            i.Source = rf.Source; i.Owner = rf.Owner; i.RepoName = rf.Repo;
                        }
                        var info = await Sources.GetRepoAsync(i.Source, i.Owner!, i.RepoName!, ct);
                        i.Owner = info.OwnerLogin; i.RepoName = info.Name; i.Repo = info;
                        var rr = await Sources.GetReleasesAsync(i.Source, i.Owner, i.RepoName, null, ct);
                        i.Releases = rr.Releases; i.ReleasesLoaded = true;
                        i.IsSelected = true;
                    }
                    catch (GitHubException ex) when (ex.RateLimited) { limited = true; ScanStatus = ex.Message; }
                    catch (GitHubException ex) when (ex.Status == 404 || i.Source.StartsWith("unknown:")) { i.Kind = "None"; i.Owner = null; i.RepoName = null; i.Note = "The project page it points to couldn't be read"; }
                    catch (GitHubException) { i.ReleasesLoaded = true; }
                }
                finally { done++; gate.Release(); }
            }));

            // links that turned out dead go back to the "other" list
            foreach (var dead in ScanLinked.Where(i => i.Kind == "None").ToList()) { ScanLinked.Remove(dead); allOther.Add(dead); }
            RebuildOther();
            if (!limited) ScanStatus = $"Done. {ScanLinked.Count} program{(ScanLinked.Count == 1 ? "" : "s")} linked to GitHub, {allOther.Count} others.";
            AddLog($"Scanned this PC: {ScanLinked.Count} linked to GitHub", "info");
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled."; HasScanned = true; }
        catch (Exception ex) { ScanStatus = "Scan failed: " + ex.Message; AddLog("Scan failed: " + ex.Message, "error"); HasScanned = true; }
        finally { IsScanning = false; RaiseScanCounts(); }
    }

    /// <summary>Picks the best search result for a program name, or null if nothing convincing turns up.</summary>
    static (RepoInfo? Repo, string Kind) PickMatch(ScanItem item, IEnumerable<RepoInfo> results)
    {
        string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var clean = Norm(UninstallService.CleanName(item.Name));
        var pub = Norm(item.Entry.Publisher);
        if (clean.Length < 3) return (null, "None");
        RepoInfo? best = null; int bestScore = 0;
        foreach (var r in results)
        {
            var n = Norm(r.Name);
            // a matching name alone is only a guess ("Apollo" is the name of several unrelated projects); the publisher has to agree to be "Likely"
            int s = n == clean ? 70 : (n.Length >= 4 && clean.Length >= 4 && (n.Contains(clean) || clean.Contains(n))) ? 45 : 0;
            if (s == 0 || r.Stars < 15) continue;
            var owner = Norm(r.OwnerLogin);
            if (pub.Length > 2 && owner.Length > 2 && (pub.Contains(owner) || owner.Contains(pub))) s += 35;
            if (r.Stars >= 500) s += 5;
            if (s > bestScore) { best = r; bestScore = s; }
        }
        if (best == null) return (null, "None");
        return (best, bestScore >= 100 ? "Likely" : "Possible");
    }

    async Task<bool> LookUpAsync(ScanItem item, CancellationToken ct)
    {
        if (item.IsBusy || item.HasRepo) return false;
        item.IsBusy = true; item.Note = null;
        try
        {
            var wait = nextSearchAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            nextSearchAt = DateTime.UtcNow + SearchGap();
            var q = UninstallService.CleanName(item.Name);
            if (q.Length < 2) { item.Note = "Name too short to search for"; return false; }
            var (results, _) = await GitHub.SearchAsync($"{q} in:name fork:false", 1, ct);
            var (repo, kind) = PickMatch(item, results);
            if (repo == null) { item.Note = "No match found on GitHub"; return false; }

            item.Source = "github"; item.Owner = repo.OwnerLogin; item.RepoName = repo.Name; item.Repo = repo; item.Kind = kind;
            item.IsTracked = Find(item.Owner, item.RepoName) != null;
            try { var rr = await GitHub.GetReleasesAsync(item.Owner, item.RepoName, null, ct); item.Releases = rr.Releases; } catch (GitHubException ex) when (!ex.RateLimited) { }
            item.ReleasesLoaded = true;
            item.IsSelected = kind == "Likely" && !item.IsTracked && item.Latest != null;
            allOther.Remove(item); ScanOther.Remove(item); ScanLinked.Add(item);
            RaiseScanCounts();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (GitHubException ex) { item.Note = ex.RateLimited ? "GitHub's search limit was reached — try again in a minute" : ex.Message; if (ex.RateLimited) Toast(item.Note, "warn"); return false; }
        catch (Exception ex) { item.Note = ex.Message; return false; }
        finally { item.IsBusy = false; }
    }

    /// <summary>Lets the user paste the project's address for a program ReleaseSync couldn't match by itself (like a self-hosted emulator).</summary>
    void AskLink(ScanItem item)
    {
        Prompt = new PromptVM
        {
            Title = $"Link {item.Name} to its project page",
            Message = "Paste the address of its repository — GitHub, GitLab, Codeberg, or a self-hosted site such as git.example.com/owner/project.",
            ShowInput = true, Placeholder = "https://…", OkText = "Link it",
            OnOk = p =>
            {
                if (!SourceClient.TryParse(p.Text, out var rf) || rf == null) return "That doesn't look like a repository address.";
                _ = LinkManuallyAsync(item, rf);
                return null;
            }
        };
    }

    async Task LinkManuallyAsync(ScanItem item, RepoRef rf)
    {
        item.IsBusy = true; item.Note = null;
        try
        {
            rf = await Sources.ResolveAsync(rf);
            var info = await Sources.GetRepoAsync(rf.Source, rf.Owner, rf.Repo);
            item.Source = rf.Source; item.FullPath = rf.FullPath; item.Owner = info.OwnerLogin; item.RepoName = info.Name; item.Repo = info; item.Kind = "Manual";
            var rr = await Sources.GetReleasesAsync(rf.Source, item.Owner, item.RepoName, null);
            item.Releases = rr.Releases; item.ReleasesLoaded = true;
            item.IsTracked = Find(item.Owner, item.RepoName) != null;
            item.IsSelected = !item.IsTracked && item.Latest != null;
            allOther.Remove(item); ScanOther.Remove(item);
            if (!ScanLinked.Contains(item)) ScanLinked.Add(item);
            Toast($"Linked {item.Name} to {info.FullName}", "success");
        }
        catch (Exception ex) { item.Note = ex.Message; Toast(ex.Message, "error"); }
        finally { item.IsBusy = false; RaiseScanCounts(); }
    }

    async Task LookUpAllAsync()
    {
        if (IsLookingUp) return;
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource(); var ct = cts.Token;
        IsLookingUp = true;
        var todo = ScanOther.Where(i => !i.HasRepo).ToList();
        int n = 0, hits = 0;
        try
        {
            foreach (var i in todo)
            {
                ct.ThrowIfCancellationRequested();
                ScanStatus = $"Looking for “{i.Name}” on GitHub…  {++n}/{todo.Count}";
                if (await LookUpAsync(i, ct)) hits++;
                if (i.Note != null && i.Note.Contains("limit")) break;
            }
            ScanStatus = $"Finished looking up {n} program{(n == 1 ? "" : "s")} — found {hits} on GitHub.";
        }
        catch (OperationCanceledException) { ScanStatus = $"Stopped after {n} lookups — found {hits} on GitHub."; }
        finally { IsLookingUp = false; RaiseScanCounts(); }
    }

    async Task AddScannedAsync()
    {
        var picks = ScanLinked.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (picks.Count == 0) return;
        int added = 0, updates = 0; string? stop = null;
        IsScanning = true;
        try
        {
            foreach (var i in picks)
            {
                try
                {
                    if (!i.ReleasesLoaded)
                    {
                        var rr = await Sources.GetReleasesAsync(i.Source, i.Owner!, i.RepoName!, null);
                        i.Releases = rr.Releases; i.ReleasesLoaded = true;
                    }
                    var app = BuildApp(i.Owner!, i.RepoName!, i.Repo, i.Releases, Settings.DefaultIncludePrereleases, i.Source);
                    app.LastChecked = DateTime.UtcNow;
                    var have = i.Version?.Trim() ?? "";
                    if (app.HasRelease) app.InstalledVersion = have.Length == 0 || !VersionUtil.IsNewer(app.LatestVersion!, have) ? app.LatestVersion : have;
                    else if (have.Length > 0) app.InstalledVersion = have;
                    app.InstalledAt = DateTime.UtcNow;
                    app.NotifiedVersion = app.LatestVersion;
                    app.UninstallKey = i.IsPortable ? null : i.Entry.Id;       // portable programs have nothing to uninstall through Windows
                    app.LinkVerified = i.Kind is "Linked" or "Manual";         // a name-only match is a guess until the user confirms it
                    app.InstallPath = UninstallService.FindExe(i.Entry);
                    Apps.Add(app);
                    RemoveWatch(app.Owner, app.Repo, toast: false);
                    i.IsTracked = true; i.IsSelected = false;
                    added++; if (app.HasUpdate) updates++;
                }
                catch (GitHubException ex) when (ex.RateLimited) { stop = ex.Message; break; }
                catch (Exception ex) { AddLog($"Scan: couldn't add {i.Name}: {ex.Message}", "warn"); }
            }
        }
        finally { IsScanning = false; RaiseScanCounts(); }

        ScheduleSaveApps(); RefreshViews();
        if (added > 0)
        {
            AddLog($"Added {added} app(s) from the PC scan", "success");
            Toast(updates > 0 ? $"Added {added} app{(added == 1 ? "" : "s")} — {updates} {(updates == 1 ? "has" : "have")} an update waiting" : $"Added {added} app{(added == 1 ? "" : "s")} — you're up to date", "success");
            if (updates > 0) Navigate("Updates");
        }
        if (stop != null) Toast(stop, "warn");
    }
}
