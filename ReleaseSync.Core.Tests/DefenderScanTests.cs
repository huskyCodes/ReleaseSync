using Xunit;

namespace ReleaseSync.Core.Tests;

public class DefenderScanTests
{
    const string Clean = "Scan starting...\r\nScan finished.\r\nScanning C:\\Temp\\app.exe found no threats.\r\n";
    const string Detected = "Scan starting...\r\nScan finished.\r\nScanning C:\\Temp\\app.exe found 1 threats.\r\n<===========================LIST OF DETECTED THREATS==========================>\r\n----------------------------- Threat information ------------------------------\r\nThreat                  : Virus:DOS/EICAR_Test_File\r\nResources               : 1 total\r\n    file                : C:\\Temp\\app.exe\r\n-------------------------------------------------------------------------------\r\n";

    [Fact]
    public void A_clean_scan_is_clean() => Assert.Equal((DefenderState.Clean, (string?)null), DefenderScan.Read(0, Clean));

    [Fact]
    public void A_detection_gives_the_threat_name() => Assert.Equal((DefenderState.Threat, (string?)"Virus:DOS/EICAR_Test_File"), DefenderScan.Read(2, Detected));

    [Fact]
    public void A_detection_counts_whatever_the_exit_code() => Assert.Equal(DefenderState.Threat, DefenderScan.Read(0, Detected).State);

    [Fact]
    public void A_detection_without_a_name_still_counts() =>
        Assert.Equal((DefenderState.Threat, (string?)null), DefenderScan.Read(2, "Scanning C:\\Temp\\app.exe found 2 threats.\r\n"));

    [Theory]
    [InlineData("[Failed][0x80070002] The system cannot find the file specified.\r\nCmdTool: Failed with hr = 0x80070002. Check C:\\Temp\\MpCmdRun.log for more information\r\n")]
    [InlineData("Scan starting...\r\nCmdTool: Failed with hr = 0x800106BA. Check C:\\Temp\\MpCmdRun.log for more information\r\n")]
    [InlineData("CmdTool: Failed with hr = 0x80508023.")]
    [InlineData("")]
    [InlineData(null)]
    public void A_scan_that_failed_is_not_a_detection(string? output) => Assert.Equal((DefenderState.Unavailable, (string?)null), DefenderScan.Read(2, output));

    [Fact]
    public void Any_other_exit_code_is_not_a_detection() => Assert.Equal(DefenderState.Unavailable, DefenderScan.Read(1, "Something else happened.").State);

    [Fact]
    public void A_detection_reported_in_another_language_still_stops_the_install() =>
        Assert.Equal(DefenderState.Threat, DefenderScan.Read(2, "Analyse terminée.\r\nMenaces détectées : 1\r\n").State);

    [Fact]
    public void A_file_name_cannot_fake_a_detection() =>
        Assert.Equal(DefenderState.Clean, DefenderScan.Read(0, "Scanning C:\\Temp\\found 1 threats.exe found no threats.\r\n").State);
}
