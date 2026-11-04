using Xunit;

namespace ReleaseSync.Core.Tests;

public class MsiLogTests
{
    [Theory]
    [InlineData("MSI (s) Note: 1: 1925", "administrator")]            // a bare number in a verbose log is not an error
    [InlineData("Process 4551 is being used by the installer", "Windows blocked")]
    public void An_unrecognised_log_still_gives_a_useful_sentence(string log, string notExpected)
    {
        var text = MsiLog.Explain("DeckCraft", 1603, log);
        Assert.DoesNotContain(notExpected, text);
        Assert.Contains("DeckCraft", text);
        Assert.Contains("1603", text);
    }

    [Theory]
    [InlineData("Error 1925. You do not have sufficient privileges to complete this installation for all users.", "administrator")]
    [InlineData("Error 1730. You must be an Administrator to remove this application.", "administrator")]
    [InlineData("Error 1714. The older version of DeckCraft cannot be removed.", "Uninstall it")]
    [InlineData("Error 1306. Another application has exclusive access to the file", "in use")]
    [InlineData("Error 1722. There is a problem with this Windows Installer package.", "own installer")]
    [InlineData("CustomAction failed with 0x800711C7", "Windows blocked")]
    [InlineData("Out of disk space - Volume: C:", "disk space")]
    public void The_real_reason_is_found_in_the_log(string log, string expected) =>
        Assert.Contains(expected, MsiLog.Explain("DeckCraft", 1603, "line before\n" + log + "\nline after"));

    [Fact]
    public void Being_blocked_by_Windows_wins_over_a_generic_custom_action_error() =>
        Assert.Contains("Windows blocked", MsiLog.Explain("X", 1603, "Error 1722.\nfailed with 0x800711C7"));

    [Theory]
    [InlineData(1618, "another installer")]
    [InlineData(1638, "already installed")]
    [InlineData(1925, "administrator")]
    public void Some_codes_need_no_log(int code, string expected) => Assert.Contains(expected, MsiLog.Explain("X", code, null));

    [Fact]
    public void An_installers_own_message_is_shown_as_it_is()
    {
        var log = "MSI (s): Product: FakeApp -- This app is already installed for everyone. Choose Everyone to update it.\r\n\r\nAction ended: LaunchConditions. Return value 3.\r\nMSI (s): Product: FakeApp -- Installation failed.\r\n";
        Assert.Equal("FakeApp couldn't be installed: This app is already installed for everyone. Choose Everyone to update it.", MsiLog.Explain("FakeApp", 1603, log));
    }

    [Theory]
    [InlineData("Product: FakeApp -- Installation failed.")]
    [InlineData("Product: FakeApp -- Installation completed successfully.")]
    public void The_generic_result_lines_are_not_a_reason(string log) => Assert.Contains("error 1603", MsiLog.Explain("FakeApp", 1603, log));

    [Fact]
    public void A_numbered_error_is_still_explained_in_plain_words() =>
        Assert.Contains("administrator", MsiLog.Explain("FakeApp", 1603, "Product: FakeApp -- Error 1925. You do not have sufficient privileges"));

    [Theory]
    [InlineData("LocalMachine|Registry64|{6D0C3F9E}", null, true)]
    [InlineData("LocalMachine|Registry32|Something", "/foo", true)]
    [InlineData("CurrentUser|Registry64|Something", null, false)]      // installed just for this user: leave it that way
    [InlineData(null, null, false)]                                    // a first install: the installer's own default
    [InlineData("LocalMachine|Registry64|X", "ALLUSERS=2", false)]     // the user set it themselves
    public void An_update_matches_how_the_old_copy_was_installed(string? id, string? args, bool expected) =>
        Assert.Equal(expected, MsiLog.NeedsAllUsers(id, args));

    [Fact]
    public void No_log_still_explains_what_to_try()
    {
        var text = MsiLog.Explain("DeckCraft", 1603, null);
        Assert.Contains("error 1603", text);
        Assert.Contains("Close the app", text);
    }
}
