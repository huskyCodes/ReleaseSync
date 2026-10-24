using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ReleaseSync.Core.Tests;

/// <summary>Keeps these tests away from the real data folder.</summary>
static class TestDataFolder
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("RELEASESYNC_DATA", Path.Combine(Path.GetTempPath(), "rs-core-tests-" + Guid.NewGuid().ToString("N")));
}

public sealed class InstallManifestTests : IDisposable
{
    readonly string work = Path.Combine(Path.GetTempPath(), "rs-manifest-" + Guid.NewGuid().ToString("N"));
    public InstallManifestTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    void Write(string rel, string text = "x")
    {
        var p = Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    string MakeZip(params string[] entries)
    {
        var path = Path.Combine(work, "release-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries) { using var w = new StreamWriter(z.CreateEntry(e).Open()); w.Write("x"); }
        return path;
    }

    [Fact]
    public void RemovesOnlyTheListedFiles_AndLeavesTheUsersOwnFiles()
    {
        // a folder the user chose, shared with their own work
        Write("app/game.exe"); Write("app/lib/a.dll"); Write("app/lib/deep/b.dll"); Write("app/readme.md");
        Write("app/mywork/notes.txt"); Write("app/zig/compiler.exe"); Write("app/save.dat");
        var dir = Path.Combine(work, "app");

        var r = InstallManifest.RemoveListed(dir, new[] { "game.exe", "lib/a.dll", "lib/deep/b.dll", "readme.md" });

        Assert.Equal(4, r.Removed); Assert.Equal(0, r.Failed); Assert.Equal(3, r.LeftBehind);
        Assert.False(File.Exists(Path.Combine(dir, "game.exe")));
        Assert.False(Directory.Exists(Path.Combine(dir, "lib")));                 // emptied folders go
        Assert.True(File.Exists(Path.Combine(dir, "mywork", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "zig", "compiler.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "save.dat")));
    }

    [Fact]
    public void FoldersThatStillHoldOtherFiles_AreKept()
    {
        Write("app/lib/a.dll"); Write("app/lib/mine.cfg");
        InstallManifest.RemoveListed(Path.Combine(work, "app"), new[] { "lib/a.dll" });
        Assert.True(File.Exists(Path.Combine(work, "app", "lib", "mine.cfg")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/notepad.exe")]
    [InlineData("")]
    public void NeverTouchesAnythingOutsideTheFolder(string evil)
    {
        Write("outside.txt"); Write("app/keep.txt");
        var r = InstallManifest.RemoveListed(Path.Combine(work, "app"), new[] { evil });
        Assert.Equal(0, r.Removed);
        Assert.True(File.Exists(Path.Combine(work, "outside.txt")));
        Assert.True(File.Exists(Path.Combine(work, "app", "keep.txt")));
    }

    [Fact]
    public void ListZip_DropsASingleTopLevelFolder_LikeInstallingDoes()
    {
        var wrapped = MakeZip("Game-1.0/game.exe", "Game-1.0/lib/a.dll");
        Assert.Equal(new[] { "game.exe", "lib/a.dll" }, InstallManifest.ListZip(wrapped).OrderBy(x => x));
        var flat = MakeZip("game.exe", "lib/a.dll");
        Assert.Equal(new[] { "game.exe", "lib/a.dll" }, InstallManifest.ListZip(flat).OrderBy(x => x));
        var two = MakeZip("A/x.txt", "B/y.txt");
        Assert.Equal(new[] { "A/x.txt", "B/y.txt" }, InstallManifest.ListZip(two).OrderBy(x => x));
    }

    [Fact]
    public void ListZip_IgnoresUnsafeEntries()
    {
        var zip = MakeZip("ok.txt", "../escape.txt");
        Assert.Equal(new[] { "ok.txt" }, InstallManifest.ListZip(zip));
    }

    [Fact]
    public void ListDirectory_MatchesWhatWasUnpacked()
    {
        Write("staged/game.exe"); Write("staged/lib/a.dll");
        Assert.Equal(new[] { "game.exe", "lib/a.dll" }, InstallManifest.ListDirectory(Path.Combine(work, "staged")).OrderBy(x => x));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_KeepOlderFiles_AndIgnoreAnotherFolder()
    {
        var dir = Path.Combine(work, "app"); Directory.CreateDirectory(dir);
        var id = "app" + Guid.NewGuid().ToString("N");
        InstallManifest.Save(id, dir, new[] { "game.exe", "old.dll" });
        InstallManifest.Save(id, dir, new[] { "game.exe", "new.dll" });          // an update that dropped old.dll
        Assert.Equal(new[] { "game.exe", "new.dll", "old.dll" }, InstallManifest.Load(id, dir)!.OrderBy(x => x));
        Assert.Null(InstallManifest.Load(id, Path.Combine(work, "elsewhere")));   // the app was moved: the list no longer applies
        InstallManifest.Forget(id);
        Assert.Null(InstallManifest.Load(id, dir));
    }
}
