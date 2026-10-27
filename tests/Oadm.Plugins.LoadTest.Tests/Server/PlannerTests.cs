using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The plan (docs/spec/load-test.md, The check): batches, left-out devices with their reason, time and expected rate.</summary>
public sealed class PlannerTests
{
    [Fact]
    public async Task Streams_run_in_batches_of_the_chosen_size_in_the_chosen_order()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(120);

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([.. cameras.Select(c => c.Id)], 50, 2));

        Assert.Equal(120, plan.Streams);
        Assert.Equal(3, plan.Batches);
        Assert.Equal(TimeSpan.FromMinutes(3 * 5 * 2), plan.Duration);
        Assert.Empty(plan.LeftOut);
        Assert.Empty(rig.Probes.Opened); // planning never streams
        Assert.All(rig.Ctx.Cameras.Values, c => Assert.Equal(0, c.Writes));
    }

    [Fact]
    public async Task Every_source_is_one_stream_and_a_batch_may_split_a_device()
    {
        await using var rig = await new Rig().StartAsync();
        var quad = rig.Camera("10.0.0.1", [.. Enumerable.Range(1, 4).Select(i => FakeStreamProbes.Source(i))]);
        var single = rig.Camera("10.0.0.2");

        var planner = new CheckPlanner(rig.Probes, rig.Options);
        var planned = await planner.PlanAsync([quad, single], 3, 1, CancellationToken.None);

        Assert.Equal(5, planned.Plan.Streams);
        Assert.Equal([3, 2], planned.BatchList.Select(b => b.Count));
        Assert.Equal([$"{quad.Id:D}/1", $"{quad.Id:D}/2", $"{quad.Id:D}/3"], planned.BatchList[0].Select(s => s.StreamId));
        Assert.Equal([$"{quad.Id:D}/4", $"{single.Id:D}/1"], planned.BatchList[1].Select(s => s.StreamId));
    }

    [Fact]
    public async Task Left_out_devices_say_why_in_plain_words()
    {
        await using var rig = await new Rig().StartAsync();
        var ok = rig.Camera("10.0.0.1");
        var speaker = rig.Ctx.Add(new FakeDevice { Address = "10.0.0.2", Category = DeviceCategory.Speaker });
        var unreachable = rig.Ctx.Add(new FakeDevice { Address = "10.0.0.3", Status = DeviceStatus.Unreachable });
        var login = rig.Ctx.Add(new FakeDevice { Address = "10.0.0.4", Status = DeviceStatus.CredentialsRequired });
        var refused = rig.Camera("10.0.0.5");
        rig.Probes.ListFailures[refused.Id] = new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - Connection refused");
        var mjpeg = rig.Camera("10.0.0.6", FakeStreamProbes.Source(1, 1920, 1080, 30, "jpeg"));
        var none = rig.Ctx.Add(new FakeDevice { Address = "10.0.0.7" });
        rig.Probes.Sources[none.Id] = [];

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([ok.Id, speaker.Id, unreachable.Id, login.Id, refused.Id, mjpeg.Id, none.Id], 50, 2));

        Assert.Equal(1, plan.Streams);
        var reasons = plan.LeftOut.ToDictionary(l => l.DeviceId, l => l.Reason);
        Assert.Equal(CheckPlanner.NoVideo, reasons[speaker.Id]);
        Assert.Equal(DeviceMessages.NotAnswering, reasons[unreachable.Id]);
        Assert.Equal(DeviceMessages.CredentialsRequired, reasons[login.Id]);
        Assert.Equal("Unreachable - Connection refused", reasons[refused.Id]);
        Assert.Equal(CheckPlanner.NoCodec, reasons[mjpeg.Id]);
        Assert.Equal(CheckPlanner.NoVideo, reasons[none.Id]);
        Assert.Equal("10.0.0.2", plan.LeftOut.Single(l => l.DeviceId == speaker.Id).Name);

        // Refused statuses are not asked at all.
        Assert.Equal(0, rig.Probes.ListCallsFor(unreachable.Id));
        Assert.Equal(0, rig.Probes.ListCallsFor(login.Id));
    }

    [Fact]
    public async Task The_expected_rate_comes_from_the_estimate_table_and_low_light_is_higher()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = Enumerable.Range(1, 60).Select(i => rig.Camera($"10.0.1.{i}")).ToList();

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([.. cameras.Select(c => c.Id)], 50, 1));

        // 1080p, H.265 at 30 fps: 4 Mbit/s of H.264 x 0.6; the largest batch is 50 streams.
        var one = BitrateEstimates.Mbps(1920, 1080, 30, "h265");
        Assert.Equal(2.4, one, 3);
        Assert.Equal(Math.Round(50 * one, 1), plan.PeakMbps);
        Assert.Equal(Math.Round(50 * one * BitrateEstimates.LowLightFactor, 1), plan.LowLightMbps);
    }

    [Fact]
    public void The_estimate_grows_with_resolution_and_frame_rate_and_H264_needs_more()
    {
        Assert.True(BitrateEstimates.Mbps(3840, 2160, 30, "h264") > BitrateEstimates.Mbps(1920, 1080, 30, "h264"));
        Assert.True(BitrateEstimates.Mbps(1920, 1080, 30, "h264") > BitrateEstimates.Mbps(1920, 1080, 30, "h265"));
        Assert.Equal(2 * BitrateEstimates.Mbps(1920, 1080, 15, "h264"), BitrateEstimates.Mbps(1920, 1080, 30, "h264"), 6);
    }

    [Theory]
    [InlineData(0, 50, 2)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 201, 2)]
    [InlineData(1, 50, 3)]
    public async Task Requests_out_of_range_are_refused_before_any_device_is_read(int devices, int batchSize, int minutes)
    {
        await using var rig = await new Rig().StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var ids = devices == 0 ? Array.Empty<Guid>() : [camera.Id];

        await Assert.ThrowsAsync<ArgumentException>(() => rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest(ids, batchSize, minutes)));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Start, new StartRequest(ids, batchSize, minutes)));
        Assert.Equal(0, rig.Probes.ListCalls);
    }

    [Fact]
    public async Task Start_reuses_the_sources_the_plan_read()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(3);

        await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([.. cameras.Select(c => c.Id)], 50, 1));
        await rig.StartCheckAsync(cameras);

        Assert.Equal(3, rig.Probes.ListCalls);
    }

    [Fact]
    public async Task A_start_without_any_camera_to_check_is_refused()
    {
        await using var rig = await new Rig().StartAsync();
        var speaker = rig.Ctx.Add(new FakeDevice { Address = "10.0.0.2", Category = DeviceCategory.Speaker });

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => rig.StartCheckAsync([speaker]));
        Assert.Equal("None of the chosen cameras can be checked.", refused.Message);
        Assert.Null(rig.Service.Runner);
    }

    [Fact]
    public async Task A_user_with_all_devices_skips_removed_devices()
    {
        await using var rig = await new Rig().StartAsync();
        var camera = rig.Camera("10.0.0.1");

        var plan = await rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([camera.Id, Guid.NewGuid()], 50, 1));

        Assert.Equal(1, plan.Streams);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.CallAsync<CheckPlan>(rig.Admin, LoadTestMethods.Plan, new PlanRequest([Guid.NewGuid()], 50, 1)));
    }
}
