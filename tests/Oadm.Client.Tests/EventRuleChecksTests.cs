using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.EventRules;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
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
        check.SelectedOperator = check.Operators.Single(o => o.Text == op);
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
        Assert.Equal(["=", "≠", ">", "≥", "<", "≤"], check.Operators.Select(o => o.Text));
        Assert.Equal("=", check.SelectedOperator!.Text); // equals stays equals
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
        check.SelectedOperator = check.Operators.Single(o => o.Text == ">");

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
        Assert.Equal(["Port (number)", "Active (yes or no)"], kept.Fields.Select(f => f.Text));

        Choose(trigger, "oadm.device.unreachable");
        Assert.Empty(trigger.Checks);
        Assert.False(trigger.ShowAddCheck);
        api.Dispose();
    });

    [Fact]
    public Task An_older_rule_with_equals_filters_opens_with_checks() => _ui.RunAsync(async () =>
    {
        var json = $$$"""
            {"id":"{{{Guid.NewGuid()}}}","name":"Old","enabled":true,
             "conditions":[{"kind":"event","eventTypeId":"{{{FakeOadmApi.FakeTemperatureTrigger}}}","devices":{"mode":"all"},"data":{"sensor":"Housing","heater":"true"}}],
             "actions":[]}
            """;
        var (api, editor) = await CreateAsync(new RuleDetail { DefinitionJson = json, Summary = new RuleSummary { Name = "Old" } });
        var checks = editor.Conditions[0].Checks;
        Assert.Equal(
            [("sensor", "equals", "Housing"), ("heater", "is yes", "")], // "true" on a yes / no value: is yes
            checks.Select(c => (c.SelectedField!.Name, c.SelectedOperator!.Text, c.NeedsValue ? c.Value : "")));
        Assert.Equal(
            [("sensor", RuleCheckOperator.Eq, "Housing"), ("heater", RuleCheckOperator.Yes, "")],
            editor.ToDefinition().Conditions[0].Checks.Select(c => (c.Field, c.Op, c.Value)));
        api.Dispose();
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
            editor.AddEventConditionCommand.Execute(null);
            var and = editor.Conditions[1];
            Choose(and, "oadm.device-triggers.sample-gate");
            AddCheck(and, "active", "is yes");
            Pump();
            var texts = Texts(dialog);
            Assert.Contains("Add check", texts);
            Assert.Contains("Temperature (number)", texts);
            Assert.Contains("is no", texts);
            Assert.Equal(4, dialog.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && ToolTip.GetTip(b) as string == "Remove check"));
            Assert.Equal(2, dialog.GetVisualDescendants().OfType<TextBox>().Count(b => b.IsEffectivelyVisible && b.PlaceholderText == "Value")); // no box for yes / no
            Capture(dialog, outDir, "event-rules-checks.png");

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
                "Device triggers: Housing temperature · All devices · Temperature > 40, Heater on is no, Sensor starts with Housing",
                editor.Overview.Single(l => l.Label == "When").Text);
            Assert.EndsWith("Active is yes", editor.Overview.Single(l => l.Label == "And").Text, StringComparison.Ordinal);
            Capture(dialog, outDir, "event-rules-checks-overview.png");
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
