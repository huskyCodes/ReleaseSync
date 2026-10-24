using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ReleaseSync.Core.Tests;

/// <summary>
/// Keeps Fixtures/ in step with the C# engine, and checks the engine against what the fixtures claim about it.
/// To rewrite the files after an intended change: RELEASESYNC_WRITE_FIXTURES=1 dotnet test ReleaseSync.Core.Tests --filter Fixture_files_match
/// </summary>
public class FixtureTests
{
    static string Norm(string s) => s.Replace("\r\n", "\n");

    /// <summary>A document as data: no WrittenBy, group stamps keyed in lower case, objects in a fixed order.</summary>
    static string Canonical(SyncDoc d)
    {
        var c = FixtureData.Clone(d);
        c.WrittenBy = "";
        c.GroupStamps = c.GroupStamps.Select(k => (Key: k.Key.ToLowerInvariant(), k.Value)).OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        c.Tombstones = c.Tombstones.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        c.Devices = c.Devices.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        return FixtureData.Text(c);
    }

    static void Same(string a, string b, string what)
    {
        if (a == b) return;
        var x = a.Split('\n'); var y = b.Split('\n');
        var i = Enumerable.Range(0, Math.Min(x.Length, y.Length)).FirstOrDefault(n => x[n] != y[n], Math.Min(x.Length, y.Length));
        Assert.Fail($"{what}: first difference at line {i + 1}: '{(i < x.Length ? x[i].Trim() : "(end)")}' vs '{(i < y.Length ? y[i].Trim() : "(end)")}'");
    }

    [Fact]
    public void Fixture_files_match_what_the_engine_produces()
    {
        var expected = FixtureData.Build();
        var dir = FixtureData.Dir;
        if (Environment.GetEnvironmentVariable("RELEASESYNC_WRITE_FIXTURES") == "1")
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            foreach (var (path, text) in expected)
            {
                var file = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
            }
            return;
        }

        const string how = " Fixtures are out of date. If the change is intended: RELEASESYNC_WRITE_FIXTURES=1 dotnet test ReleaseSync.Core.Tests --filter Fixture_files_match";
        Assert.True(Directory.Exists(dir), "There is no Fixtures folder." + how);
        var onDisk = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(expected.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(onDisk),
            "Files added or missing: " + string.Join(", ", expected.Keys.Except(onDisk).Concat(onDisk.Except(expected.Keys))) + "." + how);
        foreach (var (path, text) in expected)
            Assert.True(Norm(File.ReadAllText(Path.Combine(dir, path))) == text, path + " no longer matches the engine." + how);
    }

    [Fact]
    public void Building_the_fixtures_twice_gives_the_same_files()
    {
        var first = FixtureData.Build(); var second = FixtureData.Build();
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var (path, text) in first) Assert.True(second[path] == text, path + " changes from one build to the next (a clock or machine value got in)");
    }

    [Fact]
    public void Every_merge_case_is_symmetric_and_repeatable()
    {
        foreach (var c in FixtureData.MergeCases())
        {
            var ab = SyncMerger.Merge(FixtureData.Clone(c.A), FixtureData.Clone(c.B), c.Now);
            var ba = SyncMerger.Merge(FixtureData.Clone(c.B), FixtureData.Clone(c.A), c.Now);
            Same(Canonical(ab), Canonical(ba), c.Name + ": Merge(a,b) vs Merge(b,a)");
            // once a tombstone is forgotten (90 days), an old copy of the deleted item that is merged in again comes back: that is how expiry works
            if (!c.Name.StartsWith("12-"))
            {
                Same(Canonical(SyncMerger.Merge(FixtureData.Clone(ab), FixtureData.Clone(c.A), c.Now)), Canonical(ab), c.Name + ": merging the result with a again");
                Same(Canonical(SyncMerger.Merge(FixtureData.Clone(ab), FixtureData.Clone(c.B), c.Now)), Canonical(ab), c.Name + ": merging the result with b again");
            }
            Same(Canonical(SyncMerger.Merge(FixtureData.Clone(ab), FixtureData.Clone(ab), c.Now)), Canonical(ab), c.Name + ": merging the result with itself");
        }
    }

    [Fact]
    public void Crypto_cases_open_with_the_engine_and_not_with_the_wrong_passphrases()
    {
        foreach (var c in FixtureData.CryptoCases())
        {
            Assert.True(SyncCrypto.IsEncrypted(c.Envelope), c.Name);
            Assert.Equal(Norm(c.Plain), Norm(SyncCrypto.Decrypt(c.Envelope, c.Passphrase)));
            foreach (var wrong in c.Wrong) Assert.Throws<SyncPassphraseException>(() => SyncCrypto.Decrypt(c.Envelope, wrong));
        }
    }

    [Fact]
    public void An_envelope_made_by_the_engine_has_the_documented_shape()
    {
        using var doc = JsonDocument.Parse(SyncCrypto.Encrypt("{}", FixtureData.Pass));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "releasesync-enc", "Kdf", "Iter", "Salt", "Nonce", "Tag", "Data" }, names);
        Assert.Equal(1, doc.RootElement.GetProperty("releasesync-enc").GetInt32());
        Assert.Equal("pbkdf2-sha256", doc.RootElement.GetProperty("Kdf").GetString());
        Assert.Equal(200_000, doc.RootElement.GetProperty("Iter").GetInt32());
        Assert.Equal(16, Convert.FromBase64String(doc.RootElement.GetProperty("Salt").GetString()!).Length);
        Assert.Equal(12, Convert.FromBase64String(doc.RootElement.GetProperty("Nonce").GetString()!).Length);
        Assert.Equal(16, Convert.FromBase64String(doc.RootElement.GetProperty("Tag").GetString()!).Length);
    }
}
