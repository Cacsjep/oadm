using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.EventRules;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Events;

namespace Oadm.Client.Tests;

/// <summary>
/// Field checks in the rule dialog (docs/spec/event-rules.md "Field checks"): the check rows of a When / And row, their
/// operators by kind, errors below the inputs, the overview. Set OADM_SCREENSHOT_DIR for event-rules-checks.png,
/// event-rules-checks-errors.png and event-rules-checks-overview.png.
/// </summary>
public sealed class EventRuleChecksTests : IDisposable
{
    private readonly UiThread _ui = new();

    public void Dispose() => _ui.Dispose();

    private static async Task<(FakeOadmApi Api, RuleEditorViewModel Editor)> CreateAsync(RuleDetail? detail = null)
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var store = new DeviceStore();
        store.Reset(await api.ListDevicesAsync(CancellationToken.None));
        store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
        return (api, new RuleEditorViewModel(api, await api.GetEventCatalogAsync(CancellationToken.None), detail, store));
    }

    private static void Choose(ConditionViewModel condition, string typeId) =>
        condition.SelectedEventType = condition.EventTypes.Single(t => t.Id == typeId);

    private static CheckViewModel AddCheck(ConditionViewModel condition, string field, string op, string value = "")
    {
        condition.AddCheckCommand.Execute(null);
        var check = condition.Checks[^1];
        check.SelectedField = check.Fields.Single(f => f.Name == field);
        check.SelectedOperator = check.Operators.Single(o => o.Text == op || RuleChecks.Text(o.Op, check.SelectedField!.Kind) == op); // ">" or "> greater than"
        check.Value = value;
        return check;
    }

    [Fact]
    public Task A_check_row_offers_the_values_of_the_event_and_the_operators_of_their_kind() => _ui.RunAsync(async () =>
    {
        var (api, editor) = await CreateAsync();
        var trigger = editor.Conditions[0];
        Assert.False(trigger.ShowAddCheck); // no event yet
        Choose(trigger, "oadm.device.unreachable");
        Assert.False(trigger.ShowAddCheck); // nothing to check
        Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
        Assert.True(trigger.ShowAddCheck);
        Assert.Equal(["Sensor (text)", "Temperature (number)", "Heater on (yes or no)"], trigger.CheckFields.Select(f => f.Text));

        trigger.AddCheckCommand.Execute(null);
        var check = Assert.Single(trigger.Checks);
        Assert.Equal("Sensor", check.SelectedField!.DisplayName); // the first value, ready to fill in
        Assert.Equal(["equals", "does not equal", "contains", "starts with"], check.Operators.Select(o => o.Text));
        check.SelectedField = check.Fields.Single(f => f.Name == "temperature");
        Assert.Equal(["= equals", "≠ does not equal", "> greater than", "≥ greater than or equal", "< less than", "≤ less than or equal"], check.Operators.Select(o => o.Text));
        Assert.Equal("= equals", check.SelectedOperator!.Text); // equals stays equals
        check.SelectedField = check.Fields.Single(f => f.Name == "heater");
        Assert.Equal(["is yes", "is no"], check.Operators.Select(o => o.Text));
        Assert.False(check.NeedsValue); // no value box for yes / no
        Assert.Null(check.Problem);

        AddCheck(trigger, "temperature", ">", "40");
        var definition = editor.ToDefinition();
        Assert.Equal(
            [("heater", RuleCheckOperator.Yes, ""), ("temperature", RuleCheckOperator.Gt, "40")],
            definition.Conditions[0].Checks.Select(c => (c.Field, c.Op, c.Value)));
        Assert.Empty(definition.Conditions[0].Data);

        // The remove icon takes the row away.
        trigger.Checks[0].RemoveCommand.Execute(null);
        Assert.Equal("temperature", Assert.Single(trigger.Checks).SelectedField!.Name);
        api.Dispose();
    });

    [Fact]
    public Task At_most_five_checks_and_the_button_says_why() => _ui.RunAsync(async () =>
    {
        var (api, editor) = await CreateAsync();
        var trigger = editor.Conditions[0];
        Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
        for (var i = 0; i < RuleChecks.MaxPerCondition; i++)
        {
            Assert.True(trigger.AddCheckCommand.CanExecute(null));
            Assert.Null(trigger.AddCheckTip);
            AddCheck(trigger, "temperature", ">", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.False(trigger.AddCheckCommand.CanExecute(null));
        Assert.Equal("At most 5 checks.", trigger.AddCheckTip);
        trigger.AddCheckCommand.Execute(null);
        Assert.Equal(5, trigger.Checks.Count);
        trigger.Checks[0].RemoveCommand.Execute(null);
        Assert.True(trigger.AddCheckCommand.CanExecute(null));
        api.Dispose();
    });

    [Fact]
    public Task Errors_show_below_the_value_once_edited_and_block_the_step() => _ui.RunAsync(async () =>
    {
        var (api, editor) = await CreateAsync();
        editor.Name = "Too warm";
        editor.NextCommand.Execute(null);
        var trigger = editor.Conditions[0];
        Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
        trigger.AddCheckCommand.Execute(null);
        var check = trigger.Checks[0];
        check.SelectedField = check.Fields.Single(f => f.Name == "temperature");
        check.SelectedOperator = check.Operators.Single(o => o.Text == "> greater than");

        Assert.Null(check.ErrorOf(nameof(CheckViewModel.Value))); // untouched: no error yet
        Assert.Equal("Check 1: Enter a value.", editor.StepProblem);
        Assert.False(editor.NextCommand.CanExecute(null));

        check.Value = "warm";
        Assert.Equal("Enter a number, e.g. 21.5.", check.ErrorOf(nameof(CheckViewModel.Value)));
        Assert.Equal("Check 1: Enter a number, e.g. 21.5.", editor.StepProblem);
        Assert.Null(trigger.Error); // the check's error stays below the check, not below the row

        check.Value = "21.5";
        Assert.Null(check.ErrorOf(nameof(CheckViewModel.Value)));
        Assert.Null(editor.StepProblem);
        Assert.True(editor.NextCommand.CanExecute(null));

        // A second, new check shows its error when Next is tried.
        trigger.AddCheckCommand.Execute(null);
        var second = trigger.Checks[1];
        Assert.Null(second.ErrorOf(nameof(CheckViewModel.Value)));
        editor.GoTo(RuleEditorViewModel.ThenStep);
        Assert.True(editor.IsWhenStep);
        trigger.ShowAllErrors();
        Assert.Equal("Enter a value.", second.ErrorOf(nameof(CheckViewModel.Value)));
        second.Value = new string('x', 201);
        Assert.Equal("At most 200 characters.", second.ErrorOf(nameof(CheckViewModel.Value)));
        api.Dispose();
    });

    [Fact]
    public Task Changing_the_event_keeps_the_checks_whose_value_it_has() => _ui.RunAsync(async () =>
    {
        var (api, editor) = await CreateAsync();
        var trigger = editor.Conditions[0];
        Choose(trigger, FakeOadmApi.FakePersonTrigger);
        AddCheck(trigger, "active", "is yes");
        AddCheck(trigger, "objectId", ">", "3");

        Choose(trigger, "oadm.device-triggers.sample-gate"); // has "active", no object ID
        var kept = Assert.Single(trigger.Checks);
        Assert.Equal(("active", "is yes"), (kept.SelectedField!.Name, kept.SelectedOperator!.Text));
        Assert.Equal(["Active (yes or no)"], kept.Fields.Select(f => f.Text)); // the port is a value only

        Choose(trigger, "oadm.device.unreachable");
        Assert.Empty(trigger.Checks);
        Assert.False(trigger.ShowAddCheck);
        api.Dispose();
    });

    [Fact]
    public Task An_older_rule_with_equals_filters_opens_with_checks_and_saves_them_unchanged() => _ui.RunAsync(async () =>
    {
        var json = $$$"""
            {"id":"{{{Guid.NewGuid()}}}","name":"Old","enabled":true,
             "conditions":[{"kind":"event","eventTypeId":"{{{FakeOadmApi.FakeTemperatureTrigger}}}","devices":{"mode":"all"},"data":{"sensor":"Housing","heater":"true"}}],
             "actions":[]}
            """;
        var (api, editor) = await CreateAsync(Detail(json));
        var checks = editor.Conditions[0].Checks;
        Assert.Equal(
            [("sensor", "equals", "Housing"), ("heater", "is yes", "")], // "true" on a yes / no value: is yes
            checks.Select(c => (c.SelectedField!.Name, c.SelectedOperator!.Text, c.NeedsValue ? c.Value : "")));
        Assert.All(checks, c => Assert.True(c.IsUnchangedLegacy));

        // Left as they were: saved back as the older filters (the server checks them as before).
        var condition = editor.ToDefinition().Conditions[0];
        Assert.Empty(condition.Checks);
        Assert.Equal(new Dictionary<string, string> { ["sensor"] = "Housing", ["heater"] = "true" }, condition.Data);

        // A changed filter becomes a check; the other stays an older filter.
        checks[0].Value = "Lens";
        condition = editor.ToDefinition().Conditions[0];
        Assert.Equal(("sensor", RuleCheckOperator.Eq, "Lens", false), (condition.Checks.Single().Field, condition.Checks[0].Op, condition.Checks[0].Value, condition.Checks[0].IsLegacy));
        Assert.Equal(new Dictionary<string, string> { ["heater"] = "true" }, condition.Data);
        checks[0].Value = "Housing"; // back as it was: the older filter again
        Assert.Empty(editor.ToDefinition().Conditions[0].Checks);
        api.Dispose();
    });

    [Fact]
    public Task A_stored_check_the_event_no_longer_offers_stays_and_says_so() => _ui.RunAsync(async () =>
    {
        // "port" is a value only now and "humidity" is gone; a new check on either is refused by the server.
        var json = $$$"""
            {"id":"{{{Guid.NewGuid()}}}","name":"Stale","enabled":true,
             "conditions":[{"kind":"event","eventTypeId":"oadm.device-triggers.sample-gate","devices":{"mode":"all"},
               "checks":[{"field":"port","op":"eq","value":"1"},{"field":"humidity","op":"gt","value":"80"},{"field":"active","op":"gt","value":"1"}]}],
             "actions":[]}
            """;
        var (api, editor) = await CreateAsync(Detail(json));
        var checks = editor.Conditions[0].Checks;
        Assert.Equal(3, checks.Count); // nothing is dropped
        Assert.Equal(["port", "humidity", "active"], checks.Select(c => c.SelectedField!.Name));
        Assert.Equal(RuleChecks.NoSuchValueText, checks[0].ErrorOf(nameof(CheckViewModel.SelectedField))); // shown at once
        Assert.Equal("The event no longer has this value.", checks[1].ErrorOf(nameof(CheckViewModel.SelectedField)));
        Assert.Equal("humidity (text)", checks[1].SelectedField!.Text);
        Assert.Null(checks[2].SelectedOperator); // ">" no longer fits a yes / no value: not replaced by another one
        Assert.Equal("Choose how to compare.", checks[2].ErrorOf(nameof(CheckViewModel.SelectedOperator)));
        Assert.Equal("Check 1: The event no longer has this value.", editor.Conditions[0].Problem);
        Assert.False(editor.CanOpen(RuleEditorViewModel.OverviewStep)); // no Save until it is fixed

        // The user fixes or removes them.
        checks[2].SelectedOperator = checks[2].Operators.Single(o => o.Text == "is yes");
        Assert.Null(checks[2].Problem);
        checks[0].SelectedField = checks[0].Fields.Single(f => f.Name == "active");
        Assert.Null(checks[0].ErrorOf(nameof(CheckViewModel.SelectedField)));
        checks[1].RemoveCommand.Execute(null);
        Assert.Null(editor.Conditions[0].Problem);
        api.Dispose();
    });

    [Fact]
    public Task Stored_checks_beyond_five_stay_and_say_so() => _ui.RunAsync(async () =>
    {
        var six = string.Join(",", Enumerable.Range(0, 6).Select(i => $$"""{"field":"temperature","op":"gt","value":"{{i}}"}"""));
        var json = $$$"""
            {"id":"{{{Guid.NewGuid()}}}","name":"Many","enabled":true,
             "conditions":[{"kind":"event","eventTypeId":"{{{FakeOadmApi.FakeTemperatureTrigger}}}","devices":{"mode":"all"},"checks":[{{{six}}}]}],
             "actions":[]}
            """;
        var (api, editor) = await CreateAsync(Detail(json));
        var checks = editor.Conditions[0].Checks;
        Assert.Equal(6, checks.Count);
        Assert.All(checks.Take(5), c => Assert.Null(c.Problem));
        Assert.Equal("At most 5 checks.", checks[5].ErrorOf(nameof(CheckViewModel.SelectedField))); // below the Field, at once
        Assert.Equal("Check 6: At most 5 checks.", editor.Conditions[0].Problem);
        Assert.False(editor.Conditions[0].AddCheckCommand.CanExecute(null));

        checks[0].RemoveCommand.Execute(null);
        Assert.Equal(5, checks.Count);
        Assert.All(checks, c => Assert.Null(c.Problem));
        Assert.Null(editor.Conditions[0].Problem);
        api.Dispose();
    });

    [Fact]
    public Task Six_older_filters_left_as_they_were_need_no_change() => _ui.RunAsync(async () =>
    {
        // Older rules had no limit: six untouched filters save; a changed one counts them all.
        var data = string.Join(",", new[] { "a", "b", "c", "sensor", "temperature", "heater" }.Select(f => "\"" + f + "\":\"1\""));
        var json = $$$"""
            {"id":"{{{Guid.NewGuid()}}}","name":"Old","enabled":true,
             "conditions":[{"kind":"event","eventTypeId":"{{{FakeOadmApi.FakeTemperatureTrigger}}}","devices":{"mode":"all"},"data":{{{{data}}}}}],
             "actions":[]}
            """;
        var (api, editor) = await CreateAsync(Detail(json));
        var checks = editor.Conditions[0].Checks;
        Assert.Equal(6, checks.Count);
        // a, b, c are not values of the event: the server refuses them as before, so they say so.
        Assert.All(checks.Take(3), c => Assert.Equal(RuleChecks.NoSuchValueText, c.ErrorOf(nameof(CheckViewModel.SelectedField))));
        Assert.Null(checks[5].Problem); // six older filters: no limit while they stay as they were

        checks[3].Value = "Lens"; // a change: now a new check next to five older filters
        Assert.Equal(RuleChecks.TooManyText, checks[5].ErrorOf(nameof(CheckViewModel.SelectedField)));
        checks[3].Value = "1";
        Assert.Null(checks[5].Problem);
        api.Dispose();
    });

    [Fact]
    public Task A_check_without_a_value_never_throws() => _ui.RunAsync(async () =>
    {
        var (api, editor) = await CreateAsync();
        var trigger = editor.Conditions[0];
        Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
        var check = AddCheck(trigger, "temperature", ">");
        check.Value = null!; // a text box can hand back null
        Assert.Equal(string.Empty, check.ToCheck().Value);
        Assert.Equal("Temperature > ", check.Summary());
        check.ShowAllErrors();
        Assert.Equal("Enter a value.", check.ErrorOf(nameof(CheckViewModel.Value)));
        Assert.NotNull(editor.ToDefinition());
        api.Dispose();
    });

    private static RuleDetail Detail(string json) => new() { DefinitionJson = json, Summary = new RuleSummary { Name = "Rule" } };

    [Fact]
    public Task The_rule_step_and_the_event_tree_picker_render() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, editor) = await CreateAsync();
            var dialog = new RuleEditorWindow();
            dialog.Attach(editor);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            editor.Name = "Housing too warm";
            Pump();
            // The pulse diagrams of the limits sit in the info icons' tooltips, not on the page.
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<LimitDiagram>(), d => d.IsEffectivelyVisible);
            Capture(dialog, outDir, "event-rules-rule-step.png");
            InfoIcon Icon(string label) => dialog.GetVisualDescendants().OfType<FormField>().Single(f => f.Label == label).GetVisualDescendants().OfType<InfoIcon>().Single();
            LimitDiagram Open(string label, string shot)
            {
                var icon = Icon(label);
                ToolTip.SetIsOpen(icon, true);
                Pump();
                var tip = Assert.IsType<ToolTip>(ToolTip.GetTip(icon));
                Assert.Contains("wide", tip.Classes);
                var diagram = tip.GetVisualDescendants().OfType<LimitDiagram>().Single();
                Capture(dialog, outDir, shot);
                ToolTip.SetIsOpen(icon, false);
                Pump();
                return diagram;
            }

            var dead = Open("Dead time (s)", "event-rules-limit-dead-time.png");
            Assert.Equal((RuleLimit.DeadTime, "10"), (dead.Limit, dead.Value));
            var max = Open("Max per minute", "event-rules-limit-max-per-minute.png");
            Assert.Equal((RuleLimit.MaxPerMinute, "60"), (max.Limit, max.Value));
            editor.Cooldown = "0";
            editor.MaxRuns = "1000";
            Pump();
            Assert.Equal("0", Open("Dead time (s)", "event-rules-limit-no-dead-time.png").Value);
            Assert.Equal("1000", Open("Max per minute", "event-rules-limit-max-1000.png").Value);
            // The flyout of a click shows the drawing too (its own copy).
            Assert.Single(Assert.IsType<StackPanel>(((Flyout)Icon("Dead time (s)").Flyout!).Content).Children.OfType<ContentControl>());

            editor.NextCommand.Execute(null);
            Pump();
            var trigger = editor.Conditions[0];
            // The event picker: closed it names the folders of the choice; open, a tree with a search.
            var picker = dialog.GetVisualDescendants().OfType<EventPicker>().First(p => p.IsEffectivelyVisible);
            Assert.Contains("Choose an event", Texts(dialog));
            picker.SetOpen(true);
            Pump();
            Assert.True(picker.IsOpen);
            Assert.Equal(["Devices", "Selected devices", "Any device", "Device triggers", "Certificates", "Selected devices", "802.1X server", "EST server", "Sites"],
                trigger.Tree.Rows.OfType<EventTreeFolder>().Select(f => f.Text)); // the top folders start open
            Capture(dialog, outDir, "event-rules-event-picker.png");
            trigger.Tree.Toggle(trigger.Tree.Rows.OfType<EventTreeFolder>().First(f => f.Text == "Selected devices"));
            Pump();
            Capture(dialog, outDir, "event-rules-event-picker-open-folder.png");
            var search = picker.GetLogicalDescendants().OfType<SearchBox>().Single(); // in the popup: a logical, not a visual child
            search.Text = "unreach";
            Pump();
            Assert.Equal(["Device unreachable", "Unreachable"], trigger.Tree.Rows.OfType<EventTreeEvent>().Select(r => r.Text));
            Capture(dialog, outDir, "event-rules-event-picker-search.png");
            search.Text = "nothing like this";
            Pump();
            Assert.True(trigger.Tree.IsEmpty);
            Assert.Contains("No event matches the search.", Texts(dialog));
            search.Text = string.Empty;
            picker.SetOpen(false);
            Pump();

            // Choosing sets the event of the condition; the closed picker shows its folders and name.
            picker.Selected = trigger.EventTypes.Single(t => t.Id == FakeOadmApi.FakeTemperatureTrigger);
            Pump();
            Assert.Equal(FakeOadmApi.FakeTemperatureTrigger, trigger.SelectedEventType?.Id);
            Assert.Contains("Devices › Device triggers › ", Texts(dialog));
            Assert.Contains("Housing temperature", Texts(dialog));
            Capture(dialog, outDir, "event-rules-event-picker-chosen.png");
            trigger.SelectedEventType = null;
            Pump();

            // An "And" event and a schedule: each has its remove icon (the trigger has none).
            trigger.SelectedEventType = trigger.EventTypes.First(t => t.Id == FakeOadmApi.FakeTemperatureTrigger);
            editor.AddEventConditionCommand.Execute(null);
            editor.AddScheduleCommand.Execute(null);
            Pump();
            Assert.Equal(2, dialog.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && ToolTip.GetTip(b) as string == "Remove condition"));
            Capture(dialog, outDir, "event-rules-and-conditions.png");
            dialog.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

    [Fact]
    public Task The_checks_render_below_the_event_and_in_the_overview() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, editor) = await CreateAsync();
            var dialog = new RuleEditorWindow();
            dialog.Attach(editor);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            editor.Name = "Housing too warm";
            editor.NextCommand.Execute(null);
            var trigger = editor.Conditions[0];
            Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
            AddCheck(trigger, "temperature", ">", "40");
            AddCheck(trigger, "heater", "is no");
            AddCheck(trigger, "sensor", "starts with", "Housing");
            AddCheck(trigger, "temperature", "≤", "60");
            editor.AddEventConditionCommand.Execute(null);
            var and = editor.Conditions[1];
            Choose(and, "oadm.device-triggers.sample-gate");
            AddCheck(and, "active", "is yes");
            Pump();
            var texts = Texts(dialog);
            Assert.Contains("Add check", texts);
            Assert.Contains("Temperature (number)", texts);
            Assert.Contains("is no", texts);
            Assert.Contains("> greater than", texts); // a number operator: its symbol and words
            Assert.Equal(5, dialog.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && ToolTip.GetTip(b) as string == "Remove check"));
            Assert.Equal(3, dialog.GetVisualDescendants().OfType<TextBox>().Count(b => b.IsEffectivelyVisible && b.PlaceholderText == "Value")); // no box for yes / no

            // The longest operator fits its select without being cut.
            var longest = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Text == "≤ less than or equal");
            var free = new TextBlock { Text = longest.Text, FontFamily = longest.FontFamily, FontSize = longest.FontSize, FontWeight = longest.FontWeight };
            free.Measure(Size.Infinity);
            Assert.True(free.DesiredSize.Width <= longest.Bounds.Width + 0.5, $"\"{longest.Text}\" needs {free.DesiredSize.Width} px, has {longest.Bounds.Width} px.");
            Capture(dialog, outDir, "event-rules-checks.png");

            // The operator list shows the same words.
            var operators = longest.GetVisualAncestors().OfType<ComboBox>().First();
            operators.IsDropDownOpen = true;
            Pump();
            Capture(dialog, outDir, "event-rules-checks-operators.png");
            operators.IsDropDownOpen = false;
            Pump();

            // A text value has its own operators.
            var textOperators = dialog.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text == "starts with").GetVisualAncestors().OfType<ComboBox>().First();
            textOperators.IsDropDownOpen = true;
            Pump();
            Assert.Contains("does not equal", Texts(dialog));
            Capture(dialog, outDir, "event-rules-checks-operators-text.png");
            textOperators.IsDropDownOpen = false;
            Pump();

            trigger.Checks[0].Value = "hot";
            Pump();
            Assert.Contains("Enter a number, e.g. 21.5.", Texts(dialog));
            Assert.False(editor.NextCommand.CanExecute(null));
            Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Next").IsEffectivelyEnabled);
            Capture(dialog, outDir, "event-rules-checks-errors.png");

            trigger.Checks[0].Value = "40";
            editor.NextCommand.Execute(null);
            var action = editor.RuleActions[0];
            action.SelectedAction = action.Actions.Single(a => a.Id == "oadm.outputs.set");
            Pump();
            editor.NextCommand.Execute(null);
            Pump();
            Assert.True(editor.IsOverviewStep);
            Assert.Equal(
                "Device triggers: Housing temperature · All devices · Temperature > 40, Heater on is no, Sensor starts with Housing, Temperature ≤ 60",
                editor.Overview.Single(l => l.Label == "When").Text);
            Assert.EndsWith("Active is yes", editor.Overview.Single(l => l.Label == "And").Text, StringComparison.Ordinal);
            Capture(dialog, outDir, "event-rules-checks-overview.png");
            dialog.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

    [Fact]
    public Task Leaving_an_empty_value_shows_its_error_and_a_narrow_window_keeps_the_row_in_its_card() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, editor) = await CreateAsync();
            var dialog = new RuleEditorWindow();
            dialog.Attach(editor);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            editor.Name = "Housing too warm";
            editor.NextCommand.Execute(null);
            var trigger = editor.Conditions[0];
            Choose(trigger, FakeOadmApi.FakeTemperatureTrigger);
            var check = AddCheck(trigger, "temperature", ">");
            AddCheck(trigger, "heater", "is no");
            Pump();

            // Each input of a check is a label-less form field: leaving the empty value says so below it.
            var box = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.PlaceholderText == "Value");
            Assert.NotNull(box.FindAncestorOfType<FormField>());
            Assert.All(dialog.GetVisualDescendants().OfType<LeadingIcon>().Where(i => i.IsEffectivelyVisible), i => Assert.IsType<FormField>(i.Parent));
            box.Focus();
            Pump();
            Assert.Null(check.ErrorOf(nameof(CheckViewModel.Value)));
            dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.PlaceholderText == "Value of the event").Focus();
            Pump();
            Assert.Equal("Enter a value.", check.ErrorOf(nameof(CheckViewModel.Value)));
            Assert.Contains("Enter a value.", Texts(dialog));
            Capture(dialog, outDir, "event-rules-checks-left-empty.png");

            // Narrow windows: the event select and the check rows shrink and stay inside the When row (at 700 px the
            // former fixed widths ran out of it).
            var card = box.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("ruleItem"));
            foreach (var width in new[] { 900, 700 })
            {
                dialog.Width = width;
                Pump();
                var rows = card.GetVisualDescendants().OfType<Grid>().Where(g => g.IsEffectivelyVisible && g.Classes.Contains("checkRow")).ToList();
                Assert.Equal(3, rows.Count); // the event select and two checks
                foreach (var cell in rows.SelectMany(r => r.Children).Where(c => c.IsVisible))
                {
                    var right = cell.TranslatePoint(new Point(cell.Bounds.Width, 0), card);
                    Assert.NotNull(right);
                    Assert.True(right.Value.X <= card.Bounds.Width, $"{width} px: a cell of a check row ends at {right.Value.X}, the When row at {card.Bounds.Width}.");
                }

                Capture(dialog, outDir, $"event-rules-checks-{width}.png");
            }

            dialog.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

    private static List<string> Texts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

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
