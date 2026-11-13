using System.IO;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class SelfUpdateTests
{
    const string Notes = "**To install:** download `ReleaseSync.exe` below and open it.\r\n\r\n**New in this version**\r\n\r\n- One **big** thing\r\n- See [the guide](https://example.test/guide) now\r\n\r\nSee the [project status](https://example.test#status) for more.\r\n\r\n**Full Changelog**: https://example.test/compare";

    [Fact]
    public void Only_the_list_of_changes_is_kept()
    {
        Assert.Equal("• One big thing\n• See the guide now", SelfUpdate.Changes(Notes));
    }

    [Fact]
    public void Notes_in_another_shape_are_still_shown_without_the_changelog_link()
    {
        Assert.Equal("Fixed a crash.", SelfUpdate.Changes("Fixed a crash.\n\n**Full Changelog**: https://example.test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("**Full Changelog**: https://example.test")]
    public void No_notes_says_so(string? notes) => Assert.Equal("No list of changes was published for this version.", SelfUpdate.Changes(notes));

    [Fact]
    public void Several_versions_are_each_headed_by_their_version()
    {
        var text = SelfUpdate.ChangesSince(new[] { ("v1.12.0", (string?)"Newest."), ("v1.11.0", "Older.") });
        Assert.Equal("v1.12.0\nNewest.\n\nv1.11.0\nOlder.", text);
    }

    [Fact]
    public void One_version_needs_no_heading() => Assert.Equal("Only one.", SelfUpdate.ChangesSince(new[] { ("v2", (string?)"Only one.") }));

    [Theory]
    [InlineData("sha256:FDCB3553746B023ADF45571DCB4B686A6AAC2E2A51C129C39E1CD64E48C96852")]
    [InlineData("fdcb3553746b023adf45571dcb4b686a6aac2e2a51c129c39e1cd64e48c96852  ReleaseSync.exe\n")]
    public void A_checksum_is_read_from_either_form(string text) =>
        Assert.Equal("fdcb3553746b023adf45571dcb4b686a6aac2e2a51c129c39e1cd64e48c96852", SelfUpdate.Sha256From(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:abcd")]
    [InlineData("sha256:tooshort")]
    public void Anything_else_is_not_a_checksum(string? text) => Assert.Null(SelfUpdate.Sha256From(text));

    [Theory]
    [InlineData("https://github.com/fake/app/releases/tag/v1.2.3", "v1.2.3")]
    [InlineData("/fake/app/releases/tag/1.0", "1.0")]
    [InlineData("https://github.com/fake/app/releases/tag/v1.0%2Bbuild", "v1.0+build")]
    [InlineData("https://github.com/fake/app/releases", null)]        // a project with no releases redirects here
    [InlineData("https://github.com/login", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_version_is_read_from_the_latest_release_redirect(string? location, string? expected) => Assert.Equal(expected, SelfUpdate.TagFromRedirect(location));

    [Fact]
    public void A_file_must_match_its_checksum()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "abc");
            Assert.True(SelfUpdate.FileMatches(file, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));      // the standard SHA-256 test value for "abc"
            Assert.False(SelfUpdate.FileMatches(file, new string('0', 64)));
        }
        finally { File.Delete(file); }
    }
}
