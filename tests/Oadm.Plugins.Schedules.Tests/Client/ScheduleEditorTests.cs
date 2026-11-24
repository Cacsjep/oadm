using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Schedules.Tests.Client;

/// <summary>The schedule wizard against the sample server: the steps, the errors below the fields, the Overview; no Avalonia.</summary>
public sealed class ScheduleEditorTests
{
    private static async Task<(ScheduleEditorViewModel Editor, FakePageContext Ctx)> OpenAsync(Guid? id, Func<Schedule, ScheduleCheck>? check = null)
    {
        var ctx = Samples.Context(check);
        var editor = new ScheduleEditorViewModel(ctx, id, new FakeTimeProvider(Samples.Now));
        await editor.LoadAsync();
        await editor.SettleAsync();
        return (editor, ctx);
    }

    private static ActionKindInfo Kind(NodeKind kind) => Samples.Kinds.Single(k => k.Kind == kind);

    /// <summary>A new schedule up to step Then: a name, the tag Site A.</summary>
    private static async Task<ScheduleEditorViewModel> ToThenAsync(Func<Schedule, ScheduleCheck>? check = null)
    {
        var (editor, _) = await OpenAsync(null, check);
        editor.Name = "Upgrade Site A";
        editor.NextCommand.Execute(null);
        editor.SetChoice(DeviceChoice.Devices([], ["Site A"]));
        await editor.SettleAsync();
        editor.NextCommand.Execute(null);
        editor.NextCommand.Execute(null);
        Assert.Equal(ScheduleEditorViewModel.ThenStep, editor.Step);
        return editor;
    }

    [Fact]
    public async Task A_new_schedule_starts_on_step_schedule_without_errors_and_weekly_on_mondays_at_23()
    {
        var (editor, _) = await OpenAsync(null);

        Assert.Equal(SchedulesTexts.NewTitle, editor.Title);
        Assert.Equal(ScheduleEditorViewModel.ScheduleStep, editor.Step);
        Assert.Equal(["Schedule", "Devices", "When", "Then", "Overview"], editor.Steps.Select(s => s.Name));
        Assert.True(editor.On);
        Assert.False(editor.Stages);
        Assert.Equal(Recurrence.Weekly, editor.Repeat.Value);
        Assert.Equal([DayOfWeek.Monday], editor.Days.Where(d => d.IsChecked).Select(d => d.Day));
        Assert.Equal("23:00", editor.Time);
        Assert.False(editor.HasErrors); // untouched: no error shows
        Assert.Null(editor.ErrorOf(nameof(ScheduleEditorViewModel.Name)));
        Assert.Equal(SchedulesTexts.EnterName, editor.StepProblem); // but Next says why it waits
        Assert.False(editor.NextCommand.CanExecute(null));
        Assert.Equal(SchedulesTexts.EnterName, editor.Problem);
        Assert.Equal(Samples.Kinds.Select(k => k.Kind).OrderBy(NodeLook.Rank), editor.AddableKinds.Select(k => k.Kind));
    }

    [Fact]
    public async Task A_step_opens_only_when_every_step_before_it_is_complete()
    {
        var (editor, _) = await OpenAsync(null);

        Assert.False(editor.Steps[ScheduleEditorViewModel.DevicesStep].CanOpen);
        editor.Steps[ScheduleEditorViewModel.WhenStep].OpenCommand.Execute(null);
        Assert.Equal(ScheduleEditorViewModel.ScheduleStep, editor.Step);

        editor.Name = "Nightly restart";
        await editor.SettleAsync();
        Assert.True(editor.NextCommand.CanExecute(null));
        Assert.True(editor.Steps[ScheduleEditorViewModel.DevicesStep].CanOpen);
        Assert.False(editor.Steps[ScheduleEditorViewModel.WhenStep].CanOpen); // no devices yet

        editor.NextCommand.Execute(null);
        Assert.Equal(ScheduleEditorViewModel.DevicesStep, editor.Step);
        Assert.Equal(SchedulesTexts.ChooseDevicesFirst, editor.StepProblem);
        Assert.False(editor.NextCommand.CanExecute(null));

        editor.SetChoice(DeviceChoice.AllDevices);
        await editor.SettleAsync();
        Assert.Equal(12, editor.ChosenDevices.Count);
        Assert.Equal("Selected devices: All", editor.DevicesButtonText);
        Assert.True(editor.Steps[ScheduleEditorViewModel.WhenStep].CanOpen);
        Assert.True(editor.Steps[ScheduleEditorViewModel.ThenStep].CanOpen); // When is complete by its defaults
        Assert.False(editor.Steps[ScheduleEditorViewModel.OverviewStep].CanOpen); // no action yet

        editor.Steps[ScheduleEditorViewModel.ThenStep].OpenCommand.Execute(null);
        Assert.Equal(ScheduleEditorViewModel.ThenStep, editor.Step);
        Assert.Equal(SchedulesTexts.AddActionFirst, editor.StepProblem);

        editor.BackCommand.Execute(null);
        Assert.Equal(ScheduleEditorViewModel.WhenStep, editor.Step);
        editor.Steps[ScheduleEditorViewModel.ScheduleStep].OpenCommand.Execute(null); // back always works
        Assert.Equal(ScheduleEditorViewModel.ScheduleStep, editor.Step);
    }

    [Fact]
    public async Task Errors_show_below_their_field_only_after_an_edit_a_leave_or_a_step_left()
    {
        var (editor, _) = await OpenAsync(null);

        // Left: the empty name says so.
        editor.FieldLeft(nameof(ScheduleEditorViewModel.Name));
        Assert.Equal(SchedulesTexts.EnterName, editor.ErrorOf(nameof(ScheduleEditorViewModel.Name)));

        // Edited: a wrong time at once; the window end only once it is edited.
        editor.Time = "25:00";
        Assert.Equal(SchedulesTexts.EnterTime, editor.ErrorOf(nameof(ScheduleEditorViewModel.Time)));
        editor.Time = "23:00";
        Assert.Null(editor.ErrorOf(nameof(ScheduleEditorViewModel.Time)));
        editor.WindowEnd = "23:00";
        Assert.Equal(SchedulesTexts.SameAsStart, editor.ErrorOf(nameof(ScheduleEditorViewModel.WindowEnd)));
        editor.WindowEnd = string.Empty;
        Assert.Null(editor.ErrorOf(nameof(ScheduleEditorViewModel.WindowEnd)));

        // Weekly without a day: below Days, once a day was turned off.
        editor.Days[0].IsChecked = false;
        Assert.Equal(SchedulesTexts.ChooseDays, editor.DaysError);
        editor.Days[4].IsChecked = true;
        Assert.Null(editor.DaysError);

        // The devices: shown when step Devices is left, not before.
        Assert.Null(editor.ChoiceError);
        editor.Name = "Nightly";
        editor.NextCommand.Execute(null);
        Assert.Null(editor.ChoiceError);
        editor.BackCommand.Execute(null);
        Assert.Equal(SchedulesTexts.ChooseDevicesFirst, editor.ChoiceError);
    }

    [Fact]
    public async Task The_servers_check_shows_below_its_field_and_blocks_next()
    {
        var (editor, _) = await OpenAsync(null);

        editor.Name = "Taken";
        await editor.SettleAsync();

        Assert.Equal("A schedule with this name exists.", editor.ErrorOf(nameof(ScheduleEditorViewModel.Name)));
        Assert.Equal("A schedule with this name exists.", editor.StepProblem);
        Assert.False(editor.NextCommand.CanExecute(null));

        editor.Name = "Upgrade Site A";
        await editor.SettleAsync();
        Assert.Null(editor.ErrorOf(nameof(ScheduleEditorViewModel.Name)));
        Assert.True(editor.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_estimate_warning_shows_below_when_only_when_it_applies_and_save_stays_possible()
    {
        const string warning = "A run takes about 24 h, longer than the 5 h between runs. A start during a run is skipped.";
        var editor = await ToThenAsync(s => Samples.Check(s, s.When.Recurrence == Recurrence.Daily ? warning : null));
        editor.Steps[ScheduleEditorViewModel.WhenStep].OpenCommand.Execute(null);

        Assert.False(editor.HasWhenWarning);
        editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Daily);
        await editor.SettleAsync();
        Assert.Equal(warning, editor.WhenWarning);
        Assert.Null(editor.ErrorOf(nameof(ScheduleEditorViewModel.Repeat)));
        Assert.True(editor.NextCommand.CanExecute(null)); // a warning never blocks

        editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Weekly);
        await editor.SettleAsync();
        Assert.False(editor.HasWhenWarning);
    }

    [Fact]
    public async Task Then_adds_actions_from_the_node_list_moves_and_removes_them_and_shows_the_servers_errors_on_the_node()
    {
        var editor = await ToThenAsync();

        Assert.True(editor.ShowsAddFirst);
        Assert.False(editor.HasActionsError); // untouched
        editor.AddActionCommand.Execute(null);
        Assert.True(editor.Picker.IsOpen);
        Assert.Equal(Samples.Kinds.Select(k => k.Name).Order(), editor.Picker.ShownItems.Select(i => i.Label).Order());
        Assert.Contains("Restart", editor.Picker.ShownItems.Select(i => i.Label));
        editor.Picker.Choose(editor.Picker.ShownItems.Single(i => i.Label == "Restart"));

        var restart = Assert.Single(editor.Nodes);
        Assert.Same(restart, editor.SelectedNode);
        Assert.True(restart.ContinueOnError); // on by default
        Assert.Equal(SchedulesTexts.RestartNote, restart.Note);
        Assert.False(editor.ShowsAddFirst);

        var config = editor.AddAfter(-1, Kind(NodeKind.Configuration))!;
        await editor.SettleAsync();
        Assert.Equal([NodeKind.Configuration, NodeKind.Restart], editor.Nodes.Select(n => n.Kind));
        Assert.Equal("Choose the configuration file.", config.CanvasError);
        Assert.NotNull(editor.Chain.Nodes[0].Below);
        Assert.StartsWith("Configuration: ", editor.StepProblem);

        var online = editor.AddAfter(1, Kind(NodeKind.UpgradeOnline))!;
        Assert.Equal(SchedulesTexts.MyAxisNote, online.Note);
        editor.MoveUpCommand.Execute(null);
        Assert.Equal([NodeKind.Configuration, NodeKind.UpgradeOnline, NodeKind.Restart], editor.Nodes.Select(n => n.Kind));

        foreach (var _ in editor.Nodes.ToList())
        {
            editor.SelectedNode = editor.Nodes[0];
            editor.RemoveNodeCommand.Execute(null);
        }

        await editor.SettleAsync();
        Assert.Empty(editor.Nodes);
        Assert.Equal(SchedulesTexts.AddActionFirst, editor.ActionsError); // the list's error below the canvas
    }

    [Fact]
    public async Task At_most_ten_actions()
    {
        var editor = await ToThenAsync();
        for (var i = 0; i < SchedulesInfo.MaxActions; i++)
        {
            Assert.NotNull(editor.AddAfter(editor.Nodes.Count - 1, Kind(NodeKind.Restart)));
        }

        Assert.Null(editor.AddAfter(editor.Nodes.Count - 1, Kind(NodeKind.Restart)));
        Assert.False(editor.AddActionCommand.CanExecute(null));
        Assert.All(editor.Chain.Nodes, n => Assert.False(n.CanAddAfter));
    }

    [Fact]
    public async Task The_overview_has_one_line_per_fact_the_steps_before_do_not_show()
    {
        var editor = await ToThenAsync();
        editor.AddAfter(-1, Kind(NodeKind.UpgradeOnline));
        editor.AddAfter(0, Kind(NodeKind.Restart));
        await editor.SettleAsync();
        editor.NextCommand.Execute(null);

        Assert.Equal(ScheduleEditorViewModel.OverviewStep, editor.Step);
        Assert.Equal(["Next run", "Then", "Estimated"], editor.Overview.Select(l => l.Label)); // no stages, no device count
        Assert.Equal("Tue 24 Nov, 23:00", editor.Overview[0].Text);
        Assert.Equal("Upgrade AXIS OS online, Restart", editor.Overview[1].Text);
        Assert.Equal("about 3 h 20 min", editor.Overview[2].Text);

        editor.Steps[ScheduleEditorViewModel.ScheduleStep].OpenCommand.Execute(null);
        editor.Stages = true;
        await editor.SettleAsync();
        editor.Steps[ScheduleEditorViewModel.OverviewStep].OpenCommand.Execute(null);
        Assert.Equal(["Next run", "Then", "Stages", "Estimated"], editor.Overview.Select(l => l.Label));
        Assert.Equal("1 → 3 → 8 → 15 → 30 → 93", editor.Overview[2].Text);
        Assert.DoesNotContain(editor.Overview, l => l.Text.Contains("device", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_sends_the_schedule_and_closes()
    {
        var editor = await ToThenAsync();
        editor.AddAfter(-1, Kind(NodeKind.Restart));
        await editor.SettleAsync();
        editor.NextCommand.Execute(null);
        bool? closed = null;
        editor.CloseRequested += (_, saved) => closed = saved;

        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = Assert.IsType<Schedule>(editor.Saved);
        Assert.Equal("Upgrade Site A", saved.Name);
        Assert.Equal(["Site A"], saved.Devices.Tags);
        Assert.Equal(Recurrence.Weekly, saved.When.Recurrence);
        Assert.Equal([DayOfWeek.Monday], saved.When.Days);
        Assert.Equal(new TimeOnly(23, 0), saved.When.Time);
        Assert.Null(saved.When.WindowEnd);
        Assert.Equal([NodeKind.Restart], saved.Actions.Select(a => a.Kind));
    }

    [Fact]
    public async Task Editing_fills_every_step_from_the_saved_schedule()
    {
        var (editor, ctx) = await OpenAsync(Samples.WeeklyId);

        Assert.Equal(SchedulesTexts.EditTitle, editor.Title);
        Assert.Equal("Upgrade Site A", editor.Name);
        Assert.True(editor.Stages);
        Assert.Equal("Selected devices: Tag Site A and 1 device", editor.DevicesButtonText);
        Assert.Equal(9, editor.ChosenDevices.Count); // eight with the tag and the chosen one
        Assert.Equal("05:00", editor.WindowEnd);
        Assert.Equal([NodeKind.UpgradeOnline, NodeKind.Restart], editor.Nodes.Select(n => n.Kind));
        Assert.False(editor.HasErrors);
        Assert.Equal(0, ctx.CallsOf(SchedulesMethods.Check)); // the reply of get carries the check
        Assert.All(editor.Steps.Skip(1), s => Assert.True(s.CanOpen));
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Once_needs_a_date_and_monthly_a_day()
    {
        var (editor, _) = await OpenAsync(null);
        editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Once);
        Assert.True(editor.IsOnce);
        editor.Date = null;
        Assert.Equal(SchedulesTexts.ChooseDate, editor.DateError);

        editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Monthly);
        Assert.Null(editor.DateError);
        editor.DayOfMonth = 15;
        var when = editor.ToSchedule().When;
        Assert.Equal(15, when.DayOfMonth);
        Assert.Null(when.Date);
        Assert.Empty(when.Days);
    }

    [Fact]
    public async Task The_check_waits_until_the_typing_paused_and_then_runs_once()
    {
        var (editor, ctx) = await OpenAsync(null);
        var clock = (FakeTimeProvider)editor.Clock;
        var checks = ctx.CallsOf(SchedulesMethods.Check);

        editor.Name = "N";
        clock.Advance(ScheduleEditorViewModel.CheckDelay / 2);
        editor.Name = "Ni";
        clock.Advance(ScheduleEditorViewModel.CheckDelay / 2);
        editor.Name = "Nightly";
        Assert.Equal(checks, ctx.CallsOf(SchedulesMethods.Check)); // still typing

        await editor.SettleAsync();
        Assert.Equal(checks + 1, ctx.CallsOf(SchedulesMethods.Check));
        Assert.Contains("\"Nightly\"", ctx.Calls.Last(c => c.Method == SchedulesMethods.Check).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_device_ids_go_to_the_check_only_when_the_choice_changed_and_save_always_sends_them()
    {
        var ids = Samples.Devices().Select(d => d.Id).Take(5).ToList();
        var (editor, ctx) = await OpenAsync(null);
        editor.Name = "Five cameras";
        editor.SetChoice(DeviceChoice.Devices(ids));
        await editor.SettleAsync();
        var changed = Samples.Parse<ScheduleRequest>(ctx.Calls.Last(c => c.Method == SchedulesMethods.Check).Payload);
        Assert.False(changed.DevicesUnchanged);
        Assert.Equal(ids, changed.Schedule.Devices.DeviceIds);

        editor.Name = "Five cameras at night";
        await editor.SettleAsync();
        var unchanged = Samples.Parse<ScheduleRequest>(ctx.Calls.Last(c => c.Method == SchedulesMethods.Check).Payload);
        Assert.True(unchanged.DevicesUnchanged);
        Assert.Equal(9, unchanged.KnownDeviceCount); // the last check's count
        Assert.Empty(unchanged.Schedule.Devices.DeviceIds);
        Assert.Null(editor.ChoiceError);

        editor.AddAfter(-1, Kind(NodeKind.Restart));
        await editor.SettleAsync();
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = Samples.Parse<ScheduleRequest>(ctx.Calls.Last(c => c.Method == SchedulesMethods.Save).Payload);
        Assert.False(saved.DevicesUnchanged);
        Assert.Null(saved.KnownDeviceCount);
        Assert.Equal(ids, saved.Schedule.Devices.DeviceIds);
    }

    [Fact]
    public async Task A_save_refused_for_the_devices_shows_the_error_below_the_devices_button_never_in_the_footer()
    {
        var gone = false;
        var (editor, ctx) = await OpenAsync(Samples.WeeklyId, s => gone ? Samples.Check(s) with { DevicesError = "Choose at least one device or tag." } : Samples.Check(s));
        Assert.Equal("Selected devices: Tag Site A and 1 device", editor.DevicesButtonText);
        editor.Steps[ScheduleEditorViewModel.OverviewStep].OpenCommand.Execute(null);
        Assert.True(editor.IsOverviewStep);

        gone = true; // meanwhile the devices of the choice were removed from OADM
        ctx.Answers[SchedulesMethods.Save] = _ => throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.FailedPrecondition, "Choose at least one device or tag."));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.ErrorText);
        Assert.Equal(ScheduleEditorViewModel.DevicesStep, editor.Step);
        Assert.Equal("Choose at least one device or tag.", editor.ChoiceError);
    }

    [Fact]
    public async Task A_device_removed_from_oadm_is_not_counted_on_the_devices_button()
    {
        var ctx = Samples.Context();
        var stored = Samples.Weekly with { Devices = new ScheduleDevices { Kind = ScheduleDevicesKind.Chosen, Tags = ["Site A"] } }; // the server dropped the removed one
        ctx.Answers[SchedulesMethods.Get] = _ => Samples.Serialize(new ScheduleReply(stored, Samples.Check(stored)));
        var editor = new ScheduleEditorViewModel(ctx, Samples.WeeklyId, new FakeTimeProvider(Samples.Now));
        await editor.LoadAsync();

        Assert.Equal("Selected devices: Tag Site A", editor.DevicesButtonText);
        Assert.Equal(8, editor.ChosenDevices.Count);
    }
}
