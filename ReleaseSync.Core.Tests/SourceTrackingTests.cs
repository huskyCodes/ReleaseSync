using System;
using System.Collections.Generic;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class SourceTrackingTests
{
    static TrackedApp Following(string installed, string latest, DateTime latestDate) => new()
    {
        Owner = "fake", Repo = "app", TrackSource = true, InstalledVersion = installed, LatestVersion = latest, LatestPublished = latestDate,
        Releases = new List<ReleaseSummary> { new() { Tag = latest, Published = latestDate } },
        Assets = new List<AssetInfo> { new() { Name = "app-main-5397c0a.zip", Url = "https://example.test/archive/5397c0a.zip" } },
    };

    [Fact]
    public void A_newer_commit_is_an_update()
    {
        Assert.True(Following("main@1a2b3c4", "main@5397c0a", new DateTime(2026, 11, 10)).HasUpdate);
    }

    [Fact]
    public void The_same_commit_is_not_an_update()
    {
        Assert.False(Following("main@5397c0a", "main@5397c0a", new DateTime(2026, 11, 10)).HasUpdate);
    }

    [Fact]
    public void Installing_the_newest_commit_isnt_called_a_rollback()
    {
        var app = Following("main@1a2b3c4", "main@5397c0a", new DateTime(2026, 11, 10));
        Assert.False(app.IsNewer("main@1a2b3c4", "main@5397c0a"));
    }

    [Fact]
    public void The_source_download_can_be_installed()
    {
        Assert.Equal(AssetKind.Zip, AssetPicker.KindOf("app-main-5397c0a.zip"));
        Assert.NotNull(Following("main@1a2b3c4", "main@5397c0a", DateTime.UtcNow).ChosenAsset);
    }

    [Fact]
    public void Source_code_is_labelled_as_source_code_everywhere()
    {
        var app = Following("main@1a2b3c4", "main@5397c0a", new DateTime(2026, 11, 10));
        Assert.Equal("Update source", app.ActionText);
        Assert.Equal("Open folder", app.LaunchText);
        Assert.Equal("New source code", app.StatusText);
        Assert.True(app.CanLaunch);                                   // opens the folder: there's no program file to need
        Assert.Contains("main branch", app.SourceNoteText);
        Assert.Contains("nothing is built for you", app.SourceNoteText);
        Assert.Equal("Latest commit", app.LatestLabel);
        Assert.Equal("Download source again", app.ReinstallText);
        Assert.StartsWith("Committed ", app.PublishedLine);
        app.InstalledVersion = app.LatestVersion;
        Assert.Equal("Latest source", app.StatusText);
        app.InstalledVersion = null;
        Assert.Equal("Download source", app.ActionText);
        Assert.False(app.CanLaunch);
    }

    [Fact]
    public void A_normal_app_keeps_its_usual_labels()
    {
        var app = new TrackedApp { Owner = "fake", Repo = "app", InstalledVersion = "1.0", LatestVersion = "2.0" };
        Assert.Equal("Update", app.ActionText);
        Assert.Equal("Launch", app.LaunchText);
        Assert.Equal("Update available", app.StatusText);
        Assert.Equal("Latest release", app.LatestLabel);
        Assert.Equal("Reinstall", app.ReinstallText);
        Assert.StartsWith("Released ", app.PublishedLine);
    }

    [Fact]
    public void Only_a_project_with_no_releases_offers_to_follow_its_source()
    {
        var none = new TrackedApp { Owner = "fake", Repo = "app", LastError = "No releases have been published for this repository. You can follow its source code instead." };
        Assert.True(none.CanFollowSource);
        none.TrackSource = true;
        Assert.False(none.CanFollowSource);
        Assert.False(new TrackedApp { Owner = "fake", Repo = "app", LastError = "Can't reach GitHub." }.CanFollowSource);
    }
}
