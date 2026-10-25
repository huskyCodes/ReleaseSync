using System;
using System.Linq;
using System.Text;
using Xunit;

namespace ReleaseSync.Core.Tests;

/// <summary>Expected values here are typed by hand from docs/LAN-PRESENCE.md, not produced by the code under test.</summary>
public class LanMessagesTests
{
    [Fact]
    public void Hello_is_written_compact_in_the_spec_order()
    {
        var text = LanMessages.SerializeToString(new LanHello { DeviceId = "0123456789abcdef0123456789abcdef", Name = "Example", Platform = "Mac", AppVersion = "1.0" });
        Assert.Equal("{\"Type\":\"hello\",\"Proto\":1,\"DeviceId\":\"0123456789abcdef0123456789abcdef\",\"Name\":\"Example\",\"Platform\":\"Mac\",\"AppVersion\":\"1.0\"}", text);
    }

    [Fact]
    public void Text_is_escaped_like_the_sync_file()
    {
        var text = LanMessages.SerializeToString(new LanHello { Name = "é<&'+😀", Platform = "P", AppVersion = "1" });
        Assert.Contains("\"Name\":\"\\u00E9\\u003C\\u0026\\u0027\\u002B\\uD83D\\uDE00\"", text);
    }

    [Fact]
    public void A_property_with_no_value_is_left_out_and_false_and_empty_are_written()
    {
        Assert.Equal("{\"Type\":\"goodbye\"}", LanMessages.SerializeToString(new LanGoodbye()));
        Assert.Equal("{\"Type\":\"state\",\"Seq\":0,\"Open\":false,\"Installed\":[]}", LanMessages.SerializeToString(new LanState()));
    }

    [Fact]
    public void Times_are_utc_with_a_z_and_no_trailing_zeros()
    {
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string Written(DateTime when) => LanMessages.SerializeToString(new LanState { Seq = 1, Installed = { new LanInstalled { Key = "k", Version = "1", InstalledAt = when } } });
        Assert.Contains("\"InstalledAt\":\"2026-01-02T03:04:05Z\"", Written(t));
        Assert.Contains("\"InstalledAt\":\"2026-01-02T03:04:05.12Z\"", Written(t.AddMilliseconds(120)));
        Assert.Contains("\"InstalledAt\":\"2026-01-02T03:04:05.1234567Z\"", Written(t.AddTicks(1234567)));
    }

    [Fact]
    public void A_time_with_an_offset_is_read_as_the_same_moment_in_utc_whatever_this_computers_zone_is()
    {
        var m = (LanState)LanMessages.Parse(Encoding.UTF8.GetBytes("{\"Type\":\"state\",\"Installed\":[{\"Key\":\"k\",\"Version\":\"1\",\"InstalledAt\":\"2026-01-02T05:04:05+02:00\"}]}"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), m.Installed[0].InstalledAt);
        Assert.Equal(DateTimeKind.Utc, m.Installed[0].InstalledAt!.Value.Kind);
    }

    [Theory]
    [InlineData(22, 21)]      // 3-byte characters: 66 bytes, the cut at 64 would fall inside the 22nd, so 21 are kept
    [InlineData(21, 21)]      // exactly 63 bytes
    public void Free_text_is_cut_between_characters(int sent, int kept)
    {
        var s = LanMessages.Shorten(string.Concat(Enumerable.Repeat("€", sent)), 64);
        Assert.Equal(kept, s.Length);
        Assert.True(Encoding.UTF8.GetByteCount(s) <= 64);
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_never_split()
    {
        var s = LanMessages.Shorten("ab😀", 5);           // a, b, then 4 bytes: only 3 left, so the emoji is dropped whole
        Assert.Equal("ab", s);
        Assert.Equal("ab😀", LanMessages.Shorten("ab😀", 6));
    }

    [Fact]
    public void The_installed_list_is_cleaned_as_section_11_says()
    {
        var list = LanMessages.Inventory(new[]
        {
            new LanInstalled { Key = "github:b", Version = "1" }, new LanInstalled { Key = "", Version = "1" }, new LanInstalled { Key = "github:a", Version = "1" },
            new LanInstalled { Key = "github:b", Version = "2" },                                                    // same key again: the last one wins
            new LanInstalled { Key = new string('k', 201), Version = "1" },                                           // key too long: skipped
            new LanInstalled { Key = "github:c", Version = new string('1', 65) },                                     // version too long: skipped
            new LanInstalled { Key = "github:d", Version = "1", Latest = new string('2', 65) },                       // latest too long: skipped
        });
        Assert.Equal(new[] { "github:a", "github:b" }, list.Select(i => i.Key));
        Assert.Equal("2", list.Single(i => i.Key == "github:b").Version);
    }

    [Fact]
    public void Only_the_first_thousand_entries_in_key_order_are_kept()
    {
        var many = Enumerable.Range(0, 1002).Select(n => new LanInstalled { Key = $"github:fake/app-{n:D4}", Version = "1" }).Reverse();
        var list = LanMessages.Inventory(many);
        Assert.Equal(1000, list.Count);
        Assert.Equal("github:fake/app-0000", list[0].Key);
        Assert.Equal("github:fake/app-0999", list[^1].Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{\"N\":1}")]
    [InlineData("{\"Type\":\"ping\",\"N\":\"1\"}")]
    [InlineData("{\"Type\":\"ping\",\"N\":1} x")]
    public void Bad_messages_are_errors(string text) =>
        Assert.Throws<LanProtocolException>(() => LanMessages.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void An_unknown_type_is_ignored_not_an_error() =>
        Assert.IsType<LanUnknown>(LanMessages.Parse(Encoding.UTF8.GetBytes("{\"Type\":\"suggest\",\"Key\":\"github:x/y\"}")));

    [Fact]
    public void A_message_over_the_record_limit_is_refused() =>
        Assert.Throws<LanProtocolException>(() => LanMessages.Parse(new byte[LanLimits.RecordPlaintext + 1]));
}
