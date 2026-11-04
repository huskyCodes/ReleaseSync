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
    public void No_log_still_explains_what_to_try()
    {
        var text = MsiLog.Explain("DeckCraft", 1603, null);
        Assert.Contains("error 1603", text);
        Assert.Contains("Close the app", text);
    }
}
