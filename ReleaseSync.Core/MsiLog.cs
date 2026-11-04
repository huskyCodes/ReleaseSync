using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReleaseSync.Core;

/// <summary>Turns a failed Windows Installer run into a reason a person can act on. Error 1603 only means "it failed"; the real reason is in the log.</summary>
public static class MsiLog
{
    static readonly (string Pattern, string Reason)[] Reasons =
    {
        (@"0x800711C7|Application Control policy|Smart App Control", "Windows blocked part of this installer (Smart App Control or a similar protection)."),
        (@"Error 1925|Error 1730|insufficient privileges|You must be an Administrator|requires administrator", "this installer needs an administrator account. Sign in with one, or ask whoever manages this PC."),
        (@"Error 1714|older version of .* cannot be removed", "Windows couldn't remove the old version first. Uninstall it from Installed apps, then install again."),
        (@"Error 1638|Another version of this product is already installed", "another version is already installed. Uninstall it from Installed apps, then install again."),
        (@"Error 1618|Another installation is (already )?in progress", "another installer is running. Wait for it to finish, then try again."),
        (@"Error 1310|Error 1303|Error 1304|Error 1305|Error 1306", "a file it needs to replace is in use. Close the app and try again."),
        (@"Error 1601|Error 1704|Out of disk space|disk full|not enough (free )?(disk )?space", "there isn't enough free disk space."),
        (@"Error 1720|Error 1721|Error 1722|Error 1723", "a step inside the app's own installer failed, which may be Windows blocking it. Try again with the app closed, or use its portable version if it has one."),
    };

    /// <summary>
    /// Many installers can install for everyone or just for the current user, and default to just the user when run quietly. They refuse to update a copy installed
    /// for everyone that way, so an update always matches the copy it replaces ("LocalMachine|…" in the installed-programs entry id). A new install follows what the user
    /// chose. "Just me" adds nothing, so an installer that only offers one way still works. Arguments the user set themselves win.
    /// </summary>
    public static bool InstallForEveryone(string? installedEntryId, bool wanted, string? customArgs)
    {
        if (customArgs != null && customArgs.Contains("ALLUSERS", StringComparison.OrdinalIgnoreCase)) return false;
        return installedEntryId != null ? installedEntryId.StartsWith("LocalMachine|", StringComparison.Ordinal) : wanted;
    }

    /// <summary>A plain sentence for a failed install. The log text is optional; without it the sentence is more general.</summary>
    public static string Explain(string appName, int code, string? log)
    {
        var start = $"{appName} couldn't be installed: ";
        if (code == 1618) return start + Reasons.First(r => r.Pattern.Contains("1618")).Reason;
        if (code == 1638) return start + Reasons.First(r => r.Pattern.Contains("1638")).Reason;
        if (code == 1925 || code == 1730) return start + Reasons.First(r => r.Pattern.Contains("1925")).Reason;
        if (!string.IsNullOrEmpty(log))
        {
            // an app's own installer often says exactly what's wrong: that beats anything worked out here
            var own = Regex.Match(log, @"Product: [^\r\n]*? -- (?!Installation (?:failed|completed)|Error \d)([^\r\n]+)");
            if (own.Success) return start + own.Groups[1].Value.Trim();
        }
        if (!string.IsNullOrEmpty(log))
            foreach (var (pattern, reason) in Reasons)
                if (Regex.IsMatch(log, pattern, RegexOptions.IgnoreCase)) return start + reason;
        return start + $"the installer stopped with error {code}. Close the app if it's open and try again.";
    }
}
