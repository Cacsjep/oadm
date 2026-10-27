using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// Device groups (docs/spec/device-scope.md): a group user checks only the group's cameras (chosen ids outside it refused
/// whole), and the runs belong to the scope that ran them.
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        await using var rig = await new Rig().StartAsync();
        var inside = rig.Camera("10.0.0.1");
        var outside = rig.Camera("10.0.0.2");

        await new PluginCanary(rig.Ctx, rig.Plugin, inside, outside)
            .Method(LoadTestMethods.Plan, """{"deviceIds":["{inside}","{device}"],"batchSize":50,"phaseMinutes":1}""")
            .Method(LoadTestMethods.Start, """{"deviceIds":["{inside}","{device}"],"batchSize":50,"phaseMinutes":1}""")
            .WithoutDevices(LoadTestMethods.ListRuns, "the runs of the caller's own device scope")
            .WithoutDevices(LoadTestMethods.GetRun, "a run of the caller's own device scope", $$"""{"runId":"{{Guid.NewGuid()}}"}""")
            .WithoutDevices(LoadTestMethods.GetStream, "a stream of a run of the caller's own device scope", $$"""{"runId":"{{Guid.NewGuid()}}","streamId":"x/1"}""")
            .WithoutDevices(LoadTestMethods.DeleteRun, "a run of the caller's own device scope", $$"""{"runId":"{{Guid.NewGuid()}}"}""")
            .WithoutDevices(LoadTestMethods.Cancel, "the running check of the caller's own device scope")
            .RunAsync();
        Assert.Equal(0, rig.Probes.ListCallsFor(outside.Id));
        Assert.Equal(0, rig.Probes.OpensFor(outside.Id));
    }

    [Fact]
    public async Task A_choice_with_a_camera_outside_the_group_is_refused_whole()
    {
        await using var rig = await new Rig().StartAsync();
        var inside = rig.Camera("10.0.0.1");
        var outside = rig.Camera("10.0.0.2");
        var group = rig.Ctx.CallAsGroup([inside.Id], "anna");

        var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.StartCheckAsync([inside, outside], call: group));

        Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Message);
        Assert.Equal(0, rig.Probes.ListCalls);
        Assert.Null(rig.Service.Runner);
    }

    [Fact]
    public async Task All_devices_means_the_callers_video_devices()
    {
        await using var rig = await new Rig().StartAsync();
        var inside = rig.Camera("10.0.0.1");
        rig.Camera("10.0.0.2");
        rig.Ctx.Add(new FakeDevice { Address = "10.0.0.3", Category = DeviceCategory.Speaker });
        var group = rig.Ctx.CallAsGroup([inside.Id], "anna");

        var mine = await rig.CallAsync<CheckPlan>(group, LoadTestMethods.Plan, new PlanRequest([], 50, 1, AllDevices: true));
        var all = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([], 50, 1, AllDevices: true));

        Assert.Equal(1, mine.Streams);
        Assert.Equal(2, all.Streams);
        Assert.Empty(all.LeftOut);
    }

    [Fact]
    public async Task Runs_belong_to_the_scope_that_ran_them()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var a = rig.Camera("10.0.0.1");
        var b = rig.Camera("10.0.0.2");
        var keyA = rig.Ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = rig.Ctx.FakeScopes.Add("Building B", [b.Id]);
        var anna = FakeCallContext.WithGroup(rig.Ctx, [a.Id], "anna", keyA);
        var bert = FakeCallContext.WithGroup(rig.Ctx, [b.Id], "bert", keyB);

        var started = await rig.StartCheckAsync([a], call: anna);
        await rig.AdvanceAsync(2);

        // One check per server: the other group is told who runs it, but sees nothing of it.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartCheckAsync([b], call: bert));
        Assert.Equal("A check by anna is running (40 % done). Start again when it ends.", refused.Message);
        Assert.Empty(rig.Service.ListRuns(bert).Runs);
        Assert.Null(rig.Service.ListRuns(bert).RunningId);
        Assert.Null(await rig.Ctx.InvokeAsync(rig.Plugin, bert, LoadTestMethods.Cancel, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.DetailAsync(started.RunId, bert));
        Assert.Empty(rig.LiveEvents(bert));
        Assert.NotEmpty(rig.LiveEvents(anna));
        Assert.NotEmpty(rig.LiveEvents(rig.Admin));

        await rig.RunToEndAsync();
        Assert.Equal([started.RunId], rig.Service.ListRuns(anna).Runs.Select(r => r.RunId));
        Assert.Equal([started.RunId], rig.Service.ListRuns(rig.Admin).Runs.Select(r => r.RunId));
        Assert.Empty(rig.Service.ListRuns(bert).Runs);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Ctx.InvokeAsync(rig.Plugin, bert, LoadTestMethods.DeleteRun, Rig.Json(new RunRequest(started.RunId))));
        Assert.Single((await rig.DetailAsync(started.RunId, anna)).Streams);

        // A camera that left the group no longer shows in the group's run.
        rig.Ctx.FakeScopes.Add("Building A", [], keyA);
        var now = FakeCallContext.WithGroup(rig.Ctx, [], "anna", keyA);
        Assert.Empty((await rig.DetailAsync(started.RunId, now)).Streams);
        Assert.Single((await rig.DetailAsync(started.RunId)).Streams);
    }

    [Fact]
    public void Viewers_see_the_page_and_runs_and_only_the_run_permission_starts_cancels_and_deletes()
    {
        var plugin = new LoadTestPlugin();

        foreach (var method in new[] { LoadTestMethods.Plan, LoadTestMethods.ListRuns, LoadTestMethods.GetRun, LoadTestMethods.GetStream })
        {
            Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(method));
        }

        foreach (var method in new[] { LoadTestMethods.Start, LoadTestMethods.Cancel, LoadTestMethods.DeleteRun })
        {
            Assert.Equal(LoadTestInfo.RunPermission, plugin.RequiredPermission(method));
            Assert.Equal(UserRole.Operator, plugin.RequiredRole(method));
            Assert.False(plugin.IsAudited(method));
        }

        var run = Assert.Single(plugin.Permissions);
        Assert.Equal("run", run.Key);
        Assert.True(run.OperatorDefault);
        Assert.False(run.ViewerDefault);
        Assert.Equal(CorePluginGroup.Monitoring, plugin.Group);
        Assert.False(((ICorePlugin)plugin).RequiresAllDevices(LoadTestMethods.Start));
    }
}
