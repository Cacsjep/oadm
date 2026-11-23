using System.Diagnostics;

using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>A run of 100,000 devices (docs/spec/schedules.md "Scale"): resolving, staging and the counts stay fast.</summary>
public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task A_run_of_the_device_limit_resolves_stages_and_counts_fast()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(DeviceLimits.MaxDevices - 10, DeviceStatus.Unreachable, tag: "Site A");
        h.AddDevices(10, DeviceStatus.Ok);
        var view = await h.Devices.ListAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var choice = DeviceChoice.Resolve(new ScheduleDevices { Tags = ["site a"], DeviceIds = [devices[0].Id] }, view);
        var stages = Rollout.StageSizes(choice.Devices.Count);
        var resolve = watch.Elapsed;
        Assert.Equal(DeviceLimits.MaxDevices - 10, choice.Devices.Count);
        Assert.Equal(6, stages.Count);

        var s = await h.AddAsync(x => x with { Stages = true });
        watch.Restart();
        await h.Engine.RunNowAsync(s.Schedule.Id, "olaf", CancellationToken.None);
        var run = await h.EndedAsync(s.Schedule.Id, "the run of 100,000 devices ended");
        var runTime = watch.Elapsed;

        Assert.Equal((DeviceLimits.MaxDevices, 10, DeviceLimits.MaxDevices - 10), (run.Devices, run.Done, run.NotReachable.Values.Sum()));
        Assert.Equal(10, h.Runner.ScheduleTasks);
        output.WriteLine($"resolve + stages {resolve.TotalMilliseconds:0} ms, run {runTime.TotalMilliseconds:0} ms");
        Assert.True(resolve < TimeSpan.FromSeconds(1), $"resolve + stages took {resolve.TotalMilliseconds:0} ms");
        Assert.True(runTime < TimeSpan.FromSeconds(3), $"the run took {runTime.TotalMilliseconds:0} ms");
    }
}
