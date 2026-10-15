using Oadm.Plugins.ActionRules.Rules;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The rule builder: the events the devices offered, and new rules created by the copy task.</summary>
public sealed class BuildTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";
    private const string TcpWhileActive = "com.axis.action.unlimited.notification.tcp";
    private const string VirtualInputTopic = "tns1:Device/tnsaxis:IO/VirtualInput";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-build-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _os12 = new();
    private readonly FakeDevice _os10 = new() { Address = "10.0.0.200", Model = "AXIS M3206-LVE", FirmwareVersion = "10.12.338" };
    private readonly FakeCameras _cameras = new();
    private readonly RecordingRunner _runner = new();
    private readonly ActionRulesPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        var devices = new FakeDevices();
        devices.Items.Add(_os12);
        devices.Items.Add(_os10);
        _cameras.Cameras[_os12.Id] = new RuleCamera("os12");
        _cameras.Cameras[_os10.Id] = new RuleCamera("os10");
        await _plugin.StartAsync(new FakeContext(devices, _cameras, _runner, new RecordingEvents(), _data), CancellationToken.None);
        await _plugin.ReadAsync(_os12, CancellationToken.None);
        await _plugin.ReadAsync(_os10, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static NewFilter Input(string port) => new(VirtualInputTopic, [new("port", port)]);

    private static Dictionary<string, string> TcpSettings() => new()
    {
        ["host"] = "192.0.2.10",
        ["port"] = "9999",
        ["qos"] = "0",
        ["message"] = "Door {0}",
        ["period"] = "5",
    };

    [Fact]
    public async Task The_events_of_the_devices_are_merged_without_the_schedules()
    {
        var reply = ActionRulesJson.Deserialize<EventsReply>(await _plugin.InvokeAsync(
            ActionRulesMethods.Events, ActionRulesJson.Serialize(new EventsRequest([_os12.Id, _os10.Id, Guid.NewGuid()])), CancellationToken.None));

        Assert.Equal((3, 1), (reply.Devices, reply.Unknown)); // the third device was never read
        var input = Assert.Single(reply.Events, e => e.Event.Topic == VirtualInputTopic);
        Assert.Equal(2, input.Devices);
        Assert.True(input.Event.Stateful);
        Assert.DoesNotContain(reply.Events, e => e.Event.Topic.Contains("Recurring", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task A_new_rule_runs_while_the_input_is_active_during_a_new_schedule(string os)
    {
        var device = os == "os12" ? _os12 : _os10;
        var schedule = new SchedulePatch("Night shift", "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
        var rule = new NewRule("Door alarm", true, Input("20"), true, [], ScheduleText.OwnId("Night shift"), 0, TcpWhileActive, TcpSettings())
        {
            NewSchedule = schedule,
        };

        var camera = await BuildAsync(device, rule);

        var created = Assert.Single(camera.Rules).Value;
        Assert.Null(created.Start);
        Assert.Equal(2, created.Conditions.Count);
        Assert.Equal(RuleText.Key(PageTests.VirtualInput), RuleText.Key(created.Conditions[0]));
        Assert.Equal("com.axis.schedules.oadm.night-shift", RuleText.ScheduleId(created.Conditions[1]));
        Assert.True(camera.Schedules.ContainsKey("com.axis.schedules.oadm.night-shift"));
        Assert.Equal(TcpWhileActive, camera.Configs[created.ConfigId].Template);
        Assert.Equal("Door {0}", camera.Param(Assert.Single(camera.Rules).Key, "message"));
    }

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task An_edit_changes_the_event_adds_a_new_schedule_and_takes_a_recipient_by_name(string os)
    {
        var device = os == "os12" ? _os12 : _os10;
        var camera = _cameras.Cameras[device.Id];
        camera.Recipients["70"] = new DeviceRecipient("70", "VMS", "com.axis.recipient.tcp", [new("host", "192.0.2.77"), new("port", "8888"), new("qos", "0")]);
        var id = PageTests.AddTcp(camera, "Door", "camera 7");
        var rule = (await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None)).Single(r => r.Id == id);
        await _plugin.ReadAsync(device, CancellationToken.None);
        var patch = new RulePatch
        {
            NewEvent = Input("5"),
            ScheduleId = ScheduleText.OwnId("Night"),
            NewSchedule = new SchedulePatch("Night", "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO"),
            RecipientName = "VMS",
        };

        await InvokeAndRunAsync(device, ActionRulesMethods.Edit, new RunRequest([new DeviceTargets(device.Id, [new RuleRef(id, RuleView.VersionOf(rule))])], patch));

        var changed = Assert.Single(camera.Rules);
        Assert.Equal(RuleText.Key(new EventFilter(VirtualInputTopic, "boolean(//SimpleItem[@Name=\"port\" and @Value=\"5\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])")),
            RuleText.Key(changed.Value.Start!));
        Assert.Equal("com.axis.schedules.oadm.night", RuleText.ScheduleId(Assert.Single(changed.Value.Conditions)));
        Assert.True(camera.Schedules.ContainsKey("com.axis.schedules.oadm.night"));
        Assert.Equal(("192.0.2.77", "8888", "camera 7"), (camera.Param(changed.Key, "host"), camera.Param(changed.Key, "port"), camera.Param(changed.Key, "message")));

        // Always: the schedule condition goes, the event stays.
        rule = (await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None)).Single();
        await InvokeAndRunAsync(device, ActionRulesMethods.Edit, new RunRequest([new DeviceTargets(device.Id, [new RuleRef(rule.Id, RuleView.VersionOf(rule))])], new RulePatch { ScheduleId = string.Empty }));
        changed = Assert.Single(camera.Rules);
        Assert.Empty(changed.Value.Conditions);
        Assert.NotNull(changed.Value.Start);
    }

    [Fact]
    public void An_event_from_the_page_is_never_written_as_sent()
    {
        var patch = _plugin.PreparePatch(new RulePatch { Event = new EventFilter("tns1:Anything", "true()") }, [_os12.Id]);
        Assert.Null(patch.Event);
        var ex = Assert.Throws<ArgumentException>(() => _plugin.PreparePatch(new RulePatch { NewEvent = new NewFilter(VirtualInputTopic, [new("port", "1\"] or true()")]) }, [_os12.Id]));
        Assert.StartsWith("A value of the event cannot be used.", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => _plugin.PreparePatch(new RulePatch { ScheduleId = "com.axis.schedules.genid.id-3" }, [_os12.Id]));
    }

    [Fact]
    public async Task A_new_recipient_is_added_with_every_setting()
    {
        var camera = await InvokeAndRunAsync(_os12, ActionRulesMethods.RecipientNew, new NewRecipientRequest([_os12.Id], "com.axis.recipient.tcp", "Alarm server",
            new Dictionary<string, string> { ["host"] = "192.0.2.90", ["port"] = "7000", ["qos"] = "0" }));

        var added = Assert.Single(camera.Recipients.Values, r => r.Name == "Alarm server");
        Assert.Equal(("com.axis.recipient.tcp", "192.0.2.90"), (added.Template, added.Parameters.Single(p => p.Name == "host").Value));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(ActionRulesMethods.RecipientNew,
            ActionRulesJson.Serialize(new NewRecipientRequest([_os12.Id], "com.axis.action.fixed.io", "x", new Dictionary<string, string>())), CancellationToken.None));
        Assert.Equal("Choose the kind of recipient. (Parameter 'request')", ex.Message);
        Assert.Equal(ActionRulesMethods.ManagePermission, _plugin.RequiredPermission(ActionRulesMethods.RecipientNew));
    }

    [Fact]
    public async Task A_renamed_own_schedule_keeps_its_id()
    {
        var rule = new NewRule("Night", true, Input("20"), true, [], "com.axis.schedules.oadm.night", 0, TcpWhileActive, TcpSettings())
        {
            NewSchedule = new SchedulePatch("Night shift", "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=MO"),
        };

        var camera = await BuildAsync(_os12, rule);

        Assert.Equal("Night shift", camera.Schedules["com.axis.schedules.oadm.night"].Name);
        Assert.Equal("com.axis.schedules.oadm.night", RuleText.ScheduleId(Assert.Single(camera.Rules).Value.Conditions[1]));
    }

    [Fact]
    public async Task Missing_lists_read_as_empty_and_erasing_actions_are_refused()
    {
        var json = ActionRulesJson.Serialize(new BuildRequest([_os12.Id], new NewRule("Door", true, Input("3"), false, null!, null, 0, Tcp, TcpSettings())))
            .Replace("\"values\":[{\"name\":\"port\",\"value\":\"3\"}]", "\"values\":null", StringComparison.Ordinal);
        await _plugin.InvokeAsync(ActionRulesMethods.Build, json, CancellationToken.None);
        Assert.Single(_runner.Runs);

        var erase = new NewRule("Reset", true, Input("3"), false, [], null, 0, "com.axis.action.fixed.erase.configuration", new Dictionary<string, string>());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(
            ActionRulesMethods.Build, ActionRulesJson.Serialize(new BuildRequest([_os12.Id], erase)), CancellationToken.None));
        Assert.StartsWith(ActionNames.RefusedText, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ActionNames.RefusedText, new RulePatch { Template = "com.axis.action.fixed.erase.configuration" }.Problem());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_events_of_5000_devices_merge_fast()
    {
        var devices = new FakeDevices();
        var cameras = new FakeCameras();
        var plugin = new ActionRulesPlugin();
        await plugin.StartAsync(new FakeContext(devices, cameras, new RecordingRunner(), new RecordingEvents(), _data + "-perf"), CancellationToken.None);
        try
        {
            var os = new[] { "os10", "os11", "os12" };
            for (var i = 0; i < 5_000; i++)
            {
                var device = new FakeDevice { Address = $"10.1.{i / 250}.{i % 250}" };
                devices.Items.Add(device);
                cameras.Cameras[device.Id] = new RuleCamera(os[i % 3]);
            }

            var read = System.Diagnostics.Stopwatch.StartNew();
            await Parallel.ForEachAsync(devices.Items, async (d, ct) => await plugin.ReadAsync(d, ct));
            read.Stop();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reply = plugin.Events(new EventsRequest([.. devices.Items.Select(d => d.Id)]));
            watch.Stop();

            Assert.Equal(5_000, reply.Events.Single(e => e.Event.Topic == VirtualInputTopic).Devices);
            Assert.True(watch.ElapsedMilliseconds < 1_000, $"merge took {watch.ElapsedMilliseconds} ms");
            Console.WriteLine($"read 5,000 devices {read.ElapsedMilliseconds} ms, merge {watch.ElapsedMilliseconds} ms, {reply.Events.Count} events");
        }
        finally
        {
            await plugin.DisposeAsync();
            if (Directory.Exists(_data + "-perf"))
            {
                Directory.Delete(_data + "-perf", recursive: true);
            }
        }
    }

    [Fact]
    public async Task An_event_that_is_not_on_or_off_cannot_be_a_condition()
    {
        var reply = _plugin.Events(new EventsRequest([_os12.Id]));
        var oneShot = reply.Events.First(e => !e.Event.Stateful).Event;
        var rule = new NewRule("Door", true, new NewFilter(oneShot.Topic, []), true, [], null, 0, TcpWhileActive, TcpSettings());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(
            ActionRulesMethods.Build, ActionRulesJson.Serialize(new BuildRequest([_os12.Id], rule)), CancellationToken.None));

        Assert.StartsWith($"\"{oneShot.Title}\" is not on or off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_rule_takes_the_recipient_of_each_device_by_name_and_its_wait_time()
    {
        var camera = _cameras.Cameras[_os12.Id];
        camera.Recipients["70"] = new DeviceRecipient("70", "VMS", "com.axis.recipient.tcp", [new("host", "192.0.2.77"), new("port", "8888"), new("qos", "0")]);
        var rule = new NewRule("Door", true, Input("3"), false, [], null, 30, Tcp, new Dictionary<string, string> { ["message"] = "m" })
        {
            RecipientName = "VMS",
        };

        await BuildAsync(_os12, rule);

        var created = Assert.Single(camera.Rules);
        Assert.Equal(("192.0.2.77", "8888"), (camera.Param(created.Key, "host"), camera.Param(created.Key, "port")));
        Assert.NotNull(created.Value.Start);
        Assert.Equal("PT30S", created.Value.Timeout);
    }

    [Fact]
    public void An_empty_key_fingerprint_is_left_out()
    {
        var set = SoapActionEngine.ParseActionTemplates(System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "os12-GetActionTemplates.xml"))));
        var recipients = SoapActionEngine.ParseRecipientTemplates(System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "os12-GetRecipientTemplates.xml"))));
        var device = new DeviceRuleSet([], set, recipients, [], [], new Dictionary<string, string>());
        var token = "com.axis.action.fixed.send_images.sftp";
        var values = set.Single(t => t.Token == token).Parameters.Concat(device.RecipientParameters(token)).ToDictionary(n => n, n => n == "ssh_host_pub_key_md5" ? string.Empty : "1");

        var action = RulePatchApply.ApplyAction(new RuleAction(token, []), new RulePatch { Parameters = values }, device);

        Assert.Null(action.Get("ssh_host_pub_key_md5"));
        Assert.Equal("1", action.Get("ssh_host_pub_key_sha256"));
    }

    [Fact]
    public async Task A_device_without_the_recipient_changes_nothing()
    {
        var rule = new NewRule("Door", true, Input("3"), false, [], null, 0, Tcp, new Dictionary<string, string> { ["message"] = "m" }) { RecipientName = "Nowhere" };

        var camera = _cameras.Cameras[_os10.Id];
        await Assert.ThrowsAsync<ActionEngineException>(() => BuildAsync(_os10, rule));

        Assert.Empty(camera.Rules);
    }

    [Theory]
    [InlineData("while", "Actions that run while the rule is active need \"While the event is active\".")]
    [InlineData("quote", "A value of the event cannot be used.")]
    [InlineData("schedule", "Choose the schedule.")]
    [InlineData("scheduleEvent", "Choose a schedule as the schedule, not as an event.")]
    [InlineData("unknown", "No device has the setting \"colour\".")]
    [InlineData("name", "Enter a name of 1 to 128 characters.")]
    public async Task A_bad_rule_starts_nothing(string what, string message)
    {
        var rule = new NewRule("Door", true, Input("3"), false, [], null, 0, Tcp, TcpSettings());
        rule = what switch
        {
            "while" => rule with { Template = TcpWhileActive },
            "quote" => rule with { Event = new NewFilter(VirtualInputTopic, [new("port", "3\"]) or true()")]) },
            "schedule" => rule with { ScheduleId = "com.axis.schedules.genid.id-3" },
            "scheduleEvent" => rule with { Event = new NewFilter(RuleText.ScheduleIntervalTopic, []) },
            "unknown" => rule with { Parameters = new Dictionary<string, string> { ["colour"] = "red" } },
            _ => rule with { Name = " " },
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(
            ActionRulesMethods.Build, ActionRulesJson.Serialize(new BuildRequest([_os12.Id], rule)), CancellationToken.None));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Building_needs_the_manage_right() =>
        Assert.Equal((ActionRulesMethods.ManagePermission, PermissionKeysShow()), (_plugin.RequiredPermission(ActionRulesMethods.Build), _plugin.RequiredPermission(ActionRulesMethods.Events)));

    private static string PermissionKeysShow() => Sdk.Plugins.PermissionKeys.Show;

    /// <summary>Starts the build through the page method, then runs the recorded task on the device's camera.</summary>
    private Task<RuleCamera> BuildAsync(FakeDevice device, NewRule rule) =>
        InvokeAndRunAsync(device, ActionRulesMethods.Build, new BuildRequest([device.Id], rule));

    /// <summary>Starts a page method that starts tasks, then runs the recorded task on the device's camera.</summary>
    private async Task<RuleCamera> InvokeAndRunAsync(FakeDevice device, string method, object request)
    {
        var started = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(method, ActionRulesJson.Serialize(request), CancellationToken.None));
        var payload = _runner.Runs[^1].Payload;
        Assert.Contains(started.RunId.ToString(), payload!, StringComparison.Ordinal);
        var camera = _cameras.Cameras[device.Id];
        var ctx = new RecordingContext(camera);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, device, payload, CancellationToken.None));
        return camera;
    }
}
