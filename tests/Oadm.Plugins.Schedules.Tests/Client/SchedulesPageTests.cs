using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Schedules.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Schedules.Tests.Client;

/// <summary>The Schedules page against the sample server: the grid's texts, the toolbar, the runs, delete, live changes; no Avalonia.</summary>
public sealed class SchedulesPageTests
{
    private static readonly string Manage = PermissionKeys.Of(SchedulesInfo.PluginId, PermissionKeys.Manage);

    private static async Task<(SchedulesViewModel Page, FakePageContext Ctx)> OpenAsync(FakePageContext? ctx = null)
    {
        ctx ??= Samples.Context();
        var page = new SchedulesViewModel(ctx, new FakeTimeProvider(Samples.Now));
        page.Activate();
        await page.ReloadTask;
        return (page, ctx);
    }

    [Fact]
    public async Task The_grid_shows_each_schedule_in_short_texts()
    {
        var (page, _) = await OpenAsync();

        Assert.False(page.IsLoading);
        Assert.False(page.ShowEmpty);
        Assert.Equal(["Upgrade Site A", "Restart lobby cameras", "Certificates"], page.Schedules.Select(r => r.Name));
        var weekly = page.Schedules[0];
        Assert.Equal("Tag Site A and 1 device", weekly.DevicesText);
        Assert.Equal("Mondays 23:00 until 05:00", weekly.WhenText);
        Assert.Equal("Upgrade AXIS OS online, Restart", weekly.ThenText);
        Assert.Equal("Mon 30 Nov, 23:00", weekly.NextRunText);
        Assert.Equal("212 done, 3 failed, 12 offline", weekly.LastRunText);
        Assert.True(weekly.LastRunError);
        Assert.Equal("anna", weekly.Owner);

        var nightly = page.Schedules[1];
        Assert.Equal(SchedulesTexts.AllDevices, nightly.DevicesText);
        Assert.Equal("Daily 03:30", nightly.WhenText);
        Assert.True(nightly.LastRunOk);

        // Turned off, and a schedule that will not run as it should: the reason as a warning in Last run.
        var monthly = page.Schedules[2];
        Assert.Equal("Tags Parking, Lobby and 40 devices", monthly.DevicesText);
        Assert.Equal("Day 1 of each month 02:00", monthly.WhenText);
        Assert.Equal(SchedulesTexts.TurnedOff, monthly.NextRunText);
        Assert.Equal("Sign in to MyAxis.", monthly.LastRunText);
        Assert.True(monthly.LastRunWarning);
        Assert.True(page.ShowChooseSchedule);
    }

    [Fact]
    public async Task The_toolbar_waits_for_a_selection_and_the_manage_right()
    {
        var (page, ctx) = await OpenAsync();

        Assert.True(page.MayManage);
        Assert.True(page.NewScheduleCommand.CanExecute(null));
        Assert.False(page.EditCommand.CanExecute(null));
        Assert.False(page.RunNowCommand.CanExecute(null));
        Assert.False(page.ToggleCommand.CanExecute(null));
        Assert.False(page.DeleteCommand.CanExecute(null));

        page.SelectedSchedule = page.Schedules[0];
        Assert.True(page.EditCommand.CanExecute(null));
        Assert.True(page.RunNowCommand.CanExecute(null));
        Assert.True(page.DeleteCommand.CanExecute(null));
        Assert.Equal(SchedulesTexts.TurnOff, page.ToggleText);
        page.SelectedSchedule = page.Schedules[2];
        Assert.Equal(SchedulesTexts.TurnOn, page.ToggleText);

        ctx.Denied.Add(Manage);
        var (viewer, _) = await OpenAsync(ctx);
        viewer.SelectedSchedule = viewer.Schedules[0];
        Assert.False(viewer.MayManage);
        Assert.False(viewer.NewScheduleCommand.CanExecute(null));
        Assert.False(viewer.EditCommand.CanExecute(null));
        Assert.False(viewer.RunNowCommand.CanExecute(null));
        Assert.False(viewer.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_asks_first_with_a_red_delete_button()
    {
        var (page, ctx) = await OpenAsync();
        page.SelectedSchedule = page.Schedules[1];

        ctx.ConfirmAnswer = false;
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(SchedulesMethods.Delete));

        ctx.ConfirmAnswer = true;
        await page.DeleteCommand.ExecuteAsync(null);
        var (title, message, confirm) = ctx.Confirmations[^1];
        Assert.Equal(SchedulesTexts.DeleteTitle, title);
        Assert.Contains("Restart lobby cameras", message, StringComparison.Ordinal);
        Assert.Equal("Delete", confirm);
        Assert.True(MessageWindow.IsDestructive(confirm)); // the host's confirmation makes it red
        Assert.Equal(1, ctx.CallsOf(SchedulesMethods.Delete));
        Assert.DoesNotContain(page.Schedules, r => r.Name == "Restart lobby cameras");
        Assert.Null(page.SelectedSchedule);
    }

    [Fact]
    public async Task A_selected_schedule_shows_its_runs_newest_first_and_a_failed_run_its_failed_devices()
    {
        var (page, ctx) = await OpenAsync();

        page.SelectedSchedule = page.Schedules[0];
        await page.RunsTask;
        Assert.Equal(4, page.Runs.Count);
        Assert.True(page.Runs.Zip(page.Runs.Skip(1)).All(p => p.First.StartedSortKey >= p.Second.StartedSortKey));
        Assert.Equal("Failed", page.Runs[0].ResultText);
        Assert.Equal("212 done, 3 failed, 12 offline", page.Runs[0].Detail);
        Assert.Equal("Stopped at stage 2: 1 failed, 1 done, 12 offline", page.Runs[1].ResultText); // why it stopped
        Assert.Equal(string.Empty, page.Runs[1].Detail);
        Assert.Equal("Done with warnings", page.Runs[2].ResultText);
        Assert.Equal("Missed", page.Runs[3].ResultText);
        Assert.Equal(string.Empty, page.Runs[3].Detail); // Missed says it all
        Assert.False(page.ShowFailures);
        Assert.Equal(2, page.RunsSpan);

        page.SelectedRun = page.Runs[0];
        await page.FailuresTask;
        Assert.True(page.ShowFailures);
        Assert.Equal(1, page.RunsSpan);
        Assert.Equal(3, page.Failures.Count);
        Assert.Equal("Upgrade AXIS OS from axis.com", page.Failures[0].Step);

        page.SelectedRun = page.Runs[2]; // done with warnings: no failed devices
        await page.FailuresTask;
        Assert.False(page.ShowFailures);
        Assert.Equal(1, ctx.CallsOf(SchedulesMethods.RunFailures));

        page.SelectedSchedule = page.Schedules[1];
        await page.RunsTask;
        Assert.True(page.ShowNoRuns);
    }

    [Fact]
    public async Task Run_now_and_turn_off_change_the_selected_schedule()
    {
        var (page, ctx) = await OpenAsync();
        page.SelectedSchedule = page.Schedules[0];

        await page.RunNowCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(SchedulesMethods.RunNow));
        await page.ReloadTask;

        await page.ToggleCommand.ExecuteAsync(null);
        Assert.Contains("\"on\":false", ctx.Calls.Last(c => c.Method == SchedulesMethods.TurnOn).Payload, StringComparison.Ordinal);
        Assert.False(page.Schedules[0].On);
        Assert.Equal(SchedulesTexts.TurnOn, page.ToggleText);
        Assert.Null(page.ErrorText);
    }

    [Fact]
    public async Task The_changed_event_reads_the_list_and_the_runs_again()
    {
        var (page, ctx) = await OpenAsync();
        page.SelectedSchedule = page.Schedules[0];
        await page.RunsTask;
        var lists = ctx.CallsOf(SchedulesMethods.List);
        var runs = ctx.CallsOf(SchedulesMethods.Runs);

        ctx.Push(SchedulesMethods.ChangedTopic, new ChangedEvent(Samples.WeeklyId));

        await Eventually.TrueAsync(() => ctx.CallsOf(SchedulesMethods.List) > lists && ctx.CallsOf(SchedulesMethods.Runs) > runs, "the page read the list and the runs again");
        page.Deactivate();
    }

    [Fact]
    public async Task An_empty_list_says_what_to_do_first()
    {
        var ctx = Samples.Context();
        ctx.Answers[SchedulesMethods.List] = _ => Samples.Serialize(new SchedulesReply([]));
        var (page, _) = await OpenAsync(ctx);

        Assert.True(page.ShowEmpty);
        Assert.False(page.ShowChooseSchedule);
        Assert.Equal("Create a schedule to run tasks at a set time.", SchedulesTexts.Empty);
    }

    [Fact]
    public async Task The_first_read_shows_the_loading_overlay_until_the_list_is_there()
    {
        var ctx = Samples.Context();
        var gate = new TaskCompletionSource();
        ctx.MethodGates[SchedulesMethods.List] = gate.Task;
        var page = new SchedulesViewModel(ctx, new FakeTimeProvider(Samples.Now));
        page.Activate();

        Assert.True(page.IsLoading);
        Assert.False(page.ShowEmpty);
        gate.SetResult();
        await page.ReloadTask;
        Assert.False(page.IsLoading);
        page.Deactivate();
    }

    [Fact]
    public async Task Search_filters_by_name_action_devices_or_owner()
    {
        var (page, _) = await OpenAsync();

        page.SearchText = "restart";
        Assert.Equal(["Upgrade Site A", "Restart lobby cameras"], page.Schedules.Select(r => r.Name));
        page.SearchText = "ben";
        Assert.Equal(["Restart lobby cameras"], page.Schedules.Select(r => r.Name));
        page.SearchText = string.Empty;
        Assert.Equal(3, page.Schedules.Count);
    }

    [Fact]
    public async Task A_save_during_a_live_reload_joins_it_and_reads_once_more_never_two_reads_at_once()
    {
        var ctx = Samples.Context();
        var (page, _) = await OpenAsync(ctx);
        var gate = new TaskCompletionSource();
        ctx.MethodGates[SchedulesMethods.List] = gate.Task;
        var lists = ctx.CallsOf(SchedulesMethods.List);

        ctx.Push(SchedulesMethods.ChangedTopic, new ChangedEvent(null)); // a live reload, held at the list
        await Eventually.TrueAsync(() => ctx.CallsOf(SchedulesMethods.List) == lists + 1, "the live reload asked for the list");
        page.ShowEditor = _ => Task.FromResult(true); // the wizard saved
        var opened = page.NewScheduleCommand.ExecuteAsync(null);

        Assert.False(opened.IsCompleted); // waits for the running reload
        Assert.Equal(lists + 1, ctx.CallsOf(SchedulesMethods.List));
        gate.SetResult();
        await opened;

        Assert.Equal(1, ctx.MostAtOnce[SchedulesMethods.List]);
        Assert.Equal(lists + 2, ctx.CallsOf(SchedulesMethods.List)); // once more after the save
        page.Deactivate();
    }

    [Fact]
    public async Task Delete_waits_for_chosen_runs_that_ended_and_sends_their_ids()
    {
        var running = Samples.Run(Guid.NewGuid(), Samples.WeeklyId, 0, RunResult.Running, 3);
        var ctx = Samples.Context();
        ctx.Answers[SchedulesMethods.Runs] = _ => Samples.Serialize(new RunsReply([running, .. Samples.Runs(Samples.WeeklyId)]));
        var (page, _) = await OpenAsync(ctx);
        Assert.False(page.DeleteRunsCommand.CanExecute(null));
        Assert.False(page.DeleteAllRunsCommand.CanExecute(null)); // no schedule chosen

        page.SelectedSchedule = page.Schedules[0];
        await page.RunsTask;
        Assert.False(page.DeleteRunsCommand.CanExecute(null)); // no run chosen
        Assert.True(page.DeleteAllRunsCommand.CanExecute(null));
        page.SetSelectedRuns([page.Runs.Single(r => r.Id == running.Id)]);
        Assert.False(page.DeleteRunsCommand.CanExecute(null)); // a running run is never deleted
        var ended = page.Runs.Where(r => !r.IsRunning).Take(2).ToList();
        page.SetSelectedRuns([page.Runs.Single(r => r.Id == running.Id), .. ended]);
        Assert.True(page.DeleteRunsCommand.CanExecute(null));

        await page.DeleteRunsCommand.ExecuteAsync(null);
        var request = Samples.Parse<DeleteRunsRequest>(ctx.Calls.Last(c => c.Method == SchedulesMethods.DeleteRuns).Payload);
        Assert.Equal(Samples.WeeklyId, request.ScheduleId);
        Assert.False(request.All);
        Assert.Equal(ended.Select(r => r.Id), request.RunIds);
        Assert.Empty(page.SelectedRuns);
        Assert.False(page.DeleteRunsCommand.CanExecute(null));

        ctx.Denied.Add(Manage);
        var (viewer, _) = await OpenAsync(ctx);
        viewer.SelectedSchedule = viewer.Schedules[0];
        await viewer.RunsTask;
        viewer.SetSelectedRuns([.. viewer.Runs]);
        Assert.False(viewer.DeleteRunsCommand.CanExecute(null));
        Assert.False(viewer.DeleteAllRunsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_all_asks_first_with_a_red_delete_and_deletes_every_run_of_the_schedule()
    {
        var (page, ctx) = await OpenAsync();
        page.SelectedSchedule = page.Schedules[0];
        await page.RunsTask;

        ctx.ConfirmAnswer = false;
        await page.DeleteAllRunsCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(SchedulesMethods.DeleteRuns));
        Assert.Equal(4, page.Runs.Count);

        ctx.ConfirmAnswer = true;
        await page.DeleteAllRunsCommand.ExecuteAsync(null);
        var (title, message, confirm) = ctx.Confirmations[^1];
        Assert.Equal(SchedulesTexts.DeleteAllRunsTitle, title);
        Assert.Equal("Delete every run of Upgrade Site A?", message);
        Assert.True(MessageWindow.IsDestructive(confirm));
        Assert.True(Samples.Parse<DeleteRunsRequest>(ctx.Calls.Last(c => c.Method == SchedulesMethods.DeleteRuns).Payload).All);
        Assert.Empty(page.Runs);
        Assert.True(page.ShowNoRuns);
        Assert.False(page.DeleteAllRunsCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_running_run_shows_its_step_and_progress_and_a_refresh_updates_it_in_place()
    {
        var progress = 42;
        var runningId = Guid.NewGuid();
        ScheduleRun Running() => Samples.Run(runningId, Samples.WeeklyId, 0, RunResult.Running, 3) with
        {
            Started = Samples.Now, Ended = null, CurrentStep = "Stage 2 of 6", Progress = progress,
        };
        var ctx = Samples.Context();
        ctx.Answers[SchedulesMethods.Runs] = _ => Samples.Serialize(new RunsReply([Running(), .. Samples.Runs(Samples.WeeklyId)]));
        var (page, _) = await OpenAsync(ctx);
        page.SelectedSchedule = page.Schedules[0];
        await page.RunsTask;

        var running = page.Runs.Single(r => r.Id == runningId);
        Assert.True(running.IsRunning);
        Assert.Equal("Stage 2 of 6", running.CurrentStep);
        Assert.Equal(42, running.Progress);
        Assert.Equal("42 %", running.ProgressText);
        Assert.All(page.Runs.Where(r => r != running), r => Assert.True(r.CurrentStep.Length == 0 && r.ProgressText.Length == 0)); // ended: both empty

        // A failed run chosen with its failed devices; then the server reports more progress.
        var failed = page.Runs.First(r => r.HasFailures);
        page.SelectedRun = failed;
        page.SetSelectedRuns([failed]);
        await page.FailuresTask;
        var failureReads = ctx.CallsOf(SchedulesMethods.RunFailures);
        var shown = page.Failures.ToList();
        var resets = 0;
        page.Runs.CollectionChanged += (_, _) => resets++;
        progress = 60;
        page.RequestReload();
        await page.ReloadTask;

        Assert.Same(running, page.Runs.Single(r => r.Id == runningId)); // the same row, updated
        Assert.Equal("60 %", running.ProgressText);
        Assert.Equal(0, resets); // no rebuild of the list
        Assert.Same(failed, page.SelectedRun);
        Assert.Equal([failed], page.SelectedRuns);
        Assert.Equal(failureReads, ctx.CallsOf(SchedulesMethods.RunFailures));
        Assert.Equal(shown, page.Failures);
    }

    [Fact]
    public void Every_result_reads_once_without_repeated_counts_or_double_punctuation()
    {
        ScheduleRun Run(RunResult result, int done = 0, int warnings = 0, int failed = 0, int notStarted = 0, string? reason = null, int offline = 0) => new()
        {
            Result = result, Done = done, Warnings = warnings, Failed = failed, NotStarted = notStarted, Reason = reason,
            NotReachable = offline == 0 ? new Dictionary<string, int>() : new Dictionary<string, int> { ["Offline"] = offline },
        };

        Assert.Equal("230 done", ScheduleFormat.Summary(Run(RunResult.Done, done: 230)));
        Assert.Equal("220 done, 4 warnings, 12 offline", ScheduleFormat.Summary(Run(RunResult.DoneWithWarnings, done: 220, warnings: 4, offline: 12)));
        Assert.Equal("212 done, 3 failed", ScheduleFormat.Summary(Run(RunResult.Failed, done: 212, failed: 3)));
        Assert.Equal("Stopped at stage 1: 1 failed, 6 not started", ScheduleFormat.Summary(Run(RunResult.Stopped, failed: 1, notStarted: 6, reason: "Stopped at stage 1: 1 failed.")));
        Assert.Equal("Stopped at stage 1: 1 failed, 6 not started", ScheduleFormat.Summary(Run(RunResult.Stopped, failed: 1, notStarted: 6, reason: "Stopped at stage 1: 1 failed")));
        Assert.Equal("Stopped at stage 3: 1 failed", ScheduleFormat.Summary(Run(RunResult.Stopped, failed: 1, reason: "Stopped at stage 3: 1 failed.")));
        Assert.Equal("The owner no longer has the right Restart.", ScheduleFormat.Summary(Run(RunResult.Failed, reason: "The owner no longer has the right Restart.")));
        Assert.Equal("The owner no longer has the right Restart. 9 not started",
            ScheduleFormat.Summary(Run(RunResult.Failed, notStarted: 9, reason: "The owner no longer has the right Restart.")));
        Assert.Equal("Missed", ScheduleFormat.Summary(Run(RunResult.Missed)));
        Assert.Equal("Skipped", ScheduleFormat.Summary(Run(RunResult.Skipped)));
        Assert.Equal("Running, 12 done", ScheduleFormat.Summary(Run(RunResult.Running, done: 12)));

        var row = RunRow.Of(Run(RunResult.Stopped, failed: 1, notStarted: 6, reason: "Stopped at stage 1: 1 failed."), Samples.Now);
        Assert.Equal("Stopped at stage 1: 1 failed, 6 not started", row.ResultText);
        Assert.Equal(string.Empty, row.Detail); // nothing after it
        var failed = RunRow.Of(Run(RunResult.Failed, done: 212, failed: 3), Samples.Now);
        Assert.Equal("Failed: 212 done, 3 failed", failed.ResultText + ": " + failed.Detail); // as the chip shows it
        Assert.All(new[] { row.ResultText, failed.Detail }, t => Assert.DoesNotContain("..", t, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "about 1 min")]
    [InlineData(20, "about 20 min")]
    [InlineData(200, "about 3 h 20 min")]
    [InlineData(181, "about 3 h")]
    [InlineData(1440, "about 24 h")]
    public void The_estimate_reads_about(int minutes, string text) => Assert.Equal(text, ScheduleFormat.Duration(TimeSpan.FromMinutes(minutes)));
}
