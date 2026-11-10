using System;
using System.Collections.Generic;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class AppVersionTests
{
    static TrackedApp Hookshot(string installed) => new()
    {
        Owner = "fake", Repo = "app", InstalledVersion = installed, LatestVersion = "v2.0.11", LatestPublished = new DateTime(2026, 11, 9, 19, 9, 54, DateTimeKind.Utc),
        Releases = new List<ReleaseSummary>
        {
            new() { Tag = "v2.0.11", Published = new DateTime(2026, 11, 9, 19, 9, 54, DateTimeKind.Utc) },
            new() { Tag = "v2.0.10", Published = new DateTime(2026, 11, 9, 17, 32, 4, DateTimeKind.Utc) },
            new() { Tag = "v2.0.3", Published = new DateTime(2026, 11, 8, 19, 24, 38, DateTimeKind.Utc) },
        }
    };

    [Fact]
    public void A_version_left_by_an_app_updating_itself_can_still_be_updated_without_a_rollback_warning()
    {
        var app = Hookshot("honest-eta");                       // a tag that was never one of the saved releases
        Assert.True(app.HasUpdate);
        Assert.False(app.IsNewer("honest-eta", "v2.0.11"));      // installing v2.0.11 isn't a rollback
    }

    [Fact]
    public void After_updating_the_update_is_gone()
    {
        Assert.False(Hookshot("v2.0.11").HasUpdate);
    }

    [Fact]
    public void An_older_listed_version_still_counts_as_a_rollback()
    {
        var app = Hookshot("v2.0.11");
        Assert.True(app.IsNewer("v2.0.11", "v2.0.3"));
    }

    [Theory]
    [InlineData("honest-eta", "v2.0.11")]
    [InlineData("nightly", "1.4")]
    public void Without_dates_a_version_number_outranks_a_label_and_never_both_ways(string label, string version)
    {
        Assert.True(VersionUtil.IsNewer(version, label));
        Assert.False(VersionUtil.IsNewer(label, version));
    }
}
