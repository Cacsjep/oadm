using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>Stage sizes, the estimate and its warning (docs/spec/schedules.md "A run").</summary>
public sealed class RolloutTests
{
    [Fact]
    public void Stage_sizes_follow_the_spec()
    {
        Assert.Equal([1], Rollout.StageSizes(1));
        Assert.Equal([1, 2, 4, 13], Rollout.StageSizes(20));
        Assert.Equal([1, 3, 8, 15, 30, 93], Rollout.StageSizes(150));
        Assert.Equal([1, 2_000, 5_000, 10_000, 20_000, 62_999], Rollout.StageSizes(100_000));
        Assert.Empty(Rollout.StageSizes(0));
        foreach (var n in new[] { 2, 3, 7, 33, 999, 12_345 })
        {
            var sizes = Rollout.StageSizes(n);
            Assert.Equal(n, sizes.Sum());
            Assert.Equal(1, sizes[0]);
        }
    }

    [Fact]
    public void The_estimate_counts_waves_of_the_parallel_limit_and_one_per_stage()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Rollout.Estimate(40, stages: false, parallel: 16, TimeSpan.FromMinutes(10)));

        // 20 devices in stages 1, 2, 4, 13: one wave each.
        Assert.Equal(TimeSpan.FromMinutes(40), Rollout.Estimate(20, stages: true, parallel: 16, TimeSpan.FromMinutes(10)));
        Assert.Null(Rollout.Estimate(0, false, 16, TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(14), ScheduleChecks.StartingDeviceTime([EngineHarness.Action("a", NodeKind.UpgradeOnline), EngineHarness.Action("b", NodeKind.Restart), EngineHarness.Action("c", NodeKind.Dot1x)]));
    }

    [Fact]
    public void A_run_longer_than_the_time_between_two_runs_warns()
    {
        Assert.Equal(
            "A run takes about 24 h, longer than the 5 h between runs. A start during a run is skipped.",
            Rollout.WhenWarning(TimeSpan.FromHours(24), TimeSpan.FromHours(5)));
        Assert.Null(Rollout.WhenWarning(TimeSpan.FromHours(2), TimeSpan.FromDays(1)));
        Assert.Null(Rollout.WhenWarning(TimeSpan.FromHours(2), null));
        Assert.Equal("3 h 20 min", Rollout.Duration(TimeSpan.FromMinutes(200)));
        Assert.Equal("7 days", Rollout.Duration(TimeSpan.FromDays(7)));
    }
}
