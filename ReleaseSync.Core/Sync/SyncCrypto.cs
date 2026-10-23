using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReleaseSync.Core;

public sealed class SyncException : Exception
{
    public SyncException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thrown when the sync file is locked and the passphrase is missing or wrong.</summary>
public sealed class SyncPassphraseException : Exception
{
    public SyncPassphraseException(string message) : base(message) { }
}

/// <summary>
/// Optional end-to-end encryption for the sync file: AES-256-GCM with a key made from your passphrase (PBKDF2-SHA256).
/// Wherever the file is stored, only someone with the passphrase can read it.
/// </summary>
public static class SyncCrypto
{
    const string Marker = "releasesync-enc";
    const int Iterations = 200_000;

    public static bool IsEncrypted(string text)
    {
        var t = text.AsSpan().TrimStart();
        return t.Length > 0 && t[0] == '{' && text.Contains("\"" + Marker + "\"", StringComparison.Ordinal);
    }

    static byte[] Key(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static string Encrypt(string plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(Key(passphrase, salt, Iterations), 16)) aes.Encrypt(nonce, data, cipher, tag, Encoding.ASCII.GetBytes(Marker));
        return JsonSerializer.Serialize(new Envelope
        {
            V = 1, Kdf = "pbkdf2-sha256", Iter = Iterations, Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Decrypt(string envelope, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new SyncPassphraseException("This sync file is locked. Enter the passphrase you set up on your other device.");
        Envelope e;
        try { e = JsonSerializer.Deserialize<Envelope>(envelope) ?? throw new SyncException("The sync file is damaged."); }
        catch (JsonException ex) { throw new SyncException("The sync file is damaged.", ex); }
        if (e.V != 1 || e.Iter is < 1000 or > 10_000_000) throw new SyncException("The sync file was made by a newer version of ReleaseSync. Update ReleaseSync and try again.");
        try
        {
            var salt = Convert.FromBase64String(e.Salt); var nonce = Convert.FromBase64String(e.Nonce);
            var tag = Convert.FromBase64String(e.Tag); var cipher = Convert.FromBase64String(e.Data);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(passphrase, salt, e.Iter), 16);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(Marker));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { throw new SyncPassphraseException("That passphrase doesn't open the sync file."); }
        catch (FormatException ex) { throw new SyncException("The sync file is damaged.", ex); }
    }

    sealed class Envelope
    {
        [System.Text.Json.Serialization.JsonPropertyName(Marker)] public int V { get; set; }
        public string Kdf { get; set; } = "";
        public int Iter { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }
}
