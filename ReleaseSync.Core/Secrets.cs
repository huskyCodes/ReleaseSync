using System;
using System.Collections.Concurrent;

namespace ReleaseSync.Core;

/// <summary>Where a platform keeps secrets (Windows Credential Manager, the Mac Keychain, Android's keystore…). Core only talks to this.</summary>
public interface ISecretStore
{
    string? Read(string target);
    bool Write(string target, string secret);
    void Delete(string target);
}

/// <summary>Holds the platform's secret store. Each app sets <see cref="Store"/> once at start-up; until then secrets live in memory only.</summary>
public static class Secrets
{
    public const string GitHubTarget = "ReleaseSync/GitHubToken";
    public const string VirusTotalTarget = "ReleaseSync/VirusTotalKey";
    public const string SyncPassphraseTarget = "ReleaseSync/SyncPassphrase";

    public static ISecretStore Store { get; set; } = new MemorySecretStore();

    /// <summary>Reads a token saved by very old versions in a Windows-only format; set by the Windows app, null elsewhere.</summary>
    public static Func<string?, string?>? LegacyUnprotect { get; set; }
}

/// <summary>Keeps secrets in memory only. The default, and what tests use.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    readonly ConcurrentDictionary<string, string> items = new();
    public string? Read(string target) => items.TryGetValue(target, out var v) ? v : null;
    public bool Write(string target, string secret) { items[target] = secret; return true; }
    public void Delete(string target) => items.TryRemove(target, out _);
}
