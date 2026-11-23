using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>Runs (docs/spec/schedules.md "A run", rules 1, 3, 4, 6): tasks, counts, stages, owner, parallel limit, window end, times.</summary>
public sealed class EngineTests
{
    private static Task<ScheduleRun> RunNowAsync(EngineHarness h, StoredSchedule s) => h.Engine.RunNowAsync(s.Schedule.Id, "olaf", CancellationToken.None);

    private static IReadOnlyList<FakePublishedRuleEvent> Ended(EngineHarness h) => [.. h.Rules.Published.Where(e => e.TypeId == SchedulesInfo.RunEndedEvent)];

    [Fact]
    public async Task A_run_gives_every_ok_device_one_task_with_a_step_per_action_and_one_event()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(3);
        h.AddDevices(2, DeviceStatus.Unreachable);
        var s = await h.AddAsync(x => x with { Actions = [EngineHarness.Action("a1", NodeKind.Restart), EngineHarness.Action("a2", NodeKind.Restart)] });

        var started = await RunNowAsync(h, s);
        Assert.Equal(RunResult.Running, started.Result);
        Assert.Equal("olaf", started.StartedBy);
        var run = await h.EndedAsync(s.Schedule.Id);

        Assert.Equal((5, 3, 0, 0, 0), (run.Devices, run.Done, run.Warnings, run.Failed, run.NotStarted));
        Assert.Equal(2, run.NotReachable[DeviceMessages.StatusText(DeviceStatus.Unreachable)]);
        Assert.Equal(RunResult.DoneWithWarnings, run.Result); // two devices were not reachable
        Assert.Equal(3, h.Runner.ScheduleTasks);
        Assert.Equal(6, h.Runner.Actions.Count(a => a.PluginId == "oadm.restart"));
        Assert.All(devices, d => Assert.Equal(2, h.Runner.Actions.Count(a => a.DeviceId == d.Id)));

        var batch = h.Runner.Runs.First(r => r.PluginId == SchedulesInfo.TaskPluginId);
        Assert.Equal("Run Nightly", batch.Audit!.Target);
        Assert.Equal("Schedule Nightly (anna)", batch.Audit.UserName);
        Assert.False(batch.Audit.EachTask); // one audit entry for the run (one per stage with stages)
        Assert.Single(h.Runner.Runs, r => r.PluginId == SchedulesInfo.TaskPluginId);
        Assert.Equal("Run Nightly", new ScheduleTaskPlugin(() => h.Engine).GetTaskName(batch.Payload));

        var ended = Assert.Single(Ended(h));
        Assert.Equal(("Nightly", "Done with warnings", "3", "0", "2", "0", ""),
            (ended.Data["schedule"], ended.Data["result"], ended.Data["done"], ended.Data["failed"], ended.Data["notReachable"], ended.Data["notStarted"], ended.Data["firstFailures"]));
        Assert.NotNull(h.Store.LastEnded(s.Schedule.Id)!.DeviceTime);
    }

    [Fact]
    public async Task The_task_has_one_named_step_per_action_and_fails_when_one_failed()
    {
        await using var h = new EngineHarness();
        var device = h.AddDevices(1)[0];
        h.Runner.Decide = (plugin, _) => Task.FromResult<(TaskEndState, string?)>(plugin == "oadm.restart" ? (TaskEndState.Failed, "The device did not come back.") : (TaskEndState.Done, null));
        var s = await h.AddAsync(x => x with
        {
            Actions = [EngineHarness.Action("a1", NodeKind.Restart, continueOnError: false), EngineHarness.Action("a2", NodeKind.Restart)],
        });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Failed, 1), (run.Result, run.Failed));
        Assert.Single(h.Runner.Actions); // Continue on error off: the second action never ran
        var failure = Assert.Single(await h.Store.FailuresAsync(run.Id, CancellationToken.None));
        Assert.Equal((device.Id, "Restart the device", "The device did not come back."), (failure.DeviceId, failure.Step, failure.Message));
        Assert.Equal($"{device.Address} ({device.Serial}): Restart the device: The device did not come back.", Ended(h)[0].Data["firstFailures"]);
    }

    [Fact]
    public async Task Never_two_runs_of_one_schedule_a_start_meanwhile_is_skipped()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var gate = new TaskCompletionSource();
        h.Runner.Decide = async (_, _) =>
        {
            await gate.Task;
            return (TaskEndState.Done, null);
        };
        var s = await h.AddAsync();
        var started = await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 1, "the action runs");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunNowAsync(h, s));
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 23, 0, 5, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);
        var skipped = h.Runs(s.Schedule.Id)[0];
        Assert.Equal((RunResult.Skipped, ScheduleEngine.SkippedText), (skipped.Result, skipped.Reason));
        Assert.Equal("Skipped", Assert.Single(Ended(h)).Data["result"]);

        gate.SetResult();
        await Eventually.TrueAsync(() => !h.Engine.IsRunning(s.Schedule.Id), "the run ended");
        var run = h.Runs(s.Schedule.Id).Single(r => r.Id == started.Id);
        Assert.Equal(RunResult.Done, run.Result);
        Assert.Equal(1, h.Runner.ScheduleTasks);
        Assert.Equal(2, Ended(h).Count);
    }

    [Fact]
    public async Task A_due_start_runs_once_at_its_time()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var s = await h.AddAsync();

        await h.Engine.TickAsync(CancellationToken.None);
        Assert.Empty(h.Runs(s.Schedule.Id));

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 23, 0, 20, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Done, (string?)null), (run.Result, run.StartedBy));
        await h.Engine.TickAsync(CancellationToken.None);
        Assert.Single(h.Runs(s.Schedule.Id));
    }

    [Fact]
    public async Task A_start_missed_while_the_server_was_off_runs_within_24_hours_and_its_window_only_once()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var daily = await h.AddAsync();
        var windowed = await h.AddAsync(x => x with { Name = "Window", When = x.When with { WindowEnd = new TimeOnly(5, 0) } });
        var weekly = await h.AddAsync(x => x with { Name = "Weekly", When = new ScheduleWhen { Recurrence = Recurrence.Weekly, Days = [DayOfWeek.Monday], Time = new TimeOnly(23, 0) } });

        // Wednesday 10:00: Daily's latest start (Tue 23:00) is 11 h ago; Window's window closed at 05:00; Weekly's start is 35 h ago.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);

        Assert.Equal(RunResult.Done, (await h.EndedAsync(daily.Schedule.Id)).Result);
        Assert.Single(h.Runs(daily.Schedule.Id)); // one catch-up, not one per missed day
        var missed = Assert.Single(h.Runs(windowed.Schedule.Id));
        Assert.Equal((RunResult.Missed, new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero)), (missed.Result, missed.Started));
        Assert.Equal(RunResult.Missed, Assert.Single(h.Runs(weekly.Schedule.Id)).Result);
        Assert.Equal(2, Ended(h).Count(e => e.Data["result"] == "Missed"));
    }

    [Fact]
    public async Task Stages_stop_at_the_first_failure_and_leave_the_rest_not_started()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(20);

        // A gate: the four tasks of stage 3 all run before any of them ends (else the failure would cancel the ones still queued,
        // which is right but not what this test counts).
        var stage3 = devices.Skip(3).Take(4).Select(d => d.Id).ToHashSet();
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        h.Runner.Decide = async (_, id) =>
        {
            if (stage3.Contains(id))
            {
                if (Interlocked.Increment(ref running) == stage3.Count)
                {
                    allRunning.SetResult();
                }

                await allRunning.Task;
            }

            return id == devices[4].Id ? (TaskEndState.Failed, "No answer.") : (TaskEndState.Done, null);
        };
        var s = await h.AddAsync(x => x with { Stages = true });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);

        // Stages 1, 2, 4, 13: device 5 is in stage 3; the other three of stage 3 run to their end.
        Assert.Equal((RunResult.Stopped, 6, 1, 13), (run.Result, run.Done, run.Failed, run.NotStarted));
        Assert.Equal("Stopped at stage 3: 1 failed.", run.Reason);
        Assert.Equal((3, 4), (run.Stage, run.StageCount));
        Assert.Equal(7, h.Runner.ScheduleTasks);
        Assert.Equal(3, h.Runner.Runs.Count(r => r.PluginId == SchedulesInfo.TaskPluginId)); // one start (and audit entry) per stage
        Assert.Equal("Stopped", Assert.Single(Ended(h)).Data["result"]);
    }

    [Fact]
    public async Task Offline_devices_warnings_and_not_needed_never_stop_the_stages()
    {
        await using var h = new EngineHarness();
        var offline = h.AddDevices(3, DeviceStatus.Unreachable);
        var ok = h.AddDevices(17);
        h.Runner.Decide = (_, id) => Task.FromResult<(TaskEndState, string?)>(id == ok[2].Id ? (TaskEndState.DoneWithWarnings, "Restarted late.") : (TaskEndState.Done, null));
        var s = await h.AddAsync(x => x with { Stages = true });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.DoneWithWarnings, 16, 1, 0, 0), (run.Result, run.Done, run.Warnings, run.Failed, run.NotStarted));
        Assert.Equal(3, run.NotReachable.Values.Sum());

        // The first stage takes a device that is Ok.
        var first = h.Runner.Runs.First(r => r.PluginId == SchedulesInfo.TaskPluginId);
        Assert.Equal([ok[0].Id], first.DeviceIds);
        Assert.DoesNotContain(h.Runner.Runs.SelectMany(r => r.DeviceIds), id => offline.Any(d => d.Id == id));
    }

    [Fact]
    public async Task A_deleted_or_disabled_owner_runs_nothing()
    {
        await using var h = new EngineHarness();
        h.AddDevices(2);
        var s = await h.AddAsync();
        h.Owner(null);

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Failed, ScheduleEngine.OwnerGone), (run.Result, run.Reason));
        Assert.Equal(0, h.Runner.ScheduleTasks);
        Assert.Equal("Failed", Assert.Single(Ended(h)).Data["result"]);
    }

    [Fact]
    public async Task An_owner_who_lost_a_right_runs_nothing()
    {
        await using var h = new EngineHarness();
        h.AddDevices(2);
        var s = await h.AddAsync();
        h.Owner(new TestCall(FakeCallContext.Over(h.Devices, tasks: h.OwnerTasks, userName: "anna"), new HashSet<string> { ActionCatalog.Manage.Permission }));

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Failed, "The owner no longer has the right Restart."), (run.Result, run.Reason));
        Assert.Equal(0, h.Runner.ScheduleTasks);

        h.Owner(new TestCall(FakeCallContext.Over(h.Devices, tasks: h.OwnerTasks, userName: "anna"), new HashSet<string> { ActionCatalog.Restart.Permission }));
        await RunNowAsync(h, s);
        Assert.Equal("The owner no longer has the right Change and run schedules.", (await h.EndedAsync(s.Schedule.Id)).Reason);
    }

    [Fact]
    public async Task A_chosen_device_outside_the_owners_view_runs_nothing_a_removed_one_is_left_out()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(3);
        var s = await h.AddAsync(x => x with { Devices = new ScheduleDevices { Kind = ScheduleDevicesKind.Chosen, DeviceIds = [devices[0].Id, devices[1].Id] } });
        h.Owner(new TestCall(FakeCallContext.OverGroup([devices[0].Id, devices[2].Id], h.Devices, tasks: h.OwnerTasks, userName: "anna")));

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Failed, "The owner can no longer reach a chosen device."), (run.Result, run.Reason));
        Assert.Equal(0, h.Runner.ScheduleTasks);

        h.Devices.Remove(devices[1].Id);
        await RunNowAsync(h, s);
        var second = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Done, 1, 1), (second.Result, second.Devices, second.Done));
    }

    [Fact]
    public async Task A_host_without_user_calls_runs_nothing()
    {
        await using var h = new EngineHarness(withUserCalls: false);
        h.AddDevices(1);
        var s = await h.AddAsync();
        await RunNowAsync(h, s);
        Assert.Equal(ScheduleEngine.NoUserCalls, (await h.EndedAsync(s.Schedule.Id)).Reason);
        Assert.Equal(0, h.Runner.ScheduleTasks);
    }

    [Fact]
    public async Task A_run_starts_its_devices_at_once_and_the_engines_parallel_limit_holds()
    {
        await using var h = new EngineHarness();
        h.Runner.Limit = 2; // the server setting Parallel tasks per plugin
        h.AddDevices(5);
        var gate = new TaskCompletionSource();
        h.Runner.Decide = async (_, _) =>
        {
            await gate.Task;
            return (TaskEndState.Done, null);
        };
        var s = await h.AddAsync();

        await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Running == 2 && h.Runner.Actions.Count == 2, "two tasks run and wait");
        Assert.Equal(5, h.Runner.ScheduleTasks); // all started at once; the engine's limit holds them

        gate.SetResult();
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal(5, run.Done);
        Assert.Equal(2, h.Runner.MaxRunning);
    }

    [Fact]
    public async Task The_window_end_cancels_queued_tasks_and_starts_no_new_one()
    {
        await using var h = new EngineHarness();
        h.Runner.Limit = 1; // the server setting: one at a time, the others wait as queued
        h.AddDevices(5);
        var gate = new TaskCompletionSource();
        h.Runner.Decide = async (_, _) =>
        {
            await gate.Task;
            return (TaskEndState.Done, null);
        };
        var s = await h.AddAsync(x => x with { When = x.When with { WindowEnd = new TimeOnly(5, 0) } });

        await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 1 && h.Runner.ScheduleTasks == 5, "one task runs, four wait");

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 5, 0, 0, TimeSpan.Zero));
        await Eventually.TrueAsync(() => h.Engine.LiveRun(s.Schedule.Id) is { NotStarted: 4 }, "the window end counts four devices not started");
        gate.SetResult();
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.DoneWithWarnings, 1, 4), (run.Result, run.Done, run.NotStarted));
        Assert.Equal("The window ended before 4 devices started.", run.Reason);
        Assert.Single(h.Runner.Actions);
        Assert.Equal(5, h.Runner.ScheduleTasks);
    }

    [Fact]
    public async Task Upgrade_online_runs_through_the_owners_tasks_and_a_missing_sign_in_warns()
    {
        await using var h = new EngineHarness();
        var device = h.AddDevices(1)[0];
        h.OwnerTasks.Decide = (_, _) => Task.FromResult<(TaskEndState, string?)>((TaskEndState.Failed, AxisDownloadTexts.SignIn));
        var s = await h.AddAsync(x => x with { Actions = [EngineHarness.Action("a1", NodeKind.UpgradeOnline)] });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal(("oadm.firmware", device.Id), Assert.Single(h.OwnerTasks.Actions));
        Assert.Empty(h.Runner.Actions);
        Assert.Equal("Sign in to MyAxis.", Assert.Single(await h.Store.FailuresAsync(run.Id, CancellationToken.None)).Message);
        Assert.True(h.Store.Find(s.Schedule.Id)!.SignInMissing);
        using var payload = JsonDocument.Parse(h.OwnerTasks.Runs.Single().Payload!);
        Assert.Equal("ownTrack", payload.RootElement.GetProperty("latest").GetProperty("target").GetString());

        h.OwnerTasks.Decide = (_, _) => Task.FromResult<(TaskEndState, string?)>((TaskEndState.Done, null));
        await RunNowAsync(h, s);
        Assert.Equal(RunResult.Done, (await h.EndedAsync(s.Schedule.Id)).Result);
        Assert.False(h.Store.Find(s.Schedule.Id)!.SignInMissing);
    }

    [Fact]
    public async Task Runs_keep_the_last_50_rows_per_schedule()
    {
        await using var h = new EngineHarness();
        var s = await h.AddAsync();
        for (var i = 0; i < 52; i++)
        {
            await RunNowAsync(h, s);
            await h.EndedAsync(s.Schedule.Id);
        }

        Assert.Equal(SchedulesInfo.KeptRuns, h.Runs(s.Schedule.Id).Count);
    }
}
