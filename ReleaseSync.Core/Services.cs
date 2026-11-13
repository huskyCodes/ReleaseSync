using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

public static class AssetPicker
{
    static readonly string[] InstallExt = { ".msi", ".exe", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip", ".7z" };
    static readonly HashSet<string> OtherOs = new() { "linux", "darwin", "macos", "osx", "mac", "android", "ios", "freebsd", "ubuntu", "debian", "fedora", "appimage", "deb", "rpm", "snap", "flatpak", "wasm" };
    static readonly HashSet<string> WinTokens = new() { "win", "windows", "win32", "win64", "msvc", "w64" };
    static readonly string[] Junk = { "symbols", "debug", "source", "sbom", "checksum", "docs", "-src", "_src", ".src", "unins" };

    public static AssetKind KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".msi")) return AssetKind.Msi;
        if (n.EndsWith(".msix") || n.EndsWith(".msixbundle") || n.EndsWith(".appx") || n.EndsWith(".appxbundle")) return AssetKind.Msix;
        if (n.EndsWith(".zip") || n.EndsWith(".7z")) return AssetKind.Zip;      // any archive the app unpacks
        if (n.EndsWith(".exe")) return n.Contains("portable") ? AssetKind.Portable : AssetKind.Installer;
        return AssetKind.Unknown;
    }

    public static string ResolveArch(string pref)
    {
        if (!string.Equals(pref, "Auto", StringComparison.OrdinalIgnoreCase)) return pref.ToLowerInvariant();
        return RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X86 => "x86", _ => "x64" };
    }

    static string DetectArch(string n)
    {
        if (n.Contains("arm64") || n.Contains("aarch64")) return "arm64";
        if (n.Contains("x86_64") || n.Contains("x86-64") || n.Contains("x64") || n.Contains("amd64") || n.Contains("win64") || n.Contains("64bit") || n.Contains("64-bit")) return "x64";
        if (n.Contains("x86") || n.Contains("i386") || n.Contains("i686") || n.Contains("ia32") || n.Contains("win32") || n.Contains("32bit") || n.Contains("32-bit")) return "x86";
        return "";
    }

    static int Score(AssetInfo a)
    {
        var s = AppSettingsProvider.Current;
        var name = a.Name.ToLowerInvariant();
        if (!InstallExt.Any(e => name.EndsWith(e))) return int.MinValue;
        if (Junk.Any(name.Contains)) return int.MinValue;
        var tokens = Regex.Split(name, @"[^a-z0-9]+").Where(t => t.Length > 0).ToArray();
        int score = 0;
        if (tokens.Any(OtherOs.Contains)) score -= 200;
        if (tokens.Any(WinTokens.Contains)) score += 30;

        var want = ResolveArch(s.PreferredArch);
        var got = DetectArch(name);
        if (got == want) score += 40;
        else if (got.Length > 0) score -= 80;

        var kind = KindOf(a.Name);
        bool installer = kind is AssetKind.Installer or AssetKind.Msi;
        bool portable = kind is AssetKind.Zip or AssetKind.Portable;
        if (installer) score += s.PreferInstaller ? 25 : 5;
        if (portable) score += s.PreferInstaller ? 5 : 25;
        if (kind == AssetKind.Msix) score += 8;
        if (kind == AssetKind.Msi) score += 3;
        if (tokens.Contains("setup") || tokens.Contains("installer") || tokens.Contains("install")) score += s.PreferInstaller ? 10 : 0;
        if (tokens.Contains("portable")) score += s.PreferInstaller ? -5 : 10;
        return score;
    }

    /// <summary>True when at least one download looks like it installs on Windows (not just Linux/macOS builds or source archives).</summary>
    public static bool LooksLikeWindows(IEnumerable<AssetInfo>? assets) => assets != null && assets.Any(a =>
    {
        if (Score(a) == int.MinValue) return false;
        var name = a.Name.ToLowerInvariant();
        var tokens = Regex.Split(name, @"[^a-z0-9]+").Where(t => t.Length > 0).ToArray();
        if (tokens.Any(OtherOs.Contains)) return false;
        // installers only run on Windows; a plain zip needs "win"/"windows" in its name to count
        return name.EndsWith(".exe") || name.EndsWith(".msi") || name.EndsWith(".msix") || name.EndsWith(".msixbundle") || name.EndsWith(".appx") || name.EndsWith(".appxbundle")
               || tokens.Any(WinTokens.Contains);
    });

    public static AssetInfo? Pick(IEnumerable<AssetInfo>? assets, string? pattern)
    {
        if (assets == null) return null;
        var scored = assets.Select(a => (a, s: Score(a))).Where(x => x.s != int.MinValue).ToList();
        if (scored.Count == 0) return null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                var m = scored.Where(x => Regex.IsMatch(x.a.Name, pattern, RegexOptions.IgnoreCase)).ToList();
                if (m.Count > 0) return m.OrderByDescending(x => x.s).First().a;
            }
            catch { }
        }
        return scored.OrderByDescending(x => x.s).First().a;
    }

    public static IEnumerable<AssetInfo> Choices(IEnumerable<AssetInfo>? assets) =>
        (assets ?? Enumerable.Empty<AssetInfo>()).Where(a => Score(a) != int.MinValue).OrderByDescending(Score);

    /// <summary>Turns "App-1.2.3-x64.exe" into a pattern that keeps matching future releases.</summary>
    public static string MakePattern(string assetName)
    {
        const char mark = '\u0001';
        var s = Regex.Replace(assetName, @"\d+(?:\.\d+)+", mark.ToString());
        s = Regex.Escape(s).Replace(mark.ToString(), @"\d+(?:\.\d+)+");
        return "^" + s + "$";
    }
}

public static class Categorizer
{
    sealed record Rule(string Name, string[] Strong, string[] Weak, string[] NameBits, double Weight = 1.0);

    // Order matters: when two categories tie, the earlier (more specific) one wins.
    static readonly Rule[] Rules =
    {
        new("Games & Emulation",
            Strong: new[] { "emulator", "emulators", "emulation", "libretro", "retro", "retroarch", "rom", "roms", "nintendo", "playstation", "xbox", "sega", "gba", "gameboy", "snes", "nes", "n64", "ps1", "ps2", "ps3", "ps4", "psp", "arcade", "mame", "game", "games", "gaming", "gamepad", "minecraft", "speedrun", "speedrunning", "modding", "steam", "gog", "emulate", "homebrew", "console", "consoles", "switch emulator", "game launcher" },
            Weak: new[] { "controller", "controllers", "launcher", "mod", "mods", "play", "players", "joystick", "vr" },
            NameBits: new[] { "emu", "retro", "dolphin", "pcsx", "rpcs", "ryujinx", "yuzu", "citra", "cemu", "mgba", "duckstation", "ppsspp", "eden", "melonds", "flycast", "scummvm", "playnite", "lutris", "heroic", "xenia", "vita3k", "suyu", "sudachi", "citron", "torzu", "panda3ds", "mesen", "bsnes", "snes9x", "fceux", "visualboyadvance", "rmg", "azahar", "lime3ds" }),
        new("Media & Entertainment",
            Strong: new[] { "video", "videos", "audio", "music", "player", "podcast", "podcasts", "subtitle", "subtitles", "streaming", "ffmpeg", "codec", "transcode", "media player", "anime", "movie", "movies", "lyrics", "equalizer", "radio", "mpv", "vlc", "playlist", "spotify", "soundcloud", "jellyfin", "plex", "kodi", "screen recorder", "screen recording" },
            Weak: new[] { "stream", "recorder", "media", "song", "songs", "tv", "cast", "visualizer", "obs" },
            NameBits: new[] { "mpv", "vlc", "player", "music", "audio", "video", "media", "jellyfin", "kodi", "obs", "ffmpeg", "podcast", "spotify", "tube" }),
        new("Graphics & Design",
            Strong: new[] { "photo", "photos", "image", "images", "picture", "pictures", "graphics", "graphic", "design", "designer", "draw", "drawing", "paint", "painting", "sketch", "illustration", "vector", "svg", "raster", "3d", "render", "rendering", "screenshot", "screenshots", "screen capture", "icon", "icons", "wallpaper", "wallpapers", "photoshop", "gimp", "krita", "inkscape", "blender", "image editor", "photo editor", "pixel art", "animation", "cad", "modeling", "texture", "font", "fonts", "typography", "color picker", "colour" },
            Weak: new[] { "color", "palette", "art", "artist", "canvas", "pixel", "viewer", "gallery", "capture", "screen", "record", "annotate", "upload" },
            NameBits: new[] { "photo", "image", "paint", "draw", "pixel", "sketch", "gimp", "krita", "ink", "blend", "art", "shot", "picture", "icon", "color", "colour", "design", "render" }),
        new("Productivity",
            Strong: new[] { "pdf", "document", "documents", "office", "note", "notes", "notetaking", "note-taking", "markdown", "todo", "to-do", "tasks", "task", "calendar", "planner", "spreadsheet", "reader", "ebook", "epub", "writing", "writer", "wiki", "knowledge", "organizer", "clipboard", "kanban", "productivity", "scanner", "ocr", "pdf reader", "word processor", "pomodoro", "journal", "diary", "bookmark", "bookmarks", "reminder", "reminders", "mind map", "whiteboard" },
            Weak: new[] { "scan", "organize", "organise", "focus", "text", "docs", "read" },
            NameBits: new[] { "pdf", "note", "todo", "office", "doc", "write", "book", "calendar", "task", "journal", "wiki", "obsidian", "joplin", "logseq", "craft" }),
        new("Security & Privacy",
            Strong: new[] { "security", "password", "passwords", "privacy", "encryption", "encrypt", "encrypted", "antivirus", "firewall", "2fa", "authenticator", "vault", "vpn", "malware", "keepass", "bitwarden", "password manager", "tor", "anonymity", "secure", "zero-knowledge", "pgp", "gpg", "cryptography", "hash", "checksum", "forensics", "pentest" },
            Weak: new[] { "protect", "protection", "private", "safe", "lock", "scan", "sandbox" },
            NameBits: new[] { "pass", "vault", "crypt", "guard", "secure", "vpn", "wireguard", "keepass", "bitwarden", "veracrypt", "sandbox" }),
        new("Browsers & Internet",
            Strong: new[] { "browser", "chromium", "firefox", "web browser", "download manager", "downloader", "torrent", "bittorrent", "proxy", "dns", "ftp", "rss", "feed", "adblock", "ad-blocker", "wget", "aria2", "yt-dlp", "youtube", "internet", "website", "websites", "webview" },
            Weak: new[] { "web", "download", "downloads", "http", "network", "sync" },
            NameBits: new[] { "browser", "torrent", "download", "dl", "firefox", "chrom", "brave", "proxy", "dns", "ftp" }),
        new("Communication",
            Strong: new[] { "chat", "messenger", "messaging", "discord", "matrix", "irc", "email", "mail", "voip", "telegram", "signal", "whatsapp", "slack", "xmpp", "mastodon", "social", "videoconference", "video call" },
            Weak: new[] { "client", "call", "calls", "community" },
            NameBits: new[] { "chat", "mail", "discord", "matrix", "telegram", "signal", "irc", "talk", "message" }),
        new("AI & Data",
            Strong: new[] { "ai", "llm", "gpt", "machine learning", "ml", "neural", "ollama", "chatgpt", "stable diffusion", "inference", "dataset", "analytics", "data analysis", "csv", "transformer", "whisper", "speech recognition", "openai" },
            Weak: new[] { "model", "models", "data", "learning", "prompt" },
            NameBits: new[] { "gpt", "llm", "ollama", "whisper", "diffusion", "neural", "ai-" }),
        new("System & Utilities",
            Strong: new[] { "utility", "utilities", "system", "monitor", "monitoring", "cleaner", "cleanup", "optimizer", "backup", "disk", "file manager", "explorer", "taskbar", "registry", "driver", "drivers", "tweak", "tweaks", "customization", "archive", "archiver", "compression", "7-zip", "uninstaller", "benchmark", "startup", "hardware", "bluetooth", "battery", "context menu", "windows tweaks", "debloat", "powertoys", "launcher", "search", "usb", "bootable", "iso", "flash", "android", "adb", "remote", "mirror", "mirroring", "device", "devices", "partition", "format" },
            Weak: new[] { "tool", "tools", "manager", "file", "files", "windows", "power", "ram", "cpu", "gpu", "usb", "rename", "duplicate", "duplicates" },
            NameBits: new[] { "tool", "util", "clean", "optim", "monitor", "backup", "explorer", "toys", "tweak", "zip", "disk", "manager" }, Weight: 0.75),
        new("Developer Tools",
            Strong: new[] { "developer", "developers", "programming", "compiler", "debugger", "ide", "sdk", "devtools", "code editor", "text editor", "terminal", "shell", "cli", "git", "docker", "kubernetes", "devops", "api client", "database", "sql", "linter", "formatter", "package manager", "powershell", "regex", "json", "command line", "ssh", "vscode", "neovim", "vim", "emacs", "debug", "repl", "scripting", "open source library", "build tool", "version control", "vs code", "visual studio code", "ide", "telemetry" },
            Weak: new[] { "api", "library", "build", "framework", "editor", "code", "command", "script", "plugin", "extension", "rust", "cargo", "npm", "python" },
            NameBits: new[] { "code", "vscod", "dev", "git", "term", "shell", "ide", "vim", "sql", "docker", "compiler", "debug", "cli", "notepad" }, Weight: 0.75),
    };

    public static readonly string[] AllCategories = Rules.Select(r => r.Name).Append("Other").ToArray();

    static readonly Regex Camel = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static string Categorize(string name, string? description, IEnumerable<string>? topics, string? language = null)
    {
        var topicList = (topics ?? Enumerable.Empty<string>()).Select(t => t.ToLowerInvariant()).ToList();
        var topicSet = new HashSet<string>(topicList.Concat(topicList.SelectMany(t => t.Split('-'))));
        var nameTokens = Regex.Split(Camel.Replace(name, " ").ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length > 0).ToHashSet();
        var nameNorm = Norm(name);
        var descLower = (description ?? "").ToLowerInvariant();
        var descTokens = Regex.Split(descLower, @"[^a-z0-9\-+]+").Where(t => t.Length > 0).ToHashSet();
        bool Phrase(string term) => (term.Contains(' ') || term.Contains('-')) && descLower.Contains(term);

        string best = "Other"; double bestScore = 0;
        foreach (var r in Rules)
        {
            double s = 0;
            foreach (var t in r.Strong)
            {
                if (topicSet.Contains(t)) s += 4;
                if (nameTokens.Contains(t)) s += 3;
                if (descTokens.Contains(t) || Phrase(t)) s += 2;
            }
            foreach (var t in r.Weak)
            {
                if (topicSet.Contains(t)) s += 2;
                if (nameTokens.Contains(t)) s += 1.5;
                if (descTokens.Contains(t)) s += 0.75;
            }
            foreach (var b in r.NameBits)
                if (nameNorm.Contains(b.Replace("-", ""))) s += 4;
            s *= r.Weight;
            if (s > bestScore + 0.01) { best = r.Name; bestScore = s; }
        }
        return bestScore >= 2 ? best : "Other";
    }
}
public class GitHubException : Exception
{
    public bool RateLimited { get; }
    public int Status { get; }
    public GitHubException(string msg, int status = 0, bool rate = false) : base(msg) { Status = status; RateLimited = rate; }
}

public sealed class ReleaseResult
{
    public bool NotModified { get; set; }
    public string? ETag { get; set; }
    public List<ReleaseInfo> Releases { get; set; } = new();
}

public static class AppSettingsProvider
{
    public static AppSettings Current { get; set; } = new();
}

public sealed class GitHubService
{
    readonly HttpClient http;
    public int RateLimit { get; private set; }
    public int RateRemaining { get; private set; } = -1;
    public DateTime? RateReset { get; private set; }
    public string? Login { get; private set; }
    public event Action? StatusChanged;

    public GitHubService()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReleaseSync/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static readonly Regex RepoRegex = new(
        @"^(?:(?:https?://)?(?:www\.)?github\.com[/:]|git@github\.com:)?(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParseRepo(string? input, out string owner, out string repo)
    {
        owner = repo = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var t = input.Trim().Trim('"', '\'', '<', '>');
        if (t.Contains(' ')) return false;
        var m = RepoRegex.Match(t);
        if (!m.Success) return false;
        owner = m.Groups["o"].Value; repo = m.Groups["r"].Value;
        if (owner.Equals("orgs", StringComparison.OrdinalIgnoreCase) || owner.Equals("topics", StringComparison.OrdinalIgnoreCase)) return false;
        // a bare "a/b" should not look like a file path or url fragment
        if (!t.Contains("github.com", StringComparison.OrdinalIgnoreCase) && (t.Contains("://") || t.Contains('\\') || t.Count(c => c == '/') != 1)) return false;
        return true;
    }

    async Task<HttpResponseMessage> SendAsync(string url, string? etag, string? tokenOverride, CancellationToken ct, string? accept = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept != null) { req.Headers.Accept.Clear(); req.Headers.Accept.ParseAdd(accept); }
        var token = tokenOverride ?? AppSettingsProvider.Current.GitHubToken;
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new GitHubException("Can't reach GitHub. Check your internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubException("GitHub took too long to respond."); }
        UpdateRate(resp, tokenOverride == null);
        return resp;
    }

    void UpdateRate(HttpResponseMessage r, bool own)
    {
        if (!own) return;
        // searches have their own, smaller allowance; the number shown to the user is the main (core) one
        if (r.Headers.TryGetValues("x-ratelimit-resource", out var res) && !res.First().Equals("core", StringComparison.OrdinalIgnoreCase)) return;
        if (r.Headers.TryGetValues("x-ratelimit-limit", out var l) && int.TryParse(l.First(), out var lim)) RateLimit = lim;
        if (r.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && int.TryParse(rem.First(), out var rm)) RateRemaining = rm;
        if (r.Headers.TryGetValues("x-ratelimit-reset", out var rs) && long.TryParse(rs.First(), out var ts)) RateReset = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
        StatusChanged?.Invoke();
    }

    GitHubException Failure(HttpResponseMessage r, string what)
    {
        int code = (int)r.StatusCode;
        if (code == 404) return new GitHubException($"{what} wasn't found. If it's private, add a token with access in Settings.", code);
        if (code == 401) return new GitHubException("GitHub rejected your access token. Check it in Settings → GitHub.", code);
        if (code is 403 or 429)
        {
            bool limited = RateRemaining == 0 || r.Headers.Contains("Retry-After");
            if (limited)
            {
                var when = RateReset?.ToString("t") ?? "soon";
                var tip = AppSettingsProvider.Current.HasToken ? "" : " Add a token in Settings to raise the limit to 5,000 per hour.";
                return new GitHubException($"GitHub rate limit reached. It resets at {when}.{tip}", code, true);
            }
            return new GitHubException("GitHub denied the request (403).", code);
        }
        return new GitHubException($"GitHub returned an error ({code}).", code);
    }

    public async Task<RepoInfo> GetRepoAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/repos/{owner}/{repo}", null, null, ct);
        if (!r.IsSuccessStatusCode) throw Failure(r, $"{owner}/{repo}");
        return (await JsonSerializer.DeserializeAsync<RepoInfo>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct))!;
    }

    /// <summary>The repository's README as plain markdown text, or null if it has none.</summary>
    public async Task<string?> GetReadmeAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/repos/{owner}/{repo}/readme", null, null, ct, "application/vnd.github.raw+json");
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw Failure(r, $"{owner}/{repo}");
        var text = await r.Content.ReadAsStringAsync(ct);
        return text.Length > 40000 ? text[..40000] : text;
    }

    /// <summary>The repository's license as plain text, or null if it has none.</summary>
    public async Task<string?> GetLicenseAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/repos/{owner}/{repo}/license", null, null, ct, "application/vnd.github.raw+json");
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw Failure(r, $"{owner}/{repo}");
        var text = await r.Content.ReadAsStringAsync(ct);
        return text.Length > 60000 ? text[..60000] : text;
    }

    public async Task<UserInfo> GetUserAsync(string login, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/users/{login}", null, null, ct);
        if (!r.IsSuccessStatusCode) throw Failure(r, $"“{login}”");
        return (await JsonSerializer.DeserializeAsync<UserInfo>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct))!;
    }

    /// <summary>A developer's or organization's public repositories, most recently active first.</summary>
    public async Task<List<RepoInfo>> GetOwnerReposAsync(string login, int page, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/users/{login}/repos?per_page=30&sort=pushed&page={page}", null, null, ct);
        if (!r.IsSuccessStatusCode) throw Failure(r, $"“{login}”");
        return await JsonSerializer.DeserializeAsync<List<RepoInfo>>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new();
    }

    public async Task<ReleaseResult> GetReleasesAsync(string owner, string repo, string? etag, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20", etag, null, ct);
        if (r.StatusCode == HttpStatusCode.NotModified) return new ReleaseResult { NotModified = true, ETag = etag };
        if (!r.IsSuccessStatusCode) throw Failure(r, $"{owner}/{repo}");
        var list = await JsonSerializer.DeserializeAsync<List<ReleaseInfo>>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new();
        return new ReleaseResult { ETag = r.Headers.ETag?.ToString(), Releases = list.Where(x => !x.Draft).OrderByDescending(x => x.PublishedAt ?? DateTime.MinValue).ToList() };
    }

    public async Task<(List<RepoInfo> Items, int Total)> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        bool qualified = query.Contains(':');
        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&per_page=24&page={page}" + (qualified ? "&sort=stars&order=desc" : "");
        using var r = await SendAsync(url, null, null, ct);
        if (!r.IsSuccessStatusCode) throw Failure(r, "That search");
        var res = await JsonSerializer.DeserializeAsync<SearchResponse>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new();
        return (res.Items, res.Total);
    }

    /// <summary>One page (up to 100) of the projects a GitHub user has starred, newest star first. Public stars need no token.</summary>
    public async Task<List<(DateTime? StarredAt, RepoInfo Repo)>> GetStarredPageAsync(string login, int page, CancellationToken ct = default)
    {
        using var r = await SendAsync($"https://api.github.com/users/{Uri.EscapeDataString(login)}/starred?per_page=100&page={page}", null, null, ct, "application/vnd.github.star+json");
        if (!r.IsSuccessStatusCode) throw Failure(r, $"“{login}”");
        using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<(DateTime?, RepoInfo)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("repo", out var repoEl)) continue;
            var repo = JsonSerializer.Deserialize<RepoInfo>(repoEl.GetRawText());
            if (repo == null) continue;
            DateTime? at = el.TryGetProperty("starred_at", out var s) && s.ValueKind == JsonValueKind.String && DateTime.TryParse(s.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
            list.Add((at, repo));
        }
        return list;
    }

    // GitHub allows about 30 searches a minute with a token and 10 without. A small bucket lets a whole tab (a handful of searches) go out at once, then paces the rest so nothing is refused.
    readonly object searchLock = new();
    double searchTokens = -1; DateTime searchStamp = DateTime.UtcNow;
    static double SearchCapacity => AppSettingsProvider.Current.HasToken ? 16 : 6;
    static double SearchRefillPerSecond => AppSettingsProvider.Current.HasToken ? 0.45 : 0.15;

    void RefillSearch()
    {
        var now = DateTime.UtcNow;
        if (searchTokens < 0) searchTokens = SearchCapacity;
        searchTokens = Math.Min(SearchCapacity, searchTokens + (now - searchStamp).TotalSeconds * SearchRefillPerSecond);
        searchStamp = now;
    }

    /// <summary>How many searches could go out right now without waiting. Background work uses this to stay out of the way.</summary>
    public double SearchesAvailable { get { lock (searchLock) { RefillSearch(); return searchTokens; } } }

    async Task TakeSearchAsync(CancellationToken ct)
    {
        while (true)
        {
            double wait;
            lock (searchLock)
            {
                RefillSearch();
                if (searchTokens >= 1) { searchTokens -= 1; return; }
                wait = (1 - searchTokens) / SearchRefillPerSecond;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 5)), ct);
        }
    }

    /// <summary>A plain list of repositories for a search query, ordered by stars or recent updates.</summary>
    public async Task<List<RepoInfo>> SearchListAsync(string query, string sort, int perPage, CancellationToken ct = default)
    {
        await TakeSearchAsync(ct);
        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&per_page={perPage}&sort={sort}&order=desc";
        using var r = await SendAsync(url, null, null, ct);
        if (!r.IsSuccessStatusCode) throw Failure(r, "That search");
        var res = await JsonSerializer.DeserializeAsync<SearchResponse>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new();
        return res.Items;
    }

    /// <summary>Checks a token without storing it. Returns the login on success.</summary>
    public async Task<string> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        using var r = await SendAsync("https://api.github.com/user", null, token, ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized) throw new GitHubException("That token isn't valid.");
        if (!r.IsSuccessStatusCode) throw new GitHubException($"GitHub returned an error ({(int)r.StatusCode}).");
        using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
    }

    /// <summary>Reads the saved token's identity and the current rate limit.</summary>
    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await SendAsync("https://api.github.com/rate_limit", null, null, ct);
            if (!AppSettingsProvider.Current.HasToken) { Login = null; StatusChanged?.Invoke(); return; }
            using var r2 = await SendAsync("https://api.github.com/user", null, null, ct);
            if (r2.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await r2.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                Login = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
            }
            else Login = null;
            StatusChanged?.Invoke();
        }
        catch { }
    }

    public void ClearLogin() { Login = null; StatusChanged?.Invoke(); }
}

public static class Paths
{
    /// <summary>%APPDATA%\ReleaseSync, or the folder named by the RELEASESYNC_DATA environment variable (used for testing).</summary>
    public static string DataDir { get; } = ResolveDataDir();
    static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("RELEASESYNC_DATA") is { Length: > 0 } custom) return custom;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var neu = Path.Combine(roaming, "ReleaseSync");
        Legacy.CopyDataIfNeeded(Path.Combine(roaming, Legacy.OldName), neu);      // first run after the rename: bring the library across (the old folder is left alone)
        return neu;
    }
    /// <summary>%LOCALAPPDATA%\ReleaseSync (app backups, downloads) — or the old folder if it could not be moved yet.</summary>
    public static string LocalDir { get; } = Legacy.ResolveLocalDir();
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string AppsFile => Path.Combine(DataDir, "apps.json");
    public static string WatchFile => Path.Combine(DataDir, "trylater.json");
}

public sealed class Store
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,   // a window position that was never set is NaN, which plain JSON can't hold
        Converters = { new JsonStringEnumConverter() }
    };

    static T? Load<T>(string file) where T : class
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options);
        }
        catch
        {
            try { File.Move(file, file + ".bad", true); } catch { }
            // the file was damaged: bring back the newest backup that still reads, instead of silently starting from nothing
            try
            {
                var good = DataBackups.NewestReadable(Path.GetFileName(file));
                if (good != null)
                {
                    File.Copy(good, file, true);
                    RecoveryNote = $"A data file was damaged, so ReleaseSync restored your last good copy from {Path.GetFileName(Path.GetDirectoryName(good))}. The damaged file was kept as {Path.GetFileName(file)}.bad.";
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options);
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>Set when a damaged data file was repaired from a backup; shown to the user at start-up.</summary>
    public static string? RecoveryNote { get; private set; }

    static void Save(string file, object data)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, data.GetType(), Options));
        File.Move(tmp, file, true);
    }

    public AppSettings LoadSettings() => Load<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
    public List<TrackedApp> LoadApps() => Load<List<TrackedApp>>(Paths.AppsFile) ?? new();
    public List<TryLaterItem> LoadWatch() => Load<List<TryLaterItem>>(Paths.WatchFile) ?? new();
    public void SaveWatch(IEnumerable<TryLaterItem> items) => Save(Paths.WatchFile, items.ToList());
    public void SaveSettings(AppSettings s) => Save(Paths.SettingsFile, s);
    public void SaveApps(IEnumerable<TrackedApp> apps) => Save(Paths.AppsFile, apps.ToList());
}

public sealed class ImportEntry
{
    public string Owner = "", Repo = "";
    public string? Name, Group;
    public bool Prereleases;
}

public sealed class ImportResult
{
    public List<ImportEntry> Entries { get; } = new();
    public int Skipped { get; set; }
}

public static class Importer
{
    static readonly Regex UrlRegex = new(@"github\.com[/:](?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static string CleanRepo(string r) => r.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? r[..^4] : r;

    public static ImportResult Parse(string text)
    {
        var result = new ImportResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string o, string r, string? name = null, string? group = null, bool pre = false)
        {
            r = CleanRepo(r);
            if (!seen.Add($"{o}/{r}")) return;
            result.Entries.Add(new ImportEntry { Owner = o, Repo = r, Name = name, Group = group, Prereleases = pre });
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
                JsonElement apps = default;
                bool found = false;
                if (doc.RootElement.ValueKind == JsonValueKind.Array) { apps = doc.RootElement; found = true; }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("apps", out apps) && apps.ValueKind == JsonValueKind.Array) found = true;

                if (found)
                {
                    foreach (var el in apps.EnumerateArray())
                    {
                        string? url = el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        if (url == null || !GitHubService.TryParseRepo(url, out var o, out var r) || !url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                        { result.Skipped++; continue; }
                        string? name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        string? group = null;
                        if (el.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String) group = g.GetString();
                        else if (el.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.String) group = c[0].GetString();
                        bool pre = false;
                        if (el.TryGetProperty("includePrereleases", out var ip) && ip.ValueKind is JsonValueKind.True) pre = true;
                        if (el.TryGetProperty("additionalSettings", out var add))
                        {
                            try
                            {
                                var inner = add.ValueKind == JsonValueKind.String ? JsonDocument.Parse(add.GetString()!).RootElement.Clone() : add;
                                if (inner.TryGetProperty("includePrereleases", out var p) && p.ValueKind == JsonValueKind.True) pre = true;
                            }
                            catch { }
                        }
                        Add(o, r, name, group, pre);
                    }
                    return result;
                }
            }
            catch { /* fall through to plain-text scanning */ }
        }

        foreach (Match m in UrlRegex.Matches(text)) Add(m.Groups["o"].Value, m.Groups["r"].Value);
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0 || l.Contains("github.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (GitHubService.TryParseRepo(l, out var o, out var r)) Add(o, r);
        }
        return result;
    }
}


