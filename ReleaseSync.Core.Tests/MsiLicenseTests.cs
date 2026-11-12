using Xunit;

namespace ReleaseSync.Core.Tests;

public class MsiLicenseTests
{
    [Theory]
    [InlineData("MSI (s) (E8:C0) [15:55:04:864]: Product: VeraCrypt 1.26.31 -- Please accept the license agreement by passing ACCEPTLICENSE=YES during installation.", "ACCEPTLICENSE=YES")]
    [InlineData("Please accept the EULA by passing accept_eula=1 on the command line.", "ACCEPT_EULA=1")]
    [InlineData("Install by passing LICENSEACCEPTED = TRUE", "LICENSEACCEPTED=TRUE")]
    public void The_setting_an_installer_asks_for_is_read_from_its_own_message(string log, string expected) => Assert.Equal(expected, MsiLog.LicenseSetting(log));

    [Theory]
    [InlineData("Property(S): ACCEPTLICENSE = NO")]                      // a property alone isn't the installer asking
    [InlineData("Error 1603. A fatal error occurred during installation.")]
    [InlineData("Pass INSTALLDIR=C:\\Apps to choose a folder.")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_is_guessed_when_the_installer_asks_for_nothing(string? log) => Assert.Null(MsiLog.LicenseSetting(log));
}
