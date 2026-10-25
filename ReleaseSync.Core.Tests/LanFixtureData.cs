using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ReleaseSync.Core;

namespace ReleaseSync.Core.Tests;

/// <summary>
/// The fixtures for the local-network protocol (docs/LAN-PRESENCE.md), written by the C# core and read by the Swift package.
/// Everything is made up: fake names, fake keys, fake secrets. Nothing depends on the machine or the clock.
/// </summary>
public static class LanFixtureData
{
    static string Text(object o) => FixtureData.Text(o);
    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, o.GetType(), Store.Options);
    /// <summary>A deep copy as the same type (not as raw JSON), so the limits can be applied to the copy without touching the original.</summary>
    static object Copy(object o) => JsonSerializer.Deserialize(JsonSerializer.Serialize(o, o.GetType(), Store.Options), o.GetType(), Store.Options)!;
    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    public static byte[] FromHex(string h) => Convert.FromHexString(h);
    static string B64(byte[] b) => Convert.ToBase64String(b);

    static readonly DateTime T = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // ---------------------------------------------------------------- messages.json
    /// <summary>A message as the writer must produce it: the message object, and the exact compact text.</summary>
    static object Write(string name, string description, object message)
    {
        var shown = J(Copy(message));                       // what goes in (before the limits are applied)
        return new { Name = name, Description = description, Message = shown, Text = LanMessages.SerializeToString(Copy(message)) };
    }

    /// <summary>A text as a reader receives it: what the reader makes of it, written back out as compact text; or that it is ignored; or that it is an error.</summary>
    static object Read(string name, string description, string input)
    {
        try
        {
            var m = LanMessages.Parse(Encoding.UTF8.GetBytes(input));
            return m is LanUnknown u
                ? new { Name = name, Description = description, Input = input, Outcome = "ignored", Text = (string?)null }
                : new { Name = name, Description = description, Input = input, Outcome = "message", Text = (string?)LanMessages.SerializeToString(m) };
        }
        catch (LanProtocolException) { return new { Name = name, Description = description, Input = input, Outcome = "error", Text = (string?)null }; }
    }

    static string Id(char c) => new string(c, 32);

    static List<object> WriteCases()
    {
        var long22 = string.Concat(Enumerable.Repeat("€", 22));        // 66 bytes of UTF-8: over the 64-byte limit, and a cut at 64 would fall inside a character
        return new List<object>
        {
            Write("pair1", "First pairing message (Joiner to Host). Binary values are standard Base64.", new LanPair1 { Name = "Example Mac", Platform = "Mac", Commit = B64(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) }),
            Write("pair2", "Host's answer: its key (65 bytes) and nonce (16 bytes).", new LanPair2 { Name = "Example PC", Platform = "Windows", Key = B64(Enumerable.Range(0, 65).Select(i => (byte)(i == 0 ? 4 : i)).ToArray()), Nonce = B64(Enumerable.Range(40, 16).Select(i => (byte)i).ToArray()) }),
            Write("pair3", "The Joiner opens its commitment.", new LanPair3 { Key = B64(Enumerable.Range(0, 65).Select(i => (byte)(i == 0 ? 4 : 100 + i)).ToArray()), Nonce = B64(Enumerable.Range(60, 16).Select(i => (byte)i).ToArray()) }),
            Write("pair4", "The Joiner's proof, sent after its user tapped Yes.", new LanPair4 { Proof = B64(Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray()) }),
            Write("pair5", "The Host's proof, sent after its user tapped Yes and a valid pair4.", new LanPair5 { Proof = B64(Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray()) }),
            Write("hello", "The first record each side sends after the handshake.", new LanHello { DeviceId = Id('a'), Name = "Example Mac", Platform = "Mac", AppVersion = "1.0.0" }),
            Write("hello-without-device-id", "A property with no value is left out, not written as null.", new LanHello { Name = "Example Mac", Platform = "Mac", AppVersion = "1.0.0" }),
            Write("hello-escaping", "Non-ASCII and < > & ' + are written as \\uXXXX with upper-case hex; a character outside the basic plane is a surrogate pair.", new LanHello { DeviceId = Id('b'), Name = "Café <b> & 'x' + 😀", Platform = "Mac", AppVersion = "1.0.0" }),
            Write("hello-name-too-long", "Free text is cut to its limit (Name: 64 bytes) between characters: 22 euro signs are 66 bytes, so 21 are kept.", new LanHello { DeviceId = Id('c'), Name = long22, Platform = "Mac", AppVersion = "1.0.0" }),
            Write("state-empty", "Nothing installed: Open false is still written, and the empty list too.", new LanState { Seq = 1, Open = false }),
            Write("state-installed", "Entries are written in key order. Optional properties with no value are left out. Times are UTC with a trailing Z and no trailing zeros in the fraction.", new LanState
            {
                Seq = 2, Open = true, Installed = new List<LanInstalled>
                {
                    new() { Key = "github:fake/zeta", Version = "2.0.0", InstalledAt = T, Latest = "2.1.0" },
                    new() { Key = "github:fake/alpha", Version = "1.0.0" },
                    new() { Key = "codeberg:fake/beta", Version = "0.9", InstalledAt = T.AddTicks(1234567), Latest = "0.9" },
                    new() { Key = "github:fake/gamma", Version = "3", InstalledAt = T.AddMilliseconds(120) },
                },
            }),
            Write("ping", "Sent after 2 seconds with nothing sent.", new LanPing { N = 7 }),
            Write("pong", "The answer to a ping, with the same N.", new LanPong { N = 7 }),
            Write("goodbye", "A normal quit.", new LanGoodbye()),
            Write("goodbye-with-reason", "The reason is optional.", new LanGoodbye { Reason = "quit" }),
            Write("forget", "Sent just before a device deletes the pairing.", new LanForget()),
        };
    }

    static List<object> ReadCases()
    {
        string Inst(string key, string version, string? latest = null) => $"{{\"Key\":\"{key}\",\"Version\":\"{version}\"" + (latest == null ? "" : $",\"Latest\":\"{latest}\"") + "}";
        return new List<object>
        {
            Read("plain-ping", "A message in the exact form it is written in.", "{\"Type\":\"ping\",\"N\":3}"),
            Read("names-in-any-case", "Property names match in any case when reading; the Type value itself is case-sensitive and is written back in the proper form.", "{\"type\":\"ping\",\"n\":3}"),
            Read("whitespace-and-order", "Spaces, line breaks and a different property order are fine when reading.", "{ \"N\" : 3 ,\n \"Type\" : \"pong\" }"),
            Read("unknown-properties", "Properties this version does not know are ignored.", "{\"Type\":\"ping\",\"N\":3,\"Future\":{\"x\":[1,2,3]},\"Other\":true}"),
            Read("missing-properties", "A property that is missing reads as 0, false, an empty list or an empty text.", "{\"Type\":\"state\"}"),
            Read("missing-number", "A ping with no N reads as N 0.", "{\"Type\":\"ping\"}"),
            Read("unknown-type", "A Type this version does not know is ignored, not an error.", "{\"Type\":\"suggest\",\"Key\":\"github:fake/x\"}"),
            Read("type-is-case-sensitive", "\"PING\" is not \"ping\": it is a Type this version does not know.", "{\"Type\":\"PING\",\"N\":3}"),
            Read("no-type", "An object with no Type is an error.", "{\"N\":3}"),
            Read("type-not-text", "A Type that is not text is an error.", "{\"Type\":5}"),
            Read("not-an-object", "A message must be a JSON object.", "[1,2,3]"),
            Read("not-json", "Not JSON at all is an error.", "hello there"),
            Read("empty", "Nothing is an error.", ""),
            Read("trailing-garbage", "Anything after the object is an error.", "{\"Type\":\"ping\",\"N\":3} x"),
            Read("wrong-type-of-number", "A number that is text is an error.", "{\"Type\":\"ping\",\"N\":\"3\"}"),
            Read("number-too-large", "A number that does not fit is an error.", "{\"Type\":\"ping\",\"N\":99999999999}"),
            Read("wrong-type-of-flag", "A flag that is not true or false is an error.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":\"yes\",\"Installed\":[]}"),
            Read("installed-not-a-list", "A list that is not a list is an error.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":5}"),
            Read("installed-null", "A null list reads as an empty one.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":null}"),
            Read("hello-device-id-upper-case", "A DeviceId must be lower-case hex digits; anything else is dropped (the rest of the hello is kept).", "{\"Type\":\"hello\",\"Proto\":1,\"DeviceId\":\"" + new string('A', 32) + "\",\"Name\":\"Example\",\"Platform\":\"Mac\",\"AppVersion\":\"1\"}"),
            Read("hello-device-id-too-long", "A DeviceId over 32 bytes is dropped.", "{\"Type\":\"hello\",\"Proto\":1,\"DeviceId\":\"" + new string('a', 33) + "\",\"Name\":\"Example\",\"Platform\":\"Mac\",\"AppVersion\":\"1\"}"),
            Read("hello-device-id-not-hex", "A DeviceId that is not hex is dropped.", "{\"Type\":\"hello\",\"Proto\":1,\"DeviceId\":\"" + new string('g', 32) + "\",\"Name\":\"Example\",\"Platform\":\"Mac\",\"AppVersion\":\"1\"}"),
            Read("hello-texts-too-long", "Name (64 bytes), Platform (32) and AppVersion (32) are cut to their limits.", "{\"Type\":\"hello\",\"Proto\":1,\"DeviceId\":\"" + Id('d') + "\",\"Name\":\"" + new string('n', 70) + "\",\"Platform\":\"" + new string('p', 40) + "\",\"AppVersion\":\"" + new string('v', 40) + "\"}"),
            Read("hello-cut-inside-a-character", "The cut falls between characters: 22 euro signs (3 bytes each) become 21, not 21 plus half of one.", "{\"Type\":\"hello\",\"Proto\":1,\"Name\":\"" + string.Concat(Enumerable.Repeat("€", 22)) + "\",\"Platform\":\"Mac\",\"AppVersion\":\"1\"}"),
            Read("hello-escapes-in-input", "Any valid JSON escape is accepted when reading (a surrogate pair, \\u0041, \\n); the writer then uses its own escaping.", "{\"Type\":\"hello\",\"Proto\":1,\"Name\":\"\\ud83d\\ude00 \\u0041\\n\\\"q\\\"\",\"Platform\":\"Mac\",\"AppVersion\":\"1\"}"),
            Read("installed-sorted", "The list is put in key order (ordinal), whatever order it arrived in.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[" + string.Join(",", Inst("github:fake/b", "1"), Inst("codeberg:fake/z", "1"), Inst("github:fake/a", "1"), Inst("github:Fake/B2", "1")) + "]}"),
            Read("installed-duplicate-key-last-wins", "If a key appears more than once, the last entry wins.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[" + string.Join(",", Inst("github:fake/a", "1", "1"), Inst("github:fake/b", "1"), Inst("github:fake/a", "2", "3")) + "]}"),
            Read("installed-empty-key-skipped", "An entry with an empty key is skipped; a missing key counts as empty.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[" + string.Join(",", Inst("", "1"), "{\"Version\":\"9\"}", Inst("github:fake/a", "1")) + "]}"),
            Read("installed-key-too-long", "A key over 200 bytes skips the entry (200 exactly is kept).", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[" + string.Join(",", Inst("github:" + new string('k', 193), "1"), Inst("github:" + new string('k', 194), "1")) + "]}"),
            Read("installed-version-too-long", "A Version or Latest over 64 bytes skips the entry (64 exactly is kept).", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[" + string.Join(",", Inst("github:fake/a", new string('1', 64)), Inst("github:fake/b", new string('1', 65)), Inst("github:fake/c", "1", new string('2', 64)), Inst("github:fake/d", "1", new string('2', 65))) + "]}"),
            Read("installed-time-spellings", "InstalledAt is read in any of the spellings of a UTC time and written back in the standard one.", "{\"Type\":\"state\",\"Seq\":1,\"Open\":true,\"Installed\":[{\"Key\":\"github:fake/a\",\"Version\":\"1\",\"InstalledAt\":\"2026-01-02T03:04:05Z\"},{\"Key\":\"github:fake/b\",\"Version\":\"1\",\"InstalledAt\":\"2026-01-02T03:04:05.000Z\"},{\"Key\":\"github:fake/c\",\"Version\":\"1\",\"InstalledAt\":\"2026-01-02T03:04:05.1234567Z\"},{\"Key\":\"github:fake/d\",\"Version\":\"1\",\"InstalledAt\":\"2026-01-02T05:04:05+02:00\"}]}"),
        };
    }

    // ---------------------------------------------------------------- the whole set
    public static Dictionary<string, string> Build()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        files["lan/README.md"] = Readme;
        files["lan/messages.json"] = Text(new
        {
            Description = "Section 8 of docs/LAN-PRESENCE.md. Writing: build the message from Message, write it, and the bytes must equal Text (UTF-8, compact). Reading: parse Input; Outcome says what must happen: \"message\" (apply the limits of section 11, then write the result: it must equal Text), \"ignored\" (a Type this version does not know) or \"error\" (the connection is closed).",
            Writing = WriteCases(),
            Reading = ReadCases(),
        });
        return files;
    }

    static readonly string Readme = """
        # Local-network protocol fixtures

        Files written by the C# core (`ReleaseSync.Core/Lan`) for the protocol in `docs/LAN-PRESENCE.md`. Other implementations are tested against them. Everything is made up: fake names, fake keys, fake secrets. Never put anything real in these files.

        | File | Section | What it is |
        |---|---|---|
        | `messages.json` | 8, 11 | Every message as it is written, and how odd, oversized and unknown input is read. |

        Bytes are written as lower-case hex unless a file says Base64. JSON values in the files are pretty-printed for reading; the texts that are compared byte for byte are inside strings.

        ## Reading rules these fixtures settle

        The spec says what to ignore; these cases also fix what it leaves open:

        - A property that is missing reads as `0`, `false`, an empty list or an empty text.
        - A `Type` is matched exactly (case-sensitive). Property names match in any case.
        - An object with no `Type`, JSON that does not parse, anything after the object, a value of the wrong kind, or a number that does not fit in 32 bits is an error. A `Type` this version does not know is ignored.
        - The installed list: skip entries whose key is empty or over 200 bytes or whose `Version` or `Latest` is over 64 bytes; if a key appears more than once the last entry wins; sort by key (ordinal); keep the first 1,000.
        - Free text is cut to its limit in UTF-8 bytes, between characters, with nothing added.
        """;
}
