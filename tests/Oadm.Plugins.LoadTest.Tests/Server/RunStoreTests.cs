using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The kept runs (docs/spec/load-test.md, Runs): the last five, saved across restarts, a crash kept as Failed.</summary>
public sealed class RunStoreTests
{
    [Fact]
    public async Task The_sixth_run_deletes_the_oldest()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var ids = new List<Guid>();
        for (var i = 0; i < LoadTestInfo.MaxRuns + 1; i++)
        {
            ids.Add((await rig.StartCheckAsync([camera])).RunId);
            await rig.RunToEndAsync();
        }

        var runs = rig.Service.ListRuns(rig.Admin).Runs;
        Assert.Equal(5, LoadTestInfo.MaxRuns);
        Assert.Equal(LoadTestInfo.MaxRuns, runs.Count);
        Assert.Equal(ids.Skip(1).Reverse(), runs.Select(r => r.RunId));
        Assert.False(Directory.Exists(Path.Combine(rig.DataDirectory, "runs", ids[0].ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(rig.DataDirectory, "runs", ids[1].ToString("N"))));
        Assert.Equal(LoadTestInfo.MaxRuns, (await rig.Ctx.PluginRecords.ListAsync("runs", CancellationToken.None)).Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.DetailAsync(ids[0]));
    }

    [Fact]
    public async Task A_kept_run_opens_with_its_graphs_and_streams_after_a_restart()
    {
        var rig = await new Rig(phaseSeconds: 2).StartAsync();
        await using (rig)
        {
            var cameras = rig.Cameras(3);
            var started = await rig.StartCheckAsync(cameras, batchSize: 2);
            await rig.RunToEndAsync();
            var before = await rig.DetailAsync(started.RunId);
            await rig.Plugin.StopAsync(CancellationToken.None);

            await using var again = new LoadTestPlugin(rig.Options);
            await again.StartAsync(rig.Ctx, CancellationToken.None);
            var after = LoadTestJson.Deserialize<RunDetail>(await rig.Ctx.InvokeAsync(again, rig.Admin, LoadTestMethods.GetRun, Rig.Json(new RunRequest(started.RunId))));

            Assert.Equal(Rig.Json(before), Rig.Json(after));
            Assert.Equal(20, after.Graphs.Phases.Count);
            var row = after.Streams.First(r => r.Batch == 2);
            var stream = LoadTestJson.Deserialize<StreamDetail>(await rig.Ctx.InvokeAsync(again, rig.Admin, LoadTestMethods.GetStream, Rig.Json(new StreamRequest(started.RunId, row.StreamId))));
            Assert.Equal(10, stream.Mbps.Count);
            Assert.All(stream.Mbps, v => Assert.Equal(4, v, 3));
            Assert.Equal(10, stream.Phases.Count);
        }
    }

    [Fact]
    public async Task A_run_that_was_running_when_the_server_stopped_is_kept_as_failed()
    {
        var rig = new Rig();
        await using (rig)
        {
            var running = new RunSummary(Guid.NewGuid(), rig.Time.GetUtcNow(), null, "anna", RunState.Running, 10, 1, 50, 2, "Checking batch 1 of 1", false, 0, 12, 0, null, 0.4, 1, CheckPhase.Normal, TimeSpan.FromMinutes(4), []);
            await new RunStore(rig.Ctx.Records, rig.DataDirectory).SaveSummaryAsync(new StoredRun(running, ICallContext.AllDevicesScopeKey), CancellationToken.None);

            await rig.StartAsync();

            var run = Assert.Single(rig.Service.ListRuns(rig.Admin).Runs);
            Assert.Equal(RunState.Failed, run.State);
            Assert.Equal("The server stopped during the check", run.Verdict);
            Assert.Null(run.Remaining);
            var detail = await rig.DetailAsync(run.RunId);
            Assert.Empty(detail.Streams);
            Assert.Null(rig.Service.ListRuns(rig.Admin).RunningId);
        }
    }

    [Fact]
    public async Task Delete_removes_a_kept_run_but_not_the_running_one()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var first = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();
        var second = await rig.StartCheckAsync([camera]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestMethods.DeleteRun, Rig.Json(new RunRequest(second.RunId))));
        Assert.Equal("Cancel the check first.", refused.Message);
        await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestMethods.DeleteRun, Rig.Json(new RunRequest(first.RunId)));

        Assert.Equal([second.RunId], rig.Service.ListRuns(rig.Admin).Runs.Select(r => r.RunId));
        Assert.False(Directory.Exists(Path.Combine(rig.DataDirectory, "runs", first.RunId.ToString("N"))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestMethods.DeleteRun, Rig.Json(new RunRequest(first.RunId))));
        var runs = rig.Ctx.EventsSeenBy(rig.Admin).Where(e => e.Topic == LoadTestMethods.RunsTopic).Select(e => LoadTestJson.Deserialize<RunList>(e.PayloadJson)).Last();
        Assert.Equal([second.RunId], runs.Runs.Select(r => r.RunId));
    }

    [Fact]
    public async Task Hosts_without_a_data_folder_keep_the_runs_in_memory()
    {
        await using var rig = new Rig(phaseSeconds: 1);
        var bare = new Oadm.Sdk.Testing.FakeCoreContext { FakeStreamProbes = new Oadm.Sdk.Testing.FakeStreamProbes(rig.Time) };
        var device = bare.Add(new Oadm.Sdk.Testing.FakeDevice());
        bare.FakeStreamProbes.Add(device.Id);
        await using var plugin = new LoadTestPlugin(rig.Options);
        await plugin.StartAsync(bare, CancellationToken.None);

        var started = LoadTestJson.Deserialize<RunSummary>(await bare.InvokeAsync(plugin, bare.CallAsAllDevices(), LoadTestMethods.Start, Rig.Json(new StartRequest([device.Id], 50, 1))));
        await plugin.InvokeAsync(LoadTestMethods.Cancel, null, CancellationToken.None);

        var detail = LoadTestJson.Deserialize<RunDetail>(await bare.InvokeAsync(plugin, bare.CallAsAllDevices(), LoadTestMethods.GetRun, Rig.Json(new RunRequest(started.RunId))));
        Assert.Equal(RunState.Cancelled, detail.Summary.State);
        Assert.Single(detail.Streams);
    }
}
