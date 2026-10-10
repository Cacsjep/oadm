using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.EventRules;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Events;

namespace Oadm.Client.Tests;

/// <summary>
/// The Event rules page and the rule dialog against the fake server. Set OADM_SCREENSHOT_DIR for event-rules-page.png,
/// event-rules-log.png, event-rules-editor.png and event-rules-editor-errors.png.
/// </summary>
public sealed class EventRulesTests
{
    private static async Task<(FakeOadmApi Api, DeviceStore Store, IDialogService Dialogs, EventRulesViewModel Page)> CreateAsync(bool isAdmin = true)
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var store = new DeviceStore();
        store.Reset(await api.ListDevicesAsync(CancellationToken.None));
        store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        var session = new Shell.UserSession();
        session.SignIn(new UserInfo { UserName = isAdmin ? "admin" : "op", Role = isAdmin ? UserRole.Admin : UserRole.Operator }, "fake");
        var page = new EventRulesViewModel(api, dialogs, store, new ImmediateUiDispatcher(), session);
        return (api, store, dialogs, page);
    }

    [Fact]
    public async Task The_page_lists_the_rules_with_state_and_filters_them()
    {
        var (api, _, _, page) = await CreateAsync();
        page.ApplyRules(await api.ListRulesAsync(CancellationToken.None));

        Assert.Equal(3, page.Rules.Count);
        var lamp = page.Rules.Single(r => r.Name == "Person in the yard: warning lamp");
        Assert.True(lamp.IsOn);
        Assert.Equal("On", lamp.StateText);
        Assert.Equal("Person in the yard (all devices) · Mon-Fri 08:00-18:00", lamp.ConditionsText);
        Assert.StartsWith("Output 1 follows the rule on 10.0.0.30", lamp.ActionsText, StringComparison.Ordinal);
        Assert.Equal("Turned off", page.Rules.Single(r => r.Name == "Certificates about to expire").StateText);
        Assert.Equal("3 rules · 2 turned on · 1 active now", page.RulesStatus);

        page.SearchText = "offline";
        Assert.Equal("Camera offline in Building A", Assert.Single(page.Rules).Name);
        Assert.EndsWith("1 shown", page.RulesStatus, StringComparison.Ordinal);
        api.Dispose();
    }

    [Fact]
    public async Task Turning_a_rule_on_copying_and_deleting_it_go_to_the_server()
    {
        var (api, _, _, page) = await CreateAsync();
        page.ApplyRules(await api.ListRulesAsync(CancellationToken.None));
        var certificates = page.Rules.Single(r => r.Name == "Certificates about to expire");

        certificates.Enabled = true;
        await TestSupport.WaitUntilAsync(() => certificates.Summary.Enabled);
        Assert.Equal("Off", certificates.StateText);

        page.SelectedRule = certificates;
        await page.DuplicateCommand.ExecuteAsync(null);
        Assert.Contains(page.Rules, r => r.Name == "Certificates about to expire (copy)" && !r.Enabled);

        page.SelectedRule = page.Rules.Single(r => r.Name.EndsWith("(copy)", StringComparison.Ordinal));
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(3, page.Rules.Count);
        Assert.Equal(3, (await api.ListRulesAsync(CancellationToken.None)).Count);
        api.Dispose();
    }

    [Fact]
    public async Task Operators_see_the_rules_but_cannot_change_them()
    {
        var (api, _, _, page) = await CreateAsync(isAdmin: false);
        page.ApplyRules(await api.ListRulesAsync(CancellationToken.None));
        page.SelectedRule = page.Rules[0];

        Assert.False(page.NewRuleCommand.CanExecute(null));
        Assert.False(page.EditCommand.CanExecute(null));
        Assert.False(page.DeleteCommand.CanExecute(null));
        Assert.All(page.Rules, r => Assert.False(r.CanToggle));
        Assert.Equal("Only administrators can change event rules.", page.ReadOnlyHint);
        api.Dispose();
    }

    [Fact]
    public async Task The_log_is_newest_first_live_and_searchable()
    {
        var (api, _, _, page) = await CreateAsync();
        page.AppendLog((await api.ListEventLogAsync(0, null, CancellationToken.None)).Entries);
        Assert.Equal(7, page.LogEntries.Count);
        Assert.True(page.LogEntries.Zip(page.LogEntries.Skip(1)).All(p => p.First.Id > p.Second.Id));

        page.LogSearchText = "Unreachable";
        Assert.All(page.LogEntries, e => Assert.True(e.Matches("Unreachable")));
        page.LogSearchText = string.Empty;

        page.AppendLog([new EventLogEntry { Id = 100, Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow), What = "Set output", Result = EventLogResult.EventLogOk, Message = "Output 1 active" }]);
        Assert.Equal(100, page.LogEntries[0].Id);
        Assert.Equal("Done", page.LogEntries[0].ResultText);
        api.Dispose();
    }

    [Fact]
    public async Task The_dialog_builds_a_rule_with_conditions_actions_and_settings()
    {
        var (api, store, _, page) = await CreateAsync();
        var editor = await page.CreateEditorAsync(null);
        Assert.NotNull(editor);
        Assert.Equal("New event rule", editor.Title);
        Assert.Equal("Enter a name.", editor.Problem);

        editor.Name = "Person lamp";
        var trigger = editor.Conditions[0];
        trigger.SelectedEventType = trigger.EventTypes.Single(t => t.Id == "oadm.device-triggers.sample-person");
        editor.AddScheduleCommand.Execute(null);
        var action = editor.RuleActions[0];
        action.SelectedAction = action.Actions.Single(a => a.Id == "oadm.outputs.set");
        action.Targets.SelectedMode = action.Targets.Modes.Single(m => m.Value == DeviceSelectorMode.Devices);
        Assert.Equal("Choose at least one device.", action.Problem);
        action.Targets.Devices.Visible.First(d => d.Text.StartsWith("10.0.0.30", StringComparison.Ordinal)).IsChecked = true;

        // Pulse shows "Seconds"; Follow needs a rule that turns on and off (this one does).
        var seconds = action.Fields.Single(f => f.Key == "seconds");
        Assert.True(seconds.IsVisible);
        action.Fields.Single(f => f.Key == "mode").SelectChoice("follow");
        Assert.False(seconds.IsVisible);
        var port = action.Fields.Single(f => f.Key == "port");
        port.Text = "99";
        Assert.Equal("Must be 1 to 64.", port.Error);
        port.Text = "2";
        Assert.Null(editor.Problem);

        // A second action: VAPIX command with its list from the server and the "when off" part.
        editor.AddActionCommand.Execute(null);
        var command = editor.RuleActions[1];
        command.SelectedAction = command.Actions.Single(a => a.Id == "oadm.vapix-commander.command");
        await TestSupport.WaitUntilAsync(() => command.Fields.Single(f => f.Key == "command").Choices.Count == 3);
        Assert.True(command.Fields.Single(f => f.Key == "offCommand").IsVisible);
        command.Fields.Single(f => f.Key == "command").SelectChoice("saved:overlay-on");

        var rule = editor.ToDefinition();
        Assert.Equal(2, rule.Conditions.Count);
        Assert.Equal(RuleConditionKind.Schedule, rule.Conditions[1].Kind);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], rule.Conditions[1].Schedule!.Days);
        Assert.Equal(2, rule.Actions[0].Settings["port"]!.GetValue<int>());
        Assert.Equal("follow", rule.Actions[0].Settings["mode"]!.GetValue<string>());
        Assert.Single(rule.Actions[0].Targets.DeviceIds);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(editor.Saved);
        Assert.Contains(await api.ListRulesAsync(CancellationToken.None), r => r.Name == "Person lamp");
        _ = store;
        api.Dispose();
    }

    [Fact]
    public async Task A_one_shot_trigger_hides_the_off_settings_and_refuses_follow()
    {
        var (api, _, _, page) = await CreateAsync();
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        catalog.EventTypes.Add(new EventTypeEntry { Id = "test.pressed", DisplayName = "Button pressed", Group = "Test", Stateful = false, DeviceScope = true, Available = true });
        var store = new DeviceStore();
        store.Reset(await api.ListDevicesAsync(CancellationToken.None));
        var editor = new RuleEditorViewModel(api, catalog, null, store);
        editor.Name = "Doorbell";
        editor.Conditions[0].SelectedEventType = editor.Conditions[0].EventTypes.Single(t => t.Id == "test.pressed");
        Assert.False(editor.IsStateful);
        Assert.Equal("The rule runs its actions each time the event happens while the other conditions hold.", editor.StateHint);

        editor.AddEventConditionCommand.Execute(null);
        Assert.DoesNotContain(editor.Conditions[1].EventTypes, t => !t.Stateful); // only the trigger may happen once

        var action = editor.RuleActions[0];
        action.SelectedAction = action.Actions.Single(a => a.Id == "oadm.vapix-commander.command");
        Assert.False(action.Fields.Single(f => f.Key == "offCommand").IsVisible);
        api.Dispose();
    }

    [Fact]
    public async Task An_existing_rule_opens_with_its_values_and_reports_a_server_error()
    {
        var (api, _, _, page) = await CreateAsync();
        page.ApplyRules(await api.ListRulesAsync(CancellationToken.None));
        var editor = await page.CreateEditorAsync(page.Rules.Single(r => r.Name == "Person in the yard: warning lamp").Id);
        Assert.NotNull(editor);
        Assert.Equal("Change event rule", editor.Title);
        Assert.Equal("oadm.device-triggers.sample-person", editor.Conditions[0].SelectedEventType!.Id);
        Assert.Equal(RuleConditionKind.Schedule, editor.Conditions[1].Kind);
        Assert.Equal(2, editor.RuleActions.Count);
        Assert.Equal("follow", editor.RuleActions[0].Fields.Single(f => f.Key == "mode").Value);
        Assert.Null(editor.Problem);

        api.SetOnline(false);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("fake server offline", editor.ServerError);
        api.SetOnline(true);
        api.Dispose();
    }

    [Fact]
    public async Task Five_thousand_devices_fill_the_pick_list_and_filter_fast()
    {
        var (api, _, _, _) = await CreateAsync();
        var store = new DeviceStore();
        store.Reset(Enumerable.Range(0, 5_000).Select(i => new Device { Id = Guid.NewGuid().ToString(), Address = $"10.1.{i / 256}.{i % 256}", Model = "P3265-V", Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture) }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var editor = new RuleEditorViewModel(api, await api.GetEventCatalogAsync(CancellationToken.None), null, store);
        var list = editor.RuleActions[0].Targets.Devices;
        list.SearchText = "10.1.3.";
        foreach (var item in list.Visible)
        {
            item.IsChecked = true;
        }

        watch.Stop();
        Assert.Equal(256, list.Visible.Count);
        Assert.Equal("256 devices chosen", list.SummaryText);
        Assert.True(watch.ElapsedMilliseconds < 3_000, $"{watch.ElapsedMilliseconds} ms");
        api.Dispose();
    }

    [Fact]
    public async Task Page_and_dialog_render()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, _, _, page) = await CreateAsync();
            var window = new Window { Width = 1500, Height = 640, Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(20), Child = new EventRulesView { DataContext = page } } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await TestSupport.WaitUntilAsync(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return page.Rules.Count == 3 && page.LogEntries.Count > 0;
            });
            page.SelectedRule = page.Rules[0];
            Pump();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Person in the yard: warning lamp", texts);
            Capture(window, outDir, "event-rules-page.png");

            page.ShowLogCommand.Execute(null);
            Pump();
            Capture(window, outDir, "event-rules-log.png");
            window.Close();

            var editor = await page.CreateEditorAsync(page.Rules.Single(r => r.Name == "Person in the yard: warning lamp").Id);
            var dialog = new RuleEditorWindow { Height = 1500 };
            dialog.Attach(editor!);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            await TestSupport.WaitUntilAsync(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return editor!.RuleActions[1].Fields.Single(f => f.Key == "command").Choices.Count == 3;
            });
            Pump();
            Capture(dialog, outDir, "event-rules-editor.png");
            dialog.Close();

            var empty = await page.CreateEditorAsync(null);
            var errors = new RuleEditorWindow { Height = 900 };
            errors.Attach(empty!);
            Oadm.Client.App.ApplyCrispText(errors);
            errors.Show();
            await empty!.SaveCommand.ExecuteAsync(null);
            Pump();
            var errorTexts = errors.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Choose an event.", errorTexts);
            Assert.Contains("Choose an action.", errorTexts);
            Capture(errors, outDir, "event-rules-editor-errors.png");
            errors.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
