
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
    public async Task AFailedReadOfTheTagNamesAfterACommittedWriteStillPublishesTheChange()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var changes = new System.Collections.Concurrent.ConcurrentQueue<DeviceChange>();
        Views.Feed.Changed += (_, c) => changes.Enqueue(c);
        var before = Views.Catalog.Key;
        Tags.BeforeNamesReload = () => throw new InvalidOperationException("database is locked");

        // A new tag on the device: written, published, and the catalog moved (the views compile again).
        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([device.Id], Ct), ["Fresh"], [], Ct);

        Assert.Contains(changes, c => c.DeviceId == device.Id && c.Device!.Tags.Contains("Fresh"));
        Assert.NotEqual(before, Views.Catalog.Key);
        Tags.BeforeNamesReload = null;
        Assert.Contains("Fresh", (await Devices.GetAsync(device.Id, Ct))!.Tags);
    }

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
    public async Task AGroupUserNeverAddsOrRemovesATagAPluginProtects()
    {
        var mine = await AddDeviceAsync("ACCC8E000001", tags: ["Lobby", "Vlan 30"]);
        var group = await SaveGroupAsync("Lobby", tags: [await TagIdAsync("Lobby")]);
        var registry = Db.Get<ProtectedTagRegistry>();
        registry.For("oadm.radius").Protect(["Vlan 20", "vlan 30"], name => $"The tag {name} sets a VLAN.");
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator", Ct);
        var devices = await view.GetAsync([mine.Id], Ct);

        // By name, case-insensitive, with or without a definition; added or removed; refused whole.
        var add = await Assert.ThrowsAsync<ProtectedTagException>(() => Tags.SetDeviceTagsAsync(devices, ["Outdoor", "VLAN 20"], [], Ct));
        Assert.Equal("The tag VLAN 20 sets a VLAN.", add.Message);
        await Assert.ThrowsAsync<ProtectedTagException>(() => Tags.SetDeviceTagsAsync(devices, [], ["Vlan 30"], Ct));
        Assert.Equal(["Lobby", "Vlan 30"], (await Devices.GetAsync(mine.Id, Ct))!.Tags);
        Assert.DoesNotContain(await Tags.ListDefinitionsAsync(Ct), t => t.Name is "Outdoor" or "VLAN 20");
        Assert.Equal(1, (await Tags.SetDeviceTagsAsync(devices, ["Outdoor"], [], Ct)).DevicesChanged); // free tags as before

        // Users with all devices are not limited; a stopped plugin protects nothing.
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        Assert.Equal(1, (await Tags.SetDeviceTagsAsync(await all.GetAsync([mine.Id], Ct), ["Vlan 20"], [], Ct)).DevicesChanged);
        registry.RemoveOwner("oadm.radius");
        Assert.Equal(1, (await Tags.SetDeviceTagsAsync(devices, [], ["Vlan 20"], Ct)).DevicesChanged);
    }

    [Fact]
    public void TheRegistryRefusesWithItsOwnTextWhenThePluginsTextFailsAndForgetsAStoppedPlugin()
    {
        var registry = new ProtectedTagRegistry();
        var first = registry.For("a.plugin");
        first.Protect(["Vlan 20"], _ => throw new InvalidOperationException("broken"));
        registry.For("b.plugin").Protect(["Vlan 40"], _ => "");

        Assert.Equal(ProtectedTagRegistry.DefaultRefusal("vlan 20"), registry.RefusalFor(["Free", "vlan 20"])); // the name as the user wrote it
        Assert.Equal(ProtectedTagRegistry.DefaultRefusal("Vlan 40"), registry.RefusalFor(["Vlan 40"]));
        Assert.Null(registry.RefusalFor(["Free"]));
        Assert.Equal(["Vlan 20", "Vlan 40"], registry.Names.Order(StringComparer.Ordinal));

        registry.RemoveOwner("a.plugin");
        first.Protect(["Vlan 20"], name => name); // a late call of the stopped plugin changes nothing
        Assert.Null(registry.RefusalFor(["Vlan 20"]));
        registry.For("a.plugin").Protect(["Vlan 21"], name => name); // started again
        Assert.Equal("Vlan 21", registry.RefusalFor(["Vlan 21"]));
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
