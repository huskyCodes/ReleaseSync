using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ReleaseSync.Core;

namespace ReleaseSync.UI;

public partial class MainViewModel
{
    DiscoverService? discoverService;
    DiscoverService DiscoverSvc => discoverService ??= new DiscoverService(GitHub, Sources);

    public static readonly List<Choice> DiscoverModes = new()
    {
        new("Trending", "Trending"), new("Popular", "Popular"), new("New", "New"), new("Updated", "Updated"), new("Featured", "Featured"), new("Your stars", "Starred")
    };
    public static readonly List<Choice> DiscoverCategories = DiscoverCatalog.Categories.Select(c => new Choice(c.Title, c.Key)).ToList();
    public static readonly List<Choice> DiscoverCompareChoices = new() { new("Since yesterday", "Day"), new("Since last week", "Week") };

    public ICommand SetDiscoverModeCommand { get; private set; } = null!;
    public ICommand SetDiscoverCategoryCommand { get; private set; } = null!;
    public ICommand SetDiscoverCompareCommand { get; private set; } = null!;
    public ICommand RefreshDiscoverCommand { get; private set; } = null!;
    public ICommand ReloadDiscoverCommand { get; private set; } = null!;
    public ICommand SyncStarsCommand { get; private set; } = null!;
    public ICommand OpenStarsCommand { get; private set; } = null!;

    string _discoverStatus = ""; public string DiscoverStatus { get => _discoverStatus; set => Set(ref _discoverStatus, value); }
    public bool IsStarsMode => Settings.DiscoverMode == "Starred";
    public bool ShowRankControls => !IsStarsMode;
    public string DiscoverBlurb => Settings.DiscoverMode switch
    {
        "Popular" => "The most-starred projects of all time.",
        "New" => "Projects created in the last two months, by stars.",
        "Updated" => "Well-liked projects that were updated in the past week.",
        "Featured" => "Hand-picked favorites — emulators, game ports and decompilations, and the tools people reach for first.",
        "Starred" => "Everything you've starred on GitHub, newest first. Add the ones you want kept up to date.",
        _ => "Projects gaining stars fastest right now. Movement arrows show how the ranking changed."
    };
    public bool DiscoverNeedsToken => Settings.DiscoverWindowsOnly && !Settings.HasToken;

    // ---- request allowance: a live counter on the Discover page, a first-visit explainer, and automatic limits ----
    int lastRateSeen = -1;
    /// <summary>Raised with how many requests were just used, so the counter can flash.</summary>
    public event Action<int>? RateDropped;
    public bool HasRate => GitHub.RateRemaining >= 0;
    public string RateLeftText => GitHub.RateRemaining < 0 ? "—" : GitHub.RateRemaining.ToString("N0");
    public string RateOfText => GitHub.RateRemaining < 0 ? (Settings.HasToken ? "requests left" : "requests left · no token") : $"of {GitHub.RateLimit:N0} requests this hour";
    /// <summary>ok, mid (under a quarter left) or low (under 5%): the counter turns amber, then red.</summary>
    public string RateTone
    {
        get
        {
            if (GitHub.RateRemaining < 0 || GitHub.RateLimit <= 0) return "ok";
            double left = (double)GitHub.RateRemaining / GitHub.RateLimit;
            return GitHub.RateRemaining == 0 || left < 0.05 ? "low" : left < 0.25 ? "mid" : "ok";
        }
    }
    public string RateTip => GitHub.RateRemaining < 0 ? "GitHub requests left this hour. Browsing Discover uses some; the number updates live."
        : $"{GitHub.RateRemaining:N0} of {GitHub.RateLimit:N0} GitHub requests left this hour" + (GitHub.RateReset is { } r ? $" (resets at {r:t})" : "") ;
    public bool HasRateWarning => RateTone == "low" && GitHub.RateRemaining >= 0;
    public string RateWarning => GitHub.RateRemaining == 0
        ? $"You have used all your GitHub requests for this hour. Update checks and Discover resume at {(GitHub.RateReset is { } t0 ? t0.ToString("t") : "the top of the hour")}."
        : $"Only {GitHub.RateRemaining:N0} requests left this hour{(GitHub.RateReset is { } t1 ? $" (resets at {t1:t})" : "")}. Discover has paused its extras so ReleaseSync can still check your apps for updates — keep some in reserve.";
    public string DiscoverIntroLimit => Settings.HasToken ? "With your token you have 5,000 requests an hour." : "Without a token you only have 60 requests an hour, so Discover will work but run out quickly — a free token raises that to 5,000 (Settings → GitHub).";

    void RaiseDiscoverRate()
    {
        var now = GitHub.RateRemaining; var old = lastRateSeen; lastRateSeen = now;
        foreach (var n in new[] { nameof(HasRate), nameof(RateLeftText), nameof(RateOfText), nameof(RateTone), nameof(RateTip), nameof(HasRateWarning), nameof(RateWarning) }) Raise(n);
        if (old >= 0 && now >= 0 && now < old) RateDropped?.Invoke(old - now);
    }

    public bool ShowDiscoverIntro => !Settings.DiscoverIntroSeen && ShowPresets;
    public bool NoToken => !Settings.HasToken;
    bool tokenNudgeDismissed;
    /// <summary>Shown (slim, dismissible) to people browsing Discover without a GitHub token, once the first-visit card is out of the way.</summary>
    public bool ShowTokenNudge => !Settings.HasToken && ShowPresets && !ShowDiscoverIntro && !tokenNudgeDismissed;
    public ICommand OpenTokenSettingsCommand { get; private set; } = null!;
    public ICommand DismissTokenNudgeCommand { get; private set; } = null!;
    public ICommand SetDiscoverLightCommand { get; private set; } = null!;
    void RaiseIntroState() { Raise(nameof(ShowDiscoverIntro)); Raise(nameof(ShowTokenNudge)); Raise(nameof(NoToken)); Raise(nameof(DiscoverIntroLimit)); }
    public ICommand DismissDiscoverIntroCommand { get; private set; } = null!;
    public ICommand UseLightModeCommand { get; private set; } = null!;
    public ICommand ShowDiscoverIntroCommand { get; private set; } = null!;

    int FirstBatch => Settings.DiscoverLight ? 15 : 36;
    /// <summary>Running low? Stop the optional extras so the app keeps enough for checking your updates.</summary>
    bool AllowanceTight => GitHub.RateRemaining >= 0 && GitHub.RateRemaining < 150;
    bool AllowanceModerate => GitHub.RateRemaining >= 0 && GitHub.RateRemaining < 1500;

    // ---- your stars ----
    StarData? starData;
    string _starsFilter = ""; public string StarsFilter { get => _starsFilter; set { if (Set(ref _starsFilter, value)) { starsFilterTimer.Stop(); starsFilterTimer.Start(); } } }
    readonly DispatcherTimer starsFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool _syncingStars; public bool IsSyncingStars { get => _syncingStars; set { if (Set(ref _syncingStars, value)) Raise(nameof(CanSyncStars)); } }
    public bool CanSyncStars => !_syncingStars;

    // ---- paging through the current browse list ----
    List<RepoInfo> pagedOrdered = new();
    int pagedNext;
    bool pagedFilter, pagedStars;
    DiscoverService.Baseline? pagedBaseline;

    void InitDiscover()
    {
        SetDiscoverModeCommand = new RelayCommand(p =>
        {
            if (p is string s && s != Settings.DiscoverMode)
            {
                Settings.DiscoverMode = s;
                Raise(nameof(DiscoverBlurb)); Raise(nameof(IsStarsMode)); Raise(nameof(ShowRankControls));
                _ = LoadBrowseAsync(false);
            }
        });
        SetDiscoverCategoryCommand = new RelayCommand(p => { if (p is string s && s != Settings.DiscoverCategory) { prefetchCts?.Cancel(); Settings.DiscoverCategory = s; _ = LoadBrowseAsync(false); } });
        SetDiscoverCompareCommand = new RelayCommand(p => { if (p is string s && s != Settings.DiscoverCompare) { Settings.DiscoverCompare = s; _ = LoadBrowseAsync(false); } });
        RefreshDiscoverCommand = new RelayCommand(() => { if (IsStarsMode) _ = SyncStarsAsync(); else _ = LoadBrowseAsync(true); });
        ReloadDiscoverCommand = new RelayCommand(() => { Raise(nameof(DiscoverNeedsToken)); _ = LoadBrowseAsync(false); });
        SyncStarsCommand = new RelayCommand(() => _ = SyncStarsAsync());
        DismissDiscoverIntroCommand = new RelayCommand(() => { Settings.DiscoverIntroSeen = true; RaiseIntroState(); });
        OpenTokenSettingsCommand = new RelayCommand(() => { SettingsSection = "GitHub"; Navigate("Settings"); });
        DismissTokenNudgeCommand = new RelayCommand(() => { tokenNudgeDismissed = true; RaiseIntroState(); });
        SetDiscoverLightCommand = new RelayCommand(p =>
        {
            bool light = p?.ToString() == "Light";
            Settings.DiscoverLight = light;
            if (light) prefetchCts?.Cancel();                       // stop preparing the other tabs right away
            if (!HasSearched) _ = LoadBrowseAsync(false);           // redraw at the new size from what is already saved (no new requests for a list you have seen)
        });
        UseLightModeCommand = new RelayCommand(() => { Settings.DiscoverLight = true; Settings.DiscoverIntroSeen = true; RaiseIntroState(); Toast("Light mode is on — Discover will use far fewer requests", "success"); });
        ShowDiscoverIntroCommand = new RelayCommand(() => { Settings.DiscoverIntroSeen = false; tokenNudgeDismissed = false; RaiseIntroState(); Navigate("Discover"); });
        Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.DiscoverIntroSeen)) RaiseIntroState(); if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(AppSettings.GitHubToken) or nameof(AppSettings.HasToken)) { RaiseIntroState(); Raise(nameof(RateOfText)); } };
        OpenStarsCommand = new RelayCommand(() =>
        {
            Settings.DiscoverMode = "Starred";
            Raise(nameof(DiscoverBlurb)); Raise(nameof(IsStarsMode)); Raise(nameof(ShowRankControls));
            Navigate("Discover");
            _ = LoadBrowseAsync(false);
        });
        starsFilterTimer.Tick += (_, _) => { starsFilterTimer.Stop(); if (IsStarsMode && !HasSearched) _ = LoadBrowseAsync(false); };
    }

    /// <summary>Shows the browse lists when Discover opens with nothing else on it.</summary>
    void EnsureBrowse()
    {
        if (CurrentPage != "Discover" || HasSearched || SearchBusy || SearchResults.Count > 0 || ownerLogin != null) return;
        _ = LoadBrowseAsync(false);
    }

    static string CleanUser(string? s)
    {
        var t = (s ?? "").Trim().TrimEnd('/');
        foreach (var p in new[] { "https://github.com/", "http://github.com/", "github.com/", "@" })
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) t = t[p.Length..];
        var slash = t.IndexOf('/');
        return slash > 0 ? t[..slash] : t;
    }

    /// <summary>Downloads the user's starred projects from GitHub (all of them, 100 at a time) and keeps a copy on disk.</summary>
    public async Task SyncStarsAsync()
    {
        if (IsSyncingStars) return;
        var user = CleanUser(string.IsNullOrWhiteSpace(Settings.StarsUser) ? GitHub.Login : Settings.StarsUser);
        if (user.Length == 0) { Toast("Type your GitHub username first", "info"); return; }
        Settings.StarsUser = user;
        IsSyncingStars = true;
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        SearchError = null; SearchBusy = true; SearchResults.Clear(); CanLoadMore = false;
        try
        {
            var all = new List<StarItem>();
            for (int page = 1; page <= 40; page++)
            {
                DiscoverStatus = all.Count == 0 ? $"Reading {user}'s stars…" : $"Reading {user}'s stars… {all.Count} so far";
                var items = await GitHub.GetStarredPageAsync(user, page, ct);
                all.AddRange(items.Select(i => new StarItem { StarredAt = i.StarredAt, Repo = i.Repo }));
                if (items.Count < 100) break;
                await Task.Delay(200, ct);
            }
            starData = new StarData { User = user, Synced = DateTime.UtcNow, Items = all };
            StarStore.Save(starData);
            AddLog($"Synced {all.Count} starred projects from GitHub ({user})", "success");
            Toast(all.Count == 0 ? $"{user} hasn't starred anything yet" : $"Synced {all.Count} starred projects", all.Count == 0 ? "info" : "success");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (!ct.IsCancellationRequested) { SearchError = ex.Message; DiscoverStatus = ""; } SearchBusy = false; IsSyncingStars = false; return; }
        IsSyncingStars = false;
        SearchBusy = false;
        if (IsStarsMode) await LoadBrowseAsync(false);
    }

    public async Task LoadBrowseAsync(bool force)
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        ownerLogin = null; OwnerProfile = null; HasSearched = false;
        SearchResults.Clear(); SearchError = null; SearchBusy = true; CanLoadMore = false; SearchTotal = 0; SearchTitle = "Discover";
        pagedOrdered = new(); pagedNext = 0;
        DiscoverStatus = "Loading…";
        Raise(nameof(DiscoverNeedsToken)); Raise(nameof(DiscoverBlurb)); Raise(nameof(IsStarsMode)); Raise(nameof(ShowRankControls));

        var mode = Settings.DiscoverMode;
        var cat = DiscoverCatalog.Find(Settings.DiscoverCategory);
        var key = DiscoverService.Key(mode, cat.Key);
        try
        {
            string suffix;
            if (mode == "Starred")
            {
                if (string.IsNullOrWhiteSpace(Settings.StarsUser) && !string.IsNullOrEmpty(GitHub.Login)) Settings.StarsUser = GitHub.Login;
                starData ??= StarStore.Load();
                if (starData == null || starData.Items.Count == 0)
                {
                    DiscoverStatus = starData == null ? "Enter your GitHub username and press “Sync stars” to bring in everything you've starred." : "No starred projects found for that account.";
                    return;
                }
                var q = StarsFilter.Trim();
                var items = starData.Items
                    .Where(s => !s.Repo.Archived || true)
                    .Where(s => q.Length == 0 || s.Repo.FullName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                || (s.Repo.Description?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                || (s.Repo.Language?.Equals(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                || (s.Repo.Topics?.Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ?? false))
                    .OrderByDescending(s => s.StarredAt ?? DateTime.MinValue).ToList();
                foreach (var s in items) s.Repo.StarredText = s.StarredAt.HasValue ? "Starred " + Fmt.Ago(s.StarredAt) : "";
                pagedOrdered = items.Select(s => s.Repo).ToList();
                pagedBaseline = null; pagedStars = true;
                suffix = $"{(q.Length > 0 ? $"{pagedOrdered.Count} of " : "")}{starData.Items.Count} stars  ·  synced {Fmt.Ago(starData.Synced)} from {starData.User}";
            }
            else
            {
                var (items, fetched) = await DiscoverSvc.CandidatesAsync(mode, cat, force, ct);
                pagedBaseline = DiscoverSvc.BaselineFor(key, Settings.DiscoverCompare);
                pagedOrdered = DiscoverService.Order(items, mode, pagedBaseline);
                pagedStars = false;
                var parts = new List<string> { "updated " + Fmt.Ago(fetched) };
                if (pagedBaseline != null)
                {
                    var d = pagedBaseline.Age;
                    parts.Add("movement vs " + (d.TotalHours < 30 ? "yesterday" : d.TotalDays >= 6 ? "last week" : $"{Math.Round(d.TotalDays)} days ago"));
                }
                else if (mode != "Featured") parts.Add("movement arrows appear on your next visit — today's ranking is saved");
                suffix = string.Join("  ·  ", parts);
            }
            pagedFilter = Settings.DiscoverWindowsOnly && Settings.HasToken && mode != "Featured" && !AllowanceTight;
            await FillAsync(FirstBatch, ct);
            ct.ThrowIfCancellationRequested();

            if (mode == "Featured" && Settings.HasToken) _ = MarkWindowsAsync(SearchResults.ToList(), ct);
            if (!pagedStars) { DiscoverSvc.Record(key, SearchResults); StartPrefetch(cat, mode); }
            DiscoverStatus = $"{SearchResults.Count} shown  ·  {suffix}" + (AllowanceTight && Settings.DiscoverWindowsOnly && Settings.HasToken ? "  ·  Windows check paused to save your requests" : "");
            if (SearchResults.Count == 0)
                SearchError = pagedStars ? (pagedFilter ? "None of these have Windows downloads yet. Turn off “Only projects with Windows downloads” to see them all." : "Nothing matches that filter.")
                                         : pagedFilter ? "Nothing here has Windows downloads right now. Try another tab or category, or turn off “Only projects with Windows downloads”."
                                                       : "Nothing matched here right now. Try another tab or category.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) { SearchError = ex.Message; DiscoverStatus = ""; } }
        finally { if (searchCts == cts) SearchBusy = false; }
    }

    /// <summary>Adds the next batch of the current list to the page, skipping projects with no Windows download when that filter is on.</summary>
    async Task FillAsync(int want, CancellationToken ct)
    {
        int added = 0;
        void Add(RepoInfo r)
        {
            r.IsTracked = Find(r.OwnerLogin, r.Name) != null;
            r.IsTryLater = FindWatch(r.OwnerLogin, r.Name) != null;
            if (!pagedStars) DiscoverService.Annotate(r, SearchResults.Count + 1, pagedBaseline);
            SearchResults.Add(r); added++;
        }

        if (!pagedFilter)
        {
            while (pagedNext < pagedOrdered.Count && added < want) Add(pagedOrdered[pagedNext++]);
        }
        else
        {
            int checkedCount = 0;
            while (pagedNext < pagedOrdered.Count && added < want && checkedCount < (Settings.DiscoverLight ? 40 : 100) && !ct.IsCancellationRequested)
            {
                var batch = pagedOrdered.Skip(pagedNext).Take(14).ToList(); pagedNext += batch.Count; checkedCount += batch.Count;
                DiscoverStatus = $"Checking which projects have Windows downloads… {SearchResults.Count} found";
                var verdicts = await Task.WhenAll(batch.Select(async r =>
                {
                    try { return (bool?)await DiscoverSvc.HasWindowsBuildAsync(r, ct); }
                    catch (GitHubException) { return null; }          // rate limited or unreachable: keep it rather than hide it
                }));
                int batchStart = pagedNext - batch.Count;
                for (int k = 0; k < batch.Count; k++)
                {
                    if (added >= want) { pagedNext = batchStart + k; break; }      // enough for now: the rest stay for "Load more"
                    if (verdicts[k] == false) continue;
                    batch[k].HasWindows = verdicts[k] == true;
                    Add(batch[k]);
                }
            }
            DiscoverSvc.SaveWindowChecks();
        }
        CanLoadMore = pagedNext < pagedOrdered.Count;
    }

    public async Task LoadMoreBrowseAsync()
    {
        if (SearchBusy || !CanLoadMore) return;
        var cts = searchCts = new CancellationTokenSource();
        SearchBusy = true; CanLoadMore = false;
        try { await FillAsync(FirstBatch, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchError = ex.Message; }
        finally { if (searchCts == cts) SearchBusy = false; }
        DiscoverStatus = DiscoverStatus.Contains("  ·  ") ? $"{SearchResults.Count} shown  ·  " + DiscoverStatus[(DiscoverStatus.IndexOf("  ·  ") + 5)..] : $"{SearchResults.Count} shown";
    }

    /// <summary>For hand-picked projects, which are always shown, adds a "Windows" badge to the ones confirmed to have Windows downloads.</summary>
    async Task MarkWindowsAsync(List<RepoInfo> list, CancellationToken ct)
    {
        try
        {
            foreach (var r in list)
            {
                if (ct.IsCancellationRequested) return;
                try { r.HasWindows = await DiscoverSvc.HasWindowsBuildAsync(r, ct); }
                catch (GitHubException) { return; }
            }
            DiscoverSvc.SaveWindowChecks();
        }
        catch (OperationCanceledException) { }
    }
    CancellationTokenSource? prefetchCts;

    /// <summary>With a token there is plenty of room, so quietly prepare the other tabs for this category (their lists and which projects have Windows downloads) while the user looks at this one.</summary>
    string? prefetchKey;

    void StartPrefetch(DiscoverCategory cat, string current)
    {
        if (!Settings.HasToken || current == "Featured" || Settings.DiscoverLight || AllowanceModerate) return;      // light mode, or less than 1,500 left: no background work
        var tag = cat.Key + "|" + Settings.DiscoverWindowsOnly + "|" + Settings.DiscoverCompare;
        if (prefetchKey == tag && prefetchCts is { IsCancellationRequested: false }) return;      // already preparing this category
        prefetchCts?.Cancel();
        prefetchKey = tag;
        var cts = prefetchCts = new CancellationTokenSource();
        var ct = cts.Token;
        bool verify = Settings.DiscoverWindowsOnly;
        var compare = Settings.DiscoverCompare;
        _ = Task.Run(async () =>
        {
            foreach (var m in new[] { "Trending", "Popular", "New", "Updated" }.Where(x => x != current))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    for (int wait = 0; !DiscoverSvc.CanPrefetch && wait < 60; wait++) await Task.Delay(1500, ct);      // let the user's own searches go first
                    if (!DiscoverSvc.CanPrefetch) return;
                    var (items, _) = await DiscoverSvc.CandidatesAsync(m, cat, false, ct);
                    if (verify)
                    {
                        var ordered = DiscoverService.Order(items, m, DiscoverSvc.BaselineFor(DiscoverService.Key(m, cat.Key), compare));
                        await DiscoverSvc.PreverifyAsync(ordered.Take(48).ToList(), ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Application.Current?.Dispatcher.BeginInvoke(new Action(() => AddLog($"Couldn't prepare the {m} list ahead of time: {ex.Message}", "warn"))); }
            }
        }, ct);
    }}
