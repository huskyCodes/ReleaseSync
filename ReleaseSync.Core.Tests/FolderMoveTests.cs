using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace ReleaseSync.Core.Tests;

public sealed class FolderMoveTests : IDisposable
{
    readonly string work = Path.Combine(Path.GetTempPath(), "rs-move-" + Guid.NewGuid().ToString("N"));
    string Src => Path.Combine(work, "old"); string Dst => Path.Combine(work, "new");
    public FolderMoveTests() => Directory.CreateDirectory(Src);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    void Write(string rel, string text = "x", string? root = null)
    {
        var p = Path.Combine(root ?? Src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void The_whole_folder_moves_with_its_contents_and_the_old_folder_goes()
    {
        Write("app.exe", "program"); Write("data/save.dat", "my save"); Directory.CreateDirectory(Path.Combine(Src, "empty"));

        var r = FolderMove.Move(Src, Dst, null);

        Assert.Equal(2, r.Moved); Assert.Equal(0, r.LeftBehind);
        Assert.Equal("program", File.ReadAllText(Path.Combine(Dst, "app.exe")));
        Assert.Equal("my save", File.ReadAllText(Path.Combine(Dst, "data", "save.dat")));
        Assert.True(Directory.Exists(Path.Combine(Dst, "empty")));
        Assert.False(Directory.Exists(Src));
    }

    [Fact]
    public void With_a_list_only_the_apps_files_move_and_the_users_own_files_stay()
    {
        Write("app.exe"); Write("lib/a.dll"); Write("my-notes.txt", "mine"); Write("my-project/code.c", "mine");

        var r = FolderMove.Move(Src, Dst, new[] { "app.exe", "lib/a.dll" });

        Assert.Equal(2, r.Moved);
        Assert.True(File.Exists(Path.Combine(Dst, "app.exe"))); Assert.True(File.Exists(Path.Combine(Dst, "lib", "a.dll")));
        Assert.False(File.Exists(Path.Combine(Src, "app.exe"))); Assert.False(Directory.Exists(Path.Combine(Src, "lib")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Src, "my-notes.txt")));
        Assert.True(File.Exists(Path.Combine(Src, "my-project", "code.c")));
        Assert.False(File.Exists(Path.Combine(Dst, "my-notes.txt")));
    }

    [Fact]
    public void Listed_files_that_are_gone_are_simply_skipped()
    {
        Write("app.exe");
        Assert.Equal(1, FolderMove.Move(Src, Dst, new[] { "app.exe", "not-there.dll" }).Moved);
    }

    [Fact]
    public void If_copying_fails_the_originals_are_untouched_and_the_copies_are_cleaned_up()
    {
        Write("a.txt", "first"); Write("b.txt", "second");
        using var locked = new FileStream(Path.Combine(Src, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.None);      // this one can't be read, as if the app were open

        Assert.ThrowsAny<IOException>(() => FolderMove.Move(Src, Dst, null));

        locked.Dispose();
        Assert.Equal("first", File.ReadAllText(Path.Combine(Src, "a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(Src, "b.txt")));
        Assert.False(Directory.Exists(Dst));
    }

    [Fact]
    public void Cancelling_leaves_everything_where_it_was()
    {
        Write("a.txt", "first");
        using var cts = new CancellationTokenSource(); cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FolderMove.Move(Src, Dst, null, null, cts.Token));

        Assert.Equal("first", File.ReadAllText(Path.Combine(Src, "a.txt")));
        Assert.False(Directory.Exists(Dst));
    }

    [WindowsFact]
    public void An_original_that_cant_be_removed_is_reported_but_the_app_has_still_moved()
    {
        Write("a.txt", "first"); Write("b.txt", "second");
        using var held = new FileStream(Path.Combine(Src, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);      // readable, so it copies, but it can't be deleted

        var r = FolderMove.Move(Src, Dst, null);

        Assert.Equal(1, r.LeftBehind);
        Assert.Equal("second", File.ReadAllText(Path.Combine(Dst, "b.txt")));
    }

    [Fact]
    public void An_empty_existing_folder_is_fine()
    {
        Write("a.txt"); Directory.CreateDirectory(Dst);
        Assert.Null(FolderMove.Problem(Src, Dst));
        FolderMove.Move(Src, Dst, null);
        Assert.True(File.Exists(Path.Combine(Dst, "a.txt")));
    }

    [Fact]
    public void A_folder_with_files_in_it_is_refused_so_nothing_gets_overwritten()
    {
        Write("app.exe", "new"); Write("app.exe", "someone else's", Dst);

        Assert.Contains("already has files", FolderMove.Problem(Src, Dst));
        Assert.Throws<IOException>(() => FolderMove.Move(Src, Dst, null));
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(Dst, "app.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Src, "app.exe")));
    }

    [Fact]
    public void Moving_to_the_same_place_or_inside_itself_is_refused()
    {
        Assert.Contains("already in that folder", FolderMove.Problem(Src, Src + Path.DirectorySeparatorChar));
        Assert.Contains("inside its current folder", FolderMove.Problem(Src, Path.Combine(Src, "sub")));
    }

    [Fact]
    public void A_missing_source_is_refused() => Assert.Contains("can't be found", FolderMove.Problem(Path.Combine(work, "gone"), Dst));

    [Fact]
    public void A_folder_whose_name_only_starts_the_same_is_not_inside_it() =>
        Assert.Null(FolderMove.Problem(Src, Src + "-backup"));

    [Fact]
    public void Paths_inside_the_folder_follow_a_move_and_others_dont()
    {
        Assert.Equal(Path.Combine(Dst, "sub", "app.exe"), FolderMove.Rebase(Path.Combine(Src, "sub", "app.exe"), Src, Dst));
        Assert.Equal(Dst, FolderMove.Rebase(Src, Src, Dst));
        Assert.Null(FolderMove.Rebase(Path.Combine(Src + "-backup", "app.exe"), Src, Dst));
    }

    [WindowsTheory]
    [InlineData(@"C:\Old\app.exe", @"C:\New\app.exe")]
    [InlineData(@"c:\old\sub\app.exe", @"C:\New\sub\app.exe")]            // Windows paths ignore case
    [InlineData(@"C:\Old", @"C:\New")]
    [InlineData(@"C:\Old\", @"C:\New")]
    [InlineData(@"C:\Old\app.exe,0", @"C:\New\app.exe,0")]                // an icon location
    [InlineData(@"C:\Older\app.exe", null)]                                // only the name starts the same
    [InlineData(@"D:\Elsewhere\app.exe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Paths_follow_a_move(string? path, string? expected) => Assert.Equal(expected, FolderMove.Rebase(path, @"C:\Old", @"C:\New"));

    [Fact]
    public void Size_counts_only_what_would_move()
    {
        Write("a.txt", "12345"); Write("b.txt", "123");
        Assert.Equal(8, FolderMove.Size(Src, null));
        Assert.Equal(5, FolderMove.Size(Src, new[] { "a.txt" }));
    }
}
