using System.Net;

using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.ImageHealth.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Plugins): the page works on the table of the caller's devices, one per group;
/// chosen cameras outside the group are refused like unknown ones, rows and counts reach only the group's users.
/// </summary>
public sealed class DeviceGroupTests
{
    private const string Status = """{"block":"normal","blur":"normal","redirect":"normal","under-exposure":"normal","unsuitability":"suitable"}""";

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera());
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(ImageHealthMethods.Check, """{"deviceIds":["{inside}","{device}"]}""")
            .WithoutDevices(ImageHealthMethods.GetState, "the table of the caller's own group")
            .RunAsync();
    }

    [Fact]
    public async Task EachGroupChecksOnlyItsCamerasAndItsRowsReachOnlyItsUsers()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera());
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var all = ctx.CallAsAllDevices();

        // Empty choice (an older page): the caller's cameras only.
        await ctx.InvokeAsync(plugin, group, ImageHealthMethods.Check, null);
        await plugin.Monitor!.CurrentCheckOf(key);
        await ctx.InvokeAsync(plugin, all, ImageHealthMethods.Check, null);
        await plugin.Monitor.CurrentCheck;
        Assert.Empty(ctx.RefusedAccesses);
        Assert.Equal(1, ctx.ClientsCreated.GetValueOrDefault(outside.Id)); // only the check of all devices asked it

        var groupState = ImageHealthJson.Deserialize<ImageHealthState>(await ctx.InvokeAsync(plugin, group, ImageHealthMethods.GetState, null));
        var allState = ImageHealthJson.Deserialize<ImageHealthState>(await ctx.InvokeAsync(plugin, all, ImageHealthMethods.GetState, null));
        Assert.Equal([inside.Id], groupState.Rows.Select(r => r.DeviceId));
        Assert.Equal((1, 1), (groupState.Total, groupState.Running));
        Assert.Equal([inside.Id, outside.Id], allState.Rows.Select(r => r.DeviceId));

        plugin.Monitor.Publish();
        var seen = ctx.EventsSeenBy(group).Where(e => e.Topic == ImageHealthMethods.RowsTopic).SelectMany(e => ImageHealthJson.Deserialize<RowsEvent>(e.PayloadJson).Rows).Select(r => r.DeviceId).ToHashSet();
        Assert.DoesNotContain(outside.Id, seen);
        Assert.Contains(inside.Id, seen);

        // A deleted group has no table.
        ctx.FakeScopes.Delete(key);
        var gone = ImageHealthJson.Deserialize<ImageHealthState>(await ctx.InvokeAsync(plugin, group, ImageHealthMethods.GetState, null));
        Assert.Empty(gone.Rows);
        Assert.Equal(0, gone.Total);
    }

    [Fact]
    public async Task A_camera_that_left_the_group_shows_no_row_before_any_prune()
    {
        var ctx = new FakeCoreContext();
        var stays = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        var leaves = ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera());
        var key = ctx.FakeScopes.Add("Building B", [stays.Id, leaves.Id]);
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        await ctx.InvokeAsync(plugin, FakeCallContext.WithGroup(ctx, [stays.Id, leaves.Id], "anna", key), ImageHealthMethods.Check, null);
        await plugin.Monitor!.CurrentCheckOf(key);

        // A tag change moves the camera out of the group; the plugin has not been told yet.
        ctx.FakeScopes.Add("Building B", [stays.Id], key);
        var now = FakeCallContext.WithGroup(ctx, [stays.Id], "anna", key);
        var state = ImageHealthJson.Deserialize<ImageHealthState>(await ctx.InvokeAsync(plugin, now, ImageHealthMethods.GetState, null));
        Assert.Equal([stays.Id], state.Rows.Select(r => r.DeviceId));

        plugin.Monitor.Publish();
        Assert.Contains(ctx.EventsSeenBy(now).Where(e => e.Topic == ImageHealthMethods.RowsTopic),
            e => ImageHealthJson.Deserialize<RowsEvent>(e.PayloadJson).Removed.Contains(leaves.Id));
    }

    private static FakeCamera Camera() => new()
    {
        Respond = (request, _) => request.RequestUri!.OriginalString.Contains(ImageHealthPluginInfo.StatusPath, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Status) }
            : null,
    };

    private static ImageHealthOptions Fast() => new() { PublishInterval = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(30) };
}
