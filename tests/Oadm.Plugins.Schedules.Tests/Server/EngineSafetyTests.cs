using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>
/// The engine where things change under a run: a schedule changed during a look, a start that fails, a delete and a stop
/// during a run, the first stage without a device that is Ok, the offset of the run rows, the actions of a task.
/// </summary>
public sealed class EngineSafetyTests
{
    private static Task<ScheduleRun> RunNowAsync(EngineHarness h, StoredSchedule s) => h.Engine.RunNowAsync(s.Schedule.Id, "olaf", CancellationToken.None);

    private static IReadOnlyList<FakePublishedRuleEvent> Ended(EngineHarness h) => [.. h.Rules.Published.Where(e => e.TypeId == SchedulesInfo.RunEndedEvent)];

    private static TaskCompletionSource HoldActions(EngineHarness h)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.Decide = async (_, _) =>
        {
            await gate.Task;
            return (TaskEndState.Done, null);
        };
        return gate;
    }

    [Fact]
    public async Task A_look_never_starts_a_schedule_turned_off_after_the_look_began()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var first = await h.AddAsync(x => x with { Name = "First" });
        var second = await h.AddAsync(x => x with { Name = "Second" });

        // While the look starts the first schedule, the second is turned off (its snapshot of the look is stale then).
        h.Engine.Changed += id =>
        {
            if (id == first.Schedule.Id)
            {
                h.Store.UpdateAsync(second.Schedule.Id, s => s with { Schedule = s.Schedule with { On = false } }, CancellationToken.None).GetAwaiter().GetResult();
            }
        };
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 23, 0, 10, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);

        await h.EndedAsync(first.Schedule.Id);
        Assert.Empty(h.Runs(second.Schedule.Id));
        Assert.Equal(EngineHarness.Start, h.Store.Find(second.Schedule.Id)!.Handled); // nothing written over it
    }

    [Fact]
    public async Task A_start_that_fails_is_a_failed_row_and_the_others_still_run()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var broken = await h.AddAsync(x => x with { Name = "Broken" });
        var fine = await h.AddAsync(x => x with { Name = "Fine" });
        h.Records.FailOnce = broken.Schedule.Id.ToString("N");

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 23, 0, 10, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);

        var failed = Assert.Single(h.Runs(broken.Schedule.Id));
        Assert.Equal((RunResult.Failed, "The run could not start: The disk is full."), (failed.Result, failed.Reason));
        Assert.Equal(RunResult.Done, (await h.EndedAsync(fine.Schedule.Id)).Result);
    }

    [Fact]
    public async Task Deleting_a_schedule_cancels_its_queued_tasks_and_lets_the_running_one_end()
    {
        await using var h = new EngineHarness();
        h.Runner.Limit = 1;
        h.AddDevices(3);
        var gate = HoldActions(h);
        var s = await h.AddAsync();
        await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 1 && h.Runner.ScheduleTasks == 3, "one task runs, two wait");

        await h.Store.DeleteAsync(s.Schedule.Id, CancellationToken.None);
        h.Engine.StopRun(s.Schedule.Id, SchedulesPlugin.Deleted);
        await Eventually.TrueAsync(() => h.Engine.LiveRun(s.Schedule.Id) is { NotStarted: 2 }, "the two queued tasks are cancelled");
        gate.SetResult();

        await Eventually.TrueAsync(() => !h.Engine.IsRunning(s.Schedule.Id), "the run ended");
        var ended = Assert.Single(Ended(h));
        Assert.Equal(("Stopped", "1", "2"), (ended.Data["result"], ended.Data["done"], ended.Data["notStarted"]));
        Assert.Single(h.Runner.Actions);
        Assert.Empty(h.Runs(s.Schedule.Id)); // the schedule and its rows are gone
    }

    [Fact]
    public async Task A_stop_ends_the_run_at_once_and_the_next_engine_gives_its_running_devices_no_second_task()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(2);
        var gate = HoldActions(h);
        var s = await h.AddAsync();
        await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 2, "both tasks run");

        await h.Engine.DisposeAsync();
        var stopped = h.Runs(s.Schedule.Id)[0];
        Assert.Equal((RunResult.Stopped, "Schedules stopped during the run. 2 devices were still running; counted as not started.", 2), (stopped.Result, stopped.Reason, stopped.NotStarted));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunNowAsync(h, s)); // the stopped engine starts nothing
        Assert.Equal(ScheduleEngine.Stopping, refused.Message);

        // Turned on again: the devices whose task still runs get none.
        h.NewEngine();
        await RunNowAsync(h, s);
        var second = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((0, 2), (second.Done, second.NotStarted));
        Assert.Equal(2, h.Runner.ScheduleTasks);

        gate.SetResult();
        await Eventually.TrueAsync(() => h.Runner.Running == 0, "the left tasks ended");
        await RunNowAsync(h, s);
        Assert.Equal(2, (await h.EndedAsync(s.Schedule.Id)).Done);
        Assert.Equal(devices.Select(d => d.Id).Order(), h.Runner.Runs.Last(r => r.PluginId == SchedulesInfo.TaskPluginId).DeviceIds.Order());
    }

    [Fact]
    public async Task The_first_stage_takes_the_next_device_when_the_chosen_one_is_no_longer_ok()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(20);

        // The owner's view still says Ok; when the run starts the first device is offline.
        var view = new DeviceTable();
        foreach (var d in devices)
        {
            view.Add(d);
        }

        h.Owner(new TestCall(FakeCallContext.Over(view, tasks: h.OwnerTasks, userName: "anna")));
        h.Devices.Set(devices[0] with { Status = DeviceStatus.Unreachable });
        var s = await h.AddAsync(x => x with { Stages = true });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal([devices[1].Id], h.Runner.Runs.First(r => r.PluginId == SchedulesInfo.TaskPluginId).DeviceIds);
        Assert.Equal((RunResult.DoneWithWarnings, 19, 1), (run.Result, run.Done, run.NotReachable.Values.Sum()));
    }

    [Fact]
    public async Task Without_a_device_that_is_ok_the_stages_stop_before_stage_2()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(20);
        var view = new DeviceTable();
        foreach (var d in devices)
        {
            view.Add(d);
            h.Devices.Set(d with { Status = DeviceStatus.Unreachable });
        }

        h.Owner(new TestCall(FakeCallContext.Over(view, tasks: h.OwnerTasks, userName: "anna")));
        var s = await h.AddAsync(x => x with { Stages = true });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal((RunResult.Stopped, ScheduleEngine.NoDeviceTested), (run.Result, run.Reason));
        Assert.Equal(20, run.NotReachable.Values.Sum());
        Assert.Equal(0, h.Runner.ScheduleTasks);
    }

    [Fact]
    public async Task Run_rows_carry_the_offset_of_the_servers_local_time()
    {
        var plusOne = TimeZoneInfo.CreateCustomTimeZone("Test/PlusOne", TimeSpan.FromHours(1), "Plus one", "Plus one");
        await using var h = new EngineHarness(zone: plusOne);
        var s = await h.AddAsync();

        var started = await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.Equal(TimeSpan.FromHours(1), started.Started.Offset);
        Assert.Equal(TimeSpan.FromHours(1), run.Ended!.Value.Offset);
        Assert.Equal(EngineHarness.Start, run.Started); // the same instant
    }

    [Fact]
    public async Task A_device_task_runs_the_actions_of_its_runs_schedule_never_the_payloads()
    {
        await using var h = new EngineHarness();
        var devices = h.AddDevices(2);
        var gate = HoldActions(h);
        var s = await h.AddAsync();
        var run = await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 2, "the run's tasks run");

        var forged = System.Text.Json.JsonSerializer.Serialize(
            new RunTaskPayload(run.Id, s.Schedule.Id, "Nightly", [EngineHarness.Action("x", NodeKind.Network)]),
            SchedulesJson.Options);
        var ctx = new FakeTaskContext(new FakeCamera());
        var task = h.Engine.ExecuteAsync(new IdTaskContext(Guid.NewGuid(), ctx), devices[0], forged, CancellationToken.None);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 3, "the forged task runs the schedule's action");
        gate.SetResult();
        await task;
        Assert.Equal("oadm.restart", h.Runner.Actions.Last().PluginId);
        Assert.DoesNotContain(h.Runner.Actions, a => a.PluginId.StartsWith("oadm.network", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stopped_engine_looks_for_no_due_start_any_more()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var s = await h.AddAsync();
        h.Engine.Start();
        await h.Engine.DisposeAsync();

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 23, 0, 10, TimeSpan.Zero));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(h.Runs(s.Schedule.Id));
        Assert.Equal(0, h.Runner.ScheduleTasks);
        h.NewEngine();
    }

    [Fact]
    public async Task A_run_that_breaks_cancels_its_queued_tasks_and_leaves_its_running_device_alone()
    {
        await using var h = new EngineHarness();
        h.Runner.Limit = 1;
        var devices = h.AddDevices(3);
        var gate = HoldActions(h);
        var s = await h.AddAsync();
        await RunNowAsync(h, s);
        await Eventually.TrueAsync(() => h.Runner.Actions.Count == 1 && h.Runner.ScheduleTasks == 3, "one task runs, two wait");

        // The host's task list breaks: the next look at the tasks (every 5 s) throws.
        h.Runner.BreakIsActive = true;
        await Eventually.TrueAsync(
            () =>
            {
                h.Time.Advance(TimeSpan.FromSeconds(5));
                return !h.Engine.IsRunning(s.Schedule.Id);
            },
            "the broken run ended");
        h.Runner.BreakIsActive = false;

        var run = h.Runs(s.Schedule.Id)[0];
        Assert.Equal((RunResult.Failed, "The run failed: The task list broke.", 3), (run.Result, run.Reason, run.NotStarted));
        Assert.Equal(1, h.Runner.Running); // the queued two were cancelled, the running one goes on
        Assert.Equal([devices[0].Id], h.Left.Busy(s.Schedule.Id, h.Runner));
        gate.SetResult();
    }

    [Fact]
    public async Task Runs_the_server_stopped_during_end_stopped_when_it_starts_again()
    {
        var settings = new FakeSettings();
        var records = new MemoryPluginRecords();
        var id = Guid.NewGuid();
        var store = new ScheduleStore(settings, records);
        await store.SaveAsync(new StoredSchedule { Schedule = new Schedule { Id = id, Name = "Nightly" } }, CancellationToken.None);
        await store.SaveRunAsync(new StoredRun { Run = new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = id, Result = RunResult.Running } }, null, CancellationToken.None);
        store.Dispose();

        using var again = new ScheduleStore(settings, records);
        Assert.Equal(1, await again.LoadAsync(EngineHarness.Start, CancellationToken.None));
        var run = again.RunsOf(id)[0].Run;
        Assert.Equal((RunResult.Stopped, ScheduleStore.ServerStopped), (run.Result, run.Reason));
    }

    [Fact]
    public async Task A_missing_myaxis_sign_in_of_a_later_step_still_warns()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        h.Runner.Decide = (_, _) => Task.FromResult<(TaskEndState, string?)>((TaskEndState.Failed, "The device did not come back."));
        h.OwnerTasks.Decide = (_, _) => Task.FromResult<(TaskEndState, string?)>((TaskEndState.Failed, AxisDownloadTexts.SignIn));
        var s = await h.AddAsync(x => x with { Actions = [EngineHarness.Action("r", NodeKind.Restart), EngineHarness.Action("u", NodeKind.UpgradeOnline)] });

        await RunNowAsync(h, s);
        var run = await h.EndedAsync(s.Schedule.Id);
        Assert.StartsWith("2 steps failed. The device did not come back.", Assert.Single(await h.Store.FailuresAsync(run.Id, CancellationToken.None)).Message, StringComparison.Ordinal);
        Assert.True(h.Store.Find(s.Schedule.Id)!.SignInMissing);
    }

    [Fact]
    public async Task Turned_on_again_the_first_look_makes_up_nothing_and_records_one_missed_row()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var s = await h.AddAsync();

        // Turned off at 22:00, on again at Tuesday 01:00: Monday 23:00 was missed while it was off.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero));
        h.Engine.Start(catchUp: false);
        await Eventually.TrueAsync(() => h.Runs(s.Schedule.Id).Count == 1, "the missed start is recorded");
        var missed = h.Runs(s.Schedule.Id)[0];
        Assert.Equal((RunResult.Missed, ScheduleEngine.OffText), (missed.Result, missed.Reason));
        Assert.Equal(0, h.Runner.ScheduleTasks);
        Assert.Equal("Missed", Assert.Single(Ended(h)).Data["result"]);

        // It runs at its next time.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 23, 0, 10, TimeSpan.Zero));
        await h.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(RunResult.Done, (await h.EndedAsync(s.Schedule.Id)).Result);
    }

    [Fact]
    public async Task At_the_servers_start_a_missed_start_within_24_hours_runs()
    {
        await using var h = new EngineHarness();
        h.AddDevices(1);
        var s = await h.AddAsync();

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero));
        h.Engine.Start(catchUp: true);
        Assert.Equal(RunResult.Done, (await h.EndedAsync(s.Schedule.Id)).Result);
        Assert.Equal(1, h.Runner.ScheduleTasks);
    }
}
