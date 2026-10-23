using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace ReleaseSync.Core.Tests;

/// <summary>
/// Talks to the real GitHub. Does nothing unless RELEASESYNC_LIVE_GIST_TOKEN is set to a token with the gist permission.
/// It creates one secret gist, syncs two simulated devices through it, and deletes it again.
/// </summary>
public class LiveGistTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(double minutes) => T0.AddMinutes(minutes);

    [Fact]
    public async Task TwoDevicesSyncThroughARealSecretGist()
    {
        var token = Environment.GetEnvironmentVariable("RELEASESYNC_LIVE_GIST_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return;

        using var http = GistTransport.CreateClient(token);
        var pcTransport = new GistTransport(http, null);
        string? id = null;
        try
        {
            var pc = new SimulatedDevice("PC", "Windows", pcTransport) { Passphrase = "live test passphrase" };
            pc.Add("example", "live-test", At(0)); pc.Save("example", "later", At(0));
            await pc.SyncAsync(At(1));
            id = pcTransport.GistId;
            Assert.False(string.IsNullOrEmpty(id));

            // a second device that only knows the token finds the gist on its own
            var mac = new SimulatedDevice("Mac", "Mac", new GistTransport(http, null)) { Passphrase = "live test passphrase" };
            await mac.SyncAsync(At(2));
            Assert.Equal(new[] { "github:example/live-test" }, mac.AppKeys);
            Assert.Equal(new[] { "github:example/later" }, mac.LaterKeys);

            mac.Remove("example", "live-test", At(10)); await mac.SyncAsync(At(11)); await pc.SyncAsync(At(12));
            Assert.Empty(pc.AppKeys);
        }
        finally
        {
            id ??= pcTransport.GistId;
            if (id != null)
            {
                using var del = await http.DeleteAsync("gists/" + id);
                Assert.True(del.IsSuccessStatusCode, "the test gist could not be deleted: " + del.StatusCode);
            }
        }
    }
}
