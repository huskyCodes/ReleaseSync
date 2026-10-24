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
    const string SwiftWritten = "swift-written";

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
            if (Directory.Exists(dir))
                foreach (var entry in new DirectoryInfo(dir).GetFileSystemInfos().Where(e => e.Name != SwiftWritten))     // those are written by the Swift tests
                    if (entry is DirectoryInfo sub) sub.Delete(true); else entry.Delete();
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
        var onDisk = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(f => !f.StartsWith(SwiftWritten + "/")).OrderBy(x => x, StringComparer.Ordinal).ToList();
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

    static SyncDoc ReadDoc(string text) => JsonSerializer.Deserialize<SyncDoc>(text, Store.Options)!;

    /// The Swift tests write these (ReleaseSyncKit, RELEASESYNC_WRITE_SWIFT_FIXTURES=1). Here the C# engine reads them back.
    [Fact]
    public void Merge_results_written_by_Swift_read_the_same_in_the_engine()
    {
        var swiftDir = Path.Combine(FixtureData.Dir, SwiftWritten);
        Assert.True(Directory.Exists(swiftDir), "Fixtures/swift-written is missing. Run: RELEASESYNC_WRITE_SWIFT_FIXTURES=1 swift test --filter SwiftWrittenFixtureTests (in ReleaseSyncKit)");
        foreach (var c in FixtureData.MergeCases())
        {
            var fromSwift = ReadDoc(File.ReadAllText(Path.Combine(swiftDir, "merge", c.Name + ".json")));
            var byEngine = SyncMerger.Merge(FixtureData.Clone(c.A), FixtureData.Clone(c.B), c.Now);
            Same(Canonical(fromSwift), Canonical(byEngine), c.Name + ": Swift's merge vs the engine's");
            if (!c.Name.StartsWith("12-"))      // merging what Swift wrote with either input again changes nothing
            {
                Same(Canonical(SyncMerger.Merge(FixtureData.Clone(fromSwift), FixtureData.Clone(c.A), c.Now)), Canonical(fromSwift), c.Name + ": Swift's result with a");
                Same(Canonical(SyncMerger.Merge(FixtureData.Clone(fromSwift), FixtureData.Clone(c.B), c.Now)), Canonical(fromSwift), c.Name + ": Swift's result with b");
            }
        }
    }

    [Fact]
    public void The_locked_file_written_by_Swift_opens_in_the_engine()
    {
        var swiftDir = Path.Combine(FixtureData.Dir, SwiftWritten, "crypto");
        var envelope = File.ReadAllText(Path.Combine(swiftDir, "envelope.json"));
        Assert.True(SyncCrypto.IsEncrypted(envelope));
        var plain = SyncCrypto.Decrypt(envelope, FixtureData.Pass);
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(swiftDir, "plaintext.json"))), Norm(plain));
        Assert.NotEmpty(ReadDoc(plain).Apps);
        Assert.Throws<SyncPassphraseException>(() => SyncCrypto.Decrypt(envelope, "not it"));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_sync_round_in_the_engine_accepts_a_file_written_by_Swift()
    {
        var text = File.ReadAllText(Path.Combine(FixtureData.Dir, SwiftWritten, "merge", "02-union-and-ordering.json"));
        var cloud = new MemoryTransport(); await cloud.WriteAsync(text);
        var pc = new SimulatedDevice("PC", "Windows", cloud);
        var result = await pc.SyncAsync(FixtureData.T0.AddHours(1));
        Assert.True(result.Changed);
        Assert.Equal(9, pc.Library.Apps.Count);
        Assert.Contains("github:fake/a_b", pc.AppKeys);
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
