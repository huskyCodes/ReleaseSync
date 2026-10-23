using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ReleaseSync.Core;

public static class Secret
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

/// <summary>Stores the GitHub token in Windows Credential Manager (encrypted by Windows, tied to this user account).</summary>
public static class CredentialStore
{
    const string Target = "ReleaseSync/GitHubToken";
    const uint CRED_TYPE_GENERIC = 1;
    const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

    public const string VirusTotalTarget = "ReleaseSync/VirusTotalKey";

    public static bool Write(string secret, string? target = null)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = CRED_TYPE_GENERIC, TargetName = target ?? Target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = "ReleaseSync" };
            return CredWriteW(ref c, 0);
        }
        finally
        {
            Array.Clear(bytes); Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Read(string? target = null)
    {
        if (Environment.GetEnvironmentVariable("RELEASESYNC_DATA") is { Length: > 0 } && Environment.GetEnvironmentVariable("RELEASESYNC_NO_TOKEN") == "1") return null;      // lets a test copy pretend to be a brand-new user
        var name = target ?? Target;
        var found = ReadRaw(name);
        if (found == null)
        {
            // saved under the old name by an earlier version: copy it to the new name (the old entry is left in place; test copies only read it)
            found = ReadRaw(name.Replace("ReleaseSync", Legacy.OldName));
            if (found != null && !Legacy.IsTestMode) Write(found, name);
        }
        return found;
    }

    /// <summary>Reads one entry by its exact name, or null.</summary>
    public static string? ReadRaw(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.CredentialBlobSize == 0 || c.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            var s = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return s;
        }
        finally { CredFree(ptr); }
    }

    public static void Delete(string? target = null) => CredDeleteW(target ?? Target, CRED_TYPE_GENERIC, 0);
}

/// <summary>Plugs Windows Credential Manager into Core.</summary>
public sealed class WindowsSecretStore : ISecretStore
{
    public string? Read(string target) => CredentialStore.Read(target);
    public bool Write(string target, string secret) => CredentialStore.Write(secret, target);
    public void Delete(string target) => CredentialStore.Delete(target);
}
