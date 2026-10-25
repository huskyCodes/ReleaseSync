using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReleaseSync.Core.Tests;

/// <summary>
/// The golden fixtures: files written by the C# sync engine that other implementations (the Swift package, later Android) must read the same way.
/// Everything here is made up. Documents always get explicit times and names, so nothing depends on the machine or the clock.
/// </summary>
public static class FixtureData
{
    public static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(double minutes) => T0.AddMinutes(minutes);

    public const string Pass = "fake passphrase for tests";

    /// <summary>The fixtures folder in the source tree (found by walking up from the test binaries to the solution file).</summary>
    public static string Dir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReleaseSync.sln"))) d = d.Parent;
            if (d == null) throw new InvalidOperationException("Can't find ReleaseSync.sln above the test binaries.");
            return Path.Combine(d.FullName, "ReleaseSync.Core.Tests", "Fixtures");
        }
    }

    // ---------------- small builders ----------------
    static string Norm(string s) => s.Replace("\r\n", "\n");
    public static string Text(object o) => Norm(JsonSerializer.Serialize(o, o.GetType(), Store.Options)) + "\n";
    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, o.GetType(), Store.Options);
    public static T Clone<T>(T o) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o, Store.Options), Store.Options)!;

    static SyncDoc Doc(string by, DateTime updated, Action<SyncDoc>? fill = null) { var d = new SyncDoc { WrittenBy = by, UpdatedAt = updated }; fill?.Invoke(d); return d; }

    static SyncApp App(string owner, string repo, DateTime added, DateTime modified, Action<SyncApp>? edit = null, string source = "github")
    {
        var a = new SyncApp { Key = $"{source}:{owner}/{repo}".ToLowerInvariant(), Source = source, Owner = owner, Repo = repo, AddedAt = added, ModifiedAt = modified };
        edit?.Invoke(a);
        return a;
    }

    /// <summary>Don't give these a Note in fixtures: the engine resets ModifiedAt to the clock when it reads a Try later item that has one (see the README).</summary>
    static TryLaterItem Later(string owner, string repo, DateTime saved, DateTime? modified = null, string? note = null, Action<TryLaterItem>? edit = null, string source = "github")
    {
        var i = new TryLaterItem { Owner = owner, Repo = repo, Source = source, SavedAt = saved };
        if (note != null) i.Note = note;
        i.ModifiedAt = modified;
        edit?.Invoke(i);
        return i;
    }

    static SyncDevice Dev(string id, string name, string platform, DateTime seen, string version = "0.1", bool? online = null, List<SyncInstall>? installed = null) =>
        new() { Id = id, Name = name, Platform = platform, AppVersion = version, LastSeen = seen, Online = online, Installed = installed };

    static SyncInstall Inst(string key, string version, DateTime? installedAt = null, string? latest = null) => new() { Key = key, Version = version, InstalledAt = installedAt, Latest = latest };

    static readonly SyncDevice PcInfo = Dev("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Example PC", "Windows", T0, "1.9.1");
    static readonly SyncDevice MacInfo = Dev("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Example Mac", "Mac", T0);

    // ---------------- merge cases ----------------
    public sealed record MergeCase(string Name, string Description, DateTime Now, SyncDoc A, SyncDoc B);

    public static List<MergeCase> MergeCases()
    {
        var cases = new List<MergeCase>();
        void Add(string name, string description, DateTime now, SyncDoc a, SyncDoc b) => cases.Add(new MergeCase(name, description, now, a, b));
        var pc = "Example PC"; var mac = "Example Mac";

        Add("01-empty", "Two empty documents merge into an empty one stamped with the sync time.", At(10), Doc(pc, At(0)), Doc(mac, At(1)));

        Add("02-union-and-ordering",
            "Everything from both sides is kept. Apps and Try later items are sorted by key in plain character order (a-b, a.b, a1, a_b, ab), custom groups ignoring case. The key is lower-case even when Owner is not.",
            At(10),
            Doc(pc, At(1), d =>
            {
                d.Apps.Add(App("Fake-Owner", "Zeta-App", At(0), At(1)));
                d.Apps.Add(App("fake", "a_b", At(0), At(1)));
                d.Apps.Add(App("fake", "ab", At(0), At(1)));
                d.Apps.Add(App("fake", "a-b", At(0), At(1)));
                d.Apps.Add(App("example", "tool", At(0), At(1), source: "gitlab"));
                d.TryLater.Add(Later("Fake-Owner", "Later-One", At(1)));
                d.TryLater.Add(Later("fake", "z-last", At(1)));
                d.CustomGroups.AddRange(new[] { "Zebra", "apple" }); d.GroupStamps["Zebra"] = At(0); d.GroupStamps["apple"] = At(0);
                d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(1), PcInfo.AppVersion);
            }),
            Doc(mac, At(2), d =>
            {
                d.Apps.Add(App("fake", "a.b", At(0), At(2)));
                d.Apps.Add(App("fake", "a1", At(0), At(2)));
                d.Apps.Add(App("sample", "site-app", At(0), At(2), source: "codeberg"));
                d.Apps.Add(App("sample", "forge-app", At(0), At(2), source: "forgejo:git.example.test"));
                d.TryLater.Add(Later("fake", "a-first", At(2)));
                d.CustomGroups.AddRange(new[] { "Mango", "Banana" }); d.GroupStamps["Mango"] = At(0); d.GroupStamps["Banana"] = At(0);
                d.Devices[MacInfo.Id] = Dev(MacInfo.Id, MacInfo.Name, MacInfo.Platform, At(2));
            }));

        Add("03-newer-edit-wins",
            "For the same app the later ModifiedAt wins, even by one tick (100 ns). In 'fake/one-tick' the loser would win a content tie-break, so a reader that rounds times gets it wrong.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.Apps.Add(App("fake", "one-tick", At(0), At(5).AddTicks(1), a => { a.Note = "from a"; a.Pinned = true; }));
                d.Apps.Add(App("fake", "b-is-newer", At(0), At(5), a => a.Note = "old note"));
                d.Apps.Add(App("fake", "fractions", At(0).AddTicks(1234567), At(6).AddTicks(1234567), a => a.DisplayName = "Seven digit times"));
            }),
            Doc(mac, At(31), d =>
            {
                d.Apps.Add(App("fake", "one-tick", At(0), At(5), a => { a.Note = "from b"; a.Pinned = false; }));
                d.Apps.Add(App("fake", "b-is-newer", At(0), At(6), a => { a.Note = "new note"; a.Group = "Tools"; a.Mode = InstallMode.Portable; }));
                d.Apps.Add(App("fake", "fractions", At(0).AddTicks(1234567), At(6).AddTicks(1234566), a => a.DisplayName = "One tick older"));
            }));

        Add("04-tie-break-by-content",
            "Exactly equal ModifiedAt but different content: the version whose JSON text is greater (compared character by character) wins. Each app differs at a different property.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.Apps.Add(App("fake", "tie-note", At(0), At(3), a => a.Note = "apple"));
                d.Apps.Add(App("fake", "tie-pinned", At(0), At(3), a => a.Pinned = true));
                d.Apps.Add(App("fake", "tie-mode", At(0), At(3), a => a.Mode = InstallMode.Installer));
                d.Apps.Add(App("fake", "tie-group", At(0), At(3), a => a.Group = "Tools"));
                d.Apps.Add(App("fake", "tie-note-or-none", At(0), At(3), a => a.Note = "has a note"));
                d.Apps.Add(App("fake", "tie-name", At(0), At(3), a => a.DisplayName = "Zed"));
                d.Apps.Add(App("fake", "tie-added", At(0), At(3)));
                d.Apps.Add(App("fake", "tie-added-fraction", At(0), At(3)));
            }),
            Doc(mac, At(31), d =>
            {
                d.Apps.Add(App("fake", "tie-note", At(0), At(3), a => a.Note = "banana"));
                d.Apps.Add(App("fake", "tie-pinned", At(0), At(3), a => a.Pinned = false));
                d.Apps.Add(App("fake", "tie-mode", At(0), At(3), a => a.Mode = InstallMode.Portable));
                d.Apps.Add(App("fake", "tie-group", At(0), At(3)));
                d.Apps.Add(App("fake", "tie-note-or-none", At(0), At(3)));
                d.Apps.Add(App("fake", "tie-name", At(0), At(3), a => a.DisplayName = "Alpha"));
                d.Apps.Add(App("fake", "tie-added", At(1), At(3)));
                d.Apps.Add(App("fake", "tie-added-fraction", At(0).AddMilliseconds(500), At(3)));
            }));

        // Each pair is chosen so that comparing the escaped text and comparing the raw text give different winners.
        var esc = new (string Name, string A, string B)[]
        {
            ("accent", "\u00E9", "z"), ("plus", "+", "["), ("emoji", "\U0001F600", "z"), ("quote", "say \"hi\"", "say [hi]"),
            ("backslash", "a\\b", "a[b"), ("newline", "a\nb", "a[b"), ("apostrophe", "it's", "it[s"), ("angle", "<b>", "[b]"), ("ampersand", "a&b", "a[b"),
            ("slash", "a/b", "a0b"),
        };
        Add("05-tie-break-escaping",
            "Equal ModifiedAt, notes that need JSON escaping. The winner is decided on the escaped text (\\u00E9, \\u002B, \\u0022, surrogate pairs for emoji...), and '/' is not escaped. Pairs where the raw text would pick the other note.",
            At(60),
            Doc(pc, At(30), d => { foreach (var e in esc) d.Apps.Add(App("fake", "esc-" + e.Name, At(0), At(3), a => a.Note = e.A)); }),
            Doc(mac, At(31), d => { foreach (var e in esc) d.Apps.Add(App("fake", "esc-" + e.Name, At(0), At(3), a => a.Note = e.B)); }));

        Add("06-tie-break-try-later-and-devices",
            "Ties between Try later items (stamp = later of SavedAt and ModifiedAt) and between devices (same LastSeen). The comparison is on JSON text, so \"Stars\": 5 beats \"Stars\": 12.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.TryLater.Add(Later("fake", "stars", At(1), edit: i => i.Stars = 5));
                d.TryLater.Add(Later("fake", "modified-tie", At(1), At(4), edit: i => i.Description = "from a"));
                d.TryLater.Add(Later("fake", "stamp-is-saved-at", At(9), At(2), edit: i => i.Description = "saved later, modified earlier"));
                d.Devices["cccccccccccccccccccccccccccccccc"] = Dev("cccccccccccccccccccccccccccccccc", "Example Mac", "Mac", At(7));
            }),
            Doc(mac, At(31), d =>
            {
                d.TryLater.Add(Later("fake", "stars", At(1), edit: i => i.Stars = 12));
                d.TryLater.Add(Later("fake", "modified-tie", At(1), At(4), edit: i => i.Description = "from b"));
                d.TryLater.Add(Later("fake", "stamp-is-saved-at", At(1), At(5), edit: i => i.Description = "saved earlier, modified later"));
                d.Devices["cccccccccccccccccccccccccccccccc"] = Dev("cccccccccccccccccccccccccccccccc", "Example Mac 2", "Mac", At(7));
            }));

        Add("07-deleted-after-last-edit",
            "A tombstone at or after the app's ModifiedAt and AddedAt removes the app; the tombstone stays so other devices learn of it.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.Apps.Add(App("fake", "gone-later", At(0), At(10)));
                d.Apps.Add(App("fake", "gone-same-time", At(10), At(10)));
                d.Apps.Add(App("fake", "kept-untouched", At(0), At(10)));
            }),
            Doc(mac, At(31), d =>
            {
                d.Tombstones["github:fake/gone-later"] = At(20);
                d.Tombstones["github:fake/gone-same-time"] = At(10);
            }));

        Add("08-edited-or-readded-after-delete",
            "An edit or a re-add newer than the tombstone keeps the app and drops the tombstone. If either AddedAt or ModifiedAt is newer than the tombstone, the app stays.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.Tombstones["github:fake/edited-after"] = At(5);
                d.Tombstones["github:fake/readded-after"] = At(20);
                d.Tombstones["github:fake/added-after-modified"] = At(20);
            }),
            Doc(mac, At(31), d =>
            {
                d.Apps.Add(App("fake", "edited-after", At(0), At(10)));
                d.Apps.Add(App("fake", "readded-after", At(30), At(30)));
                d.Apps.Add(App("fake", "added-after-modified", At(30), At(10)));
            }));

        Add("09-tombstones-without-items",
            "A tombstone for something neither side has is kept (until it is 90 days old). An app tombstone does not remove a Try later item for the same repository, and the other way round.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.Tombstones["github:fake/never-seen"] = At(3);
                d.Tombstones["github:fake/both-lists"] = At(40);
                d.TryLater.Add(Later("fake", "both-lists", At(1)));
            }),
            Doc(mac, At(31), d =>
            {
                d.Tombstones["later:github:fake/other"] = At(41);
                d.Apps.Add(App("fake", "other", At(0), At(1)));
            }));

        Add("10-try-later-stamps-and-tombstones",
            "A Try later item's stamp is the later of SavedAt and ModifiedAt. Tombstone keys are 'later:' plus the lower-case key. Delete wins at an equal time; a newer save wins over the delete.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.TryLater.Add(Later("fake", "modified-later", At(1), At(10), edit: i => i.Description = "edited on a"));
                d.TryLater.Add(Later("fake", "saved-again", At(20), At(5), edit: i => i.Description = "saved again on a"));
                d.TryLater.Add(Later("Fake-Owner", "Deleted-Equal", At(2), At(10)));
                d.TryLater.Add(Later("fake", "resaved", At(30)));
                d.TryLater.Add(Later("example", "tool", At(1), source: "forgejo:git.example.test"));
            }),
            Doc(mac, At(31), d =>
            {
                d.TryLater.Add(Later("fake", "modified-later", At(1), edit: i => i.Description = "not edited on b"));
                d.TryLater.Add(Later("fake", "saved-again", At(1), At(10), edit: i => i.Description = "older stamp on b"));
                d.Tombstones["later:github:fake-owner/deleted-equal"] = At(10);
                d.Tombstones["later:github:fake/resaved"] = At(10);
                d.Tombstones["later:forgejo:git.example.test:example/tool"] = At(11);
                d.Tombstones["later:github:fake/nothing-here"] = At(12);
            }));

        Add("11-groups",
            "Group names are compared ignoring case; the greater spelling is kept. Blank names are dropped. A group tombstone is 'group:' plus the trimmed lower-case name and removes the group unless the group was created after the deletion.",
            At(60),
            Doc(pc, At(30), d =>
            {
                d.CustomGroups.AddRange(new[] { "Games", "Tools", "My Games ", "Retro", "Old", "Edge" });
                d.GroupStamps["Games"] = At(1); d.GroupStamps["Tools"] = At(1); d.GroupStamps["My Games "] = At(1); d.GroupStamps["Retro"] = At(20); d.GroupStamps["Edge"] = At(7);
            }),
            Doc(mac, At(31), d =>
            {
                d.CustomGroups.AddRange(new[] { "games", "TOOLS", "", "   " });
                d.GroupStamps["games"] = At(2); d.GroupStamps["TOOLS"] = At(2);
                d.Tombstones["group:my games"] = At(5);
                d.Tombstones["group:retro"] = At(10);
                d.Tombstones["group:old"] = At(1);
                d.Tombstones["group:edge"] = At(7);
            }));

        var now = T0.AddDays(100);
        Add("12-tombstone-expiry",
            "Tombstones older than 90 days are forgotten at the end of the merge; one exactly 90 days old is kept. A deletion still applies in the same merge even if its tombstone is then forgotten. Merging the result with an old copy that still has the item brings it back, so this case is not repeatable.",
            now,
            Doc(pc, now.AddMinutes(-5), d =>
            {
                d.Tombstones["github:fake/age-89-days"] = now - TimeSpan.FromDays(89);
                d.Tombstones["github:fake/age-exactly-90-days"] = now - TimeSpan.FromDays(90);
                d.Tombstones["github:fake/age-90-days-and-1-tick"] = now - TimeSpan.FromDays(90) - TimeSpan.FromTicks(1);
                d.Tombstones["later:github:fake/age-120-days"] = now - TimeSpan.FromDays(120);
                d.Tombstones["group:old-group"] = now - TimeSpan.FromDays(200);
                d.Tombstones["github:fake/old-delete"] = now - TimeSpan.FromDays(100);
            }),
            Doc(mac, now.AddMinutes(-4), d => d.Apps.Add(App("fake", "old-delete", now - TimeSpan.FromDays(150), now - TimeSpan.FromDays(150)))));

        Add("13-devices-versions-and-skipped-entries",
            "Devices are matched by Id and the later LastSeen wins (even by one tick). Devices without an Id and apps without a Key are dropped. Version is the higher of the two. WrittenBy comes from the side with the later UpdatedAt.",
            At(60),
            Doc(pc, At(40), d =>
            {
                d.Version = 1;
                d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(5).AddTicks(1), PcInfo.AppVersion);
                d.Devices[MacInfo.Id] = Dev(MacInfo.Id, "Example Mac (old name)", MacInfo.Platform, At(5), "0.0");
                d.Devices["nameless"] = Dev("", "No id", "Windows", At(5));
                d.Apps.Add(new SyncApp { Key = "", Owner = "fake", Repo = "no-key", AddedAt = At(0), ModifiedAt = At(0) });
            }),
            Doc(mac, At(20), d =>
            {
                d.Devices[PcInfo.Id] = Dev(PcInfo.Id, "Example PC (old name)", PcInfo.Platform, At(5), "0.0");
                d.Devices[MacInfo.Id] = Dev(MacInfo.Id, MacInfo.Name, MacInfo.Platform, At(9));
            }));

        var shared = "github:fake/shared-app";
        Add("14-device-inventory",
            "A device entry is taken whole from the side with the later LastSeen, including Online and its Installed list; nothing is combined inside it. At an equal LastSeen the greater JSON text wins, so a list with 'Latest' beats one without, and Online:true beats Online:false. A device that never wrote the new fields (an older app) keeps none.",
            At(60),
            Doc(pc, At(40), d =>
            {
                d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(20), PcInfo.AppVersion, true, new List<SyncInstall> { Inst(shared, "1.0", At(3)), Inst("github:fake/pc-only", "3.1") });
                d.Devices[MacInfo.Id] = Dev(MacInfo.Id, MacInfo.Name, MacInfo.Platform, At(30), MacInfo.AppVersion, false, new List<SyncInstall> { Inst(shared, "1.0", At(3), "1.1") });
                d.Devices["cccccccccccccccccccccccccccccccc"] = Dev("cccccccccccccccccccccccccccccccc", "Example Tablet", "Android", At(7), "0.1", true, new List<SyncInstall> { Inst(shared, "1.0") });
                d.Devices["dddddddddddddddddddddddddddddddd"] = Dev("dddddddddddddddddddddddddddddddd", "Old PC", "Windows", At(8), "1.8");
            }),
            Doc(mac, At(41), d =>
            {
                d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(21), PcInfo.AppVersion, false, new List<SyncInstall>());
                d.Devices[MacInfo.Id] = Dev(MacInfo.Id, MacInfo.Name, MacInfo.Platform, At(29), MacInfo.AppVersion, true, new List<SyncInstall> { Inst(shared, "1.1", At(29)) });
                d.Devices["cccccccccccccccccccccccccccccccc"] = Dev("cccccccccccccccccccccccccccccccc", "Example Tablet", "Android", At(7), "0.1", false, new List<SyncInstall> { Inst(shared, "1.0") });
                d.Devices["dddddddddddddddddddddddddddddddd"] = Dev("dddddddddddddddddddddddddddddddd", "Old PC", "Windows", At(8), "1.8");
            }));

        return cases;
    }

    // ---------------- sync round cases ----------------
    sealed record RoundCase(string Name, string Description, DateTime Now, SyncDoc Local, SyncDoc? Remote, string? RemoteText, string? Passphrase, SyncDevice? Self = null);

    static List<RoundCase> RoundCases()
    {
        var self = MacInfo;
        var now = At(120);
        // this device's copy of the library, as it is after an earlier sync: it already knows about the PC
        SyncDoc Library(string by) => Doc(by, At(0), d =>
        {
            d.Apps.Add(App("fake", "shared-app", At(0), At(1)));
            d.TryLater.Add(Later("fake", "shared-later", At(1)));
            d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(100), PcInfo.AppVersion);
        });
        SyncDoc Remote(DateTime seen, Action<SyncDoc>? more = null) => Doc("Example PC", At(100), d =>
        {
            d.Apps.Add(App("fake", "shared-app", At(0), At(1)));
            d.TryLater.Add(Later("fake", "shared-later", At(1)));
            d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(100), PcInfo.AppVersion);
            d.Devices[self.Id] = Dev(self.Id, self.Name, self.Platform, seen, self.AppVersion);
            more?.Invoke(d);
        });
        var cases = new List<RoundCase>();
        void Add(string name, string description, SyncDoc local, SyncDoc? remote, string? remoteText = null, string? pass = null, SyncDevice? thisDevice = null) =>
            cases.Add(new RoundCase(name, description, now, local, remote, remoteText, pass, thisDevice));
        // what this Mac says about itself in the file
        var mine = new List<SyncInstall> { Inst("github:fake/shared-app", "1.0", At(5), "1.2"), Inst("github:fake/mac-only", "2.0.1", At(6)) };
        SyncDevice MacWith(bool? online, List<SyncInstall>? installed) => Dev(self.Id, self.Name, self.Platform, T0, self.AppVersion, online, installed);
        var locked = Envelope(Text(Remote(now)), Pass, Bytes(1, 16), Bytes(2, 12));

        Add("01-first-sync", "Nothing stored yet: this device writes its library.", Library("Example Mac"), null);
        Add("02-nothing-new", "The file already says the same and this device checked in recently: nothing changes and nothing is written.", Library("Example Mac"), Remote(now - TimeSpan.FromHours(1)));
        Add("03-heartbeat", "Nothing changed, but this device last checked in more than 2 hours ago, so it writes to refresh that.", Library("Example Mac"), Remote(now - TimeSpan.FromHours(3)));
        Add("04-heartbeat-boundary", "Exactly 2 hours is not 'more than 2 hours': nothing is written.", Library("Example Mac"), Remote(now - TimeSpan.FromHours(2)));
        Add("05-file-has-more", "The file has an app this device lacks: the library should be updated (Changed), and nothing needs writing.", Library("Example Mac"),
            Remote(now - TimeSpan.FromHours(1), d => d.Apps.Add(App("fake", "from-the-pc", At(50), At(50)))));
        Add("06-this-device-has-more", "This device has an app the file lacks: it is written.",
            Doc("Example Mac", At(0), d => { d.Apps.Add(App("fake", "shared-app", At(0), At(1))); d.Apps.Add(App("fake", "from-the-mac", At(60), At(60))); d.TryLater.Add(Later("fake", "shared-later", At(1))); d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(100), PcInfo.AppVersion); }),
            Remote(now - TimeSpan.FromHours(1)));
        Add("07-both-have-more", "Each side has something new: the library should be updated and the file written.",
            Doc("Example Mac", At(0), d => { d.Apps.Add(App("fake", "shared-app", At(0), At(1))); d.Apps.Add(App("fake", "from-the-mac", At(60), At(60))); d.TryLater.Add(Later("fake", "shared-later", At(1))); d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(100), PcInfo.AppVersion); }),
            Remote(now - TimeSpan.FromHours(1), d => d.Apps.Add(App("fake", "from-the-pc", At(50), At(50)))));
        Add("08-this-device-not-listed", "The file does not list this device yet: it is written so the device shows up.", Library("Example Mac"), Remote(now, d => d.Devices.Remove(self.Id)));
        Add("09-turn-on-passphrase", "A passphrase is set but the file is not locked: it is written locked.", Library("Example Mac"), Remote(now - TimeSpan.FromHours(1)), pass: Pass);
        Add("10-turn-off-passphrase", "The file is locked but no passphrase is set on this device: the sync stops.", Library("Example Mac"), null, locked);
        Add("11-locked-and-opened", "The file is locked and the passphrase opens it. Nothing else changed, so it is not rewritten.", Library("Example Mac"), null, locked, Pass);
        Add("12-wrong-passphrase", "The passphrase does not open the file: the sync stops and nothing is written.", Library("Example Mac"), null, locked, "wrong passphrase");
        Add("13-newer-format", "The file was made by a newer version of ReleaseSync (Version 4; this one reads up to 3): the sync stops.", Library("Example Mac"), null, Text(Doc("Future", At(0), d => d.Version = 4)));
        Add("14-unreadable-file", "The file is not valid JSON: the sync stops and nothing is written.", Library("Example Mac"), null, "this is not a library");
        Add("15-blank-file", "A blank file counts as nothing stored.", Library("Example Mac"), null, "  \n");
        Add("16-older-format-read", "A file written as Version 2 (before devices listed what they have installed) is still read. The result is Version 3, and the device is listed with the new fields, so it is written.",
            Library("Example Mac"), Remote(now - TimeSpan.FromHours(1), d => d.Version = 2), thisDevice: MacWith(true, mine));
        Add("17-publishes-inventory", "This device says it is online and lists what it has installed; the file does not say so yet, so it is written.",
            Library("Example Mac"), Remote(now - TimeSpan.FromHours(1)), thisDevice: MacWith(true, mine));
        Add("18-inventory-unchanged", "The file already has the same online flag and list (in any order) and this device checked in recently: nothing is written. The order of the list does not matter.",
            Library("Example Mac"), Remote(now - TimeSpan.FromHours(1), d => d.Devices[self.Id] = Dev(self.Id, self.Name, self.Platform, now - TimeSpan.FromHours(1), self.AppVersion, true, mine.AsEnumerable().Reverse().ToList())),
            thisDevice: MacWith(true, mine));
        Add("19-inventory-changed", "One program on this device has a newer version than the file says: the change is written.",
            Library("Example Mac"), Remote(now - TimeSpan.FromHours(1), d => d.Devices[self.Id] = Dev(self.Id, self.Name, self.Platform, now - TimeSpan.FromHours(1), self.AppVersion, true, mine)),
            thisDevice: MacWith(true, new List<SyncInstall> { Inst("github:fake/shared-app", "1.2", At(110), "1.2"), Inst("github:fake/mac-only", "2.0.1", At(6)) }));
        Add("20-goes-offline", "This device is closing and says so: Online becomes false, which is written even though it checked in a minute ago.",
            Library("Example Mac"), Remote(now - TimeSpan.FromMinutes(1), d => d.Devices[self.Id] = Dev(self.Id, self.Name, self.Platform, now - TimeSpan.FromMinutes(1), self.AppVersion, true, mine)),
            thisDevice: MacWith(false, mine));
        Add("21-other-device-details-kept", "What the PC says about itself is kept as the file has it: its list and online flag come through the merge untouched.",
            Library("Example Mac"), Remote(now - TimeSpan.FromHours(1), d => d.Devices[PcInfo.Id] = Dev(PcInfo.Id, PcInfo.Name, PcInfo.Platform, At(110), PcInfo.AppVersion, true, new List<SyncInstall> { Inst("github:fake/shared-app", "0.9", null, "1.2") })));
        return cases;
    }

    // ---------------- crypto ----------------
    static byte[] Bytes(int seed, int n) => Enumerable.Range(0, n).Select(i => (byte)(seed * 37 + i * 11)).ToArray();

    /// <summary>Builds an envelope the way the format describes it (independent of SyncCrypto), with a fixed salt and nonce so the fixture never changes.</summary>
    public static string Envelope(string plain, string pass, byte[] salt, byte[] nonce, int iterations = 200_000)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pass), salt, iterations, HashAlgorithmName.SHA256, 32);
        var data = Encoding.UTF8.GetBytes(plain); var cipher = new byte[data.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag, Encoding.ASCII.GetBytes("releasesync-enc"));
        var node = new JsonObject
        {
            ["releasesync-enc"] = 1, ["Kdf"] = "pbkdf2-sha256", ["Iter"] = iterations,
            ["Salt"] = Convert.ToBase64String(salt), ["Nonce"] = Convert.ToBase64String(nonce), ["Tag"] = Convert.ToBase64String(tag), ["Data"] = Convert.ToBase64String(cipher),
        };
        return Norm(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed record CryptoCase(string Name, string Description, string Passphrase, string[] Wrong, string Plain, string Envelope);

    static List<CryptoCase>? cryptoCache;
    public static List<CryptoCase> CryptoCases() => cryptoCache ??= MakeCryptoCases();

    static List<CryptoCase> MakeCryptoCases()
    {
        var plain = Text(Doc("Example Mac", At(0), d =>
        {
            d.Apps.Add(App("fake", "locked-app", At(0), At(1), a => a.Note = "caf\u00E9 \u2713"));
            d.Devices[MacInfo.Id] = Dev(MacInfo.Id, MacInfo.Name, MacInfo.Platform, At(1));
        }));
        // the a-umlaut written as one character, and as 'a' + a combining mark: the key is made from the UTF-8 bytes as given, with no normalization
        const string composed = "p\u00E4ssw\u00F6rd \u2713 (fake)"; const string decomposed = "pa\u0308sswo\u0308rd \u2713 (fake)";
        return new List<CryptoCase>
        {
            new("01-plain-ascii", "A plain ASCII passphrase.", Pass, new[] { "Fake passphrase for tests", Pass + " " }, plain, Envelope(plain, Pass, Bytes(1, 16), Bytes(2, 12))),
            new("02-non-ascii-passphrase", "Non-ASCII passphrase; the same text written with combining marks is a different passphrase.", composed, new[] { decomposed }, plain, Envelope(plain, composed, Bytes(3, 16), Bytes(4, 12))),
            new("03-spaces-are-kept", "Leading and trailing spaces are part of the passphrase; they are not trimmed.", "  spaced out  ", new[] { "spaced out" }, plain, Envelope(plain, "  spaced out  ", Bytes(5, 16), Bytes(6, 12))),
            new("04-fewer-iterations", "An envelope that says Iter 1000 is read with 1000 iterations (the lowest accepted).", Pass, Array.Empty<string>(), plain, Envelope(plain, Pass, Bytes(7, 16), Bytes(8, 12), 1000)),
        };
    }

    sealed record ErrorCase(string Name, string Description, string? Passphrase, string EnvelopeText);

    static List<ErrorCase> ErrorCases()
    {
        var good = CryptoCases()[0];
        JsonObject Change(Action<JsonObject> edit) { var n = JsonNode.Parse(good.Envelope)!.AsObject(); edit(n); return n; }
        string Out(JsonObject n) => Norm(n.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var data = Convert.FromBase64String(JsonNode.Parse(good.Envelope)!["Data"]!.GetValue<string>()); data[0] ^= 0xFF;
        return new List<ErrorCase>
        {
            new("01-no-passphrase", "No passphrase given.", null, good.Envelope),
            new("02-empty-passphrase", "An empty passphrase counts as none.", "", good.Envelope),
            new("03-wrong-passphrase", "A passphrase that does not open it.", "not it", good.Envelope),
            new("04-tampered-data", "The ciphertext was changed, so the tag does not match.", good.Passphrase, Out(Change(n => n["Data"] = Convert.ToBase64String(data)))),
            new("05-newer-envelope", "The envelope version is 2.", good.Passphrase, Out(Change(n => n["releasesync-enc"] = 2))),
            new("06-too-few-iterations", "Iter is 999.", good.Passphrase, Out(Change(n => n["Iter"] = 999))),
            new("07-too-many-iterations", "Iter is 10,000,001.", good.Passphrase, Out(Change(n => n["Iter"] = 10_000_001))),
            new("08-damaged-base64", "The salt is not Base64.", good.Passphrase, Out(Change(n => n["Salt"] = "***"))),
            new("09-not-json", "Not JSON at all.", good.Passphrase, "{ nope"),
        };
    }

    // ---------------- vectors ----------------
    static (long UnixSeconds, long Ticks) Split(DateTime utc) { var t = utc.Ticks - DateTime.UnixEpoch.Ticks; return (Math.DivRem(t, TimeSpan.TicksPerSecond, out var r), r); }
    static string Z(DateTime utc) => JsonSerializer.Serialize(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Store.Options).Trim('"');

    /// <summary>The instant a time in the file means, the way the merge compares it (a time with no zone is taken as UTC).</summary>
    static DateTime MergeInstant(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;

    static object TimestampVectors()
    {
        var write = new[]
        {
            T0, T0.AddTicks(1), T0.AddTicks(10), T0.AddMilliseconds(100), T0.AddMilliseconds(1.5), T0.AddTicks(1234567), T0.AddTicks(9999999),
            DateTime.UnixEpoch, new DateTime(2000, 2, 29, 23, 59, 59, DateTimeKind.Utc), new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999),
        }.Select(t => { var (s, r) = Split(t); return new { UnixSeconds = s, Ticks = r, Text = Z(t) }; }).ToList();
        var read = new[]
        {
            "2026-01-01T12:00:00Z", "2026-01-01T12:00:00.1Z", "2026-01-01T12:00:00.1234567Z", "2026-01-01T12:00:00.0000001Z",
            "2026-01-01T14:00:00+02:00", "2026-01-01T07:00:00-05:00", "2026-01-01T17:30:00.5+05:30", "2026-01-01T12:00:00", "2026-01-01T12:00:00.25",
        }.Select(text =>
        {
            var t = JsonSerializer.Deserialize<DateTime>("\"" + text + "\"");
            var utc = MergeInstant(t); var (s, r) = Split(utc);
            return new { Text = text, UnixSeconds = s, Ticks = r, Canonical = Z(utc) };
        }).ToList();
        return new
        {
            Description = "Write: an instant (whole Unix seconds + 100 ns ticks) becomes this text. Read: this text means this instant, as the merge compares it; a time with no zone is taken as UTC, a time with an offset is converted. Always write UTC with a Z.",
            Write = write, Read = read,
        };
    }

    static object PresenceVectors()
    {
        var now = At(1000);
        object Online(string name, bool? online, TimeSpan? age)
        {
            var d = Dev("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Example", "Windows", age == null ? now : now - age.Value, "1.0", online);
            return new { Name = name, Device = J(d), Now = now, IsOnline = SyncPresence.IsOnline(d, now) };
        }
        var lights = new[]
        {
            Online("online-just-now", true, TimeSpan.Zero),
            Online("online-an-hour-ago", true, TimeSpan.FromHours(1)),
            Online("online-at-the-limit", true, TimeSpan.FromMinutes(150)),
            Online("online-one-tick-past-the-limit", true, TimeSpan.FromMinutes(150) + TimeSpan.FromTicks(1)),
            Online("said-offline", false, TimeSpan.Zero),
            Online("never-said", null, TimeSpan.Zero),
            Online("clock-a-little-ahead", true, TimeSpan.FromMinutes(-10)),
            Online("clock-too-far-ahead", true, TimeSpan.FromMinutes(-10) - TimeSpan.FromTicks(1)),
        };
        var many = Enumerable.Range(0, 1002).Select(n => Inst($"github:fake/app-{n:D4}", "1.0")).ToList();
        var messy = Dev("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Example", "Windows", now, "1.0", true, new List<SyncInstall>
        {
            Inst("github:fake/zeta", "1.0"), Inst("", "9.9"), Inst("github:fake/alpha", "1.0"), Inst("github:fake/zeta", "2.0", T0, "2.1"), Inst("github:fake/Alpha", "3.0"), Inst("github:fake/a-b", "1.0"), Inst("github:fake/a_b", "1.0"),
        });
        var tooMany = Dev("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Example", "Windows", now, "1.0", true, many);
        var updates = new (string Version, string? Latest)[]
        {
            ("1.0", "1.1"), ("1.0", "1.0"), ("1.1", "1.0"), ("1.0", null), ("1.0", ""), ("", "1.0"), ("1.9", "1.10"), ("v1.0", "1.0.1"), ("2.0-beta", "2.0"), ("2.0", "2.0-beta"), ("1.0.0", "1.0"),
        }.Select(u => new { u.Version, u.Latest, HasUpdate = SyncPresence.HasUpdate(Inst("github:fake/x", u.Version, null, u.Latest)) }).ToList();
        return new
        {
            Description = "Online: whether a device's light is green at Now (Online is true, and LastSeen is no more than 150 minutes before Now and no more than 10 minutes after it). Inventory: a device's Installed list as a reader should use it (no empty keys, each key once with the last entry winning, keys in plain character order, at most 1000). Updates: whether an installed program has an update waiting.",
            Online = lights,
            Inventory = new { Device = J(messy), Keys = SyncPresence.Inventory(messy).Select(i => new { i.Key, i.Version }).ToList() },
            TooMany = new
            {
                Description = "A device whose Installed list holds 1002 entries, keys github:fake/app-0000 to github:fake/app-1001: only the first 1000 in key order are used.",
                Count = SyncPresence.Inventory(tooMany).Count, First = SyncPresence.Inventory(tooMany).First().Key, Last = SyncPresence.Inventory(tooMany).Last().Key,
            },
            Updates = updates,
        };
    }

    static object EscapeVectors() => new
    {
        Description = "Each Text, written as a JSON string by the engine, gives exactly this Json (quotes included). Only the escaping matters; the file itself is written with the same escapes.",
        Strings = new[]
        {
            "", "plain", "caf\u00E9", "\u65E5\u672C\u8A9E", "\U0001F600", "a<b>c", "x&y", "it's", "1+1", "say \"hi\"", "back\\slash", "a/b", "line1\nline2", "tab\there", "cr\rhere",
            "\u0001ctrl", "\u007Fdel", "\u2028sep", "\u00A0nbsp", "e\u0301 combining", "~tilde", "`tick`", "=eq", "%pct", "#hash", "@at", "[br]", "{br}", "\u00E9\u00E8\u00EA",
        }.Select(s => new { Text = s, Json = JsonSerializer.Serialize(s, Store.Options) }).ToList(),
    };

    static object CategorizeVectors()
    {
        var cases = new List<(string Name, string? Description, string[]? Topics, string? Language)>
        {
            ("pixel-player", "A small, fast media player for video and audio.", new[] { "video", "audio" }, "C++"),
            ("RetroLauncher", null, null, null),
            ("tinyServer", "Serves files over HTTP.", null, "Go"),
            ("quick-notes", "Take notes and keep a to-do list.", new[] { "notes", "markdown" }, null),
            ("disk-peek", "See what is using your disk space.", new[] { "disk", "cleanup" }, "Rust"),
            ("text-lens", "Copy text out of any picture on your screen.", new[] { "ocr", "utility" }, "Python"),
            ("frame-cutter", "Cut and join video clips without re-encoding.", new[] { "video", "editor" }, null),
            ("wave-mixer", null, new[] { "audio" }, null),
            ("chord-finder", "Find guitar chords by ear.", new[] { "music" }, "C++"),
            ("scan-sorter", "Sorts scanned pages into folders using simple rules.", new[] { "documents", "automation" }, "Go"),
            ("ray-sketch", "A small drawing app with layers.", new[] { "drawing", "graphics" }, "Rust"),
            ("port-checker", "Check which network ports are open.", null, null),
            ("pdf-stitch", "Merge and split PDF files.", null, null),
            ("clip-shelf", "A clipboard history manager.", null, null),
            ("old-tool", null, null, null),
            ("emu-frontend", "Front end for several emulators.", new[] { "emulator", "retro" }, "C#"),
            ("vault-keeper", "A password manager with a local vault.", new[] { "password-manager", "security" }, "Rust"),
            ("chat-bridge", "Bridges chat messages between services.", new[] { "chat", "matrix" }, "Go"),
            ("llm-runner", "Run language models locally.", new[] { "llm", "machine-learning" }, "Python"),
            ("code-lens", "A code editor with a terminal.", new[] { "text-editor", "terminal" }, "TypeScript"),
            ("browser-lite", "A lightweight web browser.", new[] { "browser" }, "C++"),
            ("img-resize", "Resize pictures in bulk.", new[] { "image", "photo" }, null),
            ("ps2-tool", "Tools for PS2 game discs.", null, null),
            ("7zip-fork", "An archive manager.", null, null),
            ("3d-viewer", "View 3D models.", new[] { "3d", "render" }, null),
            ("\u00DCn\u00EFcode-Tool", "A tool with accents in its name.", null, null),
            ("", "", null, null),
            ("x", null, null, null),
            ("MixedCASEName2Go", "text", new[] { "a-b-c" }, null),
            ("tie-breaker", "A game launcher and media player.", null, null),
            ("download-manager", "Download manager and torrent client.", new[] { "torrent" }, null),
        };
        // every seed word as a name, as a topic and as a description, so each part of the scoring is exercised
        foreach (var w in new[] { "emulator", "game", "video", "player", "photo", "design", "pdf", "notes", "password", "vpn", "browser", "torrent", "chat", "email", "ai", "llm", "system", "monitor",
                                   "backup", "launcher", "developer", "git", "editor", "terminal", "tool", "utility", "ocr", "audio", "music", "stream", "web", "sync", "scan", "screen", "capture", "android", "usb", "retro" })
        {
            cases.Add((w + "-app", null, null, null));
            cases.Add(("Sample", null, new[] { w }, null));
            cases.Add(("Sample", "A project about " + w + " and nothing else.", null, null));
            cases.Add(("Sample", "The " + w + " with a media player and a code editor.", new[] { w + "-tool" }, "C#"));
        }
        return new
        {
            Description = "The kind of project the engine puts each of these in (the library's automatic groups and the Try later types).",
            Cases = cases.Select(c => new { c.Name, c.Description, c.Topics, c.Language, Category = Categorizer.Categorize(c.Name, c.Description, c.Topics, c.Language) }).ToList(),
        };
    }

    // ---------------- release data ----------------
    static object VersionVectors()
    {
        var v = new[] { "v1.2.3", "1.2.3", "1.2", "1.2.0", "1.10", "1.9", "1.2.3-beta.1", "1.2.3-rc1", "2.0.0-rc1", "2.0.0", "2.0.0-alpha", "nightly-2026-01-01", "nightly", "build 123", "123", "",
                        "latest", "V2", "release-1.4.2", "1.0.0+meta", "10.0", "0.9.9", "0", "v0.0.1", "3.1.4.1", "3.1.4", "2026.01.15", "2026.1.2", "r1234", "v1.2.3 (preview)", "1.2.3 dev", "1.2.3snapshot", "  v1.2.3  " };
        return new
        {
            Description = "Newer[i][j] is true when Versions[i] counts as a newer release than an installed Versions[j]. Clean[i] is Versions[i] with its leading v and spaces removed.",
            Versions = v, Clean = v.Select(VersionUtil.Clean).ToArray(),
            Newer = v.Select(latest => v.Select(installed => VersionUtil.IsNewer(latest, installed)).ToArray()).ToArray(),
        };
    }

    static object MarkdownVectors()
    {
        var inputs = new[]
        {
            "", "   ", "plain text", "# Heading\n\nBody text", "## What's new\n- one\n- two\n* three\n+ four", "**bold** and __bold__ and `code` and ~~gone~~", "A [link](https://example.test/x) and ![image](https://example.test/i.png)",
            "<b>html</b> <br/> text", "---\n\ntext\n\n***\n", "line one   \nline two\t\nline three", "a\n\n\n\n\nb", "snake_case_name and *emphasis* and _under_", "2 * 3 * 4", "Windows line\r\nendings\r\nhere",
            "### Fixes\n\n- Fixed a crash (#123)\n- Improved **speed** by `20%`\n\n### Known issues\n\n1. Something\n2. Something else", new string('x', 3000), string.Join("\n", Enumerable.Range(0, 400).Select(i => "- item " + i)),
            "caf\u00E9 \u2713 \U0001F600 text", "[nested [brackets]](https://example.test)", "Full Changelog: https://example.test/compare/v1...v2",
        };
        return new { Description = "Release notes as plain text for display, cut at 2500 characters with an ellipsis.", Cases = inputs.Select(i => new { Text = i, Plain = Fmt.StripMarkdown(i) }).ToList() };
    }

    static object FormatVectors() => new
    {
        Description = "Short counts (1200 becomes 1.2k) and file sizes, as the Windows app shows them.",
        Counts = new[] { 0, 1, 9, 999, 1000, 1049, 1050, 1234, 9999, 10_000, 99_999, 100_000, 999_999, 1_000_000, 1_250_000, 12_345_678, int.MaxValue }.Select(n => new { Value = n, Text = Fmt.Count(n) }).ToList(),
        Sizes = new long[] { -5, 0, 1, 1023, 1024, 1536, 10_240, 1_048_575, 1_048_576, 5_452_595, 1_073_741_823, 1_073_741_824, 10_737_418_240, 5_497_558_138_880 }.Select(n => new { Value = n, Text = Fmt.Size(n) }).ToList(),
    };

    static object RepoRefVectors()
    {
        var inputs = new[]
        {
            "example/sample-app", "Example/Sample-App", " example/sample-app ", "\"example/sample-app\"", "<example/sample-app>", "https://github.com/example/sample-app", "http://github.com/example/sample-app/", "https://www.github.com/example/sample-app.git",
            "https://github.com/example/sample-app/releases/tag/v1.0", "https://github.com/example/sample-app?tab=readme", "git@github.com:example/sample-app.git", "github.com/example/sample-app", "https://github.com/orgs/example/people",
            "https://github.com/topics/emulator", "example", "example/sample/app", "example/sample app", "a b/c", "C:\\path\\to", "./relative/path", "https://gitlab.com/example/sample-app", "https://gitlab.com/example/group/sample-app",
            "https://gitlab.com/example/sample-app/-/releases", "gitlab.com/example/sample-app.git", "https://gitlab.com/onlyone", "https://codeberg.org/example/sample-app", "codeberg.org/example/sample-app.git", "https://www.codeberg.org/example/sample-app/releases",
            "https://git.example.test/example/sample-app", "https://git.example.test/example/sample-app/releases/tag/v1", "https://git.example.test/example/sample-app.git", "https://git.example.test/group/sub/sample-app/-/tree/main", "http://localhost/example/sample-app",
            "https://example.test/", "https://example.test/onlyone", "ftp://git.example.test/example/sample-app", "", "   ", "not a link", "https://github.com/", "https://github.com/example", "example/sample-app/",
        };
        return new
        {
            Description = "What the engine makes of text pasted into the search-and-add box: whether it is a project, and which one. Source is github, gitlab, codeberg or unknown:host (a site that still has to be identified).",
            Cases = inputs.Select(i =>
            {
                var ok = SourceClient.TryParse(i, out var r);
                return new { Input = i, Ok = ok, Source = r?.Source, Owner = r?.Owner, Repo = r?.Repo, FullPath = r?.FullPath, Url = r?.Url };
            }).ToList(),
            Urls = new[] { ("github", "o", "r"), ("gitlab", "o/g", "r"), ("codeberg", "o", "r"), ("forgejo:git.example.test", "o", "r"), ("gitlab:git.example.test", "o", "r"), ("unknown:git.example.test", "o", "r"), ("weird", "o", "r") }
                .Select(t => new { t.Item1, t.Item2, t.Item3, Url = RepoRef.UrlFor(t.Item1, t.Item2, t.Item3) }).ToList(),
        };
    }

    static readonly string GitHubRepoJson = """
        {"id":1,"full_name":"example-org/sample-app","name":"sample-app","owner":{"login":"example-org","avatar_url":"https://avatars.example.test/u/1?v=4","type":"Organization"},
         "description":"A small sample application.","stargazers_count":8421,"language":"Rust","topics":["sample","desktop","media-player"],"html_url":"https://github.com/example-org/sample-app",
         "archived":false,"fork":false,"forks_count":310,"open_issues_count":42,"subscribers_count":77,"license":{"key":"mit","name":"MIT License","spdx_id":"MIT"},"homepage":"https://sample.example.test",
         "created_at":"2023-05-01T10:00:00Z","pushed_at":"2026-01-02T03:04:05Z","unknown_extra":{"ignored":true}}
        """.ReplaceLineEndings("\n");

    static readonly string GitHubReleasesJson = """
        [
         {"tag_name":"v1.1.0","name":"Version 1.1","body":"## Changes\n- faster\n- **fixes**","prerelease":false,"draft":false,"published_at":"2026-01-02T12:00:00Z","html_url":"https://github.com/example-org/sample-app/releases/tag/v1.1.0",
          "assets":[{"name":"SampleApp-1.1.0-universal.dmg","size":52428800,"browser_download_url":"https://github.com/example-org/sample-app/releases/download/v1.1.0/SampleApp-1.1.0-universal.dmg","download_count":120,"state":"uploaded","digest":"sha256:0123456789abcdef"},
                   {"name":"SampleApp-1.1.0-setup.exe","size":41943040,"browser_download_url":"https://github.com/example-org/sample-app/releases/download/v1.1.0/SampleApp-1.1.0-setup.exe","download_count":900,"state":"uploaded"},
                   {"name":"partial.zip","size":1,"browser_download_url":"https://example.test/partial.zip","download_count":0,"state":"uploading"}]},
         {"tag_name":"v1.2.0-beta.1","name":null,"body":null,"prerelease":true,"draft":false,"published_at":"2026-01-05T08:30:00.123Z","html_url":"https://github.com/example-org/sample-app/releases/tag/v1.2.0-beta.1","assets":[]},
         {"tag_name":"v1.3.0-draft","name":"Draft","body":"not published","prerelease":false,"draft":true,"published_at":null,"html_url":null,"assets":[]},
         {"tag_name":"v1.0.0","name":"First release","body":"First!","prerelease":false,"draft":false,"published_at":"2025-12-01T00:00:00+02:00","html_url":"https://github.com/example-org/sample-app/releases/tag/v1.0.0",
          "assets":[{"name":"SampleApp-1.0.0-arm64.zip","size":31457280,"browser_download_url":"https://github.com/example-org/sample-app/releases/download/v1.0.0/SampleApp-1.0.0-arm64.zip","download_count":5}]},
         {"tag_name":"untimed","name":"No date","body":"","prerelease":false,"draft":false,"published_at":null,"html_url":null,"assets":[]}
        ]
        """.ReplaceLineEndings("\n");

    static object ReleaseParsingVectors()
    {
        var repo = JsonSerializer.Deserialize<RepoInfo>(GitHubRepoJson)!;
        var releases = JsonSerializer.Deserialize<List<ReleaseInfo>>(GitHubReleasesJson)!;
        // what the engine does with a list from GitHub or a Forgejo site such as Codeberg: drop drafts, newest first
        var kept = releases.Where(x => !x.Draft).OrderByDescending(x => x.PublishedAt ?? DateTime.MinValue).ToList();
        string? Utc(DateTime? t) => t.HasValue ? JsonSerializer.Serialize(t.Value.Kind == DateTimeKind.Local ? t.Value.ToUniversalTime() : t.Value).Trim('"') : null;
        return new
        {
            Description = "The JSON GitHub (and Forgejo/Codeberg) send for a project and its releases, and what the engine makes of it. Times are shown in UTC.",
            RepoJson = GitHubRepoJson, ReleasesJson = GitHubReleasesJson,
            Repo = new
            {
                repo.FullName, repo.Name, OwnerLogin = repo.OwnerLogin, AvatarUrl = repo.AvatarUrl, repo.Description, repo.Stars, repo.Language, repo.Topics, repo.HtmlUrl, repo.Archived, repo.Fork, repo.Forks, repo.OpenIssues, repo.Watchers,
                LicenseSpdx = repo.License?.SpdxId, LicenseName = repo.License?.Name, repo.Homepage, CreatedAt = Utc(repo.CreatedAt), PushedAt = Utc(repo.PushedAt),
            },
            Releases = kept.Select(r => new
            {
                r.Tag, r.Name, r.Body, r.Prerelease, PublishedAt = Utc(r.PublishedAt), r.HtmlUrl,
                Assets = r.Assets.Select(a => new { a.Name, a.Size, a.Url, a.Downloads, a.State, a.Digest }).ToList(),
            }).ToList(),
        };
    }

    static object DiscoverRankingVectors()
    {
        RepoInfo Repo(string full, int stars) => new() { FullName = full, Name = full.Split('/')[1], Stars = stars };
        // every project is in the earlier ranking, so nothing depends on today's date
        var items = new List<RepoInfo> { Repo("fake/alpha", 5000), Repo("fake/bravo", 4200), Repo("fake/charlie", 900), Repo("fake/delta", 120_000), Repo("fake/echo", 3000), Repo("fake/foxtrot", 880), Repo("fake/golf", 50) };
        var snap = new Snapshot { At = DateTime.UtcNow };
        snap.Ranks["fake/alpha"] = new[] { 1, 4900 }; snap.Ranks["fake/bravo"] = new[] { 4, 3000 }; snap.Ranks["fake/charlie"] = new[] { 2, 880 }; snap.Ranks["fake/delta"] = new[] { 3, 119_900 };
        snap.Ranks["fake/echo"] = new[] { 7, 3000 }; snap.Ranks["fake/foxtrot"] = new[] { 5, 900 }; snap.Ranks["fake/golf"] = new[] { 6, 10 };
        var cases = new[] { ("yesterday", TimeSpan.FromHours(20)), ("three days", TimeSpan.FromDays(3)), ("a week", TimeSpan.FromDays(7)), ("a month", TimeSpan.FromDays(30)) };
        return new
        {
            Description = "Discover's ranking. Items are listed as (name, stars). Baseline is an earlier ranking: name -> [rank, stars], taken Age ago. For each mode, Order is the order the engine puts the items in, and Notes is what it shows beside each project (movement and star gain) when it is at that place in the list.",
            Items = items.Select(i => new { i.FullName, i.Stars }).ToList(),
            Baseline = snap.Ranks.ToDictionary(k => k.Key, k => k.Value),
            Cases = cases.Select(c =>
            {
                var baseline = new DiscoverService.Baseline(snap, c.Item2);
                return new
                {
                    Name = c.Item1, AgeHours = c.Item2.TotalHours,
                    Modes = new[] { "Trending", "Popular", "New", "Updated", "Featured" }.Select(mode =>
                    {
                        var ordered = DiscoverService.Order(items.Select(i => Repo(i.FullName, i.Stars)).ToList(), mode, baseline);
                        var notes = ordered.Select((r, n) => { DiscoverService.Annotate(r, n + 1, baseline); return new { r.FullName, r.Rank, r.MoveKind, r.MoveText, r.GainText, r.GainShort }; }).ToList();
                        return new { Mode = mode, Order = ordered.Select(r => r.FullName).ToList(), Notes = notes };
                    }).ToList(),
                };
            }).ToList(),
            WithoutBaseline = Repo("fake/none", 10) is var r0 && Annotated(r0) is var a0 ? new { a0.MoveKind, a0.MoveText, a0.GainText } : null,
        };
        static RepoInfo Annotated(RepoInfo r) { DiscoverService.Annotate(r, 3, null); return r; }
    }

    static object EncryptionDetection()
    {
        var env = CryptoCases()[0].Envelope;
        var inputs = new (string Name, string Text)[]
        {
            ("envelope", env), ("envelope-after-blank-lines", "\n\n  " + env), ("document", Text(Doc("Example Mac", At(0)))),
            ("empty-object", "{}"), ("note-mentions-the-marker", Text(Doc("Example Mac", At(0), d => d.Apps.Add(App("fake", "x", At(0), At(0), a => a.Note = "releasesync-enc"))))),
            ("marker-without-brace", "\"releasesync-enc\""), ("not-json", "hello"),
        };
        return new { Description = "Whether the engine treats the text as an encrypted envelope.", Cases = inputs.Select(i => new { i.Name, Text = i.Text, Encrypted = SyncCrypto.IsEncrypted(i.Text) }).ToList() };
    }

    // ---------------- the whole set ----------------
    public static Dictionary<string, string> Build()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = Readme };

        foreach (var c in MergeCases())
        {
            if (c.A.TryLater.Concat(c.B.TryLater).Any(i => i.Note != null)) throw new InvalidOperationException(c.Name + ": Try later items with a Note can't be used, see the README.");
            var merged = SyncMerger.Merge(Clone(c.A), Clone(c.B), c.Now);
            files[$"merge/{c.Name}/case.json"] = Text(new { c.Description, Now = c.Now });
            files[$"merge/{c.Name}/a.json"] = Text(c.A);
            files[$"merge/{c.Name}/b.json"] = Text(c.B);
            files[$"merge/{c.Name}/expected.json"] = Text(merged);
        }

        foreach (var c in CryptoCases())
        {
            files[$"crypto/{c.Name}/case.json"] = Text(new { c.Description, c.Passphrase, WrongPassphrases = c.Wrong });
            files[$"crypto/{c.Name}/plaintext.json"] = c.Plain;
            files[$"crypto/{c.Name}/envelope.json"] = c.Envelope + "\n";
        }
        var errors = ErrorCases().Select(e =>
        {
            string? kind = null, message = null;
            try { SyncCrypto.Decrypt(e.EnvelopeText, e.Passphrase); }
            catch (SyncPassphraseException ex) { kind = nameof(SyncPassphraseException); message = ex.Message; }
            catch (SyncException ex) { kind = nameof(SyncException); message = ex.Message; }
            return new { e.Name, e.Description, e.Passphrase, e.EnvelopeText, Error = kind ?? throw new InvalidOperationException(e.Name + " did not fail"), Message = message };
        }).ToList();
        files["crypto/errors.json"] = Text(new { Description = "Reading these must fail with this kind of error (SyncPassphraseException: wrong or missing passphrase; SyncException: damaged or too new) and this message.", Cases = errors });
        files["crypto/is-encrypted.json"] = Text(EncryptionDetection());

        files["timestamps.json"] = Text(TimestampVectors());
        files["escapes.json"] = Text(EscapeVectors());
        files["categorize.json"] = Text(CategorizeVectors());
        files["presence.json"] = Text(PresenceVectors());
        files["releases/versions.json"] = Text(VersionVectors());
        files["releases/markdown.json"] = Text(MarkdownVectors());
        files["releases/format.json"] = Text(FormatVectors());
        files["releases/repo-refs.json"] = Text(RepoRefVectors());
        files["releases/parsing.json"] = Text(ReleaseParsingVectors());
        files["discover/ranking.json"] = Text(DiscoverRankingVectors());

        var rounds = RoundCases().Select(r =>
        {
            var transport = new MemoryTransport();
            var remoteText = r.RemoteText ?? (r.Remote == null ? null : Text(r.Remote));
            if (remoteText != null) transport.WriteAsync(remoteText).GetAwaiter().GetResult();
            var writesBefore = transport.Writes;
            var local = Clone(r.Local); var self = Clone(r.Self ?? MacInfo);
            var input = new { r.Name, r.Description, r.Now, Self = Clone(r.Self ?? MacInfo), Local = J(r.Local), Remote = r.Remote == null ? (JsonElement?)null : J(r.Remote), r.RemoteText, r.Passphrase };
            try
            {
                var res = SyncService.RunAsync(transport, local, r.Passphrase, self, r.Now).GetAwaiter().GetResult();
                var written = transport.Writes > writesBefore ? transport.Text! : null;
                var encrypted = written != null && SyncCrypto.IsEncrypted(written);
                var writtenDoc = written == null ? null : JsonSerializer.Deserialize<SyncDoc>(encrypted ? SyncCrypto.Decrypt(written, r.Passphrase) : written, Store.Options)!;
                if (writtenDoc != null && Text(writtenDoc) != Text(res.Doc)) throw new InvalidOperationException(r.Name + ": what was written is not the merged document");
                return (object)new { Input = input, Expected = new { res.FirstSync, res.Changed, res.Pushed, WrittenEncrypted = encrypted, Doc = J(res.Doc) } };
            }
            catch (SyncPassphraseException ex) { return (object)new { Input = input, Expected = new { Error = nameof(SyncPassphraseException), ex.Message } }; }
            catch (SyncException ex) { return (object)new { Input = input, Expected = new { Error = nameof(SyncException), ex.Message } }; }
        }).ToList();
        files["sync-round.json"] = Text(new
        {
            Description = "One sync round (SyncService.RunAsync): the library on this device (Local), what is stored (Remote as a document, or RemoteText when it is not one), the passphrase, and the time. Expected says what the round reports, and the merged document it would apply or write. A round that fails writes nothing.",
            Cases = rounds,
        });

        // fixtures are shared, possibly in public: nothing from this machine may have leaked in
        foreach (var (path, text) in files)
            foreach (var secret in new[] { Environment.MachineName, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
                if (secret.Length >= 3 && text.Contains(secret, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{path} contains something from this machine.");
        return files;
    }

    static readonly string Readme = """
        # Sync fixtures

        Files written by the C# sync engine (`ReleaseSync.Core/Sync`). Other implementations are tested against them: they must read each file and produce the same results. The format is described in `docs/SYNC-FORMAT.md`.

        Everything here is made up: fake owners and repositories, fake devices, a fake passphrase. Never put real tokens, passphrases, e-mail addresses, paths or library data in these files.

        ## Contents

        | Path | What it is |
        |---|---|
        | `merge/<case>/a.json`, `b.json` | Two library documents. |
        | `merge/<case>/expected.json` | `Merge(a, b, Now)`: the result. |
        | `merge/<case>/case.json` | What the case shows, and the sync time `Now` to pass to the merge. |
        | `sync-round.json` | Whole sync rounds: this device's library, the stored file, the passphrase and time, and what the round reports and writes. |
        | `crypto/<case>/` | An encrypted `envelope.json`, the `plaintext.json` inside it, and `case.json` with the passphrase that opens it and passphrases that must not. |
        | `crypto/errors.json` | Envelopes that must fail to open, with the kind of error and the message. |
        | `crypto/is-encrypted.json` | Texts and whether they count as an encrypted file. |
        | `timestamps.json` | How times are written, and how the different spellings of a time are read. |
        | `escapes.json` | How strings are escaped in JSON text. The tie-break in the merge depends on this being exact. |
        | `releases/` | Release data: version comparison, release-notes cleanup, counts and sizes, project links, and how GitHub's JSON for a project and its releases is read. |
        | `discover/` | Discover: how the engine orders projects and shows their movement since an earlier ranking. |
        | `presence.json` | Devices: when a device's light is green, how its list of installed programs is read, and when a program has an update waiting. |
        | `categorize.json` | The kind of project (the automatic group) the engine puts each name, description, topics and language in. |

        ## Rules for comparing

        - Compare documents as data, not as text: key order inside objects and line endings do not matter.
        - `WrittenBy` in a merge result comes from `a` when both `UpdatedAt` are equal, so a symmetry check must ignore it.
        - `GroupStamps` keys keep the spelling of whichever side was merged first (`Games` vs `games`). When checking that `Merge(a,b)` equals `Merge(b,a)`, compare those keys lower-cased. `expected.json` is `Merge(a, b)`.
        - Times are exact to 100 ns. Compare them as instants.
        - Merging `expected.json` with `a` or `b` again must change nothing, except in `12-tombstone-expiry`: a tombstone older than 90 days is forgotten, so an old copy that still has the item brings it back.

        ## Known defect in the C# engine (left in place on purpose)

        When the C# engine reads a Try later item that has a `Note`, setting the note also sets `ModifiedAt` to the current time, replacing the value in the file. So after a read, an item with a note always looks newly edited, its note wins over other devices' copies in the merge, and the new time is written back. The fixtures leave notes off Try later items so they stay stable, and so this does not decide what the other implementations are expected to do. Apps can have notes; they are not affected.

        ## Changing or regenerating

        The test `Fixture_files_match_what_the_engine_produces` fails when a fixture no longer matches what the engine produces, so a change to the C# engine cannot go unnoticed. If the change is meant, rewrite the files and review the diff:

        ```
        RELEASESYNC_WRITE_FIXTURES=1 dotnet test ReleaseSync.Core.Tests --filter Fixture_files_match
        ```

        A diff in the fixtures is a change to the sync format or its rules. Every other implementation must be updated and tested against the new files, and the devices must keep reading each other's files.
        """.ReplaceLineEndings("\n");
}
