using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class SyncMergerPropertyTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly string[] Names = { "a/one", "b/two", "c/three", "d/four", "e/five", "f/six" };
    static readonly string[] Groups = { "Emulators", "Tools", "Media" };

    /// <summary>Random things a person might do on any device, at increasing times.</summary>
    static void RandomAction(SimulatedDevice d, Random rng, DateTime at)
    {
        var n = Names[rng.Next(Names.Length)].Split('/');
        switch (rng.Next(9))
        {
            case 0: case 1: if (d.App(n[0], n[1]) == null) d.Add(n[0], n[1], at); break;
            case 2: if (d.App(n[0], n[1]) != null) d.Edit(n[0], n[1], at, x => x.Note = "n" + rng.Next(100)); break;
            case 3: if (d.App(n[0], n[1]) != null) d.Edit(n[0], n[1], at, x => x.Pinned = !x.Pinned); break;
            case 4: if (d.App(n[0], n[1]) != null) d.Remove(n[0], n[1], at); break;
            case 5: if (!d.LaterKeys.Contains(SimulatedDevice.Key(n[0], n[1]))) d.Save(n[0], n[1], at); break;
            case 6: if (d.LaterKeys.Contains(SimulatedDevice.Key(n[0], n[1]))) d.Unsave(n[0], n[1], at); break;
            case 7: d.AddGroup(Groups[rng.Next(Groups.Length)], at); break;
            case 8: d.RemoveGroup(Groups[rng.Next(Groups.Length)], at); break;
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    public async Task ThreeDevicesDoingRandomThings_AlwaysEndUpAgreeing(int seed)
    {
        var rng = new Random(seed);
        var cloud = new MemoryTransport();
        var devices = new[] { new SimulatedDevice("PC", "Windows", cloud), new SimulatedDevice("Mac", "Mac", cloud), new SimulatedDevice("Thor", "Android", cloud) };
        var clock = T0;
        for (int step = 0; step < 120; step++)
        {
            clock = clock.AddMinutes(rng.Next(1, 40));
            var d = devices[rng.Next(devices.Length)];
            if (rng.Next(3) == 0) await d.SyncAsync(clock); else RandomAction(d, rng, clock);
        }
        // everyone comes online one after the other, twice
        for (int round = 0; round < 2; round++)
            foreach (var d in devices) { clock = clock.AddMinutes(5); await d.SyncAsync(clock); }

        Assert.Equal(devices[0].Signature, devices[1].Signature);
        Assert.Equal(devices[0].Signature, devices[2].Signature);
    }

    [Theory]
    [InlineData(21)] [InlineData(22)] [InlineData(23)] [InlineData(24)] [InlineData(25)] [InlineData(26)]
    public async Task Merge_IsSymmetric_AndRepeatingItChangesNothing(int seed)
    {
        var rng = new Random(seed);
        var a = new SimulatedDevice("PC", "Windows", new MemoryTransport());
        var b = new SimulatedDevice("Mac", "Mac", new MemoryTransport());
        var clock = T0;
        for (int i = 0; i < 40; i++) { clock = clock.AddMinutes(rng.Next(1, 30)); RandomAction(rng.Next(2) == 0 ? a : b, rng, clock); }
        await Task.CompletedTask;
        var now = clock.AddHours(1);
        a.Library.Devices[a.Info.Id] = a.Info; b.Library.Devices[b.Info.Id] = b.Info;

        var ab = SyncMerger.Merge(a.Library, b.Library, now);
        var ba = SyncMerger.Merge(b.Library, a.Library, now);
        Assert.Equal(SyncMerger.Signature(ab), SyncMerger.Signature(ba));

        var again = SyncMerger.Merge(ab, b.Library, now);
        Assert.Equal(SyncMerger.Signature(ab), SyncMerger.Signature(again));
        var again2 = SyncMerger.Merge(ab, ab, now);
        Assert.Equal(SyncMerger.Signature(ab), SyncMerger.Signature(again2));
    }
}
