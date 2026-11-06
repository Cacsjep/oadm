using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// Device groups (docs/spec/device-scope.md): a group user checks only the group's cameras (chosen ids outside it refused
/// whole), and the runs belong to the scope that ran them. A check is a shared job of its scope: a camera that leaves the
/// group is dropped (its exposure goes back), its live rows no longer reach the group, and a job the host ends writes
/// every exposure back.
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

        // One check per server: the other group is told only that a check runs, and sees nothing of it.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartCheckAsync([b], call: bert));
        Assert.Equal("A check is running. Start again when it ends.", refused.Message);
        var colleague = FakeCallContext.WithGroup(rig.Ctx, [a.Id], "carl", keyA);
        foreach (var sees in new ICallContext[] { colleague, rig.Admin })
        {
            var named = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartCheckAsync([a], call: sees));
            Assert.Equal("A check by anna is running (40 % done). Start again when it ends.", named.Message);
        }

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

    private const string Parameter = "ImageSource.I0.Sensor.ExposureValue";

    private static string Exposure(Rig rig, FakeDevice camera) => rig.Ctx.Cameras[camera.Id].Parameters[Parameter];

    /// <summary>A group with <paramref name="cameras"/> and a call of one of its users.</summary>
    private static (string Key, FakeCallContext Call) Group(Rig rig, string user, params FakeDevice[] cameras)
    {
        var key = rig.Ctx.FakeScopes.Add("Building A", cameras.Select(c => c.Id));
        return (key, FakeCallContext.WithGroup(rig.Ctx, cameras.Select(c => c.Id), user, key));
    }

    [Fact]
    public async Task A_camera_that_leaves_the_group_is_dropped_and_its_exposure_goes_back()
    {
        await using var rig = await new Rig(phaseSeconds: 3).StartAsync();
        var stays = rig.Camera("10.0.0.1");
        var leaves = rig.Camera("10.0.0.2");
        var (key, anna) = Group(rig, "anna", stays, leaves);

        var started = await rig.StartCheckAsync([stays, leaves], batchSize: 2, call: anna);
        await rig.AdvanceAsync(4 * 3);
        await rig.SettleAsync(); // Low light began: both exposures are up
        Assert.Equal(("100", "100"), (Exposure(rig, stays), Exposure(rig, leaves)));

        rig.Ctx.FakeScopes.Add("Building A", [stays.Id], key); // a tag change: the camera is no longer in the group
        await rig.AdvanceAsync(1);
        await rig.SettleAsync();

        Assert.Equal(("100", "50"), (Exposure(rig, stays), Exposure(rig, leaves)));
        Assert.Equal([stays.Id], rig.Probes.Open.Select(p => p.DeviceId));
        await rig.RunToEndAsync();
        Assert.Equal("50", Exposure(rig, stays));
        var detail = await rig.DetailAsync(started.RunId);
        var row = detail.Streams.Single(r => r.DeviceId == leaves.Id);
        Assert.Equal(PhaseResult.NotApplicable, row.Phases[(int)CheckPhase.LowLight].Result);
        Assert.Equal("Left the device group during the check", row.Note);
        Assert.Null(row.Problem);
        Assert.Equal(Verdicts.Carries, detail.Summary.Verdict); // leaving the group is no failure of the network
        Assert.Empty(detail.Summary.RestoreWarnings);
    }

    [Fact]
    public async Task A_camera_that_left_the_group_before_low_light_is_never_written_and_streams_no_more()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var stays = rig.Camera("10.0.0.1");
        var leaves = rig.Camera("10.0.0.2");
        var (key, anna) = Group(rig, "anna", stays, leaves);

        await rig.StartCheckAsync([leaves, stays], batchSize: 2, call: anna);
        await rig.AdvanceAsync(1); // Ramp25: the first camera streams
        Assert.Equal([leaves.Id], rig.Probes.Open.Select(p => p.DeviceId));
        rig.Ctx.FakeScopes.Add("Building A", [stays.Id], key);
        await rig.RunToEndAsync();

        var left = rig.Ctx.Cameras[leaves.Id];
        Assert.Equal(0, left.Writes);
        Assert.Empty(left.Requests); // not even the read of its exposure
        Assert.Single(rig.Probes.Opened, p => p.DeviceId == leaves.Id); // never opened again
        Assert.Equal(2, rig.Ctx.Cameras[stays.Id].Writes);
        Assert.Equal("50", Exposure(rig, stays));
    }

    [Fact]
    public async Task Live_rows_of_a_camera_that_left_the_group_no_longer_reach_the_group()
    {
        await using var rig = await new Rig(phaseSeconds: 3).StartAsync();
        var stays = rig.Camera("10.0.0.1");
        var leaves = rig.Camera("10.0.0.2");
        var (key, anna) = Group(rig, "anna", stays, leaves);

        await rig.StartCheckAsync([leaves, stays], batchSize: 2, call: anna);
        await rig.AdvanceAsync(2);
        Assert.Contains(rig.LiveEvents(anna), e => e.ChangedRows.Any(r => r.DeviceId == leaves.Id));
        var seenByGroup = rig.LiveEvents(anna).Count;
        var seenByAdmin = rig.LiveEvents(rig.Admin).Count;

        rig.Ctx.FakeScopes.Add("Building A", [stays.Id], key);
        await rig.AdvanceAsync(2);

        // The group keeps its live values, without the rows of the camera it no longer sees; users with all devices get them.
        var group = rig.LiveEvents(anna).Skip(seenByGroup).ToList();
        Assert.Equal(2, group.Sum(e => e.NewPoints.Phases.Count));
        Assert.DoesNotContain(group, e => e.ChangedRows.Any(r => r.DeviceId == leaves.Id));
        Assert.Contains(rig.LiveEvents(rig.Admin).Skip(seenByAdmin), e => e.ChangedRows.Any(r => r.DeviceId == leaves.Id));
    }

    [Fact]
    public async Task The_check_is_a_job_of_its_device_scope_and_ends_with_it()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var other = rig.Camera("10.0.0.2");
        var (key, anna) = Group(rig, "anna", camera);

        var started = await rig.StartCheckAsync([camera], call: anna);
        await rig.AdvanceAsync(4 * 2);
        await rig.SettleAsync();
        Assert.Equal("100", Exposure(rig, camera));

        // One shared job of the scope: its users find it, another group does not.
        var job = Assert.Single(rig.Ctx.FakeJobs.All);
        Assert.Equal("check", job.Kind);
        Assert.Same(job, rig.Ctx.FakeJobs.Find(FakeCallContext.WithGroup(rig.Ctx, [camera.Id], "carl", key), job.Id));
        Assert.Null(rig.Ctx.FakeJobs.Find(rig.Ctx.CallAsGroup([other.Id], "bert"), job.Id));

        job.Cancel(); // the host ends the job: the rules of the group changed, or it was deleted
        await job.Completion.WaitAsync(Eventually.DefaultTimeout);
        await rig.Service.Current.WaitAsync(Eventually.DefaultTimeout);

        Assert.Equal("50", Exposure(rig, camera)); // the exposure goes back although the job was ended
        Assert.All(rig.Probes.Opened, p => Assert.True(p.IsDisposed));
        Assert.Null(rig.Service.Runner);
        var run = Assert.Single(rig.Service.ListRuns(rig.Admin).Runs);
        Assert.Equal((started.RunId, RunState.Failed, "The check stopped when its device group changed"), (run.RunId, run.State, run.Verdict));
        Assert.Empty(run.RestoreWarnings);
    }

    [Fact]
    public async Task A_job_the_host_ends_because_the_plugin_stops_says_so()
    {
        var rig = await new Rig(phaseSeconds: 2).StartAsync();
        await using (rig)
        {
            var camera = rig.Camera("10.0.0.1");
            var (_, anna) = Group(rig, "anna", camera);
            await rig.StartCheckAsync([camera], call: anna);
            await rig.AdvanceAsync(4 * 2);
            await rig.SettleAsync();

            // The host ends a stopping plugin's jobs first, then stops the plugin. The restore waits here, so the run is
            // still ending when the plugin's stop comes.
            using var restore = new ManualResetEventSlim();
            rig.Ctx.Cameras[camera.Id].Respond = (request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    restore.Wait(Eventually.DefaultTimeout);
                }

                return null;
            };
            Assert.Single(rig.Ctx.FakeJobs.All).Cancel();
            var stop = rig.Plugin.StopAsync(CancellationToken.None);
            restore.Set();
            await stop.WaitAsync(Eventually.DefaultTimeout);

            Assert.Equal("50", Exposure(rig, camera));
            await using var again = new LoadTestPlugin(rig.Options);
            await again.StartAsync(rig.Ctx, CancellationToken.None);
            var run = Assert.Single(LoadTestJson.Deserialize<RunList>(await rig.Ctx.InvokeAsync(again, rig.Admin, LoadTestMethods.ListRuns, null)).Runs);
            Assert.Equal((RunState.Failed, Verdicts.PluginStopped), (run.State, run.Verdict));
        }
    }

    [Fact]
    public async Task A_check_of_a_user_with_all_devices_is_a_job_too_and_the_host_ends_it_only_with_the_plugin()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var camera = rig.Camera("10.0.0.1");

        await rig.StartCheckAsync([camera]);
        await rig.AdvanceAsync(4 * 2);
        await rig.SettleAsync();
        Assert.Single(rig.Ctx.FakeJobs.All).Cancel();
        await rig.Service.Current.WaitAsync(Eventually.DefaultTimeout);

        Assert.Equal("50", Exposure(rig, camera));
        var run = Assert.Single(rig.Service.ListRuns(rig.Admin).Runs);
        Assert.Equal((RunState.Failed, Verdicts.PluginStopped), (run.State, run.Verdict));
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

        // Only Start has the system access, for the write back of the exposure.
        Assert.NotNull(plugin.SystemAccessReason(LoadTestMethods.Start));
        foreach (var method in new[] { LoadTestMethods.Plan, LoadTestMethods.Cancel, LoadTestMethods.ListRuns, LoadTestMethods.GetRun, LoadTestMethods.GetStream, LoadTestMethods.DeleteRun })
        {
            Assert.Null(plugin.SystemAccessReason(method));
        }
    }
}
