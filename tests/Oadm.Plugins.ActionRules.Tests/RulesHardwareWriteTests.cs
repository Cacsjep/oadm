using System.Net;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using StepRun = Oadm.Sdk.Testing.StepRun;

using Xunit.Abstractions;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// Opt-in write test on every camera of dev-cameras.yaml (AXIS OS 10 and 12): creates two own rules ("OADM test ..."), turns
/// the "once" TCP notification into "while the rule is active", changes the FTP upload with its password, undoes both,
/// deletes both, and removes every "OADM test" rule at the end. Runs only with <c>OADM_ACTION_RULES_WRITE=1</c> and only
/// when the developer asks for it (CLAUDE.md "device safety" 6). Other rules of the cameras are never touched.
/// </summary>
[Trait("Category", "HardwareWrite")]
[Collection(DevCameraGroup.Name)]
public sealed class RulesHardwareWriteTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Prefix = "OADM test ";
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("OADM_ACTION_RULES_WRITE") == "1";

    private static readonly EventFilter VirtualInput = new(
        "tns1:Device/tnsaxis:IO/VirtualInput",
        "boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-hw-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _device = new();
    private readonly FakeCameras _cameras = new();
    private readonly RecordingRunner _runner = new();
    private readonly ActionRulesPlugin _plugin = new();

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (!Enabled || !DevCameras.Any)
            {
                Skip = "OADM_ACTION_RULES_WRITE is not 1 or no dev cameras are configured.";
            }
        }
    }

    public async Task InitializeAsync()
    {
        var devices = new FakeDevices();
        devices.Items.Add(_device);
        await _plugin.StartAsync(new FakeContext(devices, _cameras, _runner, new RecordingEvents(), _data), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [WriteFact]
    public async Task Change_undo_and_delete_on_every_dev_camera()
    {
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            try
            {
                await RunOnAsync(camera, vapix);
            }
            finally
            {
                await CleanUpAsync(vapix);
            }
        }
    }

    private async Task RunOnAsync(DevCamera camera, VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var inPlace = await RestActionRules.IsAvailableAsync(vapix, ct);
        var set = await SoapActionEngine.ReadAsync(vapix, ct);
        var writer = new RuleWriter(vapix, set, inPlace: false);
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var tcpId = (await writer.AddAsync(new DeviceRule(string.Empty, Prefix + "TCP " + stamp, false, VirtualInput, [], null, new RuleAction(
            "com.axis.action.fixed.notification.tcp",
            [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0"), new("message", "camera " + camera.Address + " #" + stamp)])), ct)).Id;
        var ftpParameters = set.Template("com.axis.action.fixed.send_images.ftp")!.Parameters.Concat(set.RecipientParameters("com.axis.action.fixed.send_images.ftp"))
            .Select(n => new NameValue(n, n switch
            {
                "host" => "192.0.2.11",
                "port" => "21",
                "login" => "oadm",
                "password" => "pw-" + stamp,
                "filename" => "image.jpg",
                "stream_options" => "resolution=640x360",
                "pre_duration" or "post_duration" => "1000",
                "max_images" => "1",
                _ => "0",
            })).ToList();
        var ftpId = (await writer.AddAsync(new DeviceRule(string.Empty, Prefix + "FTP " + stamp, false, VirtualInput, [], null, new RuleAction("com.axis.action.fixed.send_images.ftp", ftpParameters)), ct)).Id;
        var before = await OwnRulesAsync(vapix);
        output.WriteLine($"{camera}: rules {tcpId}, {ftpId}, {(inPlace ? "in place" : "recreated")}");

        var edit = await RunAsync(vapix, RuleTaskActions.Edit, new RulePatch
        {
            Template = "com.axis.action.unlimited.notification.tcp",
            TriggerKind = TriggerKinds.Condition,
            Parameters = new Dictionary<string, string> { ["period"] = "5" },
        }, tcpId);
        var ftpEdit = await RunAsync(vapix, RuleTaskActions.Edit, new RulePatch
        {
            Template = "com.axis.action.unlimited.send_images.ftp",
            TriggerKind = TriggerKinds.Condition,
            Enabled = false,
        }, ftpId);

        var changed = await OwnRulesAsync(vapix);
        var tcp = changed.Single(r => r.Name.StartsWith(Prefix + "TCP", StringComparison.Ordinal));
        var ftp = changed.Single(r => r.Name.StartsWith(Prefix + "FTP", StringComparison.Ordinal));
        Assert.Equal("com.axis.action.unlimited.notification.tcp", tcp.Action.Template);
        Assert.Null(tcp.StartEvent);
        Assert.Equal(RuleText.Key(VirtualInput), RuleText.Key(Assert.Single(tcp.Conditions)));
        Assert.Equal("camera " + camera.Address + " #" + stamp, tcp.Action.Get("message"));
        Assert.Equal("5", tcp.Action.Get("period"));
        Assert.Equal("com.axis.action.unlimited.send_images.ftp", ftp.Action.Template);
        Assert.Equal("pw-" + stamp, ftp.Action.Get("password"));
        Assert.Equal(inPlace, tcp.Id == tcpId);
        Assert.Equal(inPlace, ftp.Id == ftpId);

        await RunUndoAsync(vapix, edit);
        await RunUndoAsync(vapix, ftpEdit);
        Assert.Equal(Versions(before), Versions(await OwnRulesAsync(vapix)));

        var now = await OwnRulesAsync(vapix);
        var delete = await RunAsync(vapix, RuleTaskActions.Delete, null, [.. now.Select(r => r.Id)]);
        Assert.Empty(await OwnRulesAsync(vapix));
        await RunUndoAsync(vapix, delete);
        Assert.Equal(Versions(before), Versions(await OwnRulesAsync(vapix)));
        Assert.Equal("pw-" + stamp, (await OwnRulesAsync(vapix)).Single(r => r.Name.Contains("FTP", StringComparison.Ordinal)).Action.Get("password"));
        output.WriteLine($"{camera}: changed, undone, deleted and put back");
    }

    [WriteFact]
    public async Task Copy_recipients_and_schedules_on_every_dev_camera()
    {
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            try
            {
                await CopyOnAsync(camera, vapix);
            }
            finally
            {
                await CleanUpAsync(vapix);
            }
        }
    }

    private async Task CopyOnAsync(DevCamera camera, VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var scheduleName = "OADM test night " + stamp;
        var schedule = new DeviceSchedule("com.axis.schedules.genid.id-99", scheduleName, "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU");
        var filter = new EventFilter(RuleText.ScheduleIntervalTopic,
            $"boolean(//SimpleItem[@Name=\"id\" and @Value=\"{schedule.Id}\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");
        var rule = new DeviceRule(string.Empty, Prefix + "night " + stamp, false, null, [filter], null, new RuleAction(
            "com.axis.action.unlimited.notification.tcp",
            [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0"), new("message", "night"), new("period", "5")]));

        // Copy the rule: its schedule comes along with OADM's own id.
        var copy = await RunPayloadAsync(vapix, new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null) { Rule = rule, Schedules = [schedule] });
        var set = await SoapActionEngine.ReadAsync(vapix, ct);
        var own = set.Schedules.Single(s => s.Name == scheduleName);
        Assert.Equal(ScheduleText.OwnId(scheduleName), own.Id);
        var copied = set.Rules.Single(r => r.Name == rule.Name);
        Assert.Equal(own.Id, RuleText.ScheduleId(Assert.Single(copied.Conditions)));

        // Change the schedule: the id stays, the rule keeps working.
        var edit = await RunPayloadAsync(vapix, new RuleTaskPayload(RuleTaskActions.ScheduleEdit, Guid.NewGuid(), "Change",
            [new DeviceTargets(_device.Id, [new RuleRef(own.Id, ScheduleView.VersionOf(own))])], null)
        {
            SchedulePatch = new SchedulePatch(null, "DTSTART:19700101T230000\nDTEND:19700102T050000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE"),
        });
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.Equal("Mon to Wed 23:00 to 05:00 the next day", ScheduleText.Describe(set.Schedules.Single(s => s.Id == own.Id).Definition));
        Assert.Contains(set.Rules, r => r.Name == rule.Name);
        await RunUndoAsync(vapix, edit);
        Assert.Equal(ScheduleText.Normalize(schedule.Definition), ScheduleText.Normalize((await SoapActionEngine.ReadAsync(vapix, ct)).Schedules.Single(s => s.Id == own.Id).Definition));

        // A recipient and a rule that sends to it: the change takes the rule along, the password stays.
        var recipient = new DeviceRecipient(string.Empty, Prefix + "ftp " + stamp, "com.axis.recipient.ftp",
            [new("host", "192.0.2.50"), new("upload_path", "x"), new("port", "21"), new("login", "u"), new("password", "pw-" + stamp), new("passive", "1"), new("temporary", "0"), new("qos", "0")]);
        var recipientCopy = await RunPayloadAsync(vapix, new RuleTaskPayload(RuleTaskActions.RecipientDeploy, Guid.NewGuid(), "Copy", [], null) { Recipient = recipient });
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        var made = set.Recipients.Single(r => r.Name == recipient.Name);
        Assert.Equal("pw-" + stamp, made.Parameters.Single(p => p.Name == "password").Value);
        var ftpNames = set.Template("com.axis.action.fixed.send_images.ftp")!.Parameters;
        var upload = new DeviceRule(string.Empty, Prefix + "upload " + stamp, false, VirtualInput, [], null, new RuleAction("com.axis.action.fixed.send_images.ftp",
            [.. ftpNames.Select(n => new NameValue(n, n switch { "filename" => "a.jpg", "stream_options" => "resolution=640x360", "max_images" => "1", "pre_duration" or "post_duration" => "1000", _ => "0" })),
             .. made.Parameters]));
        await new RuleWriter(vapix, set, inPlace: false).AddAsync(upload, ct);
        var change = await RunPayloadAsync(vapix, new RuleTaskPayload(RuleTaskActions.RecipientEdit, Guid.NewGuid(), "Change",
            [new DeviceTargets(_device.Id, [new RuleRef(made.Id, RecipientView.VersionOf(made))])], null)
        {
            RecipientPatch = new RecipientPatch(null, new Dictionary<string, string> { ["host"] = "192.0.2.51" }),
        });
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.Equal("192.0.2.51", set.Recipients.Single(r => r.Name == recipient.Name).Parameters.Single(p => p.Name == "host").Value);
        var sent = set.Rules.Single(r => r.Name == upload.Name);
        Assert.Equal(("192.0.2.51", "pw-" + stamp), (sent.Action.Get("host"), sent.Action.Get("password")));
        await RunUndoAsync(vapix, change);
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.Equal("192.0.2.50", set.Rules.Single(r => r.Name == upload.Name).Action.Get("host"));
        Assert.Equal("pw-" + stamp, set.Recipients.Single(r => r.Name == recipient.Name).Parameters.Single(p => p.Name == "password").Value);
        await RunUndoAsync(vapix, recipientCopy);
        Assert.DoesNotContain((await SoapActionEngine.ReadAsync(vapix, ct)).Recipients, r => r.Name == recipient.Name);

        // Delete the used schedule together with its rule, then undo: the schedule comes back first.
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        own = set.Schedules.Single(s => s.Id == own.Id);
        var user = set.Rules.Single(r => r.Name == rule.Name);
        var delete = await RunPayloadAsync(vapix, new RuleTaskPayload(RuleTaskActions.ScheduleDelete, Guid.NewGuid(), "Delete",
            [new DeviceTargets(_device.Id, [new RuleRef(own.Id, ScheduleView.VersionOf(own))])], null)
        {
            Rules = [new DeviceTargets(_device.Id, [new RuleRef(user.Id, RuleView.VersionOf(user))])],
        });
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.DoesNotContain(set.Schedules, s => s.Id == own.Id);
        Assert.DoesNotContain(set.Rules, r => r.Name == rule.Name);
        await RunUndoAsync(vapix, delete);
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.Contains(set.Schedules, s => s.Id == own.Id);
        Assert.Contains(set.Rules, r => r.Name == rule.Name);

        // Undo the copy: rule and schedule are gone.
        await RunUndoAsync(vapix, copy);
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.DoesNotContain(set.Rules, r => r.Name == rule.Name);
        Assert.DoesNotContain(set.Schedules, s => s.Id == own.Id);
        output.WriteLine($"{camera}: copy, schedules and recipients done and undone");
    }

    [WriteFact]
    public async Task A_new_rule_is_built_on_every_dev_camera()
    {
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            _cameras.Fixed = vapix;
            try
            {
                await BuildOnAsync(camera, vapix);
            }
            finally
            {
                _cameras.Fixed = null;
                await CleanUpAsync(vapix);
            }
        }
    }

    /// <summary>
    /// The rule builder end to end: the camera's event list as the page read it, a rule "while virtual input 20 is active
    /// during a new schedule" with TCP while active, created by the task, checked on the camera, then undone.
    /// </summary>
    private async Task BuildOnAsync(DevCamera camera, VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var row = await _plugin.ReadAsync(_device, ct);
        Assert.Equal(RowStates.Ok, row.State);
        var events = ActionRulesJson.Deserialize<EventsReply>(await _plugin.InvokeAsync(ActionRulesMethods.Events, ActionRulesJson.Serialize(new EventsRequest([_device.Id])), ct));
        var input = events.Events.Single(e => e.Event.Topic == VirtualInput.Topic).Event;
        Assert.True(input.Stateful);
        output.WriteLine($"{camera}: {events.Events.Count} events, virtual input ports {input.Sources.Single(s => s.Name == "port").Values.Count}");

        var stamp = Guid.NewGuid().ToString("N")[..6];
        var scheduleName = Prefix + "built night " + stamp;
        var rule = new NewRule(Prefix + "built " + stamp, false, new NewFilter(input.Topic, [new("port", "20")]), true, [], ScheduleText.OwnId(scheduleName), 0,
            "com.axis.action.unlimited.notification.tcp",
            new Dictionary<string, string> { ["host"] = "192.0.2.10", ["port"] = "9999", ["qos"] = "0", ["message"] = "built " + stamp, ["period"] = "5" })
        {
            NewSchedule = new SchedulePatch(scheduleName, "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU"),
        };
        var started = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Build, ActionRulesJson.Serialize(new BuildRequest([_device.Id], rule)), ct));
        var payload = ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload);
        var ctx = await ExecuteAsync(vapix, payload);
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": Done$", l));

        var set = await SoapActionEngine.ReadAsync(vapix, ct);
        var created = set.Rules.Single(r => r.Name == rule.Name);
        Assert.Null(created.StartEvent);
        Assert.Equal(2, created.Conditions.Count);
        Assert.Equal(RuleText.Key(VirtualInput), RuleText.Key(created.Conditions[0]));
        Assert.Equal(ScheduleText.OwnId(scheduleName), RuleText.ScheduleId(created.Conditions[1]));
        Assert.Equal(("com.axis.action.unlimited.notification.tcp", "built " + stamp), (created.Action.Template, created.Action.Get("message")));
        Assert.Contains(set.Schedules, s => s.Id == ScheduleText.OwnId(scheduleName));

        await RunUndoAsync(vapix, started.RunId);
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        Assert.DoesNotContain(set.Rules, r => r.Name == rule.Name);
        Assert.DoesNotContain(set.Schedules, s => s.Id == ScheduleText.OwnId(scheduleName));
        output.WriteLine($"{camera}: built and undone");
    }

    [WriteFact]
    public async Task Edit_event_schedule_and_recipient_on_every_dev_camera()
    {
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            _cameras.Fixed = vapix;
            try
            {
                await EditPartsOnAsync(camera, vapix);
            }
            finally
            {
                _cameras.Fixed = null;
                await CleanUpAsync(vapix);
            }
        }
    }

    /// <summary>
    /// New recipient through the page method, then one edit through the page method that changes the event (virtual input
    /// 21), adds a new schedule and takes that recipient by name; checked on the camera and undone (recipient too).
    /// </summary>
    private async Task EditPartsOnAsync(DevCamera camera, VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var recipientName = Prefix + "vms " + stamp;
        var added = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.RecipientNew, ActionRulesJson.Serialize(new NewRecipientRequest(
            [_device.Id], "com.axis.recipient.tcp", recipientName, new Dictionary<string, string> { ["host"] = "192.0.2.77", ["port"] = "8888", ["qos"] = "0" })), ct));
        var ctx = await ExecuteAsync(vapix, ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload));
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": Done$", l));

        var set = await SoapActionEngine.ReadAsync(vapix, ct);
        var ruleId = (await new RuleWriter(vapix, set, inPlace: false).AddAsync(new DeviceRule(string.Empty, Prefix + "edit " + stamp, false, VirtualInput, [], null, new RuleAction(
            "com.axis.action.fixed.notification.tcp", [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0"), new("message", "edit " + stamp)])), ct)).Id;
        await _plugin.ReadAsync(_device, ct);
        var rule = (await SoapActionEngine.ReadRulesAsync(vapix, ct)).Single(r => r.Id == ruleId);
        var scheduleName = Prefix + "edit night " + stamp;
        var patch = new RulePatch
        {
            NewEvent = new NewFilter(VirtualInput.Topic, [new("port", "21")]),
            ScheduleId = ScheduleText.OwnId(scheduleName),
            NewSchedule = new SchedulePatch(scheduleName, "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU"),
            RecipientName = recipientName,
        };
        var edit = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Edit, ActionRulesJson.Serialize(
            new RunRequest([new DeviceTargets(_device.Id, [new RuleRef(rule.Id, RuleView.VersionOf(rule))])], patch)), ct));
        ctx = await ExecuteAsync(vapix, ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload));
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": Done$", l));

        set = await SoapActionEngine.ReadAsync(vapix, ct);
        var changed = set.Rules.Single(r => r.Name == rule.Name);
        Assert.Equal("21", RuleText.Terms(changed.StartEvent!.Message).Single(t => t.Name == "port").Value);
        Assert.Equal(ScheduleText.OwnId(scheduleName), RuleText.ScheduleId(Assert.Single(changed.Conditions)));
        Assert.Equal(("192.0.2.77", "8888", "edit " + stamp), (changed.Action.Get("host"), changed.Action.Get("port"), changed.Action.Get("message")));
        output.WriteLine($"{camera}: event, schedule and recipient changed");

        await RunUndoAsync(vapix, edit.RunId);
        await RunUndoAsync(vapix, added.RunId);
        set = await SoapActionEngine.ReadAsync(vapix, ct);
        var back = set.Rules.Single(r => r.Name == rule.Name);
        Assert.Equal(RuleText.Key(VirtualInput), RuleText.Key(back.StartEvent!));
        Assert.Empty(back.Conditions);
        Assert.Equal("192.0.2.10", back.Action.Get("host"));
        Assert.DoesNotContain(set.Schedules, s => s.Name == scheduleName);
        Assert.DoesNotContain(set.Recipients, r => r.Name == recipientName);
        output.WriteLine($"{camera}: undone");
    }

    /// <summary>
    /// The catalog's defaults on the real cameras: for every action a camera offers, a turned-off rule with the catalog's
    /// default values (and test values where the user must enter one) is created and deleted again. The common actions must
    /// be taken; actions that need something of the camera itself (a storage, a light, an audio clip) are only reported.
    /// </summary>
    [WriteFact]
    public async Task The_catalog_defaults_are_taken_by_every_dev_camera()
    {
        string[] common =
        [
            "com.axis.action.fixed.notification.tcp", "com.axis.action.unlimited.notification.tcp",
            "com.axis.action.fixed.notification.http", "com.axis.action.fixed.notification.https",
            "com.axis.action.fixed.send_images.ftp", "com.axis.action.unlimited.send_images.ftp",
            "com.axis.action.fixed.send_images.http", "com.axis.action.fixed.send_images.https",
            "com.axis.action.fixed.send_images.sftp", "com.axis.action.fixed.send_images.smtp", "com.axis.action.fixed.mqttpublish",
            "com.axis.action.fixed.send_videoclip.ftp", "com.axis.action.fixed.send_videoclip.https",
            "com.axis.action.fixed.set_overlay", "com.axis.action.unlimited.set_overlay",
        ];
        var failed = new List<string>();
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            try
            {
                var set = await SoapActionEngine.ReadAsync(vapix, CancellationToken.None);
                var ok = 0;
                foreach (var template in set.ActionTemplates.Where(t => !ActionNames.IsRefused(t.Token)))
                {
                    var names = template.Parameters.Concat(set.RecipientParameters(template.Token)).Distinct(StringComparer.Ordinal);
                    // As the rule builder sends it: every setting, through the same fitting as every device task.
                    var values = names.ToDictionary(n => n, n => TestValue(template.Token, n, ActionCatalog.Field(template.Token, template.RecipientTemplate, n)), StringComparer.Ordinal);
                    try
                    {
                        var action = RulePatchApply.ApplyAction(new RuleAction(template.Token, []), new RulePatch { Parameters = values }, set);
                        var rule = new DeviceRule(string.Empty, Prefix + "catalog", false, template.IsWhileActive ? null : VirtualInput, template.IsWhileActive ? [VirtualInput] : [], null, action);
                        var rest = template.Token.Contains("sftp", StringComparison.Ordinal) && await RestActionRules.IsAvailableAsync(vapix, CancellationToken.None);
                        var added = await new RuleWriter(vapix, set, inPlace: false).AddAsync(rule, CancellationToken.None);
                        await new RuleWriter(vapix, set, inPlace: false).DeleteAsync(added, CancellationToken.None);
                        if (rest)
                        {
                            var viaRest = await new RuleWriter(vapix, set, inPlace: true).AddAsync(rule, CancellationToken.None);
                            await new RuleWriter(vapix, set, inPlace: true).DeleteAsync(viaRest, CancellationToken.None);
                            output.WriteLine($"{camera}: {template.Token} also over REST");
                        }
                        ok++;
                    }
                    catch (ActionEngineException ex)
                    {
                        output.WriteLine($"{camera}: {template.Token}: {ex.Message}");
                        if (common.Contains(template.Token))
                        {
                            failed.Add($"{camera}: {template.Token}: {ex.Message}");
                        }
                    }
                }

                output.WriteLine($"{camera}: {ok} of {set.ActionTemplates.Count} actions taken with the catalog's defaults");
            }
            finally
            {
                await CleanUpAsync(vapix);
            }
        }

        Assert.Empty(failed);
    }

    /// <summary>The catalog's default, or a test value where the user must enter one (as the form asks for).</summary>
    private static string TestValue(string template, string name, CatalogField field) => name switch
    {
        "host" or "pop_host" => "192.0.2.10",
        "upload_url" => (template.EndsWith(".https", StringComparison.Ordinal) ? "https" : "http") + "://192.0.2.10/upload",
        "email_to" or "email_from" => "oadm@example.com",
        "message" or "text" => "OADM test",
        "filename" => "image.jpg",
        "topic" => "oadm/test",
        "channels" or "channel" => "1",
        "ssh_host_pub_key_sha256" => "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU",
        _ when field.Default is { Length: > 0 } d => d,
        _ when field.Choices is { Count: > 0 } c => c[0].Value,
        "port" => "9999",
        _ => field.Default ?? string.Empty,
    };

    private async Task<Guid> RunPayloadAsync(VapixClient vapix, RuleTaskPayload payload)
    {
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", payload.Action, 1, false), CancellationToken.None);
        var ctx = await ExecuteAsync(vapix, payload);
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": (Done|Skipped)$", l));
        return payload.RunId;
    }

    private static IEnumerable<string> Versions(IEnumerable<DeviceRule> rules) =>
        rules.Select(r => RuleView.VersionOf(r with { Id = string.Empty, Action = r.Action with { ConfigurationId = null } })).Order();

    private static async Task<IReadOnlyList<DeviceRule>> OwnRulesAsync(VapixClient vapix) =>
        [.. (await SoapActionEngine.ReadRulesAsync(vapix, CancellationToken.None)).Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal))];

    private async Task<Guid> RunAsync(VapixClient vapix, string action, RulePatch? patch, params string[] ids)
    {
        var rules = await SoapActionEngine.ReadRulesAsync(vapix, CancellationToken.None);
        var refs = ids.Select(id => new RuleRef(id, RuleView.VersionOf(rules.Single(r => r.Id == id)))).ToList();
        var payload = new RuleTaskPayload(action, Guid.NewGuid(), "test", [new DeviceTargets(_device.Id, refs)], patch);
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", action, 1, false), CancellationToken.None);
        var ctx = await ExecuteAsync(vapix, payload);
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": (Done|Skipped)$", l));
        return payload.RunId;
    }

    private async Task RunUndoAsync(VapixClient vapix, Guid runId)
    {
        var ctx = await ExecuteAsync(vapix, new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo", [], null, runId));
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": (Done|Skipped)$", l));
        await _plugin.Undo.TryMarkUndoneAsync(runId, true, CancellationToken.None);
    }

    private async Task<RecordingContext> ExecuteAsync(VapixClient vapix, RuleTaskPayload payload)
    {
        var ctx = new RecordingContext(vapix);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, ActionRulesJson.Serialize(payload), CancellationToken.None));
        }
        finally
        {
            foreach (var step in ctx.Steps.Snapshot())
            {
                output.WriteLine($"  {step.Name}: {step.State}" + (step.State.ToString() == "Failed" ? " - " + step.Detail : string.Empty));
            }
        }

        return ctx;
    }

    /// <summary>Removes every rule, recipient and schedule this test created, whatever happened.</summary>
    private static async Task CleanUpAsync(VapixClient vapix)
    {
        var set = await SoapActionEngine.ReadAsync(vapix, CancellationToken.None);
        var writer = new RuleWriter(vapix, set, inPlace: false);
        foreach (var rule in set.Rules.Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            await writer.DeleteAsync(rule, CancellationToken.None);
        }

        foreach (var recipient in set.Recipients.Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            await writer.DeleteRecipientAsync(recipient, CancellationToken.None);
        }

        foreach (var schedule in set.Schedules.Where(s => s.Name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            await writer.DeleteScheduleAsync(schedule, CancellationToken.None);
        }
    }
}

/// <summary>The write tests share the dev cameras: they run one after the other.</summary>
[CollectionDefinition(Name)]
public sealed class DevCameraGroup
{
    public const string Name = "dev cameras";
}
