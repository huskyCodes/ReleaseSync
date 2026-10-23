using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core.Tests;

/// <summary>Just enough of GitHub's Gist API to sync against, with a log of every request so tests can check what was sent.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public sealed record Gist(string Id, bool Public, string Description, Dictionary<string, string> Files);
    public sealed record Call(HttpMethod Method, string Url, string? Auth, string? Body);

    public Dictionary<string, Gist> Gists { get; } = new();
    public List<Call> Calls { get; } = new();
    public HttpStatusCode? Fail { get; set; }
    public bool TruncateFiles { get; set; }
    int next = 1;

    public void Add(string id, string file, string content, bool pub = false, string description = "") => Gists[id] = new Gist(id, pub, description, new() { [file] = content });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content == null ? null : await req.Content.ReadAsStringAsync(ct);
        var url = req.RequestUri!.ToString();
        Calls.Add(new Call(req.Method, url, req.Headers.Authorization?.ToString(), body));
        if (Fail is { } f) return Reply(f, "{\"message\":\"nope\"}");

        var path = req.RequestUri.AbsolutePath;
        if (path.StartsWith("/raw/")) { var g = Gists[path.Split('/')[2]]; return Reply(HttpStatusCode.OK, g.Files.Values.First()); }
        if (path == "/gists" && req.Method == HttpMethod.Get)
        {
            var arr = Gists.Values.Select(g => new { id = g.Id, files = g.Files.ToDictionary(k => k.Key, k => new { filename = k.Key }) });
            return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(arr));
        }
        if (path == "/gists" && req.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(body!);
            var id = "gist" + next++;
            var files = doc.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("content").GetString()!);
            Gists[id] = new Gist(id, doc.RootElement.GetProperty("public").GetBoolean(), doc.RootElement.GetProperty("description").GetString() ?? "", files);
            return Reply(HttpStatusCode.Created, JsonSerializer.Serialize(new { id }));
        }
        if (path.StartsWith("/gists/"))
        {
            var id = path.Split('/')[2];
            if (!Gists.TryGetValue(id, out var g)) return Reply(HttpStatusCode.NotFound, "{}");
            if (req.Method == HttpMethod.Get)
            {
                var files = g.Files.ToDictionary(k => k.Key, k => TruncateFiles
                    ? (object)new { filename = k.Key, truncated = true, raw_url = "https://gist.githubusercontent.com/raw/" + id + "/" + k.Key, content = k.Value[..Math.Min(10, k.Value.Length)] }
                    : new { filename = k.Key, truncated = false, raw_url = "", content = k.Value });
                return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { id, files }));
            }
            if (req.Method == HttpMethod.Patch)
            {
                using var doc = JsonDocument.Parse(body!);
                foreach (var p in doc.RootElement.GetProperty("files").EnumerateObject()) g.Files[p.Name] = p.Value.GetProperty("content").GetString()!;
                return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { id }));
            }
        }
        return Reply(HttpStatusCode.NotFound, "{}");
    }

    static HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
