using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseSync.Core;

public enum SignatureState { NotChecked, Trusted, Unsigned, Untrusted, Invalid }

public sealed class VerifyResult
{
    public string? Sha256;
    public bool ChecksumChecked, ChecksumMatches;
    public string? ChecksumSource;                  // "GitHub", or the name of the checksum file
    public SignatureState Signature = SignatureState.NotChecked;
    public string? Signer;
    public bool IsExecutable;                       // exe/msi — the kinds worth a signature check

    public DefenderState Defender = DefenderState.NotRun;
    public string? DefenderThreat;

    public bool ProvenanceChecked, ProvenanceMismatch;
    public string? ProvenanceText;

    public bool VtChecked;
    public int VtMalicious, VtSuspicious, VtTotal;
    public string? VtNote;

    public bool ChecksumMismatch => ChecksumChecked && !ChecksumMatches;
    public bool SignatureBad => Signature == SignatureState.Invalid;
    public bool DefenderThreatFound => Defender == DefenderState.Threat;
    public bool VtFlagged => VtChecked && VtMalicious + VtSuspicious > 0;
    public bool UnsignedConcern => IsExecutable && (Signature is SignatureState.Unsigned or SignatureState.Untrusted) && !(ChecksumChecked && ChecksumMatches);
    /// <summary>Something here should make the user stop and decide.</summary>
    public bool NeedsConfirm => UnsignedConcern || VtFlagged || ProvenanceMismatch;

    public IEnumerable<string> Findings
    {
        get
        {
            if (DefenderThreatFound) yield return $"Windows Security flagged it{(string.IsNullOrEmpty(DefenderThreat) ? "" : ": " + DefenderThreat)}";
            if (VtFlagged) yield return $"{VtMalicious + VtSuspicious} of {VtTotal} antivirus engines on VirusTotal flag this file";
            if (ProvenanceMismatch) yield return ProvenanceText ?? "its build record points to a different repository";
            if (UnsignedConcern) yield return Signature == SignatureState.Unsigned ? "it isn't digitally signed" : "its signature isn't trusted by Windows";
            if (UnsignedConcern && !ChecksumChecked) yield return "the project publishes no checksum to compare against";
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Defender == DefenderState.Clean) parts.Add("Windows Security: clean");
            else if (Defender == DefenderState.Unavailable) parts.Add("Windows Security: couldn't scan");
            if (ChecksumChecked) parts.Add(ChecksumMatches ? $"checksum verified ({ChecksumSource})" : "CHECKSUM MISMATCH");
            else parts.Add("no checksum published");
            if (IsExecutable)
                parts.Add(Signature switch
                {
                    SignatureState.Trusted => string.IsNullOrEmpty(Signer) ? "digitally signed" : $"signed by {Signer}",
                    SignatureState.Unsigned => "not digitally signed",
                    SignatureState.Untrusted => string.IsNullOrEmpty(Signer) ? "signature not trusted by Windows" : $"signed by {Signer} (not trusted by Windows)",
                    SignatureState.Invalid => "SIGNATURE INVALID",
                    _ => "signature not checked"
                });
            if (ProvenanceChecked && !ProvenanceMismatch) parts.Add("build provenance: " + ProvenanceText);
            if (VtChecked) parts.Add(VtTotal > 0 ? $"VirusTotal: {VtMalicious + VtSuspicious}/{VtTotal} flagged" : "VirusTotal: " + (VtNote ?? "no result"));
            return string.Join(" · ", parts);
        }
    }
}

public static class Verifier
{
    static readonly Regex HashLine = new(@"(?<h>\b[0-9a-fA-F]{64}\b)", RegexOptions.Compiled);

    /// <param name="source">github, gitlab, codeberg… — build-provenance lookups only exist for GitHub.</param>
    public static async Task<VerifyResult> VerifyAsync(string file, AssetInfo asset, IEnumerable<AssetInfo> siblings, HttpClient http, CancellationToken ct,
                                                       string source = "github", string owner = "", string repo = "")
    {
        var s = AppSettingsProvider.Current;
        var r = new VerifyResult();
        var ext = Path.GetExtension(file).ToLowerInvariant();
        r.IsExecutable = ext is ".exe" or ".msi";

        // ---- Windows Security (Defender) ----
        if (s.ScanWithDefender)
        {
            (r.Defender, r.DefenderThreat) = await Defender.ScanAsync(file, ct).ConfigureAwait(false);
            if (r.DefenderThreatFound) return r;            // no point checking further
        }

        // ---- checksum ----
        await using (var fs = File.OpenRead(file))
            r.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));

        string? expected = null;
        if (!string.IsNullOrEmpty(asset.Digest) && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        { expected = asset.Digest[7..]; r.ChecksumSource = "GitHub"; }
        else
        {
            foreach (var sib in ChecksumFiles(asset, siblings))
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
                    using var resp = await http.GetAsync(sib.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode || (resp.Content.Headers.ContentLength ?? 0) > 1_000_000) continue;
                    var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    expected = FindHashFor(text, asset.Name, sib.Name);
                    if (expected != null) { r.ChecksumSource = sib.Name; break; }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (HttpRequestException) { }
            }
        }
        if (expected != null)
        {
            r.ChecksumChecked = true;
            r.ChecksumMatches = string.Equals(r.Sha256, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!r.ChecksumMatches) return r;
        }

        // ---- code signature ----
        if (r.IsExecutable)
        {
            r.Signature = CheckSignature(file);
            if (r.Signature != SignatureState.Unsigned) r.Signer = SignerName(file);
            if (r.SignatureBad) return r;
        }

        // ---- build provenance (GitHub only) ----
        if (s.CheckProvenance && source == "github" && owner.Length > 0)
            await Provenance.CheckAsync(r, owner, repo, http, ct).ConfigureAwait(false);

        // ---- VirusTotal ----
        if (s.UseVirusTotal && s.HasVirusTotalKey && r.Sha256 != null)
            await VirusTotal.LookupAsync(r, s.VirusTotalKey, http, ct).ConfigureAwait(false);

        return r;
    }

    static IEnumerable<AssetInfo> ChecksumFiles(AssetInfo asset, IEnumerable<AssetInfo> siblings)
    {
        var all = siblings.Where(a => a.Name != asset.Name).ToList();
        var own = new[] { ".sha256", ".sha256sum", ".sha256.txt", ".sha-256" };
        foreach (var a in all) if (own.Any(e => a.Name.Equals(asset.Name + e, StringComparison.OrdinalIgnoreCase))) yield return a;
        foreach (var a in all)
            if (Regex.IsMatch(a.Name, @"(sha-?256|checksums?)", RegexOptions.IgnoreCase) && !Regex.IsMatch(a.Name, @"\.(sig|asc|minisig|pem|crt)$", RegexOptions.IgnoreCase)
                && !own.Any(e => a.Name.Equals(asset.Name + e, StringComparison.OrdinalIgnoreCase)))
                yield return a;
    }

    /// <summary>Reads "hash  filename", "SHA256 (filename) = hash", or a lone hash for a per-file checksum file.</summary>
    static string? FindHashFor(string text, string fileName, string checksumFileName)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        foreach (var l in lines)
        {
            var m = HashLine.Match(l);
            if (!m.Success) continue;
            var rest = l.Replace(m.Value, "").Trim();
            rest = Regex.Replace(rest, @"^SHA-?256\s*", "", RegexOptions.IgnoreCase).Trim('(', ')', ' ', '=', '*');
            if (rest.Length > 0 && Path.GetFileName(rest.Replace('/', '\\')).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return m.Value;
        }
        if (checksumFileName.StartsWith(fileName, StringComparison.OrdinalIgnoreCase) && lines.Count == 1)
        {
            var m = HashLine.Match(lines[0]);
            if (m.Success) return m.Value;
        }
        return null;
    }

    // ---------- Authenticode ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
        public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureState CheckSignature(string file)
    {
        var fi = new WINTRUST_FILE_INFO { pcwszFilePath = file };
        fi.cbStruct = (uint)Marshal.SizeOf(fi);
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WINTRUST_DATA { dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1, pFile = pFile, dwProvFlags = 0x1000 };
            data.cbStruct = (uint)Marshal.SizeOf(data);
            var code = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return code switch
            {
                0 => SignatureState.Trusted,
                0x800B0100 or 0x800B0003 or 0x800B0001 => SignatureState.Unsigned,
                0x80096010 or 0x80096011 => SignatureState.Invalid,
                _ => SignatureState.Untrusted
            };
        }
        catch { return SignatureState.NotChecked; }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    static string? SignerName(string file)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return null; }
    }
}

/// <summary>Asks Windows Security (Microsoft Defender) to scan a file, using the antivirus built into Windows.</summary>
public static class Defender
{
    static string? Locate()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(platform))
            {
                var newest = Directory.GetDirectories(platform).OrderByDescending(d => d).Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
                if (newest != null) return newest;
            }
            return File.Exists(legacy) ? legacy : null;
        }
        catch { return null; }
    }

    public static async Task<(DefenderState State, string? Threat)> ScanAsync(string file, CancellationToken ct)
    {
        // real-time protection may already have removed a bad download the moment it was written
        if (!File.Exists(file)) return (DefenderState.Threat, "Windows Security removed the file as soon as it was downloaded");
        var exe = Locate();
        if (exe == null) return (DefenderState.Unavailable, null);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"-Scan -ScanType 3 -File \"{file}\" -DisableRemediation")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return (DefenderState.Unavailable, null);
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var errors = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { p.Kill(true); } catch { } return (DefenderState.Unavailable, null); }
            var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
            if (!File.Exists(file)) return (DefenderState.Threat, "Windows Security removed the file");
            return DefenderScan.Read(p.ExitCode, text);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (DefenderState.Unavailable, null); }
    }
}

/// <summary>GitHub's record of where a release file was built, when the project publishes one (artifact attestations).</summary>
public static class Provenance
{
    public static async Task CheckAsync(VerifyResult r, string owner, string repo, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.Sha256)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/attestations/sha256:{r.Sha256!.ToLowerInvariant()}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var token = AppSettingsProvider.Current.GitHubToken;
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;      // most projects don't publish one — that's normal, not a warning
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("attestations", out var list) || list.GetArrayLength() == 0) return;

            // The lookup is scoped to this repository, so any record returned means the file was attested by a workflow in {owner}/{repo}.
            // GitHub may include the signed record inline (read it for the workflow name) or only a download link to it.
            bool inlineRead = false;
            foreach (var att in list.EnumerateArray())
            {
                if (!att.TryGetProperty("bundle", out var bundle) || bundle.ValueKind != JsonValueKind.Object
                    || !bundle.TryGetProperty("dsseEnvelope", out var env) || env.ValueKind != JsonValueKind.Object || !env.TryGetProperty("payload", out var pl)) continue;
                inlineRead = true;
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(pl.GetString() ?? ""));
                using var st = JsonDocument.Parse(json);
                if (!st.RootElement.TryGetProperty("predicate", out var pred)) continue;
                string? repoUrl = null, path = null;
                if (pred.TryGetProperty("buildDefinition", out var bd) && bd.TryGetProperty("externalParameters", out var ep) && ep.TryGetProperty("workflow", out var wf))
                {
                    repoUrl = wf.TryGetProperty("repository", out var rp) ? rp.GetString() : null;
                    path = wf.TryGetProperty("path", out var pa) ? pa.GetString() : null;
                }
                if (repoUrl == null) continue;
                r.ProvenanceChecked = true;
                var builtFrom = repoUrl.Replace("https://github.com/", "").Trim('/');
                if (builtFrom.Equals($"{owner}/{repo}", StringComparison.OrdinalIgnoreCase))
                    r.ProvenanceText = $"GitHub records it was built from {builtFrom}" + (string.IsNullOrEmpty(path) ? "" : $" by {path}");
                else
                { r.ProvenanceMismatch = true; r.ProvenanceText = $"its build record says it was built from a different repository ({builtFrom})"; }
                return;
            }
            if (!inlineRead)
            {
                r.ProvenanceChecked = true;
                r.ProvenanceText = $"GitHub holds a signed build record for this file from {owner}/{repo}";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* provenance is a bonus; never block an install because the lookup failed */ }
    }
}

/// <summary>Looks a file's fingerprint (SHA-256) up on VirusTotal. The file itself is never uploaded.</summary>
public static class VirusTotal
{
    public static async Task LookupAsync(VerifyResult r, string apiKey, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/files/{r.Sha256!.ToLowerInvariant()}");
            req.Headers.Add("x-apikey", apiKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            r.VtChecked = true;
            if ((int)resp.StatusCode == 404) { r.VtNote = "never seen before (that's common for new releases)"; return; }
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403) { r.VtNote = "your API key was rejected"; return; }
            if ((int)resp.StatusCode == 429) { r.VtNote = "daily or per-minute limit reached"; return; }
            if (!resp.IsSuccessStatusCode) { r.VtNote = "unavailable right now"; return; }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token).ConfigureAwait(false);
            var stats = doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("last_analysis_stats");
            int Get(string n) => stats.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
            r.VtMalicious = Get("malicious"); r.VtSuspicious = Get("suspicious");
            r.VtTotal = r.VtMalicious + r.VtSuspicious + Get("harmless") + Get("undetected");
            if (r.VtTotal == 0) r.VtNote = "no results yet";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { r.VtChecked = true; r.VtNote ??= "unavailable right now"; }
    }
}

/// <summary>Finds and closes running copies of an app so its files can be replaced.</summary>
public static class RunningApps
{
    public static List<Process> Find(TrackedApp app)
    {
        var result = new List<Process>();
        var s = AppSettingsProvider.Current;
        var dirs = new List<string>();
        var exe = app.InstallPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && string.IsNullOrEmpty(app.UninstallKey))
        {
            // a portable app owns its folder — unless it's just sitting loose in Downloads or on the desktop
            var folder = Path.GetDirectoryName(exe)!;
            var loose = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
            if (Path.GetPathRoot(folder) != folder && !loose.Any(l => folder.Equals(l, StringComparison.OrdinalIgnoreCase))) dirs.Add(folder);
        }
        var ownFolder = InstallService.PortableTarget(app);
        if (Directory.Exists(ownFolder) && InstallService.IsSafeToDelete(ownFolder)) dirs.Add(ownFolder);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) { p.Dispose(); continue; }
                bool hit = (!string.IsNullOrEmpty(exe) && path.Equals(exe, StringComparison.OrdinalIgnoreCase))
                           || dirs.Any(d => path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (hit) result.Add(p); else p.Dispose();
            }
            catch { p.Dispose(); }   // protected/system processes can't be inspected
        }
        return result;
    }

    /// <summary>Asks politely first (like clicking the window's X), then force-closes whatever is left.</summary>
    public static async Task<bool> CloseAsync(List<Process> procs, CancellationToken ct = default)
    {
        foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
        for (int i = 0; i < 40 && procs.Any(p => !Exited(p)); i++) await Task.Delay(250, ct);
        foreach (var p in procs.Where(p => !Exited(p))) { try { p.Kill(true); } catch { } }
        for (int i = 0; i < 20 && procs.Any(p => !Exited(p)); i++) await Task.Delay(250, ct);
        var ok = procs.All(Exited);
        foreach (var p in procs) p.Dispose();
        return ok;
    }

    static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }
}
