using System;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class VersionUtilTests
{
    static readonly DateTime Older = new(2026, 11, 1), Newer = new(2026, 11, 5);

    [Theory]
    [InlineData("v1.2.4", "v1.2.3", true)]
    [InlineData("v1.2.3", "v1.2.4", false)]
    [InlineData("v1.10.0", "v1.9.0", true)]
    [InlineData("v0.4.6", "0.4.6", false)]
    [InlineData("v1.22.0", "1.21.442", true)]
    [InlineData("launcher@0.2.45", "launcher@0.2.31", true)]
    [InlineData("v0.1.9.3", "v0.1.9.1", true)]
    [InlineData("v2", "v1.5", true)]
    [InlineData("v1.5", "v2", false)]
    public void Real_version_numbers_compare_as_numbers(string latest, string installed, bool expected) => Assert.Equal(expected, VersionUtil.IsNewer(latest, installed));

    [Theory]
    [InlineData("v1.2.3", "v1.2.3-rc1", true)]
    [InlineData("v1.2.3-rc1", "v1.2.3", false)]
    public void A_release_beats_its_own_pre_release(string latest, string installed, bool expected) => Assert.Equal(expected, VersionUtil.IsNewer(latest, installed));

    [Theory]
    [InlineData("build-83", "build-75", true)]
    [InlineData("build-75", "build-83", false)]
    [InlineData("nx2-build-34", "nx2-build-25", true)]
    [InlineData("nx2-build-25", "nx2-build-34", false)]
    [InlineData("daily-20261106", "daily-20261105", true)]
    public void Build_labels_of_one_style_compare_by_all_their_numbers(string latest, string installed, bool expected) => Assert.Equal(expected, VersionUtil.IsNewer(latest, installed));

    [Fact]
    public void Across_styles_the_publish_dates_decide()
    {
        Assert.True(VersionUtil.IsNewer("nx2-build-90", "build-83", Newer, Older));
        Assert.False(VersionUtil.IsNewer("nx2-build-34", "build-83", Older, Newer));
        Assert.True(VersionUtil.IsNewer("daily-20261105", "build-212", Newer, Older));
        Assert.False(VersionUtil.IsNewer("build-212", "daily-20261105", Older, Newer));
    }

    [Theory]
    [InlineData("nx2-build-90", "build-83")]
    [InlineData("latest", "stable")]
    public void Across_styles_with_no_dates_a_difference_is_an_update(string latest, string installed) => Assert.True(VersionUtil.IsNewer(latest, installed));

    [Theory]
    [InlineData("branch-fix-oot-blackscreen-17ac56ed", true)]
    [InlineData("branch-switchbrew-names-736c1d29", true)]
    [InlineData("pr-1234", true)]
    [InlineData("feature/new-ui", true)]
    [InlineData("daily-20261105", false)]
    [InlineData("build-83", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("branchless-1.0", false)]
    public void Throwaway_builds_are_recognized(string tag, bool expected) => Assert.Equal(expected, VersionUtil.IsOneOff(tag));

    [Fact]
    public void The_same_version_is_never_newer()
    {
        Assert.False(VersionUtil.IsNewer("build-83", "build-83"));
        Assert.False(VersionUtil.IsNewer("v1.0", "1.0", Newer, Older));
    }
}
