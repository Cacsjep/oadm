using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Plugins.ActionRules.Client;
using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The page's New rule: the rule builder's form and what it sends.</summary>
public sealed class BuilderPageTests
{
    private const string VirtualInputTopic = "tns1:Device/tnsaxis:IO/VirtualInput";
    private const string TcpWhileActive = "com.axis.action.unlimited.notification.tcp";

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    /// <summary>Fills the form: virtual input 20 while active, TCP while active with every setting.</summary>
    private static void Fill(NewRuleViewModel form)
    {
        form.Name = "Door alarm";
        form.Event.SelectedChoice = form.Event.Choices.Single(c => (c.Value as OfferedEvent)?.Event.Topic == VirtualInputTopic);
        var port = Assert.Single(form.Event.Filters);
        port.Selected = port.Options.First(o => o.Value == "20");
        form.ModeIndex = 1;
        form.SelectedAction = form.Actions.Single(a => (a.Value as ActionOption)?.Token == TcpWhileActive);
        form.Parameters.Single(p => p.Name == "host").Value = "192.0.2.10";
        form.Parameters.Single(p => p.Name == "port").Value = "9999";
        form.Parameters.Single(p => p.Name == "message").Value = "Door";
    }

    [Fact]
    [Trait("Category", "Timing")] // the plugin's events arrive on the thread pool, waited for in real time
    public async Task A_new_rule_goes_to_the_chosen_devices_as_the_form_describes_it()
    {
        await using var rig = new PageRig();
        var a = rig.Add("10.0.0.1");
        var b = rig.Add("10.0.0.2", os: "os10");
        var broken = rig.Add("10.0.0.3", status: Sdk.Devices.DeviceStatus.CredentialsRequired);
        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => !vm.IsReading && vm.NewRuleUnavailable(a.Id) is null && vm.NewRuleUnavailable(b.Id) is null);
        Assert.True(vm.NewRuleCommand.CanExecute(null));
        rig.Dialogs.Devices.AddRange([a.Id, b.Id, broken.Id]);
        rig.Dialogs.NewRule = form =>
        {
            Assert.False(form.CanApply);
            Assert.DoesNotContain(form.Actions, c => (c.Value as ActionOption)?.Token == TcpWhileActive); // once: only "once" actions
            Assert.Contains(form.Actions, c => c.IsHeader && c.Text == "Notifications");
            Fill(form);
            Assert.Equal("Repeat every (s)", form.Parameters.Single(p => p.Name == "period").Label);
            Assert.Equal("1", form.Parameters.Single(p => p.Name == "period").Value); // the catalog's default
            Assert.DoesNotContain(form.Parameters, p => p.Name == "qos" && p.IsShown);
            Assert.True(form.CanApply, form.ApplyBlockedReason);
            return form.Rule();
        };

        await vm.NewRuleCommand.ExecuteAsync(null);

        Assert.Equal("Its rules were not read. Refresh first.", rig.Dialogs.Offered.Single(o => o.Device == broken.Id).Reason);
        Assert.Null(vm.ErrorText);
        Assert.Equal([a.Id, b.Id], rig.Runner.Runs[0].DeviceIds);
        var payload = ActionRulesJson.Deserialize<RuleTaskPayload>(rig.Runner.Runs[0].Payload);
        Assert.Equal((RuleTaskActions.Deploy, "Create action rule Door alarm"), (payload.Action, payload.Description));
        Assert.Null(payload.Rule!.StartEvent);
        Assert.Equal(RuleText.Key(PageTests.VirtualInput), RuleText.Key(Assert.Single(payload.Rule.Conditions)));
        Assert.Equal(("192.0.2.10", "1"), (payload.Patch!.Parameters!["host"], payload.Patch.Parameters["period"]));
    }

    [Fact]
    public void A_number_out_of_range_and_a_missing_value_block_create()
    {
        var set = TemplateSet.From(
            [new ActionTemplate(TcpWhileActive, "com.axis.recipient.tcp", ["message", "period"])],
            [new RecipientTemplate("com.axis.recipient.tcp", ["host", "port", "qos"])]);
        var device = Guid.NewGuid();
        var input = new OfferedEvent(new DeviceEvent(VirtualInputTopic, "Device › Input ports › Virtual input", true, "active",
            [new EventItem("port", "Port", [new EventValue("20", null)])], [new EventItem("active", null, [])]), 1);
        var form = new NewRuleViewModel([device], new Dictionary<Guid, TemplateSet> { [device] = set }, [input], new Dictionary<Guid, DeviceRulesRow>());
        Fill(form);

        form.Parameters.Single(p => p.Name == "period").Value = "0";
        Assert.Equal("Repeat every (s): Enter a number of 1 or more.", form.ApplyBlockedReason);
        form.Parameters.Single(p => p.Name == "period").Value = "5";
        form.Parameters.Single(p => p.Name == "host").Value = string.Empty;
        Assert.Equal("Host: Enter a value.", form.ApplyBlockedReason);
        form.Parameters.Single(p => p.Name == "host").Value = "192.0.2.10";
        form.AddConditionCommand.Execute(null);
        Assert.Equal("Choose the event of every condition, or remove it.", form.ApplyBlockedReason);
        form.ApplyCommand.Execute(null);
        Assert.Equal("Choose the event or remove the condition.", form.Conditions[0].Error); // below the condition's select
        form.Conditions[0].RemoveCommand!.Execute(null);
        Assert.True(form.CanApply, form.ApplyBlockedReason);
    }

    [Fact]
    public void The_edit_form_changes_the_event_the_schedule_and_the_recipient()
    {
        var set = TemplateSet.From(
            [new ActionTemplate("com.axis.action.fixed.notification.tcp", "com.axis.recipient.tcp", ["message"])],
            [new RecipientTemplate("com.axis.recipient.tcp", ["host", "port", "qos"])]);
        var device = Guid.NewGuid();
        var row = new DeviceRulesRow(device, "10.0.0.1", null, null, RowStates.Ok, null, true, [], set.Id, new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow)
        {
            Recipients = [new RecipientView("1", "VMS", "com.axis.recipient.tcp", [], [], "v")],
            Schedules = [new ScheduleView("com.axis.schedules.oadm.night", "Night shift", "x", false, 0, "v")],
        };
        var rule = new RuleView("1", "Door", true, PageTests.VirtualInput, [], null, "com.axis.action.fixed.notification.tcp",
            [new("host", "192.0.2.10"), new("port", "9"), new("qos", "0"), new("message", "m")], [], "v1");
        var input = new OfferedEvent(new DeviceEvent(VirtualInputTopic, "Device › Input ports › Virtual input", true, "active",
            [new EventItem("port", "Port", [new EventValue("20", null)])], [new EventItem("active", null, [])]), 1);
        var form = new EditRulesViewModel([new RuleMember(row, rule)], new Dictionary<Guid, TemplateSet> { [device] = set }, null, [input], new Dictionary<Guid, DeviceRulesRow> { [device] = row });

        form.ChangeEvent = true;
        Assert.Equal("Choose the event.", form.ApplyBlockedReason);
        form.Event.SelectedChoice = form.Event.Choices.Single(c => !c.IsHeader);
        form.ChangeSchedule = true;
        form.Schedule.Selected = form.Schedule.Choices.Single(c => c.Text == "Night shift");
        form.ChangeRecipient = true;
        Assert.True(form.HasRecipient);
        Assert.True(form.CanApply, form.ApplyBlockedReason);

        var patch = form.Patch();
        Assert.Equal(VirtualInputTopic, patch.NewEvent!.Topic);
        Assert.Equal(("com.axis.schedules.oadm.night", "Night shift", "VMS"), (patch.ScheduleId, patch.NewSchedule!.Name, patch.RecipientName));
        form.Schedule.Selected = form.Schedule.Choices[0];
        Assert.Equal(string.Empty, form.Patch().ScheduleId); // Always
    }

    [Fact]
    public void New_recipient_offers_the_kinds_every_device_has_with_their_defaults()
    {
        var a = TemplateSet.From([], [new RecipientTemplate("com.axis.recipient.ftp", ["host", "port", "qos"]), new RecipientTemplate("com.axis.recipient.tcp", ["host", "port", "qos"])]);
        var b = TemplateSet.From([], [new RecipientTemplate("com.axis.recipient.ftp", ["host", "port", "qos"])]);
        var (d1, d2) = (Guid.NewGuid(), Guid.NewGuid());
        var form = RecipientFormViewModel.ForNew([d1, d2], new Dictionary<Guid, TemplateSet> { [d1] = a, [d2] = b });

        Assert.Equal(["FTP"], form.Kinds.Select(k => k.Name));
        Assert.Equal(("New recipient", "Add"), (form.Title, form.ApplyText));
        Assert.Equal("21", form.Parameters.Single(p => p.Name == "port").Value);
        Assert.False(form.CanApply); // name and host first
        form.Name = "Archive";
        form.Parameters.Single(p => p.Name == "host").Value = "192.0.2.5";
        Assert.True(form.CanApply, form.ApplyBlockedReason);
        Assert.Equal(("Archive", "192.0.2.5", "0"), (form.Patch().Name, form.Patch().Parameters!["host"], form.Patch().Parameters!["qos"]));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void The_form_for_5000_devices_opens_and_changes_fast()
    {
        var set = TemplateSet.From(
            [new ActionTemplate(TcpWhileActive, "com.axis.recipient.tcp", ["message", "period"]), new ActionTemplate("com.axis.action.fixed.erase.configuration", null, [])],
            [new RecipientTemplate("com.axis.recipient.tcp", ["host", "port", "qos"])]);
        var devices = Enumerable.Range(0, 5_000).Select(_ => Guid.NewGuid()).ToList();
        var sets = devices.ToDictionary(d => d, _ => set);
        var rows = devices.ToDictionary(d => d, d => new DeviceRulesRow(d, "10.0.0.1", null, null, RowStates.Ok, null, true, [], set.Id,
            new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow)
        {
            Recipients = [new RecipientView("1", "VMS", "com.axis.recipient.tcp", [], [], "v")],
            Schedules = [new ScheduleView("com.axis.schedules.office_hours", "Office Hours", "x", true, 0, "v")],
        });
        var input = new OfferedEvent(new DeviceEvent(VirtualInputTopic, "Device › Input ports › Virtual input", true, "active",
            [new EventItem("port", "Port", [new EventValue("20", null)])], [new EventItem("active", null, [])]), 5_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var form = new NewRuleViewModel(devices, sets, [input], rows);
        Fill(form);
        form.Recipient.Selected = form.Recipient.Choices.Single(r => r.Name == "VMS");
        watch.Stop();

        Assert.DoesNotContain(form.Actions, a => (a.Value as ActionOption)?.Token.Contains("erase", StringComparison.Ordinal) == true);
        Assert.Equal("Each device uses its own recipient of this name.", form.Recipient.Hint);
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static void Capture(Avalonia.Controls.Window window, string name)
    {
        window.Show();
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();
    }

    [Fact]
    public async Task The_forms_render()
    {
        await HeadlessEntry.Session.Dispatch(() =>
        {
            var set = TemplateSet.From(
                [new ActionTemplate(TcpWhileActive, "com.axis.recipient.tcp", ["message", "period"]),
                 new ActionTemplate("com.axis.action.fixed.send_images.ftp", "com.axis.recipient.ftp", ["create_folder", "filename", "max_sequence_number", "stream_options", "pre_duration", "post_duration", "max_images"])],
                [new RecipientTemplate("com.axis.recipient.tcp", ["host", "port", "qos"]),
                 new RecipientTemplate("com.axis.recipient.ftp", ["host", "upload_path", "port", "login", "password", "passive", "temporary", "qos"])]);
            var device = Guid.NewGuid();
            var input = new OfferedEvent(new DeviceEvent(VirtualInputTopic, "Device › Input ports › Virtual input", true, "active",
                [new EventItem("port", "Port", [new EventValue("20", null)])], [new EventItem("active", null, [])]), 1);
            var form = new NewRuleViewModel([device], new Dictionary<Guid, TemplateSet> { [device] = set }, [input], new Dictionary<Guid, DeviceRulesRow>());
            form.Name = "Door alarm";
            form.Event.SelectedChoice = form.Event.Choices.Single(c => !c.IsHeader);
            form.SelectedAction = form.Actions.Single(c => (c.Value as ActionOption)?.Token == "com.axis.action.fixed.send_images.ftp");
            Capture(new NewRuleWindow { DataContext = form }, "action-rules-new-rule.png");

            var row = new DeviceRulesRow(device, "10.0.0.1", null, null, RowStates.Ok, null, true, [], set.Id, new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow)
            {
                Recipients = [new RecipientView("1", "VMS", "com.axis.recipient.tcp", [], [], "v")],
            };
            var rule = new RuleView("1", "Door", true, PageTests.VirtualInput, [], null, TcpWhileActive,
                [new("host", "192.0.2.10"), new("port", "9"), new("qos", "0"), new("message", "m"), new("period", "5")], [], "v1");
            var edit = new EditRulesViewModel([new RuleMember(row, rule)], new Dictionary<Guid, TemplateSet> { [device] = set }, null, [input], new Dictionary<Guid, DeviceRulesRow> { [device] = row })
            {
                ChangeEvent = true,
                ChangeSchedule = true,
            };
            edit.Event.SelectedChoice = edit.Event.Choices.Single(c => !c.IsHeader);
            Capture(new EditRulesWindow { DataContext = edit, Height = 1000 }, "action-rules-edit-form.png");

            var recipient = RecipientFormViewModel.ForNew([device], new Dictionary<Guid, TemplateSet> { [device] = set });
            recipient.SelectedKind = recipient.Kinds.Single(k => k.Token == "com.axis.recipient.ftp");
            Capture(new RecipientFormWindow { DataContext = recipient }, "action-rules-new-recipient.png");
            return true;
        }, CancellationToken.None);
    }
}
