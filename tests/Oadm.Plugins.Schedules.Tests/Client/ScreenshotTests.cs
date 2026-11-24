using Avalonia.Controls;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Schedules.Tests.Client;

/// <summary>
/// The page and every step of the wizard rendered offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs
/// (OADM_TEST_APPEARANCE=Light for the light theme).
/// </summary>
public sealed class ScreenshotTests
{
    private const string Warning = "A run takes about 24 h, longer than the 5 h between runs. A start during a run is skipped.";

    private static List<TextBlock> VisibleTexts(Control root, string text) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text == text)];

    private static int PrimaryButtons(Control root) =>
        root.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));

    [Fact]
    public async Task The_page_renders_the_schedules_the_runs_and_a_runs_failed_devices()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var page = new SchedulesViewModel(ctx, new FakeTimeProvider(Samples.Now));
            var view = new SchedulesView { DataContext = page };
            var window = Headless.Host(view);
            window.Show();
            await page.ReloadTask;
            Headless.Pump();

            Assert.Equal(0, PrimaryButtons(window)); // command buttons only
            Assert.Contains(window.GetVisualDescendants().OfType<ToolbarButton>(), b => b.Text == SchedulesTexts.Delete && b.Classes.Contains("danger"));
            Assert.Single(VisibleTexts(window, SchedulesTexts.ChooseSchedule));
            Headless.Capture(window, $"schedules-page-{suffix}.png");

            page.SelectedSchedule = page.Schedules[0];
            await page.RunsTask;
            page.SelectedRun = page.Runs[0];
            await page.FailuresTask;
            Headless.Pump();

            Assert.True(view.FindControl<DataGrid>("FailureGrid")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<ToolbarButton>("DeleteButton")!.IsEffectivelyEnabled);
            Assert.Empty(VisibleTexts(window, SchedulesTexts.ChooseSchedule));
            Assert.Single(VisibleTexts(window, SchedulesTexts.Runs)); // the shared pane header of the Tasks pane
            Assert.Contains("danger", view.FindControl<Button>("DeleteAllRunsButton")!.Classes);
            Assert.True(view.FindControl<Button>("DeleteRunsButton")!.IsEffectivelyEnabled); // the chosen run ended
            Assert.Equal(DataGridSelectionMode.Extended, view.FindControl<DataGrid>("RunGrid")!.SelectionMode);
            Headless.Capture(window, $"schedules-page-runs-{suffix}.png");
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_page_says_what_to_do_first()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Answers[SchedulesMethods.List] = _ => Samples.Serialize(new SchedulesReply([]));
            var page = new SchedulesViewModel(ctx, new FakeTimeProvider(Samples.Now));
            var window = Headless.Host(new SchedulesView { DataContext = page });
            window.Show();
            await page.ReloadTask;
            Headless.Pump();

            Assert.Single(VisibleTexts(window, SchedulesTexts.Empty));
            Headless.Capture(window, $"schedules-page-empty-{suffix}.png");
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_run_with_a_thousand_failed_devices_creates_rows_only_for_what_shows()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Answers[SchedulesMethods.RunFailures] = _ => Samples.Serialize(new RunFailuresReply(Samples.Failures(SchedulesInfo.MaxFailuresPerRun)));
            var page = new SchedulesViewModel(ctx, new FakeTimeProvider(Samples.Now));
            var view = new SchedulesView { DataContext = page };
            var window = Headless.Host(view);
            window.Show();
            await page.ReloadTask;
            page.SelectedSchedule = page.Schedules[0];
            await page.RunsTask;
            page.SelectedRun = page.Runs[0];
            await page.FailuresTask;
            Headless.Pump();

            Assert.Equal(SchedulesInfo.MaxFailuresPerRun, page.Failures.Count);
            var grid = view.FindControl<DataGrid>("FailureGrid")!;
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            Assert.InRange(rows, 1, 100); // virtualized
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Every_step_of_the_wizard_renders_with_its_errors_below_the_fields()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context(s => Samples.Check(s, s.When.Recurrence == Recurrence.Daily ? Warning : null));
            var editor = new ScheduleEditorViewModel(ctx, null, new FakeTimeProvider(Samples.Now));
            var window = new ScheduleEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.SettleAsync();
            Headless.Pump();

            // 1 Schedule: untouched, no error; Next says why it waits.
            Assert.Empty(VisibleTexts(window, SchedulesTexts.EnterName));
            Assert.False(window.FindControl<Button>("NextButton")!.IsEffectivelyEnabled);
            Assert.Equal(1, PrimaryButtons(window));
            Headless.Capture(window, $"schedules-wizard-1-schedule-{suffix}.png");
            editor.FieldLeft(nameof(ScheduleEditorViewModel.Name));
            Headless.Pump();
            Assert.Single(VisibleTexts(window, SchedulesTexts.EnterName));
            Headless.Capture(window, $"schedules-wizard-1-schedule-error-{suffix}.png");
            editor.Name = "Upgrade Site A";
            editor.Stages = true;
            await editor.SettleAsync();
            editor.NextCommand.Execute(null);

            // 2 Devices: the error below the button, then the devices the choice gives.
            editor.SetChoice(DeviceChoice.Devices([]));
            await editor.SettleAsync();
            Headless.Pump();
            Assert.Single(VisibleTexts(window, SchedulesTexts.ChooseDevicesFirst));
            Headless.Capture(window, $"schedules-wizard-2-devices-error-{suffix}.png");
            editor.SetChoice(DeviceChoice.Devices([Samples.Devices().Last().Id], ["Site A"]));
            await editor.SettleAsync();
            Headless.Pump();
            Assert.Empty(VisibleTexts(window, SchedulesTexts.ChooseDevicesFirst));
            Assert.Equal(9, window.FindControl<DataGrid>("DeviceGrid")!.ItemsSource!.Cast<object>().Count());
            Headless.Capture(window, $"schedules-wizard-2-devices-{suffix}.png");
            editor.NextCommand.Execute(null);

            // 3 When: daily with a window, the estimate warning below When; then a wrong time below Time.
            editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Daily);
            editor.WindowEnd = "05:00";
            await editor.SettleAsync();
            Headless.Pump();
            Assert.True(window.FindControl<TextBlock>("WhenWarning")!.IsEffectivelyVisible);
            Headless.Capture(window, $"schedules-wizard-3-when-warning-{suffix}.png");
            editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Weekly);
            editor.Days[2].IsChecked = true;
            editor.Time = "24:00";
            await editor.SettleAsync();
            Headless.Pump();
            Assert.False(window.FindControl<TextBlock>("WhenWarning")!.IsEffectivelyVisible);
            Assert.Single(VisibleTexts(window, SchedulesTexts.EnterTime));
            Headless.Capture(window, $"schedules-wizard-3-when-error-{suffix}.png");
            editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Once);
            editor.Date = null;
            Headless.Pump();
            Assert.Single(VisibleTexts(window, SchedulesTexts.ChooseDate)); // once, below the date
            Headless.Capture(window, $"schedules-wizard-3-when-once-{suffix}.png");
            editor.Date = new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero);
            Headless.Pump();
            Headless.Capture(window, $"schedules-wizard-3-when-once-date-{suffix}.png");
            editor.Repeat = ScheduleEditorViewModel.RepeatChoices.Single(c => c.Value == Recurrence.Weekly);
            editor.Time = "23:00";
            await editor.SettleAsync();
            editor.NextCommand.Execute(null);

            // 4 Then: empty, the node list, three actions with Configuration's error on its card and in the panel.
            Headless.Pump();
            Assert.True(editor.IsThenStep);
            Assert.Single(VisibleTexts(window, SchedulesTexts.AddFirstAction));
            Headless.Capture(window, $"schedules-wizard-4-then-empty-{suffix}.png");
            editor.AddActionCommand.Execute(null);
            Headless.Pump();
            Assert.True(window.FindControl<NodePicker>("NodePicker")!.IsEffectivelyVisible);
            Headless.Capture(window, $"schedules-wizard-4-then-picker-{suffix}.png");
            editor.Picker.Choose(editor.Picker.ShownItems.Single(i => i.Label == "Upgrade AXIS OS online"));
            editor.AddAfter(0, Samples.Kinds.Single(k => k.Kind == NodeKind.Restart));
            var config = editor.AddAfter(0, Samples.Kinds.Single(k => k.Kind == NodeKind.Configuration))!;
            await editor.SettleAsync();
            editor.SelectedNode = config;
            editor.FitView();
            Headless.Pump();
            Assert.Equal(3, window.FindControl<NodeChainCanvas>("Canvas")!.Cards.Count);
            Assert.True(window.FindControl<StackPanel>("NodeForm")!.IsEffectivelyVisible);
            Headless.Capture(window, $"schedules-wizard-4-then-{suffix}.png");
            editor.SelectedNode = editor.Nodes[0];
            Headless.Pump();
            Assert.Single(VisibleTexts(window, SchedulesTexts.MyAxisNote));
            Headless.Capture(window, $"schedules-wizard-4-then-online-{suffix}.png");
            editor.SelectedNode = config;
            editor.RemoveNodeCommand.Execute(null);
            await editor.SettleAsync();
            editor.NextCommand.Execute(null);

            // 5 Overview: the facts the steps before do not show, then Save.
            Headless.Pump();
            Assert.True(editor.IsOverviewStep);
            Assert.Equal(["Next run", "Then", "Stages", "Estimated"], editor.Overview.Select(l => l.Label));
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
            Assert.Equal(1, PrimaryButtons(window));
            Headless.Capture(window, $"schedules-wizard-5-overview-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
