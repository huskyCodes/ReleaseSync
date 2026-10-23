using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

public sealed class InstallProgress
{
    public double Percent;   // 0..100, 0 means indeterminate
    public string Text = "";
}

public sealed class InstallService
{
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };

    public InstallService() => http.DefaultRequestHeaders.UserAgent.ParseAdd("ReleaseSync/1.0");

    public async Task<string> DownloadAsync(TrackedApp app, string tag, AssetInfo asset, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var s = AppSettingsProvider.Current;
        var dir = Path.Combine(s.DownloadFolder, Fmt.SafeName(app.Owner.Replace('/', '-') + "-" + app.Repo), Fmt.SafeName(string.IsNullOrEmpty(tag) ? "latest" : tag));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Fmt.SafeName(asset.Name));
        var tmp = dest + ".part";

        using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buf = new byte[81920];
            long done = 0; int n; var sw = Stopwatch.StartNew(); long lastTick = 0;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (sw.ElapsedMilliseconds - lastTick > 90)
                {
                    lastTick = sw.ElapsedMilliseconds;
                    double pct = total > 0 ? Math.Max(1, done * 100.0 / total) : 0;
                    double speed = done / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    progress.Report(new InstallProgress { Percent = pct, Text = $"Downloading {Fmt.Size(done)} / {Fmt.Size(total)}  ·  {Fmt.Size((long)speed)}/s" });
                }
            }
        }
        File.Move(tmp, dest, true);
        return dest;
    }

    /// <summary>Downloads a file without installing it, into the download folder.</summary>
    public Task<string> DownloadOnlyAsync(TrackedApp app, ReleaseSummary release, AssetInfo asset, IProgress<InstallProgress> progress, CancellationToken ct) =>
        DownloadAsync(app, release.Tag, asset, progress, ct);

    public async Task InstallAsync(TrackedApp app, ReleaseSummary release, AssetInfo asset, IProgress<InstallProgress> progress, CancellationToken ct,
                                   Func<VerifyResult, Task<bool>>? confirmUnsigned = null, bool unattended = false, string? installDir = null)
    {
        var s = AppSettingsProvider.Current;
        progress.Report(new InstallProgress { Percent = 0, Text = "Starting download…" });
        var file = await DownloadAsync(app, release.Tag, asset, progress, ct).ConfigureAwait(false);
        try
        {
            if (s.VerifyDownloads || s.ScanWithDefender || s.CheckProvenance || (s.UseVirusTotal && s.HasVirusTotalKey))
            {
                progress.Report(new InstallProgress { Percent = 100, Text = "Checking the download…" });
                var v = await Verifier.VerifyAsync(file, asset, release.Assets, http, ct, app.Source, app.Owner, app.Repo).ConfigureAwait(false);
                app.VerifyNote = v.Summary;
                if (v.DefenderThreatFound)
                    throw new InvalidOperationException($"Windows Security says this download is dangerous{(string.IsNullOrEmpty(v.DefenderThreat) ? "" : " (" + v.DefenderThreat + ")")}. ReleaseSync stopped and deleted it. Don't install it.");
                if (v.ChecksumMismatch)
                    throw new InvalidOperationException("The download's checksum doesn't match the one the publisher posted, so ReleaseSync deleted it. Try again — if it keeps happening, don't install this file.");
                if (v.SignatureBad)
                    throw new InvalidOperationException("The installer's digital signature is invalid — the file was changed after it was signed. ReleaseSync stopped and deleted it.");
                // "don't ask again" only covers the unsigned case, never a VirusTotal flag or a mismatched build record
                if (v.NeedsConfirm && !(app.TrustUnsigned && !v.VtFlagged && !v.ProvenanceMismatch))
                {
                    if (unattended || confirmUnsigned == null)
                        throw new InvalidOperationException("Skipped automatic install: " + string.Join("; ", v.Findings) + ". Open ReleaseSync and update it yourself to review it.");
                    if (!await confirmUnsigned(v).ConfigureAwait(false)) throw new OperationCanceledException();
                }
            }

            progress.Report(new InstallProgress { Percent = 100, Text = "Installing…" });
            var kind = AssetPicker.KindOf(asset.Name);
            bool portable = app.Mode == InstallMode.Portable || (app.Mode == InstallMode.Auto && kind is AssetKind.Zip or AssetKind.Portable);
            if (app.Mode == InstallMode.Installer && kind == AssetKind.Zip) portable = false;

            // an app the user already had (portable exe/zip somewhere on disk) is updated where it lives instead of being installed a second time
            var existingExe = app.InstallPath;
            bool external = string.IsNullOrEmpty(app.UninstallKey) && !string.IsNullOrEmpty(existingExe) && File.Exists(existingExe)
                            && !existingExe.StartsWith(s.InstallRoot, StringComparison.OrdinalIgnoreCase);
            bool setupLike = Regex.IsMatch(asset.Name, @"setup|install", RegexOptions.IgnoreCase);
            bool inPlace = external && app.Mode != InstallMode.Installer && (kind is AssetKind.Zip or AssetKind.Portable || (kind == AssetKind.Installer && !setupLike));

            var before = UninstallService.Snapshot();
            bool hadShell = Platform.HasShell();
            bool installerRun = false;
            if (inPlace) await InstallInPlaceAsync(app, file, existingExe!, kind, ct).ConfigureAwait(false);
            else if (kind == AssetKind.Msi && !portable) { await RunMsiAsync(file, app, ct, installDir).ConfigureAwait(false); installerRun = true; }
            else if (kind == AssetKind.Msix) await RunMsixAsync(file, ct).ConfigureAwait(false);
            else if (portable || kind == AssetKind.Zip) await InstallPortableAsync(app, file, kind, progress, ct).ConfigureAwait(false);
            else { await RunExeAsync(file, app, ct, installDir).ConfigureAwait(false); installerRun = true; }

            if (hadShell) await Platform.RestoreShellIfMissingAsync().ConfigureAwait(false);
            if (installerRun)
            {
                // remember which Windows uninstall entry this installer created so it can be removed later
                var entry = UninstallService.FindNew(before, UninstallService.Snapshot(), app);
                if (entry != null)
                {
                    app.UninstallKey = entry.Id;
                    var exe = UninstallService.FindExe(entry);
                    if (exe != null) app.InstallPath = exe;
                }
            }
            else if (app.InstallPath != null) app.UninstallKey = null;
        }
        finally
        {
            if (!s.KeepDownloads)
            {
                try { File.Delete(file); var d = Path.GetDirectoryName(file)!; if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        app.InstalledAssetName = asset.Name;
    }

    static async Task RunProcessAsync(ProcessStartInfo psi, CancellationToken ct, Func<int, string?> interpret)
    {
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException("Installation was cancelled (administrator permission was declined)."); }
        if (p == null) throw new InvalidOperationException("Couldn't start the installer.");
        using (p)
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var err = interpret(p.ExitCode);
            if (err != null) throw new InvalidOperationException(err);
        }
    }

    /// <summary>Most installers are built with NSIS or Inno Setup, and each takes the install folder in its own way.</summary>
    static string? DetectInstallerKind(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[(int)Math.Min(fs.Length, 8 * 1024 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            if (text.Contains("Nullsoft", StringComparison.Ordinal)) return "nsis";
            if (text.Contains("Inno Setup", StringComparison.Ordinal)) return "inno";
        }
        catch { }
        return null;
    }

    static Task RunMsiAsync(string file, TrackedApp app, CancellationToken ct, string? installDir = null)
    {
        var s = AppSettingsProvider.Current;
        // DisableShutdown: stop Windows Installer from closing programs (Explorer, notably) to replace files they've loaded; ReleaseSync closes the app itself when needed
        var args = $"/i \"{file}\" " + (s.SilentInstall ? "/qn /norestart" : "/passive /norestart") + " MSIRESTARTMANAGERCONTROL=DisableShutdown"
                   + (string.IsNullOrEmpty(installDir) ? "" : $" INSTALLDIR=\"{Platform.ToNetworkPath(installDir)}\"")
                   + (string.IsNullOrWhiteSpace(app.CustomArgs) ? "" : " " + app.CustomArgs);
        var psi = new ProcessStartInfo("msiexec.exe", args) { UseShellExecute = true, Verb = s.SilentInstall ? "runas" : "" };
        return RunProcessAsync(psi, ct, code => code switch
        {
            0 or 3010 or 1641 => null,
            1602 or 1223 => "Installation was cancelled.",
            _ => $"The installer finished with error code {code}."
        });
    }

    static Task RunExeAsync(string file, TrackedApp app, CancellationToken ct, string? installDir = null)
    {
        var s = AppSettingsProvider.Current;
        var args = !string.IsNullOrWhiteSpace(app.CustomArgs) ? app.CustomArgs! : (s.SilentInstall ? s.SilentArgs : "");
        if (!string.IsNullOrEmpty(installDir))
        {
            // tell the installer which folder to use — otherwise it may pick its default, or trip over an old location that no longer exists
            var dir = Platform.ToNetworkPath(installDir).TrimEnd('\\');
            args = DetectInstallerKind(file) switch
            {
                "nsis" => (args + $" /D={dir}").Trim(),            // NSIS: must be the last argument, never quoted
                "inno" => (args + $" /DIR=\"{dir}\"").Trim(),
                _ => args
            };
        }
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! };
        return RunProcessAsync(psi, ct, code => code is 0 or 3010 ? null : $"The installer closed with code {code}. If you cancelled it, you can try again.");
    }

    static Task RunMsixAsync(string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{file.Replace("'", "''")}'\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        return Task.Run(async () =>
        {
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("MSIX install failed: " + (err.Length > 200 ? err[..200] : err).Trim());
        }, ct);
    }

    /// <summary>Where ReleaseSync unpacks a portable app: the folder the user chose for it, else a folder named after it in the apps folder.</summary>
    public static string PortableTarget(TrackedApp app) =>
        !string.IsNullOrWhiteSpace(app.CustomInstallDir) ? app.CustomInstallDir!.TrimEnd('\\') : Path.Combine(AppSettingsProvider.Current.InstallRoot, Fmt.SafeName(app.Repo));

    /// <summary>Never offer to delete a drive root or one of the user's main folders, even if one was chosen as an install location.</summary>
    public static bool IsSafeToDelete(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('\\');
            if (Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
            var protectedDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            return !protectedDirs.Any(p => p.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    async Task InstallPortableAsync(TrackedApp app, string file, AssetKind kind, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var s = AppSettingsProvider.Current;
        var target = PortableTarget(app);
        var stagingRoot = Path.GetDirectoryName(target) is { Length: > 0 } parent ? parent : s.InstallRoot;   // same drive as the target, so the final move is instant
        Directory.CreateDirectory(stagingRoot);

        await Task.Run(() =>
        {
            var staging = Path.Combine(stagingRoot, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            try
            {
                if (kind == AssetKind.Zip) ZipFile.ExtractToDirectory(file, staging, true);
                else File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);

                // flatten a single top-level folder
                var entries = Directory.GetFileSystemEntries(staging);
                var root = staging;
                if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];

                // an archive that only wraps an installer: run it as such
                var msis = Directory.GetFiles(root, "*.msi", SearchOption.TopDirectoryOnly);
                if (kind == AssetKind.Zip && msis.Length == 1 && app.Mode != InstallMode.Portable)
                {
                    RunMsiAsync(msis[0], app, ct).GetAwaiter().GetResult();
                    app.InstallPath = null;
                    return;
                }

                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    // an update: copy the new files over the old ones and leave everything else alone (saves, settings and other data inside the folder survive)
                    if (app.HasCustomInstallDir && !IsSafeToDelete(target)) throw new IOException("That install folder is too broad to manage safely. Choose a dedicated folder for this app.");
                    var backup = AppBackups.Create(app, target, "update", ct);       // so a failed update can be undone
                    try { CopyOver(root, target, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var restored = false;
                        if (backup != null) { try { AppBackups.Restore(backup); restored = true; } catch { } }
                        throw new IOException("Couldn't replace the existing files. Close the app if it's running and try again." + (restored ? " Your previous version was put back." : ""));
                    }
                }
                else
                {
                    if (Directory.Exists(target)) Directory.Delete(target);      // empty folder
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(root, target);
                }
                var exe = FindMainExe(target, app.Repo);
                app.InstallPath = exe;
                if (exe != null && s.CreateShortcuts) CreateShortcut(app, exe);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }, ct).ConfigureAwait(false);
    }

    static void CopyOver(string source, string destination, CancellationToken ct)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(f, Path.Combine(destination, Path.GetRelativePath(source, f)), true);
        }
    }

    /// <summary>Replaces the user's existing copy with the new release, keeping it in the same folder.</summary>
    static Task InstallInPlaceAsync(TrackedApp app, string file, string existingExe, AssetKind kind, CancellationToken ct) => Task.Run(() =>
    {
        var dir = Path.GetDirectoryName(existingExe)!;
        const string lockedBase = "Couldn't replace the existing files. Close the app if it's running and try again.";
        // back up what's about to be replaced: the whole folder for a zip-based app (unless it's just Downloads or the Desktop), or the single program file
        var loose = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }
            .Any(l => dir.TrimEnd('\\').Equals(l, StringComparison.OrdinalIgnoreCase)) || Path.GetPathRoot(dir)!.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var backup = AppBackups.Create(app, kind == AssetKind.Zip && !loose ? dir : existingExe, "update", ct);
        string locked = lockedBase;
        if (backup != null) locked += " Your previous version was put back.";
        void Undo() { if (backup != null) { try { AppBackups.Restore(backup); } catch { locked = lockedBase; } } }
        try
        {
        if (kind == AssetKind.Zip)
        {
            var staging = Path.Combine(Path.GetTempPath(), "releasesync-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                ZipFile.ExtractToDirectory(file, staging, true);
                var entries = Directory.GetFileSystemEntries(staging);
                var root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(dir, Path.GetRelativePath(root, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { File.Copy(f, target, true); } catch (IOException) { throw new IOException(locked); } catch (UnauthorizedAccessException) { throw new IOException(locked); }
                }
            }
            finally { try { Directory.Delete(staging, true); } catch { } }
        }
        else
        {
            try { File.Copy(file, existingExe, true); } catch (IOException) { throw new IOException(locked); } catch (UnauthorizedAccessException) { throw new IOException(locked); }
        }
        }
        catch (IOException) { Undo(); throw; }
    }, ct);

    static string? FindMainExe(string dir, string repo)
    {
        var exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetFileName(f), @"unins|uninstall|crash|update|vc_redist|vcredist|setup", RegexOptions.IgnoreCase))
            .ToList();
        if (exes.Count == 0) return null;
        var key = new string(repo.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var named = exes.Where(f => new string(Path.GetFileNameWithoutExtension(f).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant().Contains(key)).ToList();
        var pool = named.Count > 0 ? named : exes;
        return pool.OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenByDescending(f => new FileInfo(f).Length).First();
    }

    static string ShortcutPath(TrackedApp app)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ReleaseSync");
        return Path.Combine(dir, Fmt.SafeName(app.Title) + ".lnk");
    }

    static void CreateShortcut(TrackedApp app, string exe)
    {
        try
        {
            var path = ShortcutPath(app);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.Description = app.Description ?? app.Title;
            lnk.Save();
        }
        catch { }
    }

    /// <summary>Deletes portable files and shortcut that ReleaseSync created.</summary>
    public static void RemoveFiles(TrackedApp app)
    {
        try
        {
            var target = PortableTarget(app);
            if (Directory.Exists(target) && (!app.HasCustomInstallDir || IsSafeToDelete(target)))
            {
                AppBackups.Create(app, target, "removal");           // kept, so deleting an app never destroys its settings for good
                Directory.Delete(target, true);
            }
            var lnk = ShortcutPath(app);
            if (File.Exists(lnk)) File.Delete(lnk);
        }
        catch { }
    }
}
