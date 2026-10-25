using Oadm.Plugins.Shared;
using Oadm.Sdk.Vapix;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Plugins): the page works on the table of the caller's devices, one per group;
/// cameras outside the group are refused like unknown ones (reads and runs), rows reach only the group's users.
/// </summary>
public sealed class DeviceGroupTests
{
    private static readonly IReadOnlyList<DeviceApi> Slot = [new("disk-management", "1.0"), new("disk-properties", "1.1")];

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(EdgeStorageMethods.Read, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(EdgeStorageMethods.Change, """{"action":"check","deviceIds":["{inside}","{device}"]}""")
            .Method(EdgeStorageMethods.Format, """{"action":"format","deviceIds":["{device}"]}""")
            .WithoutDevices(EdgeStorageMethods.GetState, "the table of the caller's own group")
            .RunAsync();
    }

    [Fact]
    public async Task EachGroupReadsOnlyItsCamerasAndItsRowsReachOnlyItsUsers()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var all = ctx.CallAsAllDevices();

        await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.Read, null); // an older page: the caller's cameras
        await plugin.Sweeps!.For(key)!.Current;
        Assert.Empty(ctx.RefusedAccesses);
        Assert.Equal(0, ctx.ClientsCreated.GetValueOrDefault(outside.Id));

        var state = EdgeStorageJson.Deserialize<StorageState>(await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.GetState, null));
        Assert.Equal([inside.Id], state.Rows.Select(r => r.DeviceId));
        var empty = EdgeStorageJson.Deserialize<StorageState>(await ctx.InvokeAsync(plugin, all, EdgeStorageMethods.GetState, null));
        Assert.Empty(empty.Rows); // the table of all devices was not read

        plugin.Sweeps.For(key)!.Publish();
        Assert.Contains(ctx.EventsSeenBy(group), e => e.Topic == EdgeStorageMethods.RowsTopic);
        Assert.DoesNotContain(ctx.EventsSeenBy(all), e => e.Topic == EdgeStorageMethods.RowsTopic);

        // A run on the group's camera goes through the caller's runner.
        await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.Change, $$"""{"action":"check","deviceIds":["{{inside.Id}}"]}""");
        Assert.Equal([inside.Id], ctx.TaskRuns.Single().DeviceIds);

        ctx.FakeScopes.Delete(key);
        Assert.Empty(EdgeStorageJson.Deserialize<StorageState>(await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.GetState, null)).Rows);
    }

    [Fact]
    public async Task A_user_with_all_devices_reads_the_chosen_cameras_that_still_exist()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var all = ctx.CallAsAllDevices();

        // A removed camera in the choice is skipped; a group user's choice stays refused as a whole (the canary above).
        await ctx.InvokeAsync(plugin, all, EdgeStorageMethods.Read, $$"""{"deviceIds":["{{camera.Id}}","{{Guid.NewGuid()}}"]}""");
        await plugin.Sweeps!.For(all)!.Current;

        var state = EdgeStorageJson.Deserialize<StorageState>(await ctx.InvokeAsync(plugin, all, EdgeStorageMethods.GetState, null));
        Assert.Equal([camera.Id], state.Rows.Select(r => r.DeviceId));
    }

    [Fact]
    public async Task A_camera_that_left_the_group_drops_out_of_the_rows_and_the_counts_before_any_prune()
    {
        var ctx = new Testing.FakeCoreContext();
        var stays = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        var leaves = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        var key = ctx.FakeScopes.Add("Building B", [stays.Id, leaves.Id]);
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        await ctx.InvokeAsync(plugin, Testing.FakeCallContext.WithGroup(ctx, [stays.Id, leaves.Id], "anna", key), EdgeStorageMethods.Read, null);
        var sweep = plugin.Sweeps!.For(key)!;
        await sweep.Current;
        sweep.Publish();
        var group = Testing.FakeCallContext.WithGroup(ctx, [stays.Id], "anna", key);
        Assert.Equal(2, Counted(ctx.EventsSeenBy(group).Last(e => e.Topic == EdgeStorageMethods.StateTopic).PayloadJson));

        // A tag change moves the camera out of the group; no call and no prune came yet.
        ctx.FakeScopes.Add("Building B", [stays.Id], key);
        var state = EdgeStorageJson.Deserialize<StorageState>(await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.GetState, null));
        Assert.Equal([stays.Id], state.Rows.Select(r => r.DeviceId));

        await sweep.ReadOneAsync(stays.Id, CancellationToken.None); // e.g. after a task: a new state goes out
        sweep.Publish();
        Assert.Contains(ctx.EventsSeenBy(group).Where(e => e.Topic == EdgeStorageMethods.RowsTopic),
            e => EdgeStorageJson.Deserialize<RowsEvent>(e.PayloadJson).Removed.Contains(leaves.Id));
        Assert.Equal(1, Counted(ctx.EventsSeenBy(group).Last(e => e.Topic == EdgeStorageMethods.StateTopic).PayloadJson));

        static int Counted(string? json)
        {
            var state = EdgeStorageJson.Deserialize<StorageState>(json);
            return state.Ok + state.Warnings + state.Problems + state.NoCard + state.Failed;
        }
    }

    private static DeviceSweepOptions Options() => new() { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) };
}
