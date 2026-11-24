using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>A running run's current step and progress (the runs grid, like the Tasks grid); both null once it ended.</summary>
public sealed class ProgressTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task The_step_most_devices_are_on_and_the_progress_move_as_devices_pass_their_steps()
    {
        await using var h = new EngineHarness();
        h.AddDevices(3);
        h.AddDevices(1, DeviceStatus.Unreachable);
        var restart = Gate();
        var upgrade = Gate();
        h.Runner.Decide = async (_, _) =>
        {
            await restart.Task;
            return (TaskEndState.Done, null);
        };
        h.OwnerTasks.Decide = async (_, _) =>
        {
            await upgrade.Task;
            return (TaskEndState.Done, null);
        };
        var s = await h.AddAsync(x => x with { Actions = [EngineHarness.Action("a1", NodeKind.Restart), EngineHarness.Action("a2", NodeKind.UpgradeOnline)] });

        await h.Engine.RunNowAsync(s.Schedule.Id, "olaf", CancellationToken.None);

        // Three devices restart; the fourth is not reachable: 1 of 4 ended.
        await Eventually.TrueAsync(() => h.Engine.LiveRun(s.Schedule.Id) is { CurrentStep: "Restart the device", Progress: 25 } && h.Runner.Actions.Count == 3, "all three restart");

        restart.SetResult();
        await Eventually.TrueAsync(() => h.Engine.LiveRun(s.Schedule.Id) is { CurrentStep: "Upgrade AXIS OS from axis.com", Progress: 25 } && h.OwnerTasks.Actions.Count == 3, "all three upgrade");

        upgrade.SetResult();
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((null, null), (run.CurrentStep, run.Progress));
        Assert.Equal(3, run.Done);
        Assert.Null(h.Engine.LiveRun(s.Schedule.Id));
    }

    [Fact]
    public async Task With_stages_the_step_names_its_stage()
    {
        await using var h = new EngineHarness();
        h.AddDevices(20);
        var gate = Gate();
        h.Runner.Decide = async (_, _) =>
        {
            await gate.Task;
            return (TaskEndState.Done, null);
        };
        var s = await h.AddAsync(x => x with { Stages = true });

        await h.Engine.RunNowAsync(s.Schedule.Id, "olaf", CancellationToken.None);
        await Eventually.TrueAsync(() => h.Engine.LiveRun(s.Schedule.Id) is { CurrentStep: "Stage 1 of 4: Restart the device", Progress: 0 }, "stage 1 restarts");

        gate.SetResult();
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((null, null, 20), (run.CurrentStep, run.Progress, run.Done));
    }

    [Fact]
    public void Before_a_device_started_the_step_is_the_stage_alone_or_none()
    {
        var stored = new StoredSchedule { Schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly" } };
        var staged = new RunState(stored, new ScheduleRun { Result = RunResult.Running, Devices = 150, Stage = 1, StageCount = 6 }, EngineHarness.Start);
        Assert.Equal(("Stage 1 of 6", 0), (staged.Snapshot().CurrentStep, staged.Snapshot().Progress));

        var plain = new RunState(stored, new ScheduleRun { Result = RunResult.Running, Devices = 3 }, EngineHarness.Start);
        Assert.Null(plain.Snapshot().CurrentStep);

        // 2 of 3 ended: 66 (rounded down).
        plain.Apply(new FakeDevice(), new DeviceReport(DeviceEnd.Done));
        plain.Apply(new FakeDevice(), new DeviceReport(DeviceEnd.Failed, "Restart the device", "No answer."));
        Assert.Equal(66, plain.Snapshot().Progress);
    }
}
