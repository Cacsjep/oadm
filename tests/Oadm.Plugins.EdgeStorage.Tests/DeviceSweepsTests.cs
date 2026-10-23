using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// The shared sweep per device scope (plugins/Shared/DeviceSweep.cs, <see cref="DeviceSweeps{TRow}"/>): a user's call
/// works on the sweep of its scope, which reads only the scope's devices in the background (also when a group user's call
/// starts it), publishes to that scope only, and a deleted group's sweep is gone.
/// </summary>
public sealed class DeviceSweepsTests
{
    [Fact]
    public async Task EachScopeHasItsOwnSweepOfItsDevicesAndItsEventsReachOnlyItsUsers()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = ctx.FakeScopes.Add("B", [inside.Id]);
        var plugin = new SweepPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await using var _ = plugin;
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var all = ctx.CallAsAllDevices();

        // Started from a group user's call: the reads use the plugin's context in the background and are not refused.
        await ctx.InvokeAsync(plugin, group, "read", null);
        await plugin.Sweeps!.For(group)!.Current;
        await ctx.InvokeAsync(plugin, all, "read", null);
        await plugin.Sweeps.For(all)!.Current;
        Assert.Empty(ctx.RefusedAccesses);

        Assert.Equal([inside.Id], plugin.Sweeps.For(group)!.Rows.Select(r => r.Id));
        Assert.Equal([inside.Id, outside.Id], plugin.Sweeps.For(all)!.Rows.Select(r => r.Id));
        Assert.Equal(new HashSet<string> { "all", key }, plugin.Sweeps.Keys.ToHashSet());

        // Rows go to the users of the scope only.
        plugin.Sweeps.For(group)!.Publish();
        plugin.Sweeps.For(all)!.Publish();
        Assert.Equal([$"rows:{key}:{inside.Id}"], ctx.EventsSeenBy(group).Where(e => e.Topic == "rows").Select(e => e.Topic + ":" + e.PayloadJson));
        Assert.Equal([$"rows:all:{inside.Id},{outside.Id}"], ctx.EventsSeenBy(all).Where(e => e.Topic == "rows").Select(e => e.Topic + ":" + e.PayloadJson));

        // A deleted group: no sweep, its users have none.
        ctx.FakeScopes.Delete(key);
        Assert.Null(plugin.Sweeps.For(group));
        Assert.Equal(["all"], plugin.Sweeps.Keys);
        Assert.Null(plugin.Sweeps.For("not a group"));
    }

    [Fact]
    public async Task AChangedGroupDropsTheRowsOfDevicesItNoLongerHas()
    {
        var ctx = new Testing.FakeCoreContext();
        var first = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var second = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = ctx.FakeScopes.Add("B", [first.Id, second.Id]);
        var plugin = new SweepPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await using var _ = plugin;
        var sweep = plugin.Sweeps!.For(key)!;
        sweep.Start();
        await sweep.Current;
        Assert.Equal(2, sweep.Rows.Count);

        ctx.FakeScopes.Add("B", [first.Id], key); // the group's rules changed
        await sweep.PruneAsync(CancellationToken.None);

        Assert.Equal([first.Id], sweep.Rows.Select(r => r.Id));
    }

    internal sealed record Row(Guid Id, string Address);

    /// <summary>A plugin with one sweep per scope; its read uses the context's VAPIX factory like the table pages.</summary>
    private sealed class SweepPlugin : ICorePlugin, IAsyncDisposable
    {
        public string Id => "test.sweeps";

        public string DisplayName => "Sweeps";

        public string IconKey => "plugin";

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public DeviceSweeps<Row>? Sweeps { get; private set; }

        public Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Sweeps = new DeviceSweeps<Row>(
                context,
                new DeviceSweepOptions { PublishInterval = TimeSpan.FromHours(1) },
                _ => true,
                async (device, _, token) =>
                {
                    await context.Vapix.CreateAsync(device.Id, token);
                    return new Row(device.Id, device.Address);
                },
                r => r.Address,
                (key, rows, removed) => context.Events!.Publish("rows", key + ":" + string.Join(",", rows.OrderBy(r => r.Address).Select(r => r.Id)), PluginEventAudience.DeviceScope(key)),
                key => context.Events!.Publish("state", key, PluginEventAudience.DeviceScope(key)));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            Sweeps!.For(caller)?.Start();
            return Task.FromResult<string?>(null);
        }

        public async ValueTask DisposeAsync()
        {
            if (Sweeps is not null)
            {
                await Sweeps.DisposeAsync();
            }
        }
    }
}
