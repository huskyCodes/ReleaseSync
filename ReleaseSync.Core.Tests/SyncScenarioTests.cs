using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReleaseSync.Core.Tests;

public class SyncScenarioTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(double minutes) => T0.AddMinutes(minutes);

    static (SimulatedDevice pc, SimulatedDevice mac, SimulatedDevice thor, MemoryTransport cloud) Trio()
    {
        var cloud = new MemoryTransport();
        return (new SimulatedDevice("PC", "Windows", cloud), new SimulatedDevice("Mac", "Mac", cloud), new SimulatedDevice("Thor", "Android", cloud), cloud);
    }

    [Fact]
    public async Task FirstSync_CreatesFile_AndSecondDeviceReceivesIt()
    {
        var (pc, mac, _, cloud) = Trio();
        pc.Add("neovim", "neovim", At(0));
        var first = await pc.SyncAsync(At(1));
        Assert.True(first.FirstSync); Assert.True(first.Pushed);
        Assert.NotNull(cloud.Text);

        var got = await mac.SyncAsync(At(2));
        Assert.True(got.Changed);
        Assert.Equal(new[] { "github:neovim/neovim" }, mac.AppKeys);
    }

    [Fact]
    public async Task AppsAddedOnDifferentDevices_MeetInTheMiddle()
    {
        var (pc, mac, thor, _) = Trio();
        pc.Add("a", "one", At(0)); mac.Add("b", "two", At(0)); thor.Add("c", "three", At(0));
        await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2)); await thor.SyncAsync(At(3)); await pc.SyncAsync(At(4)); await mac.SyncAsync(At(5));
        var all = new[] { "github:a/one", "github:b/two", "github:c/three" };
        Assert.Equal(all, pc.AppKeys); Assert.Equal(all, mac.AppKeys); Assert.Equal(all, thor.AppKeys);
    }

    [Fact]
    public async Task NewestEditWins()
    {
        var (pc, mac, _, _) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        pc.Edit("a", "one", At(10), x => x.Note = "from PC");
        mac.Edit("a", "one", At(20), x => x.Note = "from Mac");
        await pc.SyncAsync(At(21)); await mac.SyncAsync(At(22)); await pc.SyncAsync(At(23));
        Assert.Equal("from Mac", pc.App("a", "one")!.Note);
        Assert.Equal("from Mac", mac.App("a", "one")!.Note);
    }

    [Fact]
    public async Task Deletion_Propagates_AndAStaleDeviceDoesNotBringTheAppBack()
    {
        var (pc, mac, thor, _) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2)); await thor.SyncAsync(At(3));
        mac.Remove("a", "one", At(10)); await mac.SyncAsync(At(11));
        await pc.SyncAsync(At(12));
        Assert.Empty(pc.AppKeys);
        // Thor was offline the whole time and still has the old copy
        Assert.Single(thor.AppKeys);
        await thor.SyncAsync(At(1000));
        Assert.Empty(thor.AppKeys);
        await pc.SyncAsync(At(1001));
        Assert.Empty(pc.AppKeys);
    }

    [Fact]
    public async Task ReAddingAfterDeletion_Wins()
    {
        var (pc, mac, _, _) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        mac.Remove("a", "one", At(10)); await mac.SyncAsync(At(11));
        pc.Add("a", "one", At(20)); // PC had not synced the deletion and the user adds it again later
        pc.Library.Tombstones.Clear();
        await pc.SyncAsync(At(21)); await mac.SyncAsync(At(22));
        Assert.Single(pc.AppKeys); Assert.Single(mac.AppKeys);
    }

    [Fact]
    public async Task EditMadeAfterAnotherDeviceDeletedIt_KeepsTheApp()
    {
        var (pc, mac, _, _) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        mac.Remove("a", "one", At(10)); await mac.SyncAsync(At(11));
        pc.Edit("a", "one", At(30), x => x.Pinned = true);      // edited offline after the deletion happened
        await pc.SyncAsync(At(31)); await mac.SyncAsync(At(32));
        Assert.True(mac.App("a", "one")!.Pinned);
    }

    [Fact]
    public async Task EditMadeBeforeAnotherDeviceDeletedIt_LosesToTheDeletion()
    {
        var (pc, mac, _, _) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        pc.Edit("a", "one", At(5), x => x.Pinned = true);
        mac.Remove("a", "one", At(10)); await mac.SyncAsync(At(11));
        await pc.SyncAsync(At(12));
        Assert.Empty(pc.AppKeys);
    }

    [Fact]
    public async Task TryLater_AddRemoveAndNotes_Sync()
    {
        var (pc, _, thor, _) = Trio();
        pc.Save("ryujinx", "ryujinx", At(0)); pc.Save("dolphin-emu", "dolphin", At(0));
        await pc.SyncAsync(At(1)); await thor.SyncAsync(At(2));
        Assert.Equal(2, thor.LaterKeys.Count());

        thor.Unsave("ryujinx", "ryujinx", At(10));
        pc.Library.TryLater.First(x => x.Repo == "dolphin").Note = "check Android build"; pc.Library.TryLater.First(x => x.Repo == "dolphin").ModifiedAt = At(11);
        await thor.SyncAsync(At(12)); await pc.SyncAsync(At(13)); await thor.SyncAsync(At(14));
        Assert.Equal(new[] { "github:dolphin-emu/dolphin" }, pc.LaterKeys);
        Assert.Equal("check Android build", thor.Library.TryLater.Single().Note);
    }

    [Fact]
    public async Task SavingAgainAfterRemoving_Wins()
    {
        var (pc, mac, _, _) = Trio();
        pc.Save("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        mac.Unsave("a", "one", At(5)); await mac.SyncAsync(At(6));
        mac.Save("a", "one", At(10)); mac.Library.Tombstones.Clear(); await mac.SyncAsync(At(11)); await pc.SyncAsync(At(12));
        Assert.Single(pc.LaterKeys);
    }

    [Fact]
    public async Task Groups_DeletedGroupStaysDeleted_ButARecreatedOneSurvives()
    {
        var (pc, mac, _, _) = Trio();
        pc.AddGroup("Emulators", At(0)); pc.AddGroup("Tools", At(0));
        await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2));
        mac.RemoveGroup("Tools", At(10)); await mac.SyncAsync(At(11)); await pc.SyncAsync(At(12));
        Assert.Equal(new[] { "Emulators" }, pc.Library.CustomGroups);

        pc.AddGroup("Tools", At(20)); await pc.SyncAsync(At(21)); await mac.SyncAsync(At(22));
        Assert.Contains("Tools", mac.Library.CustomGroups);
    }

    [Fact]
    public async Task EveryDeviceShowsUpInTheRegistry()
    {
        var (pc, mac, thor, _) = Trio();
        await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2)); await thor.SyncAsync(At(3)); await pc.SyncAsync(At(4));
        Assert.Equal(new[] { "Mac", "PC", "Thor" }, pc.Library.Devices.Values.Select(d => d.Name).OrderBy(x => x));
        Assert.Equal("Android", pc.Library.Devices["dev-thor"].Platform);
    }

    [Fact]
    public async Task SyncingWhenNothingChanged_DoesNotRewriteTheFile_ExceptForTheOccasionalCheckIn()
    {
        var (pc, mac, _, cloud) = Trio();
        pc.Add("a", "one", At(0)); await pc.SyncAsync(At(1)); await mac.SyncAsync(At(2)); await pc.SyncAsync(At(3));
        int writes = cloud.Writes;
        var quiet = await pc.SyncAsync(At(10)); await mac.SyncAsync(At(11));
        Assert.False(quiet.Pushed); Assert.Equal(writes, cloud.Writes);
        var later = await pc.SyncAsync(At(60 * 7));
        Assert.True(later.Pushed);
    }

    [Fact]
    public async Task AnUnreadableFile_IsNeverOverwritten()
    {
        var (pc, _, _, cloud) = Trio();
        await cloud.WriteAsync("{ this is not json");
        pc.Add("a", "one", At(0));
        await Assert.ThrowsAsync<SyncException>(() => pc.SyncAsync(At(1)));
        Assert.Equal("{ this is not json", cloud.Text);
    }

    [Fact]
    public async Task OldDeletions_AreForgottenAfterNinetyDays()
    {
        var (pc, _, _, _) = Trio();
        pc.Add("a", "one", At(0)); pc.Remove("a", "one", At(1));
        await pc.SyncAsync(At(2));
        Assert.Single(pc.Library.Tombstones);
        await pc.SyncAsync(At(60 * 24 * 91));
        Assert.Empty(pc.Library.Tombstones);
    }
}
