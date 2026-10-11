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
using Oadm.Sdk.Client.Controls;
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
    public async Task Delete_all_empties_the_event_log_after_a_confirmation_and_events_show_as_information()
    {
        var (api, _, dialogs, page) = await CreateAsync();
        page.AppendLog((await api.ListEventLogAsync(0, null, CancellationToken.None)).Entries);
        Assert.NotEmpty(page.LogEntries);
        var info = page.LogEntries.First(e => e.KindText == "Event");
        Assert.True(info.IsInfo); // an event only says that something happened: the blue info icon, not the grey dash
        Assert.False(page.LogEntries.First(e => e.ResultText == "Done").IsInfo);

        await page.ClearLogCommand.ExecuteAsync(null);
        await dialogs.Received(1).ConfirmAsync("Delete all", Arg.Any<string>(), "Delete all");
        Assert.Empty(page.LogEntries);
        Assert.Empty((await api.ListEventLogAsync(0, null, CancellationToken.None)).Entries);

        var (opApi, _, _, operatorPage) = await CreateAsync(isAdmin: false);
        Assert.False(operatorPage.ClearLogCommand.CanExecute(null)); // administrators only
        api.Dispose();
        opApi.Dispose();
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
        Assert.Equal("The device of the event", action.Targets.ChosenText); // default: no drop-down, the picker decides
        action.Targets.SetChoice(DeviceChoice.Devices([]));
        Assert.Equal("Choose at least one device or tag.", action.Problem);
        editor.ShowPicker = picker =>
        {
            Assert.True(picker.HasChoices);
            Assert.True(picker.IsChosen);
            picker.SearchText = "10.0.0.30";
            picker.SelectAllCommand.Execute(null);
            return Task.FromResult(true);
        };
        await action.Targets.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.StartsWith("10.0.0.30", action.Targets.ChosenText, StringComparison.Ordinal);

        // Tags and devices together: the tag's devices show checked with the tag, the choice keeps both.
        var trigger2 = editor.Conditions[0].Selector;
        editor.ShowPicker = picker =>
        {
            picker.IsChosen = true;
            var tag = picker.Tags.First(t => t.Name == "Building A");
            tag.IsChecked = true;
            Assert.Contains(picker.Visible, d => d.CoveredBy == "Tag Building A" && d.Chosen && !d.CanToggle);
            picker.SearchText = "10.0.0.30";
            picker.SelectAllCommand.Execute(null);
            return Task.FromResult(true);
        };
        await trigger2.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.StartsWith("Tag Building A and ", trigger2.ChosenText, StringComparison.Ordinal);
        Assert.Equal(["Building A"], trigger2.ToSelector().Tags);
        Assert.Single(trigger2.ToSelector().DeviceIds);
        trigger2.SetChoice(DeviceChoice.AllDevices);
        Assert.Equal("All devices", trigger2.ChosenText);

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
    public async Task Five_thousand_devices_are_chosen_in_the_shared_picker_fast()
    {
        var (api, _, _, _) = await CreateAsync();
        var store = new DeviceStore();
        store.Reset(Enumerable.Range(0, 5_000).Select(i => new Device { Id = Guid.NewGuid().ToString(), Address = $"10.1.{i / 256}.{i % 256}", Model = "P3265-V", Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture) }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var editor = new RuleEditorViewModel(api, await api.GetEventCatalogAsync(CancellationToken.None), null, store);
        var targets = editor.RuleActions[0].Targets;
        DevicePickerViewModel? shown = null;
        editor.ShowPicker = picker =>
        {
            shown = picker;
            picker.IsChosen = true;
            picker.SearchText = "10.1.3.";
            picker.SelectAllCommand.Execute(null);
            return Task.FromResult(true);
        };
        await targets.ChooseDevicesCommand.ExecuteAsync(null);

        watch.Stop();
        Assert.Equal(256, shown!.SelectedIds.Count);
        Assert.Equal(256, targets.ChosenIds.Count);
        Assert.StartsWith("256 devices: 10.1.3.", targets.ChosenText, StringComparison.Ordinal);
        Assert.EndsWith("+253 more", targets.ChosenText, StringComparison.Ordinal);
        Assert.Equal(256, targets.ToSelector().DeviceIds.Count);
        Assert.True(watch.ElapsedMilliseconds < 3_000, $"{watch.ElapsedMilliseconds} ms");
        api.Dispose();
    }

    [Fact]
    public async Task Notification_actions_have_code_bodies_rows_placeholders_and_a_test()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, _, _, page) = await CreateAsync();
            var editor = (await page.CreateEditorAsync(null))!;
            var dialog = new RuleEditorWindow();
            dialog.Attach(editor);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            editor.Name = "Person in the yard: notify";
            editor.Conditions[0].SelectedEventType = editor.Conditions[0].EventTypes.Single(t => t.Id == FakeOadmApi.FakePersonTrigger);
            editor.NextCommand.Execute(null);
            editor.NextCommand.Execute(null);
            Assert.True(editor.IsThenStep);

            // The placeholders: the trigger's values first, then the rule and the device.
            Assert.Equal("Event: Person in the yard", editor.PlaceholderGroups[0].Title);
            Assert.Contains(editor.PlaceholderGroups[0].Items, i => i.Text == "{objectId}");
            Assert.Contains(editor.PlaceholderGroups.SelectMany(g => g.Items), i => i.Text == "{mac}");

            // Send HTTP request: JSON body colored, query and header rows, a test with sample values.
            var http = editor.RuleActions[0];
            http.SelectedAction = http.Actions.Single(a => a.Id == "oadm.notifications.http");
            Assert.False(http.ShowTargets);
            Assert.True(http.CanTest);
            http.Fields.Single(f => f.Key == "url").Text = "https://hooks.example.com/oadm";
            var body = http.Fields.Single(f => f.Key == "body");
            body.Text = "{\n  \"objectID\": \"{objectId}\",\n  \"camera\": \"{serial}\",\n  \"time\": \"{time}\"\n}";
            Pump();
            Assert.Equal(CodeLanguage.Json, body.Language);
            var headers = http.Fields.Single(f => f.Key == "headers");
            headers.AddRowCommand.Execute(null);
            headers.Rows[0].Name = "X-Site";
            headers.Rows[0].Value = "{tags}";
            var settings = http.Settings();
            Assert.Equal("X-Site", settings["headers"]![0]!["name"]!.GetValue<string>());
            await http.TestCommand.ExecuteAsync(null);
            Assert.True(http.TestOk);
            Pump();
            Capture(dialog, outDir, "event-rules-http-action.png");
            dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            Pump();
            Capture(dialog, outDir, "event-rules-http-action-2.png");

            // Send email: HTML body colored like XML; the rows of a header field are gone.
            http.SelectedAction = http.Actions.Single(a => a.Id == "oadm.notifications.email");
            var email = http;
            email.Fields.Single(f => f.Key == "host").Text = "smtp.example.com";
            email.Fields.Single(f => f.Key == "from").Text = "oadm@example.com";
            email.Fields.Single(f => f.Key == "to").Text = "security@example.com";
            email.Fields.Single(f => f.Key == "format").SelectChoice("html");
            email.Fields.Single(f => f.Key == "body").Text = "<p>Hello, camera <b>{serial}</b> saw object {objectId} at {time}.</p>\n<p><a href=\"{webUrl}\">Open the camera</a></p>";
            Pump();
            Assert.Equal(CodeLanguage.Xml, email.Fields.Single(f => f.Key == "body").Language);
            Assert.False(email.HasFieldErrors);
            dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToHome();
            Pump();
            Capture(dialog, outDir, "event-rules-email-action.png");
            dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            Pump();
            Capture(dialog, outDir, "event-rules-email-action-2.png");
            dialog.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Page_and_dialog_render()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, _, _, page) = await CreateAsync();
            var window = new Window { Width = 1500, Height = 900, Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(20), Child = new EventRulesView { DataContext = page } } };
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
            Assert.Contains("Event log", texts); // rules and the log on one page, like the Devices page and its tasks
            Capture(window, outDir, "event-rules-page.png");
            window.Close();

            var editor = await page.CreateEditorAsync(page.Rules.Single(r => r.Name == "Person in the yard: warning lamp").Id);
            var dialog = new RuleEditorWindow();
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
            editor!.NextCommand.Execute(null);
            Pump();
            Capture(dialog, outDir, "event-rules-editor-when.png");
            editor.NextCommand.Execute(null);
            Pump();
            Capture(dialog, outDir, "event-rules-editor-then.png");
            editor.NextCommand.Execute(null);
            Pump();
            Assert.True(editor.IsOverviewStep);
            var overview = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("When", overview);
            Assert.Contains("Then", overview);
            Capture(dialog, outDir, "event-rules-editor-overview.png");
            dialog.Close();

            var empty = await page.CreateEditorAsync(null);
            var errors = new RuleEditorWindow();
            errors.Attach(empty!);
            Oadm.Client.App.ApplyCrispText(errors);
            errors.Show();
            Assert.False(empty!.NextCommand.CanExecute(null)); // no name yet
            empty.Name = "Door";
            empty.NextCommand.Execute(null);
            Assert.True(empty.IsWhenStep);
            Assert.False(empty.NextCommand.CanExecute(null));
            Assert.Equal("Choose an event.", empty.StepProblem);
            empty.Conditions[0].ShowErrors = true;
            Pump();
            var errorTexts = errors.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Choose an event.", errorTexts);
            Capture(errors, outDir, "event-rules-editor-errors.png");

            // The shared device picker with every option: the device of the event, all devices, tags and devices.
            var devices = empty.DeviceList;
            var tagged = devices.First(d => d.Tags.Count > 0);
            var picker = new DevicePickerWindow(new DevicePickerViewModel(
                devices,
                DeviceChoice.Devices([devices.First(d => d.Tags.Count == 0).Id], [tagged.Tags[0]]),
                DevicePickerOptions.Full("The device of the event")));
            Oadm.Client.App.ApplyCrispText(picker);
            picker.Show();
            Pump();
            var pickerTexts = picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Tag " + tagged.Tags[0], pickerTexts);
            Capture(picker, outDir, "device-picker-choices.png");
            picker.Close();
            errors.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_refused_rule_change_shows_below_the_rules_only_and_rolls_the_check_box_back()
    {
        var (fake, store, dialogs, _) = await CreateAsync();
        var api = Substitute.For<IOadmApi>();
        api.SetRuleEnabledAsync(default!, default, default).ReturnsForAnyArgs<RuleSummary>(_ => throw new InvalidOperationException("Connection lost."));
        var page = new EventRulesViewModel(api, dialogs, store, new ImmediateUiDispatcher());
        page.ApplyRules(await fake.ListRulesAsync(CancellationToken.None));
        var certificates = page.Rules.Single(r => r.Name == "Certificates about to expire");

        certificates.Enabled = true; // not an RpcException: still handled, nothing unobserved
        await TestSupport.WaitUntilAsync(() => page.HasRulesError);
        Assert.False(certificates.Enabled);
        Assert.False(certificates.IsBusy);
        Assert.Equal("The rule could not be changed: Connection lost.", page.RulesErrorText);
        Assert.False(page.HasLogError);
        fake.Dispose();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task The_log_reads_and_watches_again_after_a_failure_and_after_the_page_comes_back()
    {
        var (fake, store, dialogs, _) = await CreateAsync();
        var api = Substitute.For<IOadmApi>();
        var server = new List<long> { 1 };
        var lists = 0;
        var watches = 0;
        api.WatchRulesAsync(default).ReturnsForAnyArgs(ci => Stream<RuleList>([], fail: false, ci.Arg<CancellationToken>()));
        api.ListEventLogAsync(default, default, default).ReturnsForAnyArgs(_ =>
        {
            lock (server)
            {
                if (++lists == 1)
                {
                    throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "Server is down."));
                }

                var page = new EventLogPage();
                page.Entries.AddRange(server.Select(Entry));
                return Task.FromResult(page);
            }
        });
        api.WatchEventLogAsync(default).ReturnsForAnyArgs(ci =>
        {
            lock (server)
            {
                var id = server.Count + 1;
                server.Add(id);
                var page = new EventLogPage();
                page.Entries.Add(Entry(id));
                return Stream([page], fail: ++watches == 1, ci.Arg<CancellationToken>()); // the first watch drops
            }
        });
        var vm = new EventRulesViewModel(api, dialogs, store, new ImmediateUiDispatcher());

        vm.Activate();
        await TestSupport.WaitUntilAsync(() => vm.HasLogError);
        Assert.Equal("The event log could not be read: Server is down.", vm.LogErrorText);
        Assert.False(vm.HasRulesError);
        await TestSupport.WaitUntilAsync(() => watches == 2 && vm.LogEntries.Count == 3, 10_000);
        Assert.False(vm.HasLogError);
        Assert.Equal([3L, 2, 1], vm.LogEntries.Select(e => e.Id));

        vm.Deactivate();
        vm.Activate(); // the page shown again: live again
        await TestSupport.WaitUntilAsync(() => watches == 3 && vm.LogEntries.Count == 4, 10_000);
        Assert.Equal(4, vm.LogEntries[0].Id);
        vm.Dispose();
        fake.Dispose();

        static EventLogEntry Entry(long id) => new() { Id = id, Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow), What = "Set output" };
    }

    [Fact]
    public async Task A_list_that_cannot_be_read_stays_the_fields_error()
    {
        var (fake, store, _, _) = await CreateAsync();
        var api = Substitute.For<IOadmApi>();
        api.ListActionChoicesAsync(default!, default!, default).ReturnsForAnyArgs<IReadOnlyList<ActionChoiceEntry>>(_ => throw new InvalidOperationException("Connection lost."));
        var editor = new RuleEditorViewModel(api, await fake.GetEventCatalogAsync(CancellationToken.None), null, store);
        editor.AddActionCommand.Execute(null);
        var action = editor.RuleActions[^1];
        action.SelectedAction = action.Actions.Single(a => a.Id == "oadm.vapix-commander.command");
        var command = action.Fields.Single(f => f.Key == "command");
        await TestSupport.WaitUntilAsync(() => command.LoadError is not null);

        editor.Name = "Overlay";
        editor.Revalidate(); // any later change re-checks every field
        Assert.Equal("The list could not be read: Connection lost.", command.Error);
        fake.Dispose();
    }

    [Fact]
    public async Task The_device_picker_of_a_rule_offers_the_defined_tags_and_keeps_chosen_ones()
    {
        var (api, store, _, _) = await CreateAsync();
        store.Tags.Reset([.. await api.ListTagsAsync(CancellationToken.None), new DeviceTag { Id = Guid.NewGuid().ToString(), Name = "Later", Color = TagColor.Teal, Defined = true }]);
        var editor = new RuleEditorViewModel(api, await api.GetEventCatalogAsync(CancellationToken.None), null, store);
        var targets = editor.RuleActions[0].Targets;
        targets.SetChoice(DeviceChoice.Devices([], ["Not defined yet"]));
        DevicePickerViewModel? shown = null;
        editor.ShowPicker = picker =>
        {
            shown = picker;
            return Task.FromResult(true);
        };
        await targets.ChooseDevicesCommand.ExecuteAsync(null);

        Assert.Contains(shown!.Tags, t => t.Name == "Later" && t.CountText == "0" && !t.IsChecked);
        Assert.Contains(shown.Tags, t => t.Name == "Not defined yet" && t.IsChecked);
        Assert.Equal(["Not defined yet"], targets.ToSelector().Tags);
        api.Dispose();
    }

    private static async IAsyncEnumerable<T> Stream<T>(IReadOnlyList<T> items, bool fail, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }

        if (fail)
        {
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "Stream dropped."));
        }

        await Task.Delay(Timeout.Infinite, ct);
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
