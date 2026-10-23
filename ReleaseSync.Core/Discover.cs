using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

/// <summary>A topic in Discover: the GitHub topics that feed it and a hand-picked list of well-known projects.</summary>
public sealed record DiscoverCategory(string Key, string Title, string[] Topics, string[] Seeds);

public sealed class CachedList
{
    public DateTime Fetched { get; set; }
    public List<RepoInfo> Items { get; set; } = new();
}

public sealed class WinCheck
{
    public DateTime At { get; set; }
    public bool Windows { get; set; }
}

/// <summary>A remembered ranking: where each project stood (rank and stars) at one moment.</summary>
public sealed class Snapshot
{
    public DateTime At { get; set; }
    public Dictionary<string, int[]> Ranks { get; set; } = new();
}

public sealed class DiscoverData
{
    public Dictionary<string, CachedList> Lists { get; set; } = new();
    public Dictionary<string, WinCheck> Windows { get; set; } = new();
    public Dictionary<string, List<Snapshot>> History { get; set; } = new();
}

/// <summary>One project from a GitHub user's starred list, with when they starred it.</summary>
public sealed class StarItem
{
    public DateTime? StarredAt { get; set; }
    public RepoInfo Repo { get; set; } = new();
}

public sealed class StarData
{
    public string User { get; set; } = "";
    public DateTime Synced { get; set; }
    public List<StarItem> Items { get; set; } = new();
}

/// <summary>The last copy of a user's GitHub stars, kept on disk so the page opens instantly and works offline.</summary>
public static class StarStore
{
    static string FilePath => Path.Combine(Paths.DataDir, "stars.json");

    public static StarData? Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<StarData>(File.ReadAllText(FilePath), Store.Options); }
        catch { }
        return null;
    }

    public static void Save(StarData data)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Store.Options));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
public static class DiscoverCatalog
{
    // "owner/repo" is on GitHub; "forgejo:host|owner/repo" is on a self-hosted Forgejo/Gitea site.
    public static readonly List<DiscoverCategory> Categories = new()
    {
        new("all", "Everything", new[] { "topic:windows", "topic:emulator", "topic:decompilation", "topic:source-port", "topic:developer-tools topic:windows" }, Array.Empty<string>()),
        new("games", "Games & ports",
            new[] { "topic:decompilation", "topic:source-port", "topic:pc-port", "topic:recompilation", "topic:game topic:windows" },
            new[] { "HarbourMasters/Shipwright", "HarbourMasters/2ship2harkinian", "HarbourMasters/SpaghettiKart", "HarbourMasters/Starship", "zeldaret/tww", "zeldaret/oot", "zeldaret/mm", "zeldaret/botw",
                    "Zelda64Recomp/Zelda64Recomp", "N64Recomp/N64Recomp", "sm64-port/sm64-port", "n64decomp/sm64", "pret/pokeemerald", "pret/pokered", "OpenRCT2/OpenRCT2", "OpenTTD/OpenTTD",
                    "diasurgical/devilutionX", "RSDKModding/RSDKv5-Decompilation", "Eukaryot/sonic3air", "ScummVM/scummvm", "OpenMW/openmw", "OpenRA/OpenRA", "ZDoom/gzdoom", "dhewm/dhewm3", "yquake2/yquake2", "OpenXcom/OpenXcom" }),
        new("emulators", "Emulators",
            new[] { "topic:emulator", "topic:emulation", "topic:nintendo-switch topic:emulator", "topic:playstation topic:emulator" },
            new[] { "forgejo:git.eden-emu.dev|eden-emu/eden", "dolphin-emu/dolphin", "PCSX2/pcsx2", "RPCS3/rpcs3", "stenzek/duckstation", "hrydgard/ppsspp", "flyinghead/flycast", "shadps4-emu/shadPS4",
                    "azahar-emu/azahar", "cemu-project/Cemu", "xemu-project/xemu", "melonDS-emu/melonDS", "mgba-emu/mgba", "SourMesen/Mesen2", "libretro/RetroArch", "Vita3K/Vita3K",
                    "xenia-canary/xenia-canary", "ares-emulator/ares", "joncampbell123/dosbox-x", "TASEmulators/BizHawk", "snes9x/snes9x" }),
        new("utilities", "Utilities",
            new[] { "topic:windows topic:utility", "topic:windows topic:tool", "topic:file-manager topic:windows", "topic:windows-11" },
            new[] { "microsoft/PowerToys", "files-community/Files", "pbatard/rufus", "ip7z/7zip", "WinDirStat/windirstat", "Flow-Launcher/Flow.Launcher", "ScoopInstaller/Scoop", "microsoft/winget-cli",
                    "Ventoy/Ventoy", "balena-io/etcher", "qbittorrent/qBittorrent", "ShareX/ShareX", "valinet/ExplorerPatcher", "ramensoftware/windhawk" }),
        new("media", "Media",
            new[] { "topic:media-player topic:windows", "topic:video topic:windows", "topic:music-player topic:windows", "topic:streaming topic:windows" },
            new[] { "mpv-player/mpv", "obsproject/obs-studio", "HandBrake/HandBrake", "yt-dlp/yt-dlp", "Tyrrrz/YoutubeDownloader", "jellyfin/jellyfin-desktop", "xbmc/xbmc", "audacity/audacity", "spicetify/cli", "mifi/lossless-cut" }),
        new("dev", "Developer tools",
            new[] { "topic:developer-tools topic:windows", "topic:cli topic:windows", "topic:terminal topic:windows", "topic:code-editor topic:windows" },
            new[] { "microsoft/terminal", "git-for-windows/git", "neovim/neovim", "helix-editor/helix", "VSCodium/vscodium", "zed-industries/zed", "cli/cli", "jesseduffield/lazygit",
                    "BurntSushi/ripgrep", "sharkdp/bat", "wez/wezterm", "alacritty/alacritty", "dandavison/delta" }),
        new("privacy", "Privacy & security",
            new[] { "topic:privacy topic:windows", "topic:security topic:windows", "topic:password-manager", "topic:encryption topic:windows" },
            new[] { "bitwarden/clients", "keepassxreboot/keepassxc", "Cryptomator/cryptomator", "veracrypt/VeraCrypt", "signalapp/Signal-Desktop", "simplex-chat/simplex-chat", "mullvad/mullvadvpn-app", "ProtonVPN/win-app" }),
        new("productivity", "Productivity",
            new[] { "topic:productivity topic:windows", "topic:note-taking topic:windows", "topic:knowledge-base", "topic:todo topic:windows" },
            new[] { "logseq/logseq", "laurent22/joplin", "AppFlowy-IO/AppFlowy", "siyuan-note/siyuan", "toeverything/AFFiNE", "TriliumNext/Notes", "obsidianmd/obsidian-releases", "Zettlr/Zettlr" }),
        new("customize", "Customization",
            new[] { "topic:customization topic:windows", "topic:taskbar topic:windows", "topic:windows-11 topic:theme", "topic:wallpaper topic:windows" },
            new[] { "rainmeter/rainmeter", "TranslucentTB/TranslucentTB", "MicaForEveryone/MicaForEveryone", "rocksdanister/lively", "Open-Shell/Open-Shell-Menu", "ramensoftware/windhawk", "valinet/ExplorerPatcher" }),
        new("graphics", "Graphics & design",
            new[] { "topic:graphics topic:windows", "topic:image-editor", "topic:3d topic:windows", "topic:game-engine topic:windows" },
            new[] { "aseprite/aseprite", "godotengine/godot", "KDE/krita", "upscayl/upscayl", "penpot/penpot", "ocornut/imgui", "blender/blender", "ShareX/ShareX" }),
        new("communication", "Communication",
            new[] { "topic:chat topic:windows", "topic:messenger topic:windows", "topic:voip", "topic:discord-client" },
            new[] { "Vencord/Vesktop", "signalapp/Signal-Desktop", "element-hq/element-desktop", "telegramdesktop/tdesktop", "mumble-voip/mumble", "simplex-chat/simplex-chat" }),
        new("ai", "AI & data",
            new[] { "topic:llm topic:windows", "topic:stable-diffusion topic:windows", "topic:ai topic:desktop-app", "topic:machine-learning topic:windows" },
            new[] { "ollama/ollama", "janhq/jan", "nomic-ai/gpt4all", "Mintplex-Labs/anything-llm", "comfyanonymous/ComfyUI", "invoke-ai/InvokeAI", "SillyTavern/SillyTavern", "oobabooga/text-generation-webui" }),
        new("internet", "Browsers & internet",
            new[] { "topic:browser topic:windows", "topic:download-manager", "topic:vpn topic:windows", "topic:rss-reader topic:windows" },
            new[] { "zen-browser/desktop", "brave/brave-browser", "Floorp-Projects/Floorp", "mullvad/mullvad-browser", "ungoogled-software/ungoogled-chromium-windows", "agalwood/Motrix", "qbittorrent/qBittorrent" }),
    };

    public static DiscoverCategory Find(string? key) => Categories.FirstOrDefault(c => c.Key == key) ?? Categories[0];
}

/// <summary>Builds Discover lists (trending, popular, new, updated, featured) from GitHub searches, remembers earlier rankings so movement can be shown, and checks which projects ship Windows downloads.</summary>
public sealed class DiscoverService
{
    static readonly TimeSpan ListTtl = TimeSpan.FromHours(6);
    static readonly TimeSpan WinTtl = TimeSpan.FromDays(3);
    const int MaxSnapshots = 12;

    readonly GitHubService gh;
    readonly SourceClient sources;
    readonly object gate = new();
    DiscoverData data;

    static string FilePath => Path.Combine(Paths.DataDir, "discover.json");

    public DiscoverService(GitHubService gh, SourceClient sources)
    {
        this.gh = gh; this.sources = sources;
        data = Load();
    }

    static DiscoverData Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<DiscoverData>(File.ReadAllText(FilePath), Store.Options) ?? new(); }
        catch { }
        return new();
    }

    void Save()
    {
        try
        {
            lock (gate)
            {
                // keep the file small: only the freshest few lists, and only recent history
                foreach (var k in data.Lists.Where(kv => DateTime.UtcNow - kv.Value.Fetched > TimeSpan.FromDays(2)).Select(kv => kv.Key).ToList()) data.Lists.Remove(k);
                foreach (var k in data.Lists.OrderByDescending(kv => kv.Value.Fetched).Skip(14).Select(kv => kv.Key).ToList()) data.Lists.Remove(k);
                foreach (var k in data.Windows.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromDays(14)).Select(kv => kv.Key).ToList()) data.Windows.Remove(k);
                Directory.CreateDirectory(Paths.DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Store.Options));
                File.Move(tmp, FilePath, true);
            }
        }
        catch { }
    }

    public static string Key(string mode, string category) => mode + "|" + category;

    // ---------------- candidates ----------------
    static string D(int days) => DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

    static string QueryFor(string mode, string topic) => mode switch
    {
        "Popular" => $"{topic} archived:false stars:>150",
        "New" => $"{topic} archived:false stars:>40 created:>{D(60)}",
        "Updated" => $"{topic} archived:false stars:>200 pushed:>{D(7)}",
        _ => $"{topic} archived:false stars:>40 created:>{D(365)} pushed:>{D(14)}"          // trending: young and active
    };

    static bool Noise(RepoInfo r)
    {
        if (r.Stars < 1 || r.Archived) return true;
        var n = r.Name.ToLowerInvariant();
        if (n.StartsWith("awesome") || n.EndsWith("-awesome") || n.Contains("awesome-list")) return true;
        var d = r.Description?.ToLowerInvariant() ?? "";
        return d.Contains("curated list") || d.Contains("awesome list") || d.StartsWith("a list of");
    }

    static readonly TimeSpan StaleMax = TimeSpan.FromDays(14);
    readonly HashSet<string> refreshing = new();

    /// <summary>The raw (unranked) projects for a mode and category. A saved copy is shown straight away even when it is a few hours old (and quietly refreshed behind the scenes), so switching tabs feels instant.</summary>
    public async Task<(List<RepoInfo> Items, DateTime Fetched)> CandidatesAsync(string mode, DiscoverCategory cat, bool force, CancellationToken ct)
    {
        var key = Key(mode, cat.Key);
        bool refreshBehind = false; (List<RepoInfo>, DateTime)? saved = null;
        lock (gate)
            if (!force && data.Lists.TryGetValue(key, out var hit) && hit.Items.Count > 0)
            {
                var age = DateTime.UtcNow - hit.Fetched;
                if (age < ListTtl) saved = (hit.Items, hit.Fetched);
                else if (age < StaleMax) { saved = (hit.Items, hit.Fetched); refreshBehind = refreshing.Add(key); }
            }
        if (saved != null)
        {
            if (refreshBehind) _ = Task.Run(async () => { try { await FetchSharedAsync(mode, cat, CancellationToken.None); } catch { } finally { lock (gate) refreshing.Remove(key); } });
            return saved.Value;
        }
        return await FetchSharedAsync(mode, cat, ct);
    }

    readonly Dictionary<string, Task<(List<RepoInfo> Items, DateTime Fetched)>> inflight = new();

    /// <summary>One download per list at a time: if the tab is already being prepared in the background, the user simply waits for that same download.</summary>
    Task<(List<RepoInfo> Items, DateTime Fetched)> FetchSharedAsync(string mode, DiscoverCategory cat, CancellationToken ct)
    {
        var key = Key(mode, cat.Key);
        Task<(List<RepoInfo>, DateTime)> task;
        lock (gate)
        {
            if (!inflight.TryGetValue(key, out task!))
            {
                task = FetchAsync(mode, cat, CancellationToken.None);
                inflight[key] = task;
                _ = task.ContinueWith(_ => { lock (gate) inflight.Remove(key); }, TaskScheduler.Default);
            }
        }
        return task.WaitAsync(ct);
    }

    async Task<(List<RepoInfo> Items, DateTime Fetched)> FetchAsync(string mode, DiscoverCategory cat, CancellationToken ct)
    {
        var key = Key(mode, cat.Key);
        var found = new Dictionary<string, RepoInfo>(StringComparer.OrdinalIgnoreCase);
        Exception? firstError = null; int ok = 0;
        var jobs = new List<Func<Task<List<RepoInfo>>>>();

        if (mode == "Featured")
        {
            var seeds = cat.Key == "all" ? DiscoverCatalog.Categories.SelectMany(c => c.Seeds.Take(5)).Distinct().ToList() : cat.Seeds.ToList();
            foreach (var chunk in seeds.Where(s => !s.StartsWith("forgejo:")).Chunk(6))
            {
                var q = string.Join(" ", chunk.Select(s => "repo:" + s));
                jobs.Add(() => gh.SearchListAsync(q, "stars", 30, ct));
            }
            foreach (var s in seeds.Where(s => s.StartsWith("forgejo:")))
            {
                var parts = s[8..].Split('|'); var path = parts[1].Split('/');
                jobs.Add(async () =>
                {
                    var r = await sources.GetRepoAsync("forgejo:" + parts[0], path[0], path[1], ct);
                    r.Source = "forgejo:" + parts[0];
                    return new List<RepoInfo> { r };
                });
            }
        }
        else
        {
            foreach (var topic in cat.Topics)
            {
                var t = topic;
                jobs.Add(() => gh.SearchListAsync(QueryFor(mode, t), "stars", cat.Key == "all" ? 30 : 40, ct));
            }
            // trending also needs the established projects that are busy right now, not just young ones
            if (mode == "Trending")
                foreach (var topic in cat.Topics.Take(2))
                {
                    var t = topic;
                    jobs.Add(() => gh.SearchListAsync($"{t} archived:false stars:>1000 pushed:>{D(7)}", "stars", 20, ct));
                }
        }

        // all the searches at once: the tab loads in about the time of the slowest one
        await Task.WhenAll(jobs.Select(async job =>
        {
            try
            {
                var list = await job();
                lock (found) { foreach (var r in list) if (!Noise(r)) found.TryAdd(r.FullName, r); ok++; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lock (found) firstError ??= ex; }
        }));
        ct.ThrowIfCancellationRequested();

        if (found.Count == 0)
        {
            if (firstError != null) throw firstError;
            return (new List<RepoInfo>(), DateTime.UtcNow);
        }
        var items = found.Values.ToList();
        var now = DateTime.UtcNow;
        if (ok > 0 && firstError == null) lock (gate) data.Lists[key] = new CachedList { Fetched = now, Items = items };      // a list with a failed search is shown, but not kept as if it were complete
        Save();
        return (items, now);
    }

    /// <summary>Looks up which of these projects have Windows downloads ahead of time (several at once), so that opening the tab later has nothing left to wait for.</summary>
    /// <summary>True when there is enough search allowance left for background preparation without slowing down what the user is doing.</summary>
    public bool CanPrefetch => gh.SearchesAvailable >= 11;

    public async Task PreverifyAsync(IEnumerable<RepoInfo> repos, CancellationToken ct)
    {
        using var gateSem = new SemaphoreSlim(8);
        await Task.WhenAll(repos.Select(async r =>
        {
            await gateSem.WaitAsync(ct);
            try { await HasWindowsBuildAsync(r, ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { gateSem.Release(); }
        }));
        Save();
    }
    // ---------------- ranking & movement ----------------
    public sealed record Baseline(Snapshot Snap, TimeSpan Age);

    /// <summary>The earlier ranking to compare with: about a day old for "Day", about a week old for "Week".</summary>
    public Baseline? BaselineFor(string key, string window)
    {
        lock (gate)
        {
            if (!data.History.TryGetValue(key, out var list) || list.Count == 0) return null;
            var now = DateTime.UtcNow;
            var minAge = window == "Week" ? TimeSpan.FromDays(6) : TimeSpan.FromHours(18);
            var old = list.Where(s => now - s.At >= TimeSpan.FromHours(18)).OrderByDescending(s => s.At).ToList();
            if (old.Count == 0) return null;
            var pick = old.FirstOrDefault(s => now - s.At >= minAge) ?? old.Last();      // too little history for a full week: use the oldest we have
            return new Baseline(pick, now - pick.At);
        }
    }

    /// <summary>Puts candidates in order for a mode. Trending uses real star gains when an earlier ranking exists, and an estimate from the project's age when it doesn't.</summary>
    public static List<RepoInfo> Order(List<RepoInfo> items, string mode, Baseline? baseline)
    {
        double Perday(RepoInfo r)
        {
            if (baseline != null && baseline.Snap.Ranks.TryGetValue(r.FullName, out var prev) && prev.Length > 1)
                return Math.Max(0, r.Stars - prev[1]) / Math.Max(1.0, baseline.Age.TotalDays);
            var age = r.CreatedAt.HasValue ? Math.Max(7.0, (DateTime.UtcNow - r.CreatedAt.Value).TotalDays) : 365.0;
            return r.Stars / age;
        }
        return mode switch
        {
            "Trending" => items.OrderByDescending(r => Perday(r) * Math.Log10(r.Stars + 10)).ThenByDescending(r => r.Stars).ToList(),     // gains, nudged toward projects more people already trust
            "Updated" => items.OrderByDescending(r => r.Stars).ToList(),
            _ => items.OrderByDescending(r => r.Stars).ToList()
        };
    }

    /// <summary>Fills in the rank badge, up/down movement and star gain for one project.</summary>
    public static void Annotate(RepoInfo r, int rank, Baseline? baseline)
    {
        r.Rank = rank; r.MoveKind = ""; r.MoveText = ""; r.GainText = ""; r.GainShort = "";
        if (baseline == null) return;
        var since = baseline.Age.TotalHours < 30 ? "since yesterday" : baseline.Age.TotalDays >= 6 ? "this week" : $"in {Math.Round(baseline.Age.TotalDays)} days";
        if (baseline.Snap.Ranks.TryGetValue(r.FullName, out var prev) && prev.Length > 0)
        {
            int delta = prev[0] - rank;
            if (delta > 0) { r.MoveKind = "up"; r.MoveText = "▲ " + delta; }
            else if (delta < 0) { r.MoveKind = "down"; r.MoveText = "▼ " + (-delta); }
            else { r.MoveKind = "same"; r.MoveText = "—"; }
            if (prev.Length > 1 && r.Stars > prev[1]) { r.GainText = $"+{Fmt.Count(r.Stars - prev[1])} stars {since}"; r.GainShort = $"+{Fmt.Count(r.Stars - prev[1])} ★"; }
        }
        else { r.MoveKind = "new"; r.MoveText = "NEW"; }
    }

    /// <summary>Remembers this ranking (at most about once a day) so the next visit can show movement.</summary>
    public void Record(string key, IEnumerable<RepoInfo> ranked)
    {
        lock (gate)
        {
            if (!data.History.TryGetValue(key, out var list)) data.History[key] = list = new List<Snapshot>();
            if (list.Count > 0 && DateTime.UtcNow - list.Max(s => s.At) < TimeSpan.FromHours(20)) return;
            var snap = new Snapshot { At = DateTime.UtcNow };
            foreach (var r in ranked.Take(60)) snap.Ranks[r.FullName] = new[] { r.Rank, r.Stars };
            if (snap.Ranks.Count == 0) return;
            list.Add(snap);
            while (list.Count > MaxSnapshots) list.RemoveAt(0);
        }
        Save();
    }

    public bool HasHistory(string key) { lock (gate) return data.History.TryGetValue(key, out var l) && l.Count > 0; }

    // ---------------- windows downloads ----------------
    /// <summary>Whether the project's recent releases include something that installs on Windows. Remembered for a few days.</summary>
    public async Task<bool> HasWindowsBuildAsync(RepoInfo r, CancellationToken ct)
    {
        var key = r.Source + "|" + r.FullName;
        lock (gate)
            if (data.Windows.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < WinTtl) return c.Windows;
        bool result;
        try
        {
            var res = await sources.GetReleasesAsync(r.Source, r.OwnerLogin, r.Name, null, ct);
            result = res.Releases.Take(4).Any(rel => AssetPicker.LooksLikeWindows(rel.Assets.Select(a => new AssetInfo { Name = a.Name, Url = a.Url, Size = a.Size })));
        }
        catch (OperationCanceledException) { throw; }
        catch (GitHubException ex) when (ex.RateLimited) { throw; }
        catch { result = false; }
        lock (gate) data.Windows[key] = new WinCheck { At = DateTime.UtcNow, Windows = result };
        return result;
    }

    public void SaveWindowChecks() => Save();
}
