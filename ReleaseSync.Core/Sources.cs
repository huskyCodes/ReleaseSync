using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

/// <param name="Source">github, gitlab, codeberg, "forgejo:host", "gitlab:host", or "unknown:host" (a site ReleaseSync still has to identify).</param>
/// <param name="FullPath">The whole project path, for GitLab-style nested groups.</param>
public sealed record RepoRef(string Source, string Owner, string Repo, string? FullPath = null)
{
    public static string UrlFor(string source, string owner, string repo) => source switch
    {
        "github" => $"https://github.com/{owner}/{repo}",
        "gitlab" => $"https://gitlab.com/{owner}/{repo}",
        "codeberg" => $"https://codeberg.org/{owner}/{repo}",
        _ when source.Contains(':') => $"https://{source[(source.IndexOf(':') + 1)..]}/{owner}/{repo}",
        _ => $"https://github.com/{owner}/{repo}"
    };
    public string Url => UrlFor(Source, Owner, Repo);
}

/// <summary>Reads releases and repository details from GitHub, GitLab or Codeberg through one interface.</summary>
public sealed class SourceClient
{
    readonly GitHubService gh;
    readonly HttpClient http;

    public SourceClient(GitHubService gh)
    {
        this.gh = gh;
        http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReleaseSync/1.0");
    }

    static readonly Regex GitLabRx = new(@"^(?:https?://)?(?:www\.)?gitlab\.com/(?<p>[^?#]+?)(?:/-/.*)?(?:\.git)?/?(?:[?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CodebergRx = new(@"^(?:https?://)?(?:www\.)?codeberg\.org/(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Understands GitHub, GitLab and Codeberg links (and plain owner/repo, which means GitHub).</summary>
    public static bool TryParse(string? input, out RepoRef? repo)
    {
        repo = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var t = input.Trim().Trim('"', '\'', '<', '>');
        if (t.Contains(' ')) return false;
        var m = GitLabRx.Match(t);
        if (m.Success)
        {
            var segs = m.Groups["p"].Value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2) { repo = new RepoRef("gitlab", string.Join("/", segs[..^1]), segs[^1]); return true; }
            return false;
        }
        m = CodebergRx.Match(t);
        if (m.Success) { repo = new RepoRef("codeberg", m.Groups["o"].Value, m.Groups["r"].Value); return true; }
        if (GitHubService.TryParseRepo(t, out var o, out var r)) { repo = new RepoRef("github", o, r); return true; }

        // any other web address with owner/project in its path: could be a self-hosted Forgejo/Gitea or GitLab site
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Contains('.'))
        {
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-", "releases", "src", "tree", "blob", "issues", "pulls", "pull", "commit", "commits", "wiki", "actions", "tags", "branches", "raw", "archive", "packages", "projects", "compare" };
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).TakeWhile(s => !stop.Contains(s)).ToArray();
            if (segs.Length >= 2)
            {
                var last = segs[^1]; if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) segs[^1] = last[..^4];
                repo = new RepoRef("unknown:" + uri.Host.ToLowerInvariant(), segs[0], segs[1], string.Join("/", segs));
                return true;
            }
        }
        return false;
    }

    /// <summary>Works out what kind of site an unfamiliar address is (Forgejo/Gitea or GitLab). Known sites pass straight through.</summary>
    public async Task<RepoRef> ResolveAsync(RepoRef r, CancellationToken ct = default)
    {
        if (!r.Source.StartsWith("unknown:")) return r;
        var host = r.Source[8..];
        try
        {
            using var f = await http.GetAsync($"https://{host}/api/v1/repos/{r.Owner}/{r.Repo}", ct);
            if (f.IsSuccessStatusCode) return r with { Source = "forgejo:" + host };
        }
        catch (HttpRequestException) { throw new GitHubException($"Can't reach {host}. Check the address and your internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubException($"{host} took too long to respond."); }
        try
        {
            var path = r.FullPath ?? $"{r.Owner}/{r.Repo}";
            using var g = await http.GetAsync($"https://{host}/api/v4/projects/{Uri.EscapeDataString(path)}", ct);
            if (g.IsSuccessStatusCode)
            {
                var segs = path.Split('/');
                return new RepoRef("gitlab:" + host, string.Join("/", segs[..^1]), segs[^1], path);
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        throw new GitHubException($"ReleaseSync couldn't find a project at that address. It can read GitHub, GitLab, Codeberg and self-hosted Forgejo/Gitea or GitLab sites.");
    }

    static (string Kind, string Host) Split(string source) => source switch
    {
        "github" => ("github", "github.com"),
        "gitlab" => ("gitlab", "gitlab.com"),
        "codeberg" => ("forgejo", "codeberg.org"),
        _ when source.StartsWith("forgejo:") => ("forgejo", source[8..]),
        _ when source.StartsWith("gitlab:") => ("gitlab", source[7..]),
        _ => ("github", "github.com")
    };

    public Task<RepoInfo> GetRepoAsync(string source, string owner, string repo, CancellationToken ct = default)
    {
        var (kind, host) = Split(source);
        return kind switch
        {
            "gitlab" => GitLabRepoAsync(host, owner, repo, ct),
            "forgejo" => ForgejoRepoAsync(host, owner, repo, ct),
            _ => gh.GetRepoAsync(owner, repo, ct)
        };
    }

    public Task<ReleaseResult> GetReleasesAsync(string source, string owner, string repo, string? etag, CancellationToken ct = default)
    {
        var (kind, host) = Split(source);
        return kind switch
        {
            "gitlab" => GitLabReleasesAsync(host, owner, repo, ct),
            "forgejo" => ForgejoReleasesAsync(host, owner, repo, ct),
            _ => gh.GetReleasesAsync(owner, repo, etag, ct)
        };
    }

    // ---------------- helpers ----------------
    async Task<JsonDocument> GetJsonAsync(string url, string what, CancellationToken ct)
    {
        HttpResponseMessage r;
        try { r = await http.GetAsync(url, ct); }
        catch (HttpRequestException) { throw new GitHubException("Can't reach the server. Check your internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubException("The server took too long to respond."); }
        using (r)
        {
            if (r.StatusCode == HttpStatusCode.NotFound) throw new GitHubException($"{what} wasn't found.", 404);
            if ((int)r.StatusCode == 429) throw new GitHubException("Rate limit reached for that site. Try again in a minute.", 429, true);
            if (!r.IsSuccessStatusCode) throw new GitHubException($"{what}: the server returned an error ({(int)r.StatusCode}).", (int)r.StatusCode);
            return await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
    }

    static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int I(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static DateTime? D(JsonElement e, string name) => DateTime.TryParse(S(e, name), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    // ---------------- GitLab ----------------
    async Task<RepoInfo> GitLabRepoAsync(string host, string owner, string repo, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://{host}/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}?license=true", $"{owner}/{repo}", ct);
        var e = doc.RootElement;
        var ns = e.TryGetProperty("namespace", out var n) ? n : default;
        var path = S(e, "path_with_namespace") ?? $"{owner}/{repo}";
        var info = new RepoInfo
        {
            FullName = path, Name = S(e, "path") ?? repo, Description = S(e, "description"), Stars = I(e, "star_count"), Forks = I(e, "forks_count"), OpenIssues = I(e, "open_issues_count"),
            HtmlUrl = S(e, "web_url"), Archived = B(e, "archived"), CreatedAt = D(e, "created_at"), PushedAt = D(e, "last_activity_at"),
            OwnerInfo = new OwnerDto { Login = path[..path.LastIndexOf('/')], AvatarUrl = S(e, "avatar_url") ?? (ns.ValueKind == JsonValueKind.Object ? S(ns, "avatar_url") : null) },
            Topics = e.TryGetProperty("topics", out var tp) && tp.ValueKind == JsonValueKind.Array ? tp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new(),
        };
        if (e.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object) info.License = new LicenseDto { Name = S(lic, "nickname") ?? S(lic, "name") };
        return info;
    }

    async Task<ReleaseResult> GitLabReleasesAsync(string host, string owner, string repo, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://{host}/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}/releases?per_page=20", $"{owner}/{repo}", ct);
        var list = new List<ReleaseInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var rel = new ReleaseInfo
            {
                Tag = S(e, "tag_name") ?? "", Name = S(e, "name"), Body = S(e, "description"), Prerelease = B(e, "upcoming_release"),
                PublishedAt = D(e, "released_at") ?? D(e, "created_at"),
                HtmlUrl = e.TryGetProperty("_links", out var lk) && lk.ValueKind == JsonValueKind.Object ? S(lk, "self") : null,
            };
            if (e.TryGetProperty("assets", out var assets) && assets.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                foreach (var l in links.EnumerateArray())
                    rel.Assets.Add(new AssetDto { Name = S(l, "name") ?? "", Url = S(l, "direct_asset_url") ?? S(l, "url") ?? "" });
            // keep only files that look like downloads (GitLab also lists links to docs and other pages)
            rel.Assets = rel.Assets.Where(a => a.Url.Length > 0 && a.Name.Length > 0).ToList();
            if (rel.Tag.Length > 0) list.Add(rel);
        }
        return new ReleaseResult { Releases = list.OrderByDescending(x => x.PublishedAt ?? DateTime.MinValue).ToList() };
    }

    // ---------------- Codeberg (Forgejo) ----------------
    async Task<RepoInfo> ForgejoRepoAsync(string host, string owner, string repo, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://{host}/api/v1/repos/{owner}/{repo}", $"{owner}/{repo}", ct);
        var e = doc.RootElement;
        var o = e.TryGetProperty("owner", out var ow) ? ow : default;
        return new RepoInfo
        {
            FullName = S(e, "full_name") ?? $"{owner}/{repo}", Name = S(e, "name") ?? repo, Description = S(e, "description"), Stars = I(e, "stars_count"), Forks = I(e, "forks_count"),
            OpenIssues = I(e, "open_issues_count"), Watchers = I(e, "watchers_count"), HtmlUrl = S(e, "html_url"), Homepage = S(e, "website"), Archived = B(e, "archived"), Fork = B(e, "fork"),
            Language = S(e, "language"), CreatedAt = D(e, "created_at"), PushedAt = D(e, "updated_at"),
            OwnerInfo = new OwnerDto { Login = o.ValueKind == JsonValueKind.Object ? S(o, "login") ?? owner : owner, AvatarUrl = (o.ValueKind == JsonValueKind.Object ? S(o, "avatar_url") : null) ?? S(e, "avatar_url") },
        };
    }

    async Task<ReleaseResult> ForgejoReleasesAsync(string host, string owner, string repo, CancellationToken ct)
    {
        using var r = await http.GetAsync($"https://{host}/api/v1/repos/{owner}/{repo}/releases?limit=20", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) throw new GitHubException($"{owner}/{repo} wasn't found.", 404);
        if (!r.IsSuccessStatusCode) throw new GitHubException($"{host} returned an error ({(int)r.StatusCode}).", (int)r.StatusCode);
        var list = await JsonSerializer.DeserializeAsync<List<ReleaseInfo>>(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? new();
        return new ReleaseResult { Releases = list.Where(x => !x.Draft).OrderByDescending(x => x.PublishedAt ?? DateTime.MinValue).ToList() };
    }
}

