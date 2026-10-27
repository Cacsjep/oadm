using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The check itself on the fake clock (docs/spec/load-test.md, The check and What is measured).</summary>
public sealed class RunnerTests
{
    [Fact]
    public async Task The_ramp_streams_25_50_75_and_100_percent_then_low_light_and_everything_passes()
    {
        await using var rig = await new Rig(phaseSeconds: 3).StartAsync();
        var cameras = rig.Cameras(8);

        var started = await rig.StartCheckAsync(cameras, batchSize: 8);
        Assert.Equal(RunState.Running, started.State);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal(RunState.Completed, detail.Summary.State);
        Assert.Equal([2, 4, 6, 8, 8], detail.Graphs.Steps.Select(s => s.StreamsAtOnce));
        Assert.Equal(CheckRun.AllPhases, detail.Graphs.Steps.Select(s => s.Phase));
        Assert.Equal(15, detail.Graphs.Phases.Count);

        // 4 Mbit/s per stream (the healthy fake), one point per second.
        Assert.Equal([8, 8, 8, 16, 16, 16, 24, 24, 24, 32, 32, 32, 32, 32, 32], detail.Graphs.TotalMbps.Select(v => Math.Round(v)));
        Assert.Equal(Verdicts.Carries, detail.Summary.Verdict);
        Assert.Equal("100 %", detail.Summary.Headroom);
        Assert.True(detail.Summary.Passed);
        Assert.Equal(0, detail.Summary.FailedStreams);
        Assert.Equal(32, detail.Summary.PeakMbps);
        Assert.Equal(1, detail.Summary.Progress);

        // The streams stay open from their step to the end of the batch, then close.
        Assert.Equal(8, rig.Probes.Opened.Count);
        Assert.All(rig.Probes.Opened, p => Assert.True(p.IsDisposed));
        Assert.All(rig.Probes.Opened, p => Assert.True(p.Request.PreferUdp));

        var first = detail.Streams.Single(r => r.DeviceId == cameras[0].Id);
        var last = detail.Streams.Single(r => r.DeviceId == cameras[7].Id);
        Assert.All(first.Phases, p => Assert.Equal(PhaseResult.Passed, p.Result));
        Assert.Equal([PhaseResult.NotApplicable, PhaseResult.NotApplicable, PhaseResult.NotApplicable, PhaseResult.Passed, PhaseResult.Passed], last.Phases.Select(p => p.Result));
        Assert.Equal("UDP", last.Transport);
        Assert.Equal("H.265 1920x1080 30 fps", last.Stream);
        Assert.Equal(4, last.AverageMbps);
        Assert.Equal(0, last.PacketLossPercent);
        Assert.Null(last.Problem);
    }

    [Fact]
    public async Task Loss_from_six_streams_at_once_says_where_it_starts_and_the_headroom()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(8);
        foreach (var camera in cameras)
        {
            rig.Probes.Configure(camera.Id, Scripts.LossFrom(rig.Probes, 6));
        }

        var started = await rig.StartCheckAsync(cameras, batchSize: 8);
        await rig.RunToEndAsync();

        var summary = (await rig.DetailAsync(started.RunId)).Summary;
        Assert.Equal("Loss starts at 6 cameras", summary.Verdict);
        Assert.Equal("50 %", summary.Headroom);
        Assert.False(summary.Passed);
        Assert.Equal(8, summary.FailedStreams);
        Assert.Equal(6.67, summary.WorstLossPercent, 2);
    }

    [Fact]
    public async Task Cameras_that_fail_only_in_low_light_are_counted()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(4);
        foreach (var camera in cameras.Take(3))
        {
            var fake = rig.Ctx.Cameras[camera.Id];
            rig.Probes.Configure(camera.Id, p => p.Script = _ =>
                fake.Parameters[ExposureKeeper.ParameterOf(1)] == "100" ? FakeStreamSecond.Healthy() with { Frames = 25 } : FakeStreamSecond.Healthy());
        }

        var started = await rig.StartCheckAsync(cameras, batchSize: 4);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal("3 cameras fail in Low light", detail.Summary.Verdict);
        Assert.Equal("100 %", detail.Summary.Headroom);
        var failing = detail.Streams.Where(r => r.Problem is not null).ToList();
        Assert.Equal(3, failing.Count);
        Assert.All(failing, r => Assert.Equal("The camera sends 25 of 30 frames per second", r.Problem));
        Assert.All(failing, r => Assert.Equal(PhaseResult.Failed, r.Phases[(int)CheckPhase.LowLight].Result));

        // Worst first.
        Assert.Equal(cameras[3].Id, detail.Streams[^1].DeviceId);
    }

    [Theory]
    [InlineData(297, 3, 300, 300, 0, true, PhaseResult.Failed, "Packet loss 1.0 %")]
    [InlineData(299, 1, 300, 300, 0, true, PhaseResult.Passed, null)]
    [InlineData(297, 3, 300, 300, 0, false, PhaseResult.Passed, null)]
    [InlineData(300, 0, 297, 300, 0, true, PhaseResult.Failed, "The camera sends 29.7 of 30 frames per second")]
    [InlineData(300, 0, 298, 300, 0, true, PhaseResult.Passed, null)]
    [InlineData(300, 0, 300, 300, 3, true, PhaseResult.Failed, "Late frames 1.0 %")]
    [InlineData(300, 0, 300, 300, 2, true, PhaseResult.Passed, null)]
    public void Each_limit_fails_at_1_percent(long received, long lost, long frames, long expected, long late, bool udp, PhaseResult result, string? problem)
    {
        // Packet loss counts lost / (received + lost); TCP streams have none.
        var acc = new PhaseAcc { Packets = received, Lost = lost, Frames = frames, Expected = expected, Late = late, Seconds = 10 };

        var (got, text) = CheckRunner.Judge(acc, udp);
        Assert.Equal(result, got);
        Assert.Equal(problem, text);
    }

    [Fact]
    public async Task Jitter_is_shown_and_never_fails()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p => p.Script = _ => FakeStreamSecond.Healthy() with { JitterMs = 80 });

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        Assert.True(detail.Summary.Passed);
        Assert.Equal(80, detail.Streams[0].JitterMs);
        Assert.All(detail.Graphs.AverageJitterMs, j => Assert.Equal(80, j));
    }

    [Fact]
    public async Task A_stream_that_stops_for_5_s_is_interrupted_for_the_rest_of_its_phase()
    {
        await using var rig = await new Rig(phaseSeconds: 12).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p => p.Script = s => s is >= 2 and < 9 ? FakeStreamSecond.Silent() : FakeStreamSecond.Healthy());

        var started = await rig.StartCheckAsync([camera]);
        await rig.AdvanceAsync(7);
        var row = (await rig.DetailAsync(started.RunId)).Streams[0];
        Assert.Equal(PhaseResult.Running, row.Phases[0].Result);
        Assert.Equal("Stopped sending for 5 s", row.Phases[0].Problem);
        await rig.AdvanceAsync(5);
        await rig.SettleAsync();

        row = (await rig.DetailAsync(started.RunId)).Streams[0];
        Assert.Equal(PhaseResult.Interrupted, row.Phases[0].Result);
        Assert.Equal(PhaseResult.Running, row.Phases[1].Result); // the next phase measures it again
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal([PhaseResult.Interrupted, PhaseResult.Passed, PhaseResult.Passed, PhaseResult.Passed, PhaseResult.Passed], detail.Streams[0].Phases.Select(p => p.Result));
        Assert.Equal("Loss starts at 1 camera", detail.Summary.Verdict);
        Assert.Equal("0 %", detail.Summary.Headroom);
    }

    [Fact]
    public async Task A_stream_that_does_not_start_in_10_s_is_interrupted()
    {
        await using var rig = await new Rig(phaseSeconds: 12).StartAsync();
        var slow = rig.Camera("10.0.0.1");
        var ok = rig.Camera("10.0.0.2");
        rig.Probes.Configure(slow.Id, p => p.OpenGate = new TaskCompletionSource());

        // The second of two cameras joins at 75 %.
        var started = await rig.StartCheckAsync([ok, slow], batchSize: 2);
        await rig.AdvanceAsync(24 + 9);
        var row = (await rig.DetailAsync(started.RunId)).Streams.Single(r => r.DeviceId == slow.Id);
        Assert.Equal(PhaseResult.NotApplicable, row.Phases[1].Result);
        Assert.Equal(PhaseResult.Running, row.Phases[2].Result);
        Assert.Null(row.Phases[2].Problem);

        await rig.AdvanceAsync(1);
        row = (await rig.DetailAsync(started.RunId)).Streams.Single(r => r.DeviceId == slow.Id);
        Assert.Equal("Did not start in 10 s", row.Phases[2].Problem);

        await rig.RunToEndAsync();
        var detail = await rig.DetailAsync(started.RunId);
        row = detail.Streams.Single(r => r.DeviceId == slow.Id);
        Assert.Equal(PhaseResult.Interrupted, row.Phases[2].Result);
        Assert.Equal(PhaseResult.Passed, detail.Streams.Single(r => r.DeviceId == ok.Id).Phases[2].Result);
        Assert.Equal("Loss starts at 2 cameras", detail.Summary.Verdict);
        Assert.Equal("50 %", detail.Summary.Headroom);
        Assert.All(rig.Probes.Opened.Where(p => p.DeviceId == ok.Id), p => Assert.True(p.IsDisposed));
    }

    [Fact]
    public async Task A_stream_that_starts_late_still_counts_once_it_sends()
    {
        await using var rig = await new Rig(phaseSeconds: 12).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p => p.StartDelay = TimeSpan.FromSeconds(3.5));

        var started = await rig.StartCheckAsync([camera]);
        await rig.AdvanceAsync(12);

        var phase = (await rig.DetailAsync(started.RunId)).Streams[0].Phases[0];
        Assert.Equal(PhaseResult.Passed, phase.Result);
        Assert.Equal(0, phase.FrameLossPercent);
    }

    [Fact]
    public async Task A_refused_stream_and_a_broken_stream_are_interrupted_and_tried_again_in_the_next_phase()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var broken = rig.Camera("10.0.0.1");
        var refused = rig.Camera("10.0.0.2");
        rig.Probes.OpenFailures[refused.Id] = new DeviceStreamException(DeviceStreamError.Unauthorized, "Unauthorized - HTTP 401 (check the credentials)");

        // broken streams from Ramp25, refused joins at 75 %.
        var started = await rig.StartCheckAsync([broken, refused], batchSize: 2);
        await rig.AdvanceAsync(1);
        rig.Probes.Opened.Single(p => p.DeviceId == broken.Id).Fail("The device closed the stream.");
        await rig.AdvanceAsync(4); // into Ramp75: the refused open failed
        rig.Probes.OpenFailures.Clear(); // Normal opens it again
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        var first = detail.Streams.Single(r => r.DeviceId == broken.Id);
        Assert.Equal(PhaseResult.Interrupted, first.Phases[0].Result);
        Assert.Equal("Stopped sending: The device closed the stream", first.Phases[0].Problem);
        Assert.Equal(PhaseResult.Passed, first.Phases[1].Result);
        Assert.Equal(2, rig.Probes.Opened.Count(p => p.DeviceId == broken.Id));
        var second = detail.Streams.Single(r => r.DeviceId == refused.Id);
        Assert.Equal(PhaseResult.Interrupted, second.Phases[2].Result);
        Assert.Equal("Did not start: Unauthorized - HTTP 401 (check the credentials)", second.Phases[2].Problem);
        Assert.Equal(PhaseResult.Passed, second.Phases[(int)CheckPhase.Normal].Result);
    }

    [Fact]
    public async Task Streams_over_TCP_show_frame_loss_and_no_packet_loss()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p =>
        {
            p.Transport = StreamProbeTransport.Tcp;
            p.Script = _ => FakeStreamSecond.Healthy() with { LostPackets = 50, Frames = 27 };
        });

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        var row = detail.Streams[0];
        Assert.Equal("TCP", row.Transport);
        Assert.Null(row.PacketLossPercent);
        Assert.Equal(10, row.FrameLossPercent);
        Assert.Equal("Frame loss 10.0 %", row.Problem);
        Assert.All(detail.Graphs.AverageLossPercent, v => Assert.Equal(10, v));
    }

    [Fact]
    public async Task Batches_run_one_after_the_other_and_steps_are_counted_over_all_of_them()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var cameras = rig.Cameras(6);

        var started = await rig.StartCheckAsync(cameras, batchSize: 4);
        await rig.AdvanceAsync(4);
        Assert.Equal(4, rig.Probes.Opened.Count);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal(2, detail.Summary.Batches);
        Assert.Equal([1, 2, 3, 4, 4, 1, 1, 2, 2, 2], detail.Graphs.Steps.Select(s => s.StreamsAtOnce));
        Assert.Equal(10, detail.Graphs.Phases.Count);
        Assert.Equal(4, detail.Streams.Count(r => r.Batch == 1));
        Assert.Equal(2, detail.Streams.Count(r => r.Batch == 2));

        var stream = await rig.CallAsync<StreamDetail>(rig.Admin, LoadTestMethods.GetStream, new StreamRequest(started.RunId, detail.Streams.First(r => r.Batch == 2).StreamId));
        Assert.Equal(5, stream.Mbps.Count);
        Assert.Equal(CheckRun.AllPhases, stream.Phases);
    }

    [Fact]
    public async Task Live_events_carry_each_new_second_once_and_the_changed_rows()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(4);

        var started = await rig.StartCheckAsync(cameras, batchSize: 4);
        await rig.AdvanceAsync(3);
        var live = rig.LiveEvents();
        Assert.Equal(3, live.Count);
        Assert.Equal([0, 1, 2], live.Select(e => e.FromSecond));
        Assert.All(live, e => Assert.Single(e.NewPoints.TotalMbps));
        Assert.Empty(live[0].NewPoints.Steps);
        Assert.Single(live[1].NewPoints.Steps); // Ramp25 ended in the second second
        Assert.Single(live[2].NewPoints.Steps); // the full list every time
        Assert.Equal(CheckPhase.Ramp50, live[2].Summary.Phase);
        Assert.Contains(live[0].ChangedRows, r => r.Phases[0].Result == PhaseResult.Running);
        Assert.Equal(3.0 / 10, live[2].Summary.Progress, 3);
        Assert.Equal(TimeSpan.FromSeconds(7), live[2].Summary.Remaining);
        Assert.Equal("Checking batch 1 of 1", live[2].Summary.Verdict);

        await rig.RunToEndAsync();
        live = rig.LiveEvents();
        Assert.Equal(10, live.Sum(e => e.NewPoints.Phases.Count));
        Assert.Equal(RunState.Completed, live[^1].Summary.State);

        var runs = rig.Ctx.EventsSeenBy(rig.Admin).Where(e => e.Topic == LoadTestMethods.RunsTopic).Select(e => LoadTestJson.Deserialize<RunList>(e.PayloadJson)).ToList();
        Assert.Equal(started.RunId, runs[0].RunningId);
        Assert.Null(runs[^1].RunningId);
        Assert.Equal(RunState.Completed, runs[^1].Runs[0].State);
    }

    [Fact]
    public async Task Live_rows_come_in_chunks()
    {
        await using var rig = await new Rig(phaseSeconds: 1, maxRowsPerEvent: 3).StartAsync();
        var cameras = rig.Cameras(8);

        await rig.StartCheckAsync(cameras, batchSize: 8);
        await rig.AdvanceAsync(1);

        var live = rig.LiveEvents();
        Assert.Equal(3, live.Count); // 8 changed rows
        Assert.Equal(8, live.Sum(e => e.ChangedRows.Count));
        Assert.All(live, e => Assert.True(e.ChangedRows.Count <= 3));
        Assert.Equal(1, live.Sum(e => e.NewPoints.Phases.Count));
    }

    [Fact]
    public async Task Only_one_check_runs_at_a_time()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(2);

        await rig.StartCheckAsync(cameras.Take(1));
        await rig.AdvanceAsync(2);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartCheckAsync(cameras.Skip(1), call: rig.Ctx.CallAsAllDevices("bob")));

        Assert.Equal("A check by admin is running (20 % done). Start again when it ends.", refused.Message);
        Assert.Single(rig.Probes.Opened);
        await rig.RunToEndAsync();
        await rig.StartCheckAsync(cameras.Skip(1), call: rig.Ctx.CallAsAllDevices("bob"));
    }

    [Fact]
    public async Task Cancel_keeps_the_measured_part()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(4);

        var started = await rig.StartCheckAsync(cameras, batchSize: 4);
        await rig.AdvanceAsync(3);
        var cancelled = await rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Cancel);

        Assert.Equal(RunState.Cancelled, cancelled.State);
        Assert.Equal(Verdicts.Cancelled, cancelled.Verdict);
        Assert.Null(rig.Service.Runner);
        Assert.All(rig.Probes.Opened, p => Assert.True(p.IsDisposed));
        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal(3, detail.Graphs.Phases.Count);
        Assert.Equal([CheckPhase.Ramp25, CheckPhase.Ramp50], detail.Graphs.Steps.Select(s => s.Phase));
        Assert.Equal(PhaseResult.Passed, detail.Streams.Single(r => r.DeviceId == cameras[1].Id).Phases[1].Result);
        Assert.Equal(PhaseResult.Pending, detail.Streams[0].Phases[(int)CheckPhase.Normal].Result);
        Assert.Equal("50 %", detail.Summary.Headroom);
        Assert.Null(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestMethods.Cancel, null));
    }

    [Fact]
    public async Task Start_answers_at_once_while_the_cameras_connect()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var cameras = rig.Cameras(4);
        foreach (var camera in cameras)
        {
            rig.Probes.Configure(camera.Id, p => p.OpenGate = new TaskCompletionSource());
        }

        var started = await rig.StartCheckAsync(cameras, batchSize: 4);

        Assert.Equal(RunState.Running, started.State);
        Assert.Null(started.Phase);
        Assert.Equal(0, started.Progress);
        Assert.Equal(started.RunId, rig.Service.ListRuns(rig.Admin).RunningId);
        await Eventually.TrueAsync(() => rig.Probes.OpensWaiting == 1, "the first camera connecting");
        Assert.Null(rig.Service.ListRuns(rig.Admin).Runs[0].Phase);
    }

    [Fact]
    public async Task Cancel_while_the_cameras_connect_stops_the_opens_and_writes_nothing()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var slow = rig.Camera("10.0.0.1");
        var quick = rig.Camera("10.0.0.2");
        rig.Probes.Configure(slow.Id, p => p.OpenGate = new TaskCompletionSource());

        var started = await rig.StartCheckAsync([quick, slow], batchSize: 2);
        await rig.AdvanceAsync(2 * 2); // Ramp25 and Ramp50 with the quick camera; Ramp75 opens the slow one
        await rig.SettleAsync();
        Assert.Equal(1, rig.Probes.OpensWaiting);
        var cancelled = await rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Cancel);

        Assert.Equal(RunState.Cancelled, cancelled.State);
        Assert.Equal(0, rig.Probes.OpensWaiting); // the open was stopped
        Assert.All(rig.Probes.Opened, p => Assert.True(p.IsDisposed));
        Assert.All(rig.Ctx.Cameras.Values, c => Assert.Equal(0, c.Writes));
        Assert.Null(rig.Service.Runner);
        Assert.Equal(PhaseResult.Pending, (await rig.DetailAsync(started.RunId)).Streams.Single(r => r.DeviceId == slow.Id).Phases[2].Result);
    }

    [Fact]
    public async Task Cancel_before_the_first_second_keeps_an_empty_cancelled_run()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p => p.OpenGate = new TaskCompletionSource());

        var started = await rig.StartCheckAsync([camera]);
        await Eventually.TrueAsync(() => rig.Probes.OpensWaiting == 1, "the camera connecting");
        var cancelled = await rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Cancel);

        Assert.Equal(RunState.Cancelled, cancelled.State);
        Assert.Equal(Verdicts.Cancelled, cancelled.Verdict);
        Assert.Equal(0, rig.Probes.OpensWaiting);
        Assert.Equal(0, rig.Ctx.Cameras[camera.Id].Writes);
        var detail = await rig.DetailAsync(started.RunId);
        Assert.Empty(detail.Graphs.Phases);
        Assert.Empty(detail.Graphs.Steps);
        Assert.All(detail.Streams[0].Phases, p => Assert.Equal(PhaseResult.Pending, p.Result));
        Assert.False(detail.Summary.Passed);
    }

    [Fact]
    public async Task The_firewall_rule_over_the_RTP_ports_is_open_only_while_a_check_runs()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        const string Rule = "OADM Server (Load test, UDP 50000-50799)";
        Assert.Equal(["close " + Rule], rig.Firewall.CallList); // a rule left by a crash goes at start

        await rig.StartCheckAsync([camera]);
        Assert.Equal(["close " + Rule, "open " + Rule], rig.Firewall.CallList);
        await rig.RunToEndAsync();

        Assert.Equal(["close " + Rule, "open " + Rule, "close " + Rule], rig.Firewall.CallList);
    }

    [Fact]
    public async Task A_stream_whose_UDP_is_blocked_runs_over_TCP_and_says_so_without_failing()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Probes.Configure(camera.Id, p =>
        {
            p.UdpBlocked = true;
            p.Transport = StreamProbeTransport.Tcp;
        });

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        var detail = await rig.DetailAsync(started.RunId);
        var row = detail.Streams[0];
        Assert.Equal("TCP", row.Transport);
        Assert.Null(row.PacketLossPercent);
        Assert.Equal("UDP is blocked between the camera and the server, packet loss not measured", row.Note);
        Assert.Null(row.Problem);
        Assert.True(detail.Summary.Passed);
        Assert.Null((await rig.DetailAsync(started.RunId)).Streams.Single().Problem);
    }
}
