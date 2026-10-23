using System.Text.Json;

using Oadm.Sdk.Devices;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.LiveView.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Shared objects): every view belongs to the device scope of the user who made
/// it; group users see and change only their group's views, with only the group's cameras; saving refuses cameras outside
/// the owner's devices; an administrator's edit keeps the owner.
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method("save", """{"name":"Hall","version":0,"tiles":[{"deviceId":"{inside}","camera":0,"x":0,"y":0,"width":4,"height":4},{"deviceId":"{device}","camera":0,"x":4,"y":0,"width":4,"height":4}]}""")
            .WithoutDevices("list", "the views of the caller's own group, cameras filtered to the group")
            .WithoutDevices("delete", "only a view of the caller's own group", """{"id":"none"}""")
            .RunAsync();
    }

    [Fact]
    public async Task GroupsSeeAndChangeOnlyTheirViewsAndAnAdministratorKeepsTheOwner()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = ctx.FakeScopes.Add("Building B", [b.Id]);
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        var ben = Testing.FakeCallContext.WithGroup(ctx, [b.Id], "ben", keyB);
        var admin = ctx.CallAsAllDevices();

        var hall = Save(await ctx.InvokeAsync(plugin, anna, "save", Request(null, "Hall", 0, a.Id)));
        Assert.Equal(keyA, hall.Owner);
        var lobby = Save(await ctx.InvokeAsync(plugin, ben, "save", Request(null, "Hall", 0, b.Id))); // the same name in another group
        await ctx.InvokeAsync(plugin, admin, "save", Request(null, "Hall", 0, a.Id, b.Id));

        Assert.Equal([hall.Id], (await ListAsync(plugin, ctx, anna)).Select(v => v.Id));
        Assert.Equal([lobby.Id], (await ListAsync(plugin, ctx, ben)).Select(v => v.Id));
        Assert.Equal(3, (await ListAsync(plugin, ctx, admin)).Count);

        // Another group's view answers like a deleted one; its delete is nothing to do.
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => ctx.InvokeAsync(plugin, ben, "save", Request(hall.Id, "Hall", hall.Version, b.Id)));
        Assert.Equal(ViewRules.Gone, gone.Message);
        await ctx.InvokeAsync(plugin, ben, "delete", JsonSerializer.Serialize(new DeleteViewRequest(hall.Id), LiveViewPluginInfo.Json));
        Assert.Equal(3, (await ListAsync(plugin, ctx, admin)).Count);

        // The administrator's edit keeps the owner and takes only the group's cameras.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ctx.InvokeAsync(plugin, admin, "save", Request(hall.Id, "Hall", hall.Version, a.Id, b.Id)));
        var edited = Save(await ctx.InvokeAsync(plugin, admin, "save", Request(hall.Id, "Hall 2", hall.Version, a.Id)));
        Assert.Equal(keyA, edited.Owner);

        // The group's users get the change, the other group nothing.
        Assert.Contains(ctx.EventsSeenBy(anna), e => e.Topic == LiveViewPluginInfo.ViewsTopic && e.PayloadJson!.Contains("Hall 2", StringComparison.Ordinal));
        Assert.DoesNotContain(ctx.EventsSeenBy(ben), e => e.PayloadJson!.Contains(hall.Id, StringComparison.Ordinal));

        // A camera that left the group is no longer shown to the group.
        ctx.FakeScopes.Add("Building A", [], keyA);
        Assert.Empty(Assert.Single(await ListAsync(plugin, ctx, Testing.FakeCallContext.WithGroup(ctx, [], "anna", keyA))).Tiles);

        // A deleted group: an administrator's save gives the view to all devices.
        ctx.FakeScopes.Delete(keyA);
        var adopted = Save(await ctx.InvokeAsync(plugin, admin, "save", Request(hall.Id, "Hall 2", edited.Version, a.Id)));
        Assert.Null(adopted.Owner);
    }

    [Fact]
    public async Task An_administrator_saves_a_groups_view_whose_camera_left_the_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var c = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.3" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id, b.Id]);
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        var hall = Save(await ctx.InvokeAsync(plugin, Testing.FakeCallContext.WithGroup(ctx, [a.Id, b.Id], "anna", keyA), "save", Request(null, "Hall", 0, a.Id, b.Id)));

        ctx.FakeScopes.Add("Building A", [a.Id], keyA); // b left the group
        var saved = Save(await ctx.InvokeAsync(plugin, admin, "save", Request(hall.Id, "Hall 2", hall.Version, a.Id, b.Id)));
        Assert.Equal(1, saved.Dropped);
        Assert.Equal([a.Id], saved.Tiles.Select(t => t.DeviceId));
        Assert.Equal(keyA, saved.Owner);
        Assert.Equal(0, Assert.Single(await ListAsync(plugin, ctx, admin)).Dropped); // only in the answer

        // A camera the administrator adds that is not in the group, and the group's own users, are still refused.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ctx.InvokeAsync(plugin, admin, "save", Request(hall.Id, "Hall 3", saved.Version, a.Id, c.Id)));
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ctx.InvokeAsync(plugin, anna, "save", Request(hall.Id, "Hall 3", saved.Version, a.Id, b.Id)));
    }

    private static async Task<IReadOnlyList<SavedView>> ListAsync(LiveViewPlugin plugin, Testing.FakeCoreContext ctx, Testing.FakeCallContext call) =>
        JsonSerializer.Deserialize<ViewList>((await ctx.InvokeAsync(plugin, call, "list", null))!, LiveViewPluginInfo.Json)!.Views;

    private static SavedView Save(string? json) => JsonSerializer.Deserialize<SavedView>(json!, LiveViewPluginInfo.Json)!;

    private static string Request(string? id, string name, long version, params Guid[] devices) =>
        JsonSerializer.Serialize(new SaveViewRequest(id, name, version, [.. devices.Select((d, i) => new ViewTile(d, 0, i * 4, 0, 4, 4))]), LiveViewPluginInfo.Json);
}
