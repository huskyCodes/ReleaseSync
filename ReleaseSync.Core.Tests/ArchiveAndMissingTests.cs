using System.Collections.Generic;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class ArchiveAndMissingTests
{
    static AssetInfo File(string name) => new() { Name = name, Url = "https://example.test/" + name, Size = 1000 };

    [Fact]
    public void A_7z_archive_is_unpacked_like_a_zip()
    {
        Assert.Equal(AssetKind.Zip, AssetPicker.KindOf("COGBox-0.6.0.7z"));
        Assert.Equal(AssetKind.Zip, AssetPicker.KindOf("App-1.0.ZIP"));
    }

    [Fact]
    public void A_release_that_only_ships_a_7z_can_now_be_installed()
    {
        var pick = AssetPicker.Pick(new List<AssetInfo> { File("COGBox-0.6.0.7z"), File("COGBox-0.6.0.7z.sha256") }, null);
        Assert.Equal("COGBox-0.6.0.7z", pick?.Name);
    }

    [Fact]
    public void A_release_with_no_usable_file_says_why_it_cant_be_installed()
    {
        var app = new TrackedApp { Owner = "fake", Repo = "app", InstalledVersion = "1.0", LatestVersion = "2.0", Assets = new List<AssetInfo> { File("app-2.0.dmg"), File("app-2.0.AppImage") } };
        Assert.True(app.HasNoInstallableFile);
        Assert.Contains("app-2.0.dmg", app.NoInstallableFileText);
    }

    [Fact]
    public void A_release_with_a_usable_file_needs_no_explanation()
    {
        var app = new TrackedApp { Owner = "fake", Repo = "app", InstalledVersion = "1.0", LatestVersion = "2.0", Assets = new List<AssetInfo> { File("app-2.0-windows-x64.zip") } };
        Assert.False(app.HasNoInstallableFile);
    }

    [Fact]
    public void A_project_that_cant_be_found_says_so_in_its_status()
    {
        var app = new TrackedApp { Owner = "fake", Repo = "app", InstalledVersion = "v2.1.0", LatestVersion = "v2.1.0", LastError = "gone", RepoMissing = true };
        Assert.Equal("Project not found", app.StatusText);
        app.RepoMissing = false;
        Assert.Equal("Check failed", app.StatusText);
    }
}
