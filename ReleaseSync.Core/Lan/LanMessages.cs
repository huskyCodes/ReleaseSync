using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReleaseSync.Core;

/// <summary>A message that breaks the rules of docs/LAN-PRESENCE.md (not JSON, wrong shape, too big…). The connection that sent it is closed.</summary>
public sealed class LanProtocolException : Exception
{
    public LanProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The numbers from section 3 and section 11 of the spec.</summary>
public static class LanLimits
{
    public const int Proto = 1;
    public const int HandshakeMessage = 1024;
    public const int PairingMessage = 1024;
    public const int RecordPlaintext = 524_288;
    public const int RecordCiphertext = RecordPlaintext + 16;
    public const int InstalledEntries = 1000;
    public const int KeyBytes = 200;
    public const int VersionBytes = 64;
    public const int DeviceIdBytes = 32;
    public const int NameBytes = 64;
    public const int PlatformBytes = 32;
    public const int AppVersionBytes = 32;
    public const int PairedDevices = 16;
    public const int Connections = 16;
}

// ---- the messages, with their properties in the order the spec gives them (it is the order they are written in) ----

public sealed class LanPair1 { public string Type { get; set; } = "pair1"; public int Proto { get; set; } = LanLimits.Proto; public string Name { get; set; } = ""; public string Platform { get; set; } = ""; public string Commit { get; set; } = ""; }
public sealed class LanPair2 { public string Type { get; set; } = "pair2"; public string Name { get; set; } = ""; public string Platform { get; set; } = ""; public string Key { get; set; } = ""; public string Nonce { get; set; } = ""; }
public sealed class LanPair3 { public string Type { get; set; } = "pair3"; public string Key { get; set; } = ""; public string Nonce { get; set; } = ""; }
public sealed class LanPair4 { public string Type { get; set; } = "pair4"; public string Proof { get; set; } = ""; }
public sealed class LanPair5 { public string Type { get; set; } = "pair5"; public string Proof { get; set; } = ""; }

public sealed class LanHello
{
    public string Type { get; set; } = "hello";
    public int Proto { get; set; } = LanLimits.Proto;
    /// <summary>The device's id in the sync file's Devices list (32 lower-case hex digits). Empty or missing when it is not valid.</summary>
    public string? DeviceId { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AppVersion { get; set; } = "";
}

public sealed class LanInstalled
{
    public string Key { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? InstalledAt { get; set; }
    public string? Latest { get; set; }
}

public sealed class LanState
{
    public string Type { get; set; } = "state";
    public int Seq { get; set; }
    public bool Open { get; set; }
    public List<LanInstalled> Installed { get; set; } = new();
}

public sealed class LanPing { public string Type { get; set; } = "ping"; public int N { get; set; } }
public sealed class LanPong { public string Type { get; set; } = "pong"; public int N { get; set; } }
public sealed class LanGoodbye { public string Type { get; set; } = "goodbye"; public string? Reason { get; set; } }
public sealed class LanForget { public string Type { get; set; } = "forget"; }

/// <summary>A message with a Type this version does not know. It is ignored, not an error.</summary>
public sealed class LanUnknown { public string Type { get; set; } = ""; }

/// <summary>Writing and reading the messages exactly as docs/LAN-PRESENCE.md section 8 says, with the limits of section 11 applied when reading.</summary>
public static class LanMessages
{
    // same conventions as the sync file (names in PascalCase, null left out, the default string escaping), but compact: no spaces, no line breaks
    static readonly JsonSerializerOptions Write = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The message as compact UTF-8 JSON. Names, platforms and the other free text are shortened to their limits first, so what is written is what a reader will accept.</summary>
    public static byte[] Serialize(object message)
    {
        message = Limited(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Write);
    }

    public static string SerializeToString(object message) => Encoding.UTF8.GetString(Serialize(message));

    /// <summary>
    /// A received message. Unknown properties are ignored and a message with an unknown Type comes back as <see cref="LanUnknown"/>.
    /// Not JSON, not an object, or no Type: <see cref="LanProtocolException"/>.
    /// </summary>
    public static object Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > LanLimits.RecordPlaintext) throw new LanProtocolException("The message is too big.");
        string type;
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new LanProtocolException("A message must be a JSON object.");
            var typeProp = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Type", StringComparison.OrdinalIgnoreCase));
            if (typeProp.Value.ValueKind != JsonValueKind.String) throw new LanProtocolException("A message needs a Type.");
            type = typeProp.Value.GetString() ?? "";
        }
        catch (JsonException ex) { throw new LanProtocolException("The message is not valid JSON.", ex); }

        try
        {
            object? m = type switch
            {
                "pair1" => JsonSerializer.Deserialize<LanPair1>(utf8, Read),
                "pair2" => JsonSerializer.Deserialize<LanPair2>(utf8, Read),
                "pair3" => JsonSerializer.Deserialize<LanPair3>(utf8, Read),
                "pair4" => JsonSerializer.Deserialize<LanPair4>(utf8, Read),
                "pair5" => JsonSerializer.Deserialize<LanPair5>(utf8, Read),
                "hello" => JsonSerializer.Deserialize<LanHello>(utf8, Read),
                "state" => JsonSerializer.Deserialize<LanState>(utf8, Read),
                "ping" => JsonSerializer.Deserialize<LanPing>(utf8, Read),
                "pong" => JsonSerializer.Deserialize<LanPong>(utf8, Read),
                "goodbye" => JsonSerializer.Deserialize<LanGoodbye>(utf8, Read),
                "forget" => JsonSerializer.Deserialize<LanForget>(utf8, Read),
                _ => new LanUnknown { Type = type },
            };
            return m == null ? throw new LanProtocolException("The message could not be read.") : Limited(m);
        }
        catch (JsonException ex) { throw new LanProtocolException("The message does not have the shape its Type needs.", ex); }
    }

    /// <summary>Applies the limits of section 11 to the free text and the installed list.</summary>
    public static object Limited(object m)
    {
        switch (m)
        {
            case LanPair1 p: p.Name = Shorten(p.Name, LanLimits.NameBytes); p.Platform = Shorten(p.Platform, LanLimits.PlatformBytes); break;
            case LanPair2 p: p.Name = Shorten(p.Name, LanLimits.NameBytes); p.Platform = Shorten(p.Platform, LanLimits.PlatformBytes); break;
            case LanHello h:
                h.Name = Shorten(h.Name, LanLimits.NameBytes); h.Platform = Shorten(h.Platform, LanLimits.PlatformBytes); h.AppVersion = Shorten(h.AppVersion, LanLimits.AppVersionBytes);
                if (!ValidDeviceId(h.DeviceId)) h.DeviceId = null;
                break;
            case LanState s: s.Installed = Inventory(s.Installed); break;
        }
        return m;
    }

    /// <summary>At most <paramref name="maxBytes"/> of UTF-8, cut between characters (never in the middle of one), with nothing added.</summary>
    public static string Shorten(string? s, int maxBytes)
    {
        s ??= "";
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var sb = new StringBuilder(); int used = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (used + n > maxBytes) break;
            sb.Append(rune.ToString()); used += n;
        }
        return sb.ToString();
    }

    /// <summary>Times in messages are UTC. A time with an offset is converted to UTC; a time with no zone at all is taken to be UTC already. Never the machine's time zone.</summary>
    static DateTime Utc(DateTime t) => t.Kind switch { DateTimeKind.Local => t.ToUniversalTime(), DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc), _ => t };

    static bool ValidDeviceId(string? id) => id is { Length: > 0 and <= LanLimits.DeviceIdBytes } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// What to use from a device's installed list: entries with an empty key, or a key, version or newest-version that is too long, are skipped;
    /// if a key appears more than once the last entry wins; the result is in key order (ordinal) and holds at most 1,000 entries.
    /// </summary>
    public static List<LanInstalled> Inventory(IEnumerable<LanInstalled>? entries)
    {
        var byKey = new Dictionary<string, LanInstalled>(StringComparer.Ordinal);
        foreach (var e in entries ?? Enumerable.Empty<LanInstalled>())
        {
            if (e == null || string.IsNullOrEmpty(e.Key) || Encoding.UTF8.GetByteCount(e.Key) > LanLimits.KeyBytes) continue;
            if (Encoding.UTF8.GetByteCount(e.Version ?? "") > LanLimits.VersionBytes || Encoding.UTF8.GetByteCount(e.Latest ?? "") > LanLimits.VersionBytes) continue;
            e.Version ??= "";
            e.InstalledAt = e.InstalledAt is { } t ? Utc(t) : null;
            byKey[e.Key] = e;
        }
        return byKey.Values.OrderBy(e => e.Key, StringComparer.Ordinal).Take(LanLimits.InstalledEntries).ToList();
    }
}
