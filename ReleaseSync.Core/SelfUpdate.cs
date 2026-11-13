using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReleaseSync.Core;

/// <summary>The parts of ReleaseSync updating itself that don't depend on any platform.</summary>
public static class SelfUpdate
{
    const string NoChanges = "No list of changes was published for this version.";

    /// <summary>
    /// Just the list of changes from a release's notes, as plain text for the update dialog: without the install instructions at the top
    /// or the links at the bottom, and with bullets instead of markdown.
    /// </summary>
    public static string Changes(string? notes)
    {
        var lines = (notes ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        int start = lines.FindIndex(l => l.Contains("New in this version", StringComparison.OrdinalIgnoreCase));
        if (start >= 0) lines = lines.Skip(start + 1).ToList();
        int end = lines.FindIndex(l => l.StartsWith("See the [project status", StringComparison.OrdinalIgnoreCase) || l.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase));
        if (end >= 0) lines = lines.Take(end).ToList();

        var text = lines.Select(l => Regex.Replace(l, @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("**", "").Replace("`", ""))
                        .Select(l => Regex.Replace(l, @"^[-*]\s+", "• ").TrimStart('#').Trim());
        var result = Regex.Replace(string.Join("\n", text), @"\n{3,}", "\n\n").Trim();
        return result.Length == 0 ? NoChanges : result;
    }

    /// <summary>The changes in every version newer than the one installed, newest first. Each is headed by its version when there is more than one.</summary>
    public static string ChangesSince(IEnumerable<(string Tag, string? Notes)> newer)
    {
        var list = newer.ToList();
        if (list.Count == 0) return NoChanges;
        if (list.Count == 1) return Changes(list[0].Notes);
        return string.Join("\n\n", list.Select(r => r.Tag + "\n" + Changes(r.Notes)));
    }

    /// <summary>
    /// The version in the address GitHub's "latest release" page redirects to (…/releases/tag/v1.2.3). Reading that is an ordinary web request, not an API call, so a routine
    /// check costs nothing from the user's hourly allowance, with or without a token. Null if the address isn't that shape.
    /// </summary>
    public static string? TagFromRedirect(string? location)
    {
        var m = Regex.Match(location ?? "", @"/releases/tag/([^/?#]+)$");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>The hex SHA-256 in a value like "sha256:ab12…" or a checksum file's "ab12…  ReleaseSync.exe". Null if there isn't one.</summary>
    public static string? Sha256From(string? text)
    {
        var m = Regex.Match(text ?? "", @"(?:^|sha256:|\s)([0-9a-fA-F]{64})(?:\s|$)");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static bool FileMatches(string file, string sha256Hex)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
