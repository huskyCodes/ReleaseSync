using System.Text.RegularExpressions;

namespace ReleaseSync.Core;

public enum DefenderState { NotRun, Clean, Threat, Unavailable }

/// <summary>Reads the result of a Windows Security command-line scan. Exit code 2 means a detection but also any failed scan, such as when another antivirus has replaced Windows Security, so it can't be trusted alone.</summary>
public static class DefenderScan
{
    public static (DefenderState State, string? Threat) Read(int exitCode, string? output)
    {
        var text = output ?? "";
        var name = Regex.Match(text, @"^\s*Threat\s*:\s*(?<n>[^\r\n]+)", RegexOptions.Multiline);
        if (name.Success) return (DefenderState.Threat, name.Groups["n"].Value.Trim());
        if (Regex.IsMatch(text, @"found\s+[1-9]\d*\s+threats?\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)) return (DefenderState.Threat, null);
        if (exitCode == 0) return (DefenderState.Clean, null);
        // a failure always carries an error code; anything else with text may be a detection on a Windows in another language, so it still stops the install
        if (exitCode == 2 && text.Trim().Length > 0 && !Regex.IsMatch(text, @"\b0x8[0-9a-f]{7}\b", RegexOptions.IgnoreCase)) return (DefenderState.Threat, null);
        return (DefenderState.Unavailable, null);
    }
}
