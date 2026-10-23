
using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The tag guard of <see cref="DeviceTagStore"/> (docs/spec/device-scope.md, Decisions: Tags): users with a group tag
/// devices of their group but never add or remove a tag a group uses; a group saved meanwhile is seen by the write; and the
/// membership audit names every device that left or entered a group.
/// </summary>
public sealed class TagGuardTests : DeviceGroupTestBase
{
    [Fact]
    public async Task AGroupUserTagsWithFreeTagsButNeverAddsOrRemovesATagAGroupUses()
    {
        var mine = await AddDeviceAsync("ACCC8E000001", tags: ["Lobby"]);
        await AddDeviceAsync("ACCC8E000002", tags: ["Ptz"]);
        var group = await SaveGroupAsync("Lobby", tags: [await TagIdAsync("Lobby")]);
        await SaveGroupAsync("Ptz", tags: [await TagIdAsync("Ptz")]);
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator", Ct);
        var devices = await view.GetAsync([mine.Id], Ct);

        var free = await Tags.SetDeviceTagsAsync(devices, ["Outdoor"], [], Ct);
        Assert.Equal(1, free.DevicesChanged);

        // Adding Ptz would move the device into the group Ptz, removing Lobby out of its own group.
        var add = await Assert.ThrowsAsync<TagInUseByGroupException>(() => Tags.SetDeviceTagsAsync(devices, ["ptz"], [], Ct));
        Assert.Equal("A device group uses the tag Ptz. Only users with all devices can add or remove it. Nothing was changed.", add.Message);
        await Assert.ThrowsAsync<TagInUseByGroupException>(() => Tags.SetDeviceTagsAsync(devices, [], ["Lobby"], Ct));
        await Assert.ThrowsAsync<TagInUseByGroupException>(() => Tags.SetDeviceTagsAsync(devices, ["Night"], ["Lobby"], Ct));
        Assert.Equal(["Lobby", "Outdoor"], (await Devices.GetAsync(mine.Id, Ct))!.Tags);
        Assert.DoesNotContain(await Tags.ListDefinitionsAsync(Ct), t => t.Name == "Night"); // refused whole: no definition either

        // Users with all devices are not limited.
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        Assert.Equal(1, (await Tags.SetDeviceTagsAsync(await all.GetAsync([mine.Id], Ct), ["Ptz"], ["Lobby"], Ct)).DevicesChanged);
    }

    [Fact]
    public async Task AGroupSavedAfterTheViewWasReadIsSeenByTheWrite()
    {
        var mine = await AddDeviceAsync("ACCC8E000001", tags: ["Lobby"]);
        var group = await SaveGroupAsync("Lobby", tags: [await TagIdAsync("Lobby")]);
        await Tags.CreateAsync("Ptz", TagColor.None, Ct);
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator", Ct);
        var devices = await view.GetAsync([mine.Id], Ct);

        // The request was checked; an administrator saves a group with the tag before the write starts.
        await SaveGroupAsync("Ptz", tags: [await TagIdAsync("Ptz")]);

        await Assert.ThrowsAsync<TagInUseByGroupException>(() => Tags.SetDeviceTagsAsync(devices, ["Ptz"], [], Ct));
        Assert.Equal(["Lobby"], (await Devices.GetAsync(mine.Id, Ct))!.Tags);
    }

    [Fact]
    public async Task AGroupSaveDuringATagWriteCommitsAfterIt()
    {
        // The tag write's transaction (EF's BeginTransaction on SQLite = BEGIN IMMEDIATE) holds the write lock from its start:
        // a group saved during the tagging waits until the tagging ends, so the guard's read inside the transaction holds
        // until the commit. Shown with a transaction opened the same way.
        await AddDeviceAsync("ACCC8E000001", tags: ["Lobby"]);
        var tag = await TagIdAsync("Lobby");
        var factory = Db.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>();
        await using var tagging = await factory.CreateDbContextAsync();
        var transaction = await tagging.Database.BeginTransactionAsync();
        var save = Task.Run(() => SaveGroupAsync("Lobby", tags: [tag])); // SQLite waits for the lock on this thread

        await Eventually.StaysTrueAsync(() => !save.IsCompleted, "the group save waits while the write is open", TimeSpan.FromMilliseconds(300));
        await using (var reader = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(reader.DeviceGroups));
        }

        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        var saved = await save.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Lobby", saved.Group.Name);
    }

    [Fact]
    public async Task TheChangesOfATagWriteNameWhoLeftOrEnteredAGroup()
    {
        var a = await AddDeviceAsync("ACCC8E000001", tags: ["Lobby"]);
        var b = await AddDeviceAsync("ACCC8E000002");
        var group = await SaveGroupAsync("Lobby", tags: [await TagIdAsync("Lobby")]);
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);

        var before = await DeviceGroupMembership.CaptureAsync(Views, Ct);
        var tagged = await Tags.SetDeviceTagsAsync(await all.GetAsync([a.Id, b.Id], Ct), ["Lobby"], [], Ct);
        var entered = Assert.Single(await DeviceGroupMembership.ChangesAsync(Views, [.. tagged.Changes], Ct, before));
        Assert.True(entered.Entered);
        Assert.Equal(group.Group.Id, entered.GroupId);
        Assert.Equal(["ACCC8E000002"], entered.Serials);

        // A deleted tag no longer resolves: the rules captured before the delete show who left.
        before = await DeviceGroupMembership.CaptureAsync(Views, Ct);
        var deleted = await Tags.DeleteAsync("Lobby", Ct);
        Assert.Equal(2, deleted.DevicesChanged);
        var left = Assert.Single(await DeviceGroupMembership.ChangesAsync(Views, [.. deleted.Changes], Ct, before));
        Assert.False(left.Entered);
        Assert.Equal(["ACCC8E000001", "ACCC8E000002"], left.Serials);
        Assert.Empty(await DeviceGroupMembership.ChangesAsync(Views, [.. deleted.Changes], Ct)); // judged by the rules now: nobody was in it
    }

    [Fact]
    public async Task TheTagCountsOfAGroupUserAreThoseOfTheView()
    {
        await AddDeviceAsync("ACCC8E000001", tags: ["Lobby", "Ptz"]);
        await AddDeviceAsync("ACCC8E000002", tags: ["Ptz", "Old"]);
        var group = await SaveGroupAsync("Lobby", tags: [await TagIdAsync("Lobby")]);
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator", Ct);

        var mine = await Tags.ListAsync(view, Ct);
        Assert.Equal(1, mine.Single(t => t.Name == "Ptz").DeviceCount);
        Assert.Equal(0, mine.Single(t => t.Name == "Old").DeviceCount); // definitions are names: listed, but no device of the view
        var everything = await Tags.ListAsync(Ct);
        Assert.Equal(2, everything.Single(t => t.Name == "Ptz").DeviceCount);
    }
}
