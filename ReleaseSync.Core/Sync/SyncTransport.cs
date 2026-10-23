using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

/// <summary>Somewhere a single sync file can be kept: a shared folder, a private GitHub Gist, memory (tests).</summary>
public interface ISyncTransport
{
    string Description { get; }
    /// <summary>The file's text, or null if nothing has been stored yet.</summary>
    Task<string?> ReadAsync(CancellationToken ct = default);
    Task WriteAsync(string text, CancellationToken ct = default);
}

/// <summary>Keeps the file in memory. Several simulated devices can share one.</summary>
public sealed class MemoryTransport : ISyncTransport
{
    string? text;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public string Description => "memory";
    public string? Text => text;
    public Task<string?> ReadAsync(CancellationToken ct = default) { Reads++; return Task.FromResult(text); }
    public Task WriteAsync(string t, CancellationToken ct = default) { Writes++; text = t; return Task.CompletedTask; }
}

/// <summary>A file in a folder that something else (OneDrive, Dropbox, Syncthing…) copies between devices.</summary>
public sealed class FolderTransport : ISyncTransport
{
    readonly string file;
    public FolderTransport(string file) { this.file = file; }
    public string Description => "shared folder";
    public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult(File.Exists(file) ? File.ReadAllText(file) : null);
    public Task WriteAsync(string text, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the file in a private (secret) GitHub Gist owned by you. If no gist id is known, looks for one made earlier by ReleaseSync
/// and otherwise creates it on the first write. Needs a token with the "gist" permission.
/// </summary>
public sealed class GistTransport : ISyncTransport
{
    public const string FileName = "releasesync-library.json";
    public const string GistDescription = "ReleaseSync library (synced between your devices)";

    readonly HttpClient http;
    public string? GistId { get; private set; }
    public string Description => "private GitHub Gist";

    public GistTransport(HttpClient http, string? gistId) { this.http = http; GistId = string.IsNullOrWhiteSpace(gistId) ? null : gistId.Trim(); }

    /// <summary>An HttpClient set up for GitHub's API with the user's token. Tests pass a fake handler.</summary>
    public static HttpClient CreateClient(string token, HttpMessageHandler? handler = null)
    {
        var c = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        c.BaseAddress = new Uri("https://api.github.com/");
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReleaseSync", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new SyncException("Couldn't reach GitHub. Check your internet connection.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new SyncException("GitHub took too long to answer.", ex); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) throw Explain(res.StatusCode, res);
            return JsonDocument.Parse(text);
        }
    }

    static SyncException Explain(HttpStatusCode code, HttpResponseMessage res)
    {
        if (code == HttpStatusCode.Unauthorized) return new SyncException("GitHub didn't accept your token. Add a fresh one in Settings.");
        if (code == HttpStatusCode.Forbidden && res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && string.Join("", rem) == "0")
            return new SyncException("GitHub's hourly request limit is used up. Sync will work again when it resets.");
        if (code is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return new SyncException("GitHub wouldn't open the sync gist. Your token needs the “gist” permission (a classic token with the gist box ticked), or the gist was deleted.");
        return new SyncException($"GitHub answered {(int)code}.");
    }

    async Task<string?> FindAsync(CancellationToken ct)
    {
        for (int page = 1; page <= 5; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"gists?per_page=100&page={page}", null, ct).ConfigureAwait(false);
            var list = doc.RootElement;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
            foreach (var g in list.EnumerateArray())
                if (g.TryGetProperty("files", out var files) && files.TryGetProperty(FileName, out _) && g.TryGetProperty("id", out var id)) return id.GetString();
            if (list.GetArrayLength() < 100) return null;
        }
        return null;
    }

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        GistId ??= await FindAsync(ct).ConfigureAwait(false);
        if (GistId == null) return null;
        using var doc = await SendAsync(HttpMethod.Get, "gists/" + Uri.EscapeDataString(GistId), null, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty(FileName, out var f)) return null;
        if (f.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True && f.TryGetProperty("raw_url", out var raw) && raw.GetString() is { } rawUrl)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, rawUrl);
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) throw Explain(res.StatusCode, res);
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        return f.TryGetProperty("content", out var c) ? c.GetString() : null;
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        var files = new { files = new System.Collections.Generic.Dictionary<string, object> { [FileName] = new { content = text } } };
        if (GistId == null)
        {
            using var doc = await SendAsync(HttpMethod.Post, "gists", new { description = GistDescription, @public = false, files = files.files }, ct).ConfigureAwait(false);
            GistId = doc.RootElement.GetProperty("id").GetString();
        }
        else
        {
            using var _ = await SendAsync(HttpMethod.Patch, "gists/" + Uri.EscapeDataString(GistId), files, ct).ConfigureAwait(false);
        }
    }
}
