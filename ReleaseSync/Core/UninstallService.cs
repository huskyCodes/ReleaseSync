using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ReleaseSync.Core;

/// <summary>One program listed in Windows' "Installed apps".</summary>
public sealed class UninstallEntry
{
    public string Id = "";
    public string DisplayName = "";
    public string? Publisher, Version, UninstallString, QuietUninstallString, InstallLocation, DisplayIcon, UrlInfoAbout, UrlUpdateInfo, HelpLink, Comments;
    public bool CanSilent => !string.IsNullOrWhiteSpace(QuietUninstallString) || (UninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false);
}

public static class UninstallService
{
    const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    static readonly (RegistryHive Hive, RegistryView View)[] Roots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
    };

    static UninstallEntry? Read(RegistryHive hive, RegistryView view, string name, RegistryKey sub)
    {
        var display = sub.GetValue("DisplayName") as string;
        var us = sub.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(us ?? sub.GetValue("QuietUninstallString") as string)) return null;
        if (sub.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (!string.IsNullOrEmpty(sub.GetValue("ParentKeyName") as string)) return null;
        if (sub.GetValue("ReleaseType") is string rt && Regex.IsMatch(rt, "update|hotfix", RegexOptions.IgnoreCase)) return null;
        return new UninstallEntry
        {
            UrlInfoAbout = sub.GetValue("URLInfoAbout") as string,
            UrlUpdateInfo = sub.GetValue("URLUpdateInfo") as string,
            HelpLink = sub.GetValue("HelpLink") as string,
            Comments = sub.GetValue("Comments") as string,
            Id =$"{hive}|{view}|{name}",
            DisplayName = display!.Trim(),
            Publisher = sub.GetValue("Publisher") as string,
            Version = sub.GetValue("DisplayVersion") as string,
            UninstallString = us,
            QuietUninstallString = sub.GetValue("QuietUninstallString") as string,
            InstallLocation = sub.GetValue("InstallLocation") as string,
            DisplayIcon = sub.GetValue("DisplayIcon") as string,
        };
    }

    public static Dictionary<string, UninstallEntry> Snapshot()
    {
        var map = new Dictionary<string, UninstallEntry>();
        foreach (var (hive, view) in Roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallPath);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        var e = sub == null ? null : Read(hive, view, name, sub);
                        if (e != null) map[e.Id] = e;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return map;
    }

    public static UninstallEntry? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var p = id.Split('|', 3);
        if (p.Length != 3 || !Enum.TryParse<RegistryHive>(p[0], out var hive) || !Enum.TryParse<RegistryView>(p[1], out var view)) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var sub = root.OpenSubKey(UninstallPath + "\\" + p[2]);
            return sub == null ? null : Read(hive, view, p[2], sub);
        }
        catch { return null; }
    }

    /// <summary>Removes a per-user entry from Windows' installed-programs list (used when the program's folder is gone). Never touches machine-wide entries.</summary>
    public static bool DeleteUserEntry(string id)
    {
        var p = id.Split('|', 3);
        if (p.Length != 3 || p[0] != "CurrentUser" || !Enum.TryParse<RegistryView>(p[1], out var view) || string.IsNullOrWhiteSpace(p[2])) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = root.OpenSubKey(UninstallPath, true);
            if (key == null || key.OpenSubKey(p[2]) == null) return false;
            key.DeleteSubKeyTree(p[2]);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Entries that appeared (or changed version) between two snapshots.</summary>
    public static UninstallEntry? FindNew(Dictionary<string, UninstallEntry> before, Dictionary<string, UninstallEntry> after, TrackedApp app)
    {
        var fresh = after.Values.Where(e => !before.TryGetValue(e.Id, out var old) || old.Version != e.Version).ToList();
        if (fresh.Count == 0) return null;
        if (fresh.Count == 1) return fresh[0];
        return fresh.OrderByDescending(e => MatchScore(e, app)).First();
    }

    static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    static int MatchScore(UninstallEntry e, TrackedApp app)
    {
        var name = Norm(e.DisplayName);
        var repo = Norm(app.Repo); var title = Norm(app.Title);
        int score = 0;
        if (name == repo || name == title) score = 100;
        else if (repo.Length >= 3 && (name.Contains(repo) || (name.Length >= 4 && repo.Contains(name)))) score = 70;
        else if (title.Length >= 3 && (name.Contains(title) || (name.Length >= 4 && title.Contains(name)))) score = 65;
        if (score == 0) return 0;
        var pub = Norm(e.Publisher);
        if (pub.Length > 2 && Norm(app.Owner).Length > 2 && (pub.Contains(Norm(app.Owner)) || Norm(app.Owner).Contains(pub))) score += 20;
        if (!string.IsNullOrEmpty(e.InstallLocation) && Norm(e.InstallLocation).Contains(repo)) score += 10;
        return score;
    }

    /// <summary>The entry ReleaseSync recorded at install time, else the best name match (null if nothing plausible).</summary>
    public static (UninstallEntry? Entry, bool Exact) Resolve(TrackedApp app)
    {
        var stored = Get(app.UninstallKey);
        if (stored != null) return (stored, true);
        var best = Snapshot().Values.Select(e => (e, s: MatchScore(e, app))).Where(x => x.s >= 65).OrderByDescending(x => x.s).FirstOrDefault();
        return (best.e, false);
    }

    static readonly Regex GhUrl = new(@"github\.com[/:](?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_][A-Za-z0-9_.-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> NotOwners = new(StringComparer.OrdinalIgnoreCase) { "sponsors", "orgs", "settings", "topics", "marketplace", "features", "about", "apps", "users", "login", "collections", "enterprise" };

    public static (string Owner, string Repo)? ParseGitHubUrl(string? s) => ParseGh(s) is { } p ? (p.Item1, p.Item2) : null;

    static (string, string)? ParseGh(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (Match m in GhUrl.Matches(s))
        {
            var o = m.Groups["o"].Value; var r = m.Groups["r"].Value.TrimEnd('.');
            if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) r = r[..^4];
            if (NotOwners.Contains(o) || r.Length == 0) continue;
            return (o, r);
        }
        return null;
    }

    static readonly Regex UrlRx = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool ForgeHost(string host) =>
        host is "github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com" or "codeberg.org" or "www.codeberg.org"
        || Regex.IsMatch(host, @"^(git|gitea|forgejo|gitlab|code|src)\.", RegexOptions.IgnoreCase);

    /// <summary>A project page on GitHub, GitLab, Codeberg or a self-hosted forge (git.example.com…), found in free text.</summary>
    public static RepoRef? ParseForgeUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (ParseGh(s) is { } g) return new RepoRef("github", g.Item1, g.Item2);
        foreach (Match m in UrlRx.Matches(s))
        {
            var url = m.Value.TrimEnd('.', ',', ')', ';');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !ForgeHost(u.Host)) continue;
            if (SourceClient.TryParse(url, out var rr) && rr != null) return rr;
        }
        return null;
    }

    /// <summary>The project page the program itself points to (its "about" link, help link, or details embedded in its .exe).</summary>
    public static RepoRef? FindRepoLink(UninstallEntry e)
    {
        foreach (var s in new[] { e.UrlUpdateInfo, e.UrlInfoAbout, e.HelpLink, e.Comments })
            if (ParseForgeUrl(s) is { } hit) return hit;
        try
        {
            var exe = FindExe(e);
            if (exe != null)
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                foreach (var s in new[] { v.Comments, v.LegalCopyright, v.LegalTrademarks, v.ProductName, v.CompanyName })
                    if (ParseForgeUrl(s) is { } hit) return hit;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------ portable programs (not in Windows' installed list)
    static string? NameFromFolder(string folder)
    {
        var m = Regex.Match(folder, @"^(?<n>.+?)(?:[-_ .](?:v?\d|windows|win|x64|amd64|x86|portable|release|standard)|$)", RegexOptions.IgnoreCase);
        var n = m.Success ? m.Groups["n"].Value.Replace('-', ' ').Replace('_', ' ').Trim() : "";
        return n.Length >= 2 ? n : null;
    }

    static string? VersionFromText(string text)
    {
        var m = Regex.Match(text, @"v?(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Programs that live in ordinary folders: found through Desktop/Start menu shortcuts and any folders the user adds.</summary>
    public static List<UninstallEntry> PortableCandidates(IEnumerable<UninstallEntry> registry, IEnumerable<string> extraFolders)
    {
        var result = new List<UninstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regExes = registry.Select(FindExe).Where(x => x != null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var regDirs = registry.Select(e => e.InstallLocation?.Trim().Trim('"').TrimEnd('\\')).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();
        var skipRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        };

        void Add(string? exe, string? shortcutName)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return;
                if (skipRoots.Any(r => exe.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) || exe.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) return;
                if (!seen.Add(exe) || regExes.Contains(exe) || regDirs.Any(d => exe.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))) return;
                var dir = Path.GetDirectoryName(exe)!;
                var folderName = Path.GetFileName(dir);
                var exeName = Path.GetFileNameWithoutExtension(exe);
                var name = !string.IsNullOrEmpty(shortcutName) && !shortcutName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !shortcutName.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                    ? shortcutName : (NameFromFolder(folderName) ?? exeName);
                var fv = FileVersionInfo.GetVersionInfo(exe);
                var ver = VersionFromText(folderName) ?? (VersionFromText(fv.ProductVersion ?? "") is { } pv && !pv.StartsWith("0.0.0") ? pv : null);
                result.Add(new UninstallEntry { Id = "path|" + exe, DisplayName = name, Version = ver, InstallLocation = dir, DisplayIcon = exe, Publisher = string.IsNullOrWhiteSpace(fv.CompanyName) ? null : fv.CompanyName });
            }
            catch { }
        }

        // shortcuts on the desktop and in the Start menu
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(1500))
                    {
                        try { string target = shell.CreateShortcut(lnk).TargetPath; Add(target, Path.GetFileNameWithoutExtension(lnk)); } catch { }
                    }
                }
            }
        }
        catch { }

        // folders the user asked ReleaseSync to look in: each sub-folder that holds a program
        foreach (var folder in extraFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(folder).Take(400))
                {
                    var exes = Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                    if (exes.Count == 0) foreach (var inner in Directory.EnumerateDirectories(sub).Take(5)) exes.AddRange(Directory.GetFiles(inner, "*.exe", SearchOption.TopDirectoryOnly));
                    exes = exes.Where(f => !Regex.IsMatch(Path.GetFileName(f), "unins|uninstall|setup|install|crash|update|vc_redist|vcredist|helper|launcher_|_x64_", RegexOptions.IgnoreCase)).ToList();
                    if (exes.Count == 0) continue;
                    var key = Norm(Path.GetFileName(sub));
                    var best = exes.FirstOrDefault(f => key.Contains(Norm(Path.GetFileNameWithoutExtension(f)))) ?? exes.OrderByDescending(f => new FileInfo(f).Length).First();
                    Add(best, null);
                }
            }
            catch { }
        }
        return result;
    }

    /// <summary>Windows components, drivers, runtimes and big-vendor software — not worth looking up on GitHub.</summary>
    public static bool IsSystemLike(UninstallEntry e) =>
        Regex.IsMatch(e.DisplayName, @"redistributable|runtime|driver|visual c\+\+|\.net |windows sdk|webview|framework|microsoft edge|update for|security update|windows (software|app)|intel\(r\)|nvidia|realtek|amd |vulkan|directx|office|onedrive|teams", RegexOptions.IgnoreCase)
        || Regex.IsMatch(e.Publisher ?? "", @"microsoft|nvidia|intel|advanced micro|realtek|dell|hewlett|lenovo|asus|logitech|corsair|razer|qualcomm|adobe", RegexOptions.IgnoreCase);

    /// <summary>"7-Zip 26.02 (x64)" → "7-Zip" for searching.</summary>
    public static string CleanName(string display)
    {
        var s = Regex.Replace(display, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit|amd64|win64|win32|setup|installer)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+v?\d+([._-]\d+)*.*$", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>A program on this PC that matches the app: through a Desktop/Start-menu shortcut first, then the usual folders. Includes a version when the folder name shows one.</summary>
    public static (string Exe, string? Version)? FindLocalProgram(TrackedApp app)
    {
        var keys = new[] { Norm(app.Repo), Norm(app.Title) }.Where(k => k.Length >= 3).Distinct().ToArray();
        if (keys.Length == 0) return null;
        try
        {
            foreach (var c in PortableCandidates(Array.Empty<UninstallEntry>(), Array.Empty<string>()))
            {
                var n = Norm(c.DisplayName);
                var e = Norm(Path.GetFileNameWithoutExtension(c.DisplayIcon ?? ""));
                if (keys.Any(k => n == k || n.Contains(k) || e == k) && !string.IsNullOrEmpty(c.DisplayIcon)) return (c.DisplayIcon!, c.Version);
            }
        }
        catch { }
        var exe = FindLocalExe(app);
        return exe == null ? null : (exe, VersionFromText(Path.GetFileName(Path.GetDirectoryName(exe) ?? "")));
    }

    /// <summary>Looks in the usual places for a program that matches the app by name (portable downloads, copies on the desktop, Program Files…).</summary>
    public static string? FindLocalExe(TrackedApp app)
    {
        var keys = new[] { Norm(app.Repo), Norm(app.Title) }.Where(k => k.Length >= 3).Distinct().ToArray();
        if (keys.Length == 0) return null;
        bool Matches(string fileOrDir) { var n = Norm(Path.GetFileNameWithoutExtension(fileOrDir)); return keys.Any(k => n == k || n.Contains(k)); }
        bool NotJunk(string f) => !Regex.IsMatch(Path.GetFileName(f), "unins|uninstall|setup|install|crash|update", RegexOptions.IgnoreCase);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var loose = new[]
        {
            Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        try
        {
            // loose files, one folder level deep (extracted zips live in a folder named after the app)
            foreach (var dir in loose.Where(Directory.Exists))
            {
                var hit = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault(f => Matches(f) && NotJunk(f));
                if (hit != null) return hit;
                foreach (var sub in Directory.EnumerateDirectories(dir).Where(Matches).Take(8))
                {
                    var inner = Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).Where(NotJunk).OrderByDescending(f => Matches(f)).FirstOrDefault();
                    if (inner != null) return inner;
                }
            }
            // installed-style folders
            var programDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            foreach (var dir in programDirs.Where(Directory.Exists))
                foreach (var sub in Directory.EnumerateDirectories(dir).Where(Matches).Take(8))
                {
                    var exe = Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).Where(NotJunk).OrderByDescending(f => Matches(f)).ThenByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                    if (exe != null) return exe;
                }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The program for an installed app, for installers that don't tell Windows where they put it. Asks Windows' registered program paths first, then follows the app's
    /// Start menu or desktop shortcut (which works wherever it was installed), then looks in the usual folders.
    /// </summary>
    public static string? FindInstalledExe(TrackedApp app)
    {
        var keys = new[] { Norm(app.Repo), Norm(app.Title) }.Where(k => k.Length >= 3).Distinct().ToArray();
        if (keys.Length == 0) return null;
        bool Usable(string? f) => !string.IsNullOrEmpty(f) && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(f)
                                  && !Regex.IsMatch(Path.GetFileName(f), "unins|uninstall|setup|crash", RegexOptions.IgnoreCase);

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var name in new[] { app.Repo, app.Title }.Distinct())
            {
                try
                {
                    using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name + ".exe");
                    var path = (k?.GetValue(null) as string)?.Trim().Trim('"');
                    if (Usable(path)) return path;
                }
                catch { }
            }

        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(1500))
                    {
                        var n = Norm(Path.GetFileNameWithoutExtension(lnk));
                        if (!keys.Any(k => n == k || n.Contains(k))) continue;
                        try { string target = shell.CreateShortcut(lnk).TargetPath; if (Usable(target)) return target; } catch { }
                    }
                }
            }
        }
        catch { }
        return FindLocalProgram(app)?.Exe;
    }

    public static string? FindExe(UninstallEntry e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(e.DisplayIcon))
            {
                var icon = e.DisplayIcon!.Trim();
                var comma = icon.LastIndexOf(',');
                if (comma > 0 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
                icon = icon.Trim('"');
                if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) && !Regex.IsMatch(Path.GetFileName(icon), "unins|uninstall", RegexOptions.IgnoreCase)) return icon;
            }
            var loc = e.InstallLocation?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
            {
                var exes = Directory.GetFiles(loc, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !Regex.IsMatch(Path.GetFileName(f), "unins|uninstall|crash|update|setup", RegexOptions.IgnoreCase)).ToList();
                var key = Norm(e.DisplayName);
                return exes.FirstOrDefault(f => key.Contains(Norm(Path.GetFileNameWithoutExtension(f)))) ?? exes.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            }
        }
        catch { }
        return null;
    }

    static (string File, string Args) Split(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..]) : (cmd, "");
    }

    /// <summary>Runs the program's own uninstaller. True when it no longer appears in Windows' installed programs.</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(UninstallEntry e, bool silent, CancellationToken ct = default)
    {
        string file, args;
        var raw = silent && !string.IsNullOrWhiteSpace(e.QuietUninstallString) ? e.QuietUninstallString! : e.UninstallString ?? e.QuietUninstallString!;
        var msi = Regex.Match(raw, @"msiexec(\.exe)?\s+/[iIxX]\s*(\{[0-9A-Fa-f-]+\})", RegexOptions.IgnoreCase);
        if (msi.Success) { file = "msiexec.exe"; args = $"/x {msi.Groups[2].Value} " + (silent ? "/qn /norestart" : "/passive /norestart") + " MSIRESTARTMANAGERCONTROL=DisableShutdown"; }
        else (file, args) = Split(raw);

        bool hadShell = Platform.HasShell();
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync(ct);
            if (hadShell) await Platform.RestoreShellIfMissingAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return (false, "Uninstall was cancelled (administrator permission was declined)."); }
        catch (Exception ex) { return (false, "Couldn't start the uninstaller: " + ex.Message); }

        // some uninstallers hand off to a temporary copy and exit straight away, so wait for the entry to disappear
        for (int i = 0; i < 30; i++)
        {
            if (Get(e.Id) == null) return (true, "Uninstalled.");
            await Task.Delay(1000, ct);
        }
        return (false, "The uninstaller finished, but the program still appears installed. It may have been cancelled.");
    }
}
