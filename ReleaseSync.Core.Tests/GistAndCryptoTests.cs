using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class GistAndCryptoTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(double minutes) => T0.AddMinutes(minutes);

    static GistTransport Gist(FakeGitHub fake, string? id = null, string token = "test-token") => new(GistTransport.CreateClient(token, fake), id);

    // ---------- encryption ----------

    [Fact]
    public void Crypto_RoundTrips_AndNeverShowsTheContents()
    {
        var env = SyncCrypto.Encrypt("{\"Apps\":[\"neovim/neovim\"]}", "correct horse");
        Assert.True(SyncCrypto.IsEncrypted(env));
        Assert.DoesNotContain("neovim", env);
        Assert.Equal("{\"Apps\":[\"neovim/neovim\"]}", SyncCrypto.Decrypt(env, "correct horse"));
    }

    [Fact]
    public void Crypto_TwoEncryptionsOfTheSameTextDiffer()
    {
        Assert.NotEqual(SyncCrypto.Encrypt("same", "pw"), SyncCrypto.Encrypt("same", "pw"));
    }

    [Fact]
    public void Crypto_WrongOrMissingPassphrase_IsRefused()
    {
        var env = SyncCrypto.Encrypt("secret", "right");
        Assert.Throws<SyncPassphraseException>(() => SyncCrypto.Decrypt(env, "wrong"));
        Assert.Throws<SyncPassphraseException>(() => SyncCrypto.Decrypt(env, ""));
        Assert.Throws<SyncPassphraseException>(() => SyncCrypto.Decrypt(env, null));
    }

    [Fact]
    public void Crypto_TamperedFile_IsRefused()
    {
        var env = SyncCrypto.Encrypt("secret", "pw");
        var tampered = env.Replace("\"Data\": \"", "\"Data\": \"AAAA");
        Assert.ThrowsAny<Exception>(() => SyncCrypto.Decrypt(tampered, "pw"));
    }

    [Fact]
    public void Crypto_PlainJsonIsNotMistakenForEncrypted()
    {
        Assert.False(SyncCrypto.IsEncrypted("{\"Apps\":[]}"));
        Assert.False(SyncCrypto.IsEncrypted(""));
    }

    [Fact]
    public async Task EncryptedSync_WorksBetweenDevicesThatShareThePassphrase_AndLocksOutOthers()
    {
        var cloud = new MemoryTransport();
        var pc = new SimulatedDevice("PC", "Windows", cloud) { Passphrase = "hunter2 hunter2" };
        var mac = new SimulatedDevice("Mac", "Mac", cloud) { Passphrase = "hunter2 hunter2" };
        var stranger = new SimulatedDevice("Other", "Windows", cloud) { Passphrase = "guess" };
        var forgetful = new SimulatedDevice("Phone", "Android", cloud);
        pc.Add("neovim", "neovim", At(0)); await pc.SyncAsync(At(1));
        Assert.DoesNotContain("neovim", cloud.Text);

        await mac.SyncAsync(At(2));
        Assert.Equal(new[] { "github:neovim/neovim" }, mac.AppKeys);
        await Assert.ThrowsAsync<SyncPassphraseException>(() => stranger.SyncAsync(At(3)));
        await Assert.ThrowsAsync<SyncPassphraseException>(() => forgetful.SyncAsync(At(4)));
    }

    [Fact]
    public async Task TurningEncryptionOn_RewritesAnExistingPlainFile()
    {
        var cloud = new MemoryTransport();
        var pc = new SimulatedDevice("PC", "Windows", cloud);
        pc.Add("neovim", "neovim", At(0)); await pc.SyncAsync(At(1));
        Assert.Contains("neovim", cloud.Text);
        pc.Passphrase = "a long passphrase";
        var r = await pc.SyncAsync(At(2));
        Assert.True(r.Pushed);
        Assert.DoesNotContain("neovim", cloud.Text);
    }

    // ---------- gist transport against a fake GitHub ----------

    [Fact]
    public async Task Gist_FirstWriteCreatesASecretGist_AndRemembersItsId()
    {
        var fake = new FakeGitHub(); var t = Gist(fake);
        Assert.Null(await t.ReadAsync());
        await t.WriteAsync("hello");
        var g = Assert.Single(fake.Gists.Values);
        Assert.False(g.Public);
        Assert.Equal("hello", g.Files[GistTransport.FileName]);
        Assert.Equal(g.Id, t.GistId);
        await t.WriteAsync("hello again");
        Assert.Single(fake.Gists);
        Assert.Equal("hello again", await t.ReadAsync());
    }

    [Fact]
    public async Task Gist_AFreshDeviceFindsTheExistingGist_AmongOthers()
    {
        var fake = new FakeGitHub();
        fake.Add("unrelated", "notes.txt", "x", pub: true);
        fake.Add("mine", GistTransport.FileName, "stored");
        var t = Gist(fake);
        Assert.Equal("stored", await t.ReadAsync());
        Assert.Equal("mine", t.GistId);
    }

    [Fact]
    public async Task Gist_LargeFilesAreFetchedInFull()
    {
        var fake = new FakeGitHub { TruncateFiles = true };
        fake.Add("g1", GistTransport.FileName, new string('x', 5000));
        Assert.Equal(5000, (await Gist(fake, "g1").ReadAsync())!.Length);
    }

    [Fact]
    public async Task Gist_TheTokenIsOnlyEverSentToGitHub()
    {
        var fake = new FakeGitHub(); fake.Add("g1", GistTransport.FileName, "x");
        var t = Gist(fake, "g1", "fake-token-for-tests");
        await t.ReadAsync(); await t.WriteAsync("y");
        Assert.All(fake.Calls, c => Assert.StartsWith("https://api.github.com/", c.Url));
        Assert.All(fake.Calls, c => Assert.Equal("Bearer fake-token-for-tests", c.Auth));
        Assert.DoesNotContain(fake.Calls, c => c.Body != null && c.Body.Contains("fake-token-for-tests"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "token")]
    [InlineData(HttpStatusCode.Forbidden, "gist")]
    [InlineData(HttpStatusCode.NotFound, "gist")]
    public async Task Gist_ProblemsAreExplainedInPlainWords(HttpStatusCode code, string mentions)
    {
        var fake = new FakeGitHub { Fail = code };
        var ex = await Assert.ThrowsAsync<SyncException>(() => Gist(fake, "g1").ReadAsync());
        Assert.Contains(mentions, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThreeDevicesSyncThroughOnePrivateGist_Encrypted()
    {
        var fake = new FakeGitHub();
        string? id = null;
        SimulatedDevice Make(string name, string platform)
        {
            var d = new SimulatedDevice(name, platform, Gist(fake, id)) { Passphrase = "correct horse battery" };
            return d;
        }
        var pc = Make("PC", "Windows");
        pc.Add("neovim", "neovim", At(0)); pc.Save("ryujinx", "ryujinx", At(0)); pc.AddGroup("Editors", At(0));
        await pc.SyncAsync(At(1));
        id = fake.Gists.Keys.Single();                   // the id is saved in the PC's settings and shown to set up the others

        var thor = Make("Thor", "Android"); var mac = Make("Mac", "Mac");
        await thor.SyncAsync(At(2)); await mac.SyncAsync(At(3));
        thor.Add("dolphin-emu", "dolphin", At(10)); mac.Remove("neovim", "neovim", At(11));
        await thor.SyncAsync(At(12)); await mac.SyncAsync(At(13)); await pc.SyncAsync(At(14)); await thor.SyncAsync(At(15));

        Assert.Equal(new[] { "github:dolphin-emu/dolphin" }, pc.AppKeys);
        Assert.Equal(pc.Signature, thor.Signature); Assert.Equal(pc.Signature, mac.Signature);
        Assert.DoesNotContain("dolphin", fake.Gists[id].Files[GistTransport.FileName]);
        Assert.Equal(3, pc.Library.Devices.Count);
    }

    [Fact]
    public async Task FolderTransport_RoundTripsThroughARealFile()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = System.IO.Path.Combine(dir, "ReleaseSync", "library.json");
            var a = new SimulatedDevice("PC", "Windows", new FolderTransport(file)); var b = new SimulatedDevice("Mac", "Mac", new FolderTransport(file));
            a.Add("x", "y", At(0)); await a.SyncAsync(At(1)); await b.SyncAsync(At(2));
            Assert.Equal(new[] { "github:x/y" }, b.AppKeys);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
