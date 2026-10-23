using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.HardeningScan.Scanning;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): scans and results of the user's view, scans owned by their user.</summary>
public sealed class CanaryTests
{
    private static string JobId(string? reply) => HardeningJson.Deserialize<ScanJobStatus>(reply).JobId;

    /// <summary>A fake clock: the background writer and the event loop never run on their own during the canary.</summary>
    private static HardeningScanPlugin Plugin() => new(new HardeningScanOptions { Time = new FakeTimeProvider() });

    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(HardeningMethods.GetState, "results of the user's view only, no device ids")
            .Method(HardeningMethods.GetDetail, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(HardeningMethods.StartScan, """{"level":"Basic","deviceIds":["{inside}","{device}"]}""")
            .Job(HardeningMethods.CancelScan, """{"jobId":"{job}"}""", async owner => JobId(await ctx.InvokeAsync(plugin, owner, HardeningMethods.StartScan, """{"level":"Basic"}""")))
            .RunAsync();
    }

    [Fact]
    public async Task A_group_user_scans_and_sees_only_the_devices_of_the_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = ctx.CallAsGroup([inside.Id], "tech2");
        var admin = ctx.CallAsAllDevices();

        // An administrator's scan of every device first.
        await ctx.InvokeAsync(plugin, admin, HardeningMethods.StartScan, """{"level":"Basic"}""");
        await plugin.Service!.WaitAsync();

        // "All devices" of the group user = the group; the administrator's scan is not the group's.
        var status = HardeningJson.Deserialize<ScanJobStatus>(await ctx.InvokeAsync(plugin, group, HardeningMethods.StartScan, """{"level":"Basic"}"""));
        Assert.Equal(1, status.Total);
        await plugin.Service.WaitAsync();
        Assert.Empty(ctx.RefusedAccesses);

        var state = HardeningJson.Deserialize<HardeningState>(await ctx.InvokeAsync(plugin, group, HardeningMethods.GetState, null));
        Assert.Equal([inside.Id], state.Results.Select(r => r.DeviceId).Distinct());
        Assert.Equal(status.JobId, state.Job!.JobId);
        var all = HardeningJson.Deserialize<HardeningState>(await ctx.InvokeAsync(plugin, admin, HardeningMethods.GetState, null));
        Assert.Contains(all.Results, r => r.DeviceId == outside.Id);
        Assert.NotEqual(status.JobId, all.Job!.JobId);

        // Another user cannot stop it, and the events of the group's scan never reach another group's page.
        var other = ctx.CallAsGroup([outside.Id], "guard");
        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, other, HardeningMethods.CancelScan, $$"""{"jobId":"{{status.JobId}}"}"""));
        Assert.Equal(HardeningScanService.NotYours, refused.Message);
        Assert.DoesNotContain(ctx.EventsSeenBy(other), e => e.PayloadJson?.Contains(inside.Id.ToString(), StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(ctx.EventsSeenBy(group), e => e.Topic == HardeningMethods.ProgressTopic);
    }

    [Fact]
    public async Task A_device_that_leaves_the_group_during_a_scan_gets_no_row()
    {
        var ctx = new Testing.FakeCoreContext();
        var stays = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var leaves = new Testing.FakeDevice { Address = "10.0.0.49" };
        ctx.Add(leaves, new LeavingCamera(() => ctx.DeviceList.Remove(leaves))); // gone while it is read
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider(), Parallelism = 1 });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = ctx.CallAsGroup([stays.Id, leaves.Id], "tech2");

        await ctx.InvokeAsync(plugin, group, HardeningMethods.StartScan, """{"level":"Basic"}""");
        await plugin.Service!.WaitAsync();

        Assert.Contains(plugin.Service.Store.All(), d => d.DeviceId == stays.Id);
        Assert.DoesNotContain(plugin.Service.Store.All(), d => d.DeviceId == leaves.Id);
        Assert.DoesNotContain(ctx.EventsSeenBy(group), e => e.PayloadJson?.Contains(leaves.Id.ToString(), StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(ctx.EventsSeenBy(group), e => e.PayloadJson?.Contains(stays.Id.ToString(), StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Every_user_of_the_scope_may_stop_its_scan_and_no_other_user()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = new HoldingCamera();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" }, camera);
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var tech2 = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", key);
        var tech3 = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "tech3", key);
        var guard = ctx.CallAsGroup([outside.Id], "guard");

        var started = HardeningJson.Deserialize<ScanJobStatus>(await ctx.InvokeAsync(plugin, tech2, HardeningMethods.StartScan, """{"level":"Basic"}"""));
        await camera.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The same scope's scan for the second user (one per scope); another group's user cannot stop it.
        Assert.Equal(started.JobId, HardeningJson.Deserialize<ScanJobStatus>(await ctx.InvokeAsync(plugin, tech3, HardeningMethods.StartScan, """{"level":"Basic"}""")).JobId);
        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, guard, HardeningMethods.CancelScan, $$"""{"jobId":"{{started.JobId}}"}"""));
        Assert.Equal(HardeningScanService.NotYours, refused.Message);
        Assert.Equal(ScanJobStates.Running, plugin.Service!.JobOf(key)!.State);

        // tech3 did not start it and stops it.
        await ctx.InvokeAsync(plugin, tech3, HardeningMethods.CancelScan, $$"""{"jobId":"{{started.JobId}}"}""");
        await plugin.Service.WaitAsync();
        Assert.Equal(ScanJobStates.Cancelled, plugin.Service.JobOf(key)!.State);
    }

    /// <summary>A camera whose requests wait until the scan is stopped.</summary>
    private sealed class HoldingCamera : Testing.FakeCamera
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Oadm.Sdk.Vapix.BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
        {
            await HoldAsync(ct);
            return await base.GetBasicDeviceInfoAsync(ct);
        }

        public override async Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
        {
            await HoldAsync(ct);
            return await base.ListParametersAsync(groups, ct);
        }

        public override async Task<IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi>> GetApiListAsync(CancellationToken ct)
        {
            await HoldAsync(ct);
            return await base.GetApiListAsync(ct);
        }

        public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await HoldAsync(ct);
            return await base.SendAsync(request, ct);
        }

        private Task HoldAsync(CancellationToken ct)
        {
            Reached.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    /// <summary>A camera whose device leaves on the first request.</summary>
    private sealed class LeavingCamera(Action leave) : Testing.FakeCamera
    {
        private int _left;

        public override Task<Oadm.Sdk.Vapix.BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
        {
            Leave();
            return base.GetBasicDeviceInfoAsync(ct);
        }

        public override Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
        {
            Leave();
            return base.ListParametersAsync(groups, ct);
        }

        public override Task<IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi>> GetApiListAsync(CancellationToken ct)
        {
            Leave();
            return base.GetApiListAsync(ct);
        }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Leave();
            return base.SendAsync(request, ct);
        }

        private void Leave()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                leave();
            }
        }
    }
}
