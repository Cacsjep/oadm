using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>Copying rules, recipients and schedules, changing and deleting recipients and schedules, and undo of all of it.</summary>
public sealed class CopyTaskTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.unlimited.notification.tcp";
    private const string FtpImages = "com.axis.action.fixed.send_images.ftp";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-copy-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _device = new();
    private readonly ActionRulesPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        var devices = new FakeDevices();
        devices.Items.Add(_device);
        await _plugin.StartAsync(new FakeContext(devices, new FakeCameras(), new RecordingRunner(), new RecordingEvents(), _data), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static EventFilter ScheduleFilter(string id) => new(
        RuleText.ScheduleIntervalTopic,
        $"boolean(//SimpleItem[@Name=\"id\" and @Value=\"{id}\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    private static DeviceRule NightRule(string scheduleId) => new(
        string.Empty, "Night notify", true, null, [ScheduleFilter(scheduleId)], null,
        new RuleAction(Tcp, [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0"), new("message", "night"), new("period", "5")]));

    private static readonly DeviceSchedule Night = new("com.axis.schedules.genid.id-7", "Night shift", "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");

    [Theory]
    [InlineData("os12")]
    [InlineData("os11")]
    [InlineData("os10")]
    public async Task A_copied_rule_brings_its_schedule_with_the_same_own_id_everywhere(string os)
    {
        var camera = new RuleCamera(os);

        var run = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null) { Rule = NightRule(Night.Id), Schedules = [Night] });

        var schedule = camera.Schedules["com.axis.schedules.oadm.night-shift"];
        Assert.Equal(Night.Definition, schedule.Definition);
        var rule = Assert.Single(camera.Rules).Value;
        Assert.Equal("com.axis.schedules.oadm.night-shift", RuleText.ScheduleId(Assert.Single(rule.Conditions)));
        Assert.Equal(Tcp, camera.Configs[rule.ConfigId].Template);

        var again = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null) { Rule = NightRule(Night.Id), Schedules = [Night] });
        Assert.Single(camera.Rules); // the same rule is there already
        Assert.Contains("Create rule Night notify: Skipped", StepRun.Lines(again.Steps));

        await UndoAsync(camera, run);
        Assert.Empty(camera.Rules);
        Assert.False(camera.Schedules.ContainsKey("com.axis.schedules.oadm.night-shift"));
    }

    [Fact]
    public async Task A_built_in_schedule_is_the_device_own_and_a_patch_changes_the_copy()
    {
        var camera = new RuleCamera("os10");

        await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], new RulePatch { Name = "Copied", Parameters = new Dictionary<string, string> { ["message"] = "copied" } })
        {
            Rule = NightRule("com.axis.schedules.office_hours"),
            Schedules = [],
        });

        var rule = Assert.Single(camera.Rules);
        Assert.Equal("Copied", rule.Value.Name);
        Assert.Equal("copied", camera.Param(rule.Key, "message"));
        Assert.Equal("com.axis.schedules.office_hours", RuleText.ScheduleId(Assert.Single(rule.Value.Conditions)));
        Assert.DoesNotContain(camera.Requests, r => r == "SOAP AddScheduledEvent");
    }

    [Fact]
    public async Task A_setting_the_copy_lacks_on_an_older_device_is_dropped_and_a_missing_one_fails()
    {
        var camera = new RuleCamera("os10");
        var overlay = new DeviceRule(string.Empty, "Overlay", true, PageTests.VirtualInput, [], null,
            new RuleAction("com.axis.action.fixed.set_overlay", [new("text", "Alarm"), new("channels", "1"), new("duration", "5"), new("index", "1")]));

        await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null) { Rule = overlay, Schedules = [] });
        Assert.Null(camera.Param(Assert.Single(camera.Rules).Key, "index")); // AXIS OS 10 has no index

        var newer = new RuleCamera("os12");
        var old = overlay with { Action = new RuleAction("com.axis.action.fixed.set_overlay", [new("text", "Alarm"), new("channels", "1"), new("duration", "5")]) };
        var ex = await Assert.ThrowsAsync<ActionEngineException>(() => StartAsync(newer, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null) { Rule = old, Schedules = [] }));
        Assert.Contains("\"index\"", ex.Message, StringComparison.Ordinal);
        Assert.Empty(newer.Rules);
    }

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task A_changed_recipient_takes_the_rules_that_send_to_it_along(string os)
    {
        var camera = new RuleCamera(os);
        var recipient = camera.Recipients.Values.FirstOrDefault() ?? AddFtpRecipient(camera);
        var ruleId = camera.Add("Upload", FtpImages,
            [("stream_options", "x"), ("pre_duration", "0"), ("post_duration", "0"), ("max_images", "1"), ("create_folder", string.Empty), ("filename", "a.jpg"), ("max_sequence_number", "0"),
             .. recipient.Parameters.Select(p => (p.Name, p.Name == "password" ? "rule-pw" : p.Value))],
            start: PageTests.VirtualInput);
        var other = camera.Add("Other server", FtpImages,
            [("stream_options", "x"), ("pre_duration", "0"), ("post_duration", "0"), ("max_images", "1"), ("create_folder", string.Empty), ("filename", "a.jpg"), ("max_sequence_number", "0"),
             .. recipient.Parameters.Select(p => (p.Name, p.Name == "host" ? "other.example" : p.Value))],
            start: PageTests.VirtualInput);

        var payload = new RuleTaskPayload(RuleTaskActions.RecipientEdit, Guid.NewGuid(), "Change recipients",
            [new DeviceTargets(_device.Id, [new RuleRef(recipient.Id, RecipientView.VersionOf(recipient))])], null)
        {
            RecipientPatch = new RecipientPatch(null, new Dictionary<string, string> { ["host"] = "new.example", ["port"] = "2121" }),
        };
        var run = await StartAsync(camera, payload);

        var changed = camera.Recipients.Values.Single(r => r.Name == recipient.Name);
        Assert.Equal(("new.example", "2121"), (changed.Parameters.Single(p => p.Name == "host").Value, changed.Parameters.Single(p => p.Name == "port").Value));
        var upload = camera.Rules.Single(r => r.Value.Name == "Upload").Key;
        Assert.Equal(("new.example", "2121", "rule-pw"), (camera.Param(upload, "host"), camera.Param(upload, "port"), camera.Param(upload, "password")));
        Assert.Equal("other.example", camera.Param(camera.Rules.Single(r => r.Value.Name == "Other server").Key, "host")); // sends elsewhere: unchanged
        Assert.Equal(os == "os12", camera.Rules.ContainsKey(ruleId)); // in place on AXIS OS 12
        Assert.True(camera.Rules.ContainsKey(other));

        await UndoAsync(camera, run);
        var back = camera.Recipients.Values.Single(r => r.Name == recipient.Name);
        Assert.Equal(recipient.Parameters.Single(p => p.Name == "host").Value, back.Parameters.Single(p => p.Name == "host").Value);
        Assert.Equal(recipient.Parameters.Single(p => p.Name == "host").Value, camera.Param(camera.Rules.Single(r => r.Value.Name == "Upload").Key, "host"));
    }

    [Fact]
    public async Task Recipients_are_copied_with_their_password_and_deleted()
    {
        var source = new DeviceRecipient("9", "Archive", "com.axis.recipient.ftp",
            [new("host", "archive.example"), new("upload_path", "x"), new("port", "21"), new("login", "u"), new("password", "pw"), new("passive", "1"), new("temporary", "0"), new("qos", "0")]);
        var camera = new RuleCamera("os10");

        await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.RecipientDeploy, Guid.NewGuid(), "Copy", [], null) { Recipient = source });
        var copy = camera.Recipients.Values.Single(r => r.Name == "Archive");
        Assert.Equal("pw", copy.Parameters.Single(p => p.Name == "password").Value);

        var delete = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.RecipientDelete, Guid.NewGuid(), "Delete", [new DeviceTargets(_device.Id, [new RuleRef(copy.Id, RecipientView.VersionOf(copy))])], null));
        Assert.DoesNotContain(camera.Recipients.Values, r => r.Name == "Archive");
        await UndoAsync(camera, delete);
        Assert.Equal("pw", camera.Recipients.Values.Single(r => r.Name == "Archive").Parameters.Single(p => p.Name == "password").Value);
    }

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task A_schedule_keeps_its_id_when_changed_and_goes_only_with_its_rules(string os)
    {
        var camera = new RuleCamera(os);
        camera.Schedules[Night.Id] = Night;
        camera.Add("Night notify", Tcp, [("host", "192.0.2.10"), ("port", "9999"), ("qos", "0"), ("message", "night"), ("period", "5")], conditions: [ScheduleFilter(Night.Id)]);
        DeviceRuleSet Read() => SoapActionEngine.ReadAsync(camera, CancellationToken.None).GetAwaiter().GetResult();
        RuleRef Ref(DeviceSchedule s) => new(s.Id, ScheduleView.VersionOf(s));

        var edit = new RuleTaskPayload(RuleTaskActions.ScheduleEdit, Guid.NewGuid(), "Change", [new DeviceTargets(_device.Id, [Ref(Night)])], null)
        {
            SchedulePatch = new SchedulePatch("Late shift", "DTSTART:19700101T230000\nDTEND:19700102T070000\nRRULE:FREQ=WEEKLY;BYDAY=MO"),
        };
        await StartAsync(camera, edit);
        Assert.Equal(("Late shift", "Mon 23:00 to 07:00 the next day"), (camera.Schedules[Night.Id].Name, ScheduleText.Describe(camera.Schedules[Night.Id].Definition)));
        Assert.Single(camera.Rules);

        var changed = camera.Schedules[Night.Id];
        var refused = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDelete, Guid.NewGuid(), "Delete", [new DeviceTargets(_device.Id, [Ref(changed)])], null));
        Assert.True(camera.Schedules.ContainsKey(Night.Id)); // a rule uses it
        Assert.Contains(refused.Warnings, w => w.Contains("Used by 1 rule.", StringComparison.Ordinal));

        var user = Read().Rules.Single();
        var stale = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDelete, Guid.NewGuid(), "Delete", [new DeviceTargets(_device.Id, [Ref(changed)])], null)
        {
            Rules = [new DeviceTargets(_device.Id, [new RuleRef(user.Id, "old version")])],
        });
        Assert.True(camera.Schedules.ContainsKey(Night.Id)); // the rule changed since it was read: nothing is deleted
        Assert.Contains(stale.Warnings, w => w.Contains("changed since it was read", StringComparison.Ordinal));

        var delete = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDelete, Guid.NewGuid(), "Delete", [new DeviceTargets(_device.Id, [Ref(changed)])], null)
        {
            Rules = [new DeviceTargets(_device.Id, [new RuleRef(user.Id, RuleView.VersionOf(user))])],
        });
        Assert.Empty(camera.Rules);
        Assert.False(camera.Schedules.ContainsKey(Night.Id));

        await UndoAsync(camera, delete); // the schedule comes back before the rule that needs it
        Assert.True(camera.Schedules.ContainsKey(Night.Id));
        Assert.Equal(Night.Id, RuleText.ScheduleId(Assert.Single(Assert.Single(camera.Rules).Value.Conditions)));

        var builtIn = Read().Schedules.Single(s => s.Id == "com.axis.schedules.office_hours");
        var kept = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDelete, Guid.NewGuid(), "Delete", [new DeviceTargets(_device.Id, [Ref(builtIn)])], null));
        Assert.True(camera.Schedules.ContainsKey("com.axis.schedules.office_hours"));
        Assert.Contains(kept.Warnings, w => w.Contains("Built-in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_schedule_is_added_once()
    {
        var camera = new RuleCamera("os12");
        var schedule = new DeviceSchedule(ScheduleText.OwnId("Night shift"), "Night shift", Night.Definition);

        await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDeploy, Guid.NewGuid(), "Add", [], null) { Schedules = [schedule] });
        var again = await StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDeploy, Guid.NewGuid(), "Add", [], null) { Schedules = [schedule] });

        Assert.Equal(Night.Definition, camera.Schedules["com.axis.schedules.oadm.night-shift"].Definition);
        Assert.Contains("Create schedule Night shift: Skipped", StepRun.Lines(again.Steps));
        var other = schedule with { Definition = "DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL=5" };
        await Assert.ThrowsAsync<ActionEngineException>(() => StartAsync(camera, new RuleTaskPayload(RuleTaskActions.ScheduleDeploy, Guid.NewGuid(), "Add", [], null) { Schedules = [other] }));
    }

    private static DeviceRecipient AddFtpRecipient(RuleCamera camera)
    {
        var r = new DeviceRecipient("50", "FTP", "com.axis.recipient.ftp",
            [new("host", "ftp.example"), new("upload_path", "x"), new("port", "21"), new("login", "u"), new("password", "pw"), new("passive", "1"), new("temporary", "0"), new("qos", "0")]);
        camera.Recipients[r.Id] = r;
        return r;
    }

    private readonly Dictionary<RecordingContext, Guid> _runs = [];

    /// <summary>Starts a run like the plugin does (saved for undo first) and runs its task on the camera.</summary>
    private async Task<RecordingContext> StartAsync(RuleCamera camera, RuleTaskPayload payload)
    {
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", payload.Action, 1, false), CancellationToken.None);
        var ctx = await ExecuteAsync(camera, payload);
        _runs[ctx] = payload.RunId;
        return ctx;
    }

    /// <summary>Undoes the run of <paramref name="run"/>; returns the undo's context.</summary>
    private Task<RecordingContext> UndoAsync(RuleCamera camera, RecordingContext run) =>
        ExecuteAsync(camera, new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo", [], null, _runs[run]));

    private async Task<RecordingContext> ExecuteAsync(RuleCamera camera, RuleTaskPayload payload)
    {
        var ctx = new RecordingContext(camera);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, ActionRulesJson.Serialize(payload), CancellationToken.None));
        return ctx;
    }
}
