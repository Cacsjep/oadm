using System.Diagnostics;

using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The limits: 30,000 chosen devices refused at once, 1,000 streams planned and run, the size of the largest run.</summary>
public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Thirty_thousand_chosen_cameras_are_refused_without_reading_one()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(DeviceLimits.MaxDevices);
        var ids = cameras.Select(c => c.Id).ToList();

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest(ids, 50, 2));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Start, new StartRequest(ids, 50, 2)));

        Assert.Equal("A check takes at most 1,000 cameras. Choose fewer.", plan.Refusal);
        Assert.Equal(LoadTestInfo.TooManyStreams, refused.Message);
        Assert.Equal(0, rig.Probes.ListCalls);
        Assert.Null(rig.Service.Runner);
    }

    [Fact]
    public async Task More_than_1000_streams_of_fewer_devices_are_refused_and_1000_run()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var quads = Enumerable.Range(1, 251).Select(i => rig.Camera($"10.1.{i / 250}.{(i % 250) + 1}", [.. Enumerable.Range(1, 4).Select(c => FakeStreamProbes.Source(c))])).ToList();

        var over = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([.. quads.Select(c => c.Id)], 200, 1));
        Assert.Equal((1004, LoadTestInfo.TooManyStreams), (over.Streams, over.Refusal));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.StartCheckAsync(quads, 200));

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([.. quads.Take(250).Select(c => c.Id)], 200, 1));
        Assert.Equal((1000, 5, (string?)null), (plan.Streams, plan.Batches, plan.Refusal));
        Assert.Equal(Math.Round(200 * BitrateEstimates.Mbps(1920, 1080, 30, "h265"), 1), plan.PeakMbps);
        var started = await rig.StartCheckAsync(quads.Take(250), 200);
        Assert.Equal((1000, 5), (started.Streams, started.Batches));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Planning_the_summary_and_the_table_of_1000_cameras_stay_fast()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(LoadTestInfo.MaxStreams);
        var ids = cameras.Select(c => c.Id).ToList();

        var watch = Stopwatch.StartNew();
        await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest(ids, 200, 1));
        var plan = watch.Elapsed;

        var planned = await new CheckPlanner(rig.Probes, rig.Options).PlanAsync(cameras, 200, 1, CancellationToken.None);
        var run = new CheckRun(Guid.NewGuid(), "admin", "all", rig.Time.GetUtcNow(), planned, 60);
        watch.Restart();
        var summary = run.Summary();
        var rows = run.Streams.Select(s => s.Row()).ToList();
        var table = watch.Elapsed;

        output.WriteLine($"plan of 1,000 cameras: {plan.TotalMilliseconds:0} ms; summary and 1,000 rows: {table.TotalMilliseconds:0} ms");
        Assert.Equal(1000, summary.Streams);
        Assert.Equal(1000, rows.Count);
        Assert.True(plan < TimeSpan.FromSeconds(5), $"plan took {plan}");
        Assert.True(table < TimeSpan.FromSeconds(2), $"summary and rows took {table}");
    }

    [Fact]
    public async Task The_largest_run_stays_well_below_16_MB()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(LoadTestInfo.MaxStreams);
        var planned = await new CheckPlanner(rig.Probes, rig.Options).PlanAsync(cameras, 200, 5, CancellationToken.None);
        var run = new CheckRun(Guid.NewGuid(), "admin", "all", rig.Time.GetUtcNow(), planned, 300);

        // 1,000 streams in 5 batches x 5 phases x 300 s, every stream measured and failed with a problem text.
        for (var i = 0; i < 5 * 5 * 300; i++)
        {
            run.AddPoint(CheckRun.AllPhases[i / 300 % 5], 1234.56, 12.345, 98.765, 12.34);
        }

        foreach (var stream in run.Streams)
        {
            foreach (var acc in stream.Phases)
            {
                (acc.Result, acc.Problem, acc.Packets, acc.Lost, acc.Frames, acc.Expected, acc.Bytes, acc.Seconds, acc.JitterSum) =
                    (PhaseResult.Failed, "Packet loss 12.34 %", 81_234, 11_234, 8_765, 9_000, 123_456_789, 300, 1234.5);
            }

            stream.StreamText = "H.265 3840x2160 30 fps";
        }

        var rows = run.Streams.Select(s => s.Row()).ToList();
        var page = LoadTestJson.Serialize(new RunDetail(run.Summary(), run.Graphs(), rows.Take(LoadTestInfo.MaxStreamsPerPage).ToList(), rows.Count));

        output.WriteLine($"the largest run: {page.Length / 1_000_000.0:0.0} MB");
        Assert.Equal(1000, rows.Count);
        Assert.True(page.Length < 4_000_000, $"{page.Length:N0} characters");
    }

    [Fact]
    public async Task GetRun_pages_the_streams_worst_first_and_sends_the_graphs_with_the_first_page()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var cameras = rig.Cameras(5);
        rig.Probes.Configure(cameras[3].Id, Scripts.PacketLoss(5));
        var started = await rig.StartCheckAsync(cameras);
        await rig.RunToEndAsync();

        var first = await rig.CallAsync<RunDetail>(rig.Admin, LoadTestMethods.GetRun, new RunRequest(started.RunId, 0, 2));
        var second = await rig.CallAsync<RunDetail>(rig.Admin, LoadTestMethods.GetRun, new RunRequest(started.RunId, 2, 2));
        var last = await rig.CallAsync<RunDetail>(rig.Admin, LoadTestMethods.GetRun, new RunRequest(started.RunId, 4, 2));
        var all = await rig.DetailAsync(started.RunId);

        Assert.Equal((5, 5, 5), (first.TotalStreams, second.TotalStreams, all.TotalStreams));
        Assert.Equal([2, 2, 1], new[] { first.Streams.Count, second.Streams.Count, last.Streams.Count });
        Assert.Equal(cameras[3].Id, first.Streams[0].DeviceId);
        Assert.Equal(all.Streams.Select(r => r.StreamId), first.Streams.Concat(second.Streams).Concat(last.Streams).Select(r => r.StreamId));
        Assert.Equal(5, first.Graphs.Phases.Count);
        Assert.Empty(second.Graphs.Phases);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.CallAsync<RunDetail>(rig.Admin, LoadTestMethods.GetRun, new RunRequest(started.RunId, -1, 2)));
    }
}
