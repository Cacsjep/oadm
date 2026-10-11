using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceTriggers.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>The event list, the stream protocol and the live triggers against fake cameras (in memory, no network).</summary>
public sealed class DeviceTriggersTests
{
    private static readonly Dictionary<string, string> Port3 = new() { ["port"] = "3" };
    private static readonly Dictionary<string, string> Port4 = new() { ["port"] = "4" };
    private static readonly Dictionary<string, string> On = new() { ["active"] = "1" };
    private static readonly Dictionary<string, string> Off = new() { ["active"] = "0" };

    [Fact]
    public void TheRecordedEventListIsReadWithNamesStatesAndValues()
    {
        var events = EventDeclarations.Parse(Fixture.Recorded);

        var input = Assert.Single(events, e => e.Topic == Fixture.VirtualInput);
        Assert.Equal("Device › Input ports › Virtual input", input.Name);
        Assert.Equal(("Input ports", "Virtual input"), (input.Group, input.Title));
        Assert.True(input.Stateful);
        Assert.Equal("active", input.StateItem);
        var port = Assert.Single(input.Sources);
        Assert.Equal(("port", "Port number"), (port.Name, port.Label));
        Assert.Equal(Enumerable.Range(1, 64).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), port.Values.Select(v => v.Value));

        var tampering = Assert.Single(events, e => e.Topic == Fixture.Tampering);
        Assert.False(tampering.Stateful);
        Assert.Null(tampering.StateItem);
        var schedule = Assert.Single(events, e => e.Topic == "tns1:UserAlarm/tnsaxis:Recurring/Interval");
        Assert.Contains(schedule.Sources[0].Values, v => v.Value == "com.axis.schedules.office_hours" && v.DisplayName == "Office Hours");
        Assert.Contains(events, e => e.Topic == "tns1:Device/tnsaxis:MQTT/ClientStatus" && e.Name == "Device › MQTT › ClientStatus");

        // Internal data of apps and the ONVIF duplicates of the I/O events are left out, like on the device's rule page.
        Assert.DoesNotContain(events, e => e.Topic.Contains("xinternal_data", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Topic == "tns1:Device/Trigger/Relay");
        Assert.DoesNotContain(events, e => e.Topic == "tns1:VideoSource/GlobalSceneChange/ImagingService");
        Assert.Equal(42, events.Count);
    }

    [Fact]
    public void ImageHealthAnalyticsEventsAreOnOffEventsWithTheAppNames()
    {
        // Recorded on a loan AXIS Q6411-LE (AXIS OS 13.0.88) with AXIS Image Health Analytics running, 2026-10-10.
        var events = EventDeclarations.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "q6411-aiha-eventinstances.xml")));

        var blur = Assert.Single(events, e => e.Topic == "tnsaxis:CameraApplicationPlatform/AXISImageHealthAnalytics/Blur");
        Assert.Equal(("Image Health Analytics - Blur", true, "active"), (blur.Name, blur.Stateful, blur.StateItem));
        Assert.Equal(("Applications", "Image Health Analytics - Blur"), (blur.Group, blur.Title)); // the app names no levels
        var any = Assert.Single(events, e => e.Topic == "tnsaxis:CameraApplicationPlatform/AXISImageHealthAnalytics/Any-Active-Events");
        Assert.True(any.Stateful);
        var onvif = events.Where(e => e.Topic.StartsWith("tns1:AcapOnvifEvent", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(onvif);
        Assert.All(onvif, e => Assert.Equal("Applications", e.Group)); // every app event in one group, also the ONVIF copies
        Assert.Equal(("Applications", "VMD 3"), DeviceEvent.GroupAndTitle("tns1:RuleEngine/tnsaxis:VMD3/vmd3_video_1", "Video Motion Detection 3 › VMD 3"));
        string[] detections = ["Blur", "Block", "Redirect", "Under-Exposure", "Unsuitability", "Any-Active-Events"];
        Assert.All(detections, n => Assert.Contains(events, e => e.Topic == "tnsaxis:CameraApplicationPlatform/AXISImageHealthAnalytics/" + n && e.Stateful));
    }

    [Fact]
    public void OneFilterPerTopicAndValuesJoinedWithOr()
    {
        var a = Fixture.VirtualInputTrigger("A", [], "3");
        var b = Fixture.VirtualInputTrigger("B", [], "4");
        var t = Fixture.TamperingTrigger("T", []);

        var filters = JsonNode.Parse(EventStreamJson.Configure([a, b, t]))!["params"]!["eventFilterList"]!.AsArray();

        Assert.Equal(2, filters.Count);
        var input = filters.Single(f => f!["topicFilter"]!.GetValue<string>() == Fixture.VirtualInput)!;
        Assert.Equal("""(boolean(//SimpleItem[@Name="port" and @Value="3"])) or (boolean(//SimpleItem[@Name="port" and @Value="4"]))""", input["contentFilter"]!.GetValue<string>());
        Assert.Null(filters.Single(f => f!["topicFilter"]!.GetValue<string>() == Fixture.Tampering)!["contentFilter"]);

        // "Any" for one trigger of a topic: the whole topic, OADM matches the values itself.
        var any = JsonNode.Parse(EventStreamJson.Configure([a, Fixture.VirtualInputTrigger("Any", [], null)]))!["params"]!["eventFilterList"]!.AsArray();
        Assert.Null(Assert.Single(any)!["contentFilter"]);
    }

    [Fact]
    public void MessagesOfTheStreamAreRead()
    {
        var notify = EventStreamJson.Parse("""{"apiVersion":"1.0","method":"events:notify","params":{"notification":{"topic":"tns1:Device/tnsaxis:IO/VirtualInput","timestamp":1791386785057,"message":{"source":{"port":"46"},"key":{},"data":{"active":"0"}}}}}""");
        Assert.Equal(Fixture.VirtualInput, notify.Notification!.Topic);
        Assert.Equal("46", notify.Notification.Source["port"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791386785057), notify.Notification.Time);

        Assert.True(EventStreamJson.Parse("""{"apiVersion":"1.0","context":"oadm","method":"events:configure","data":{}}""").IsConfigureReply);
        Assert.Equal("Could not use supplied event filter",
            EventStreamJson.Parse("""{"apiVersion":"1.0","method":"events:configure","error":{"code":2104,"message":"Could not use supplied event filter"}}""").Error);
        Assert.True(EventStreamJson.IsOn("1") && EventStreamJson.IsOn("true") && EventStreamJson.IsOn("active") && !EventStreamJson.IsOn("0") && !EventStreamJson.IsOn("inactive"));
    }

    [Fact]
    public async Task TheDeviceReportIsTheFirstFullStateWithTheEventValues()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        rig.Cameras[a.Id].SetState(Fixture.VirtualInput, Port3, On);
        rig.Cameras[b.Id].SetState(Fixture.VirtualInput, Port3, Off);
        await rig.StartAsync();

        await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id, b.Id]));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the full report");

        Assert.Equal(
            ["replace lost []", $"set trigger {a.Id} on active=1,port=3", $"replace trigger [{a.Id}]"],
            rig.Publisher.Lines);
        Assert.Contains("events:configure", rig.Cameras[a.Id].Configures.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangesFollowAndAnyPortIsOnWhileOneIsOn()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("Any input", [a.Id], port: null));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains("replace trigger []"), "the full report");
        var camera = rig.Cameras[a.Id];

        camera.SetState(Fixture.VirtualInput, Port3, On);
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {a.Id} on active=1,port=3"), "port 3 on");
        camera.SetState(Fixture.VirtualInput, Port4, On);
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {a.Id} on active=1,port=4"), "port 4 on: the values change");
        camera.SetState(Fixture.VirtualInput, Port3, Off);
        await Task.Delay(200);
        Assert.DoesNotContain($"set trigger {a.Id} off", rig.Publisher.Lines); // port 4 is still on
        camera.SetState(Fixture.VirtualInput, Port4, Off);
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {a.Id} off"), "all off");
    }

    [Fact]
    public async Task OneShotEventsFireWithTheirValues()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.TamperingTrigger("Tampering", [a.Id]));
        await Wait.UntilAsync(() => rig.Cameras[a.Id].Configures.Count == 1, "the filter");
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(rig.Plugin.Triggers[0]).Single().Connection == TriggerConnection.Connected, "connected");

        rig.Cameras[a.Id].Fire(Fixture.Tampering, new Dictionary<string, string> { ["channel"] = "1" }, new Dictionary<string, string> { ["tampering"] = "1" });

        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"fire trigger {a.Id} channel=1,tampering=1"), "the event");
        Assert.DoesNotContain(rig.Publisher.Lines, l => l.StartsWith("replace trigger", StringComparison.Ordinal)); // one-shot: nothing to report
    }

    [Fact]
    public async Task ALostConnectionTurnsConnectionLostOnAndKeepsTheLastState()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var camera = rig.Cameras[a.Id];
        camera.SetState(Fixture.VirtualInput, Port3, On);
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"replace trigger [{a.Id}]"), "the full report");

        camera.Refuse = new HttpRequestException("Connection refused");
        camera.Drop();
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith($"set lost {a.Id} on", StringComparison.Ordinal)), "connection lost");
        Assert.Contains(rig.Publisher.Lines, l => l.StartsWith($"set lost {a.Id} on reason=Unreachable", StringComparison.Ordinal));
        Assert.DoesNotContain($"set trigger {a.Id} off", rig.Publisher.Lines); // the last state stays
        var state = rig.Plugin.Runtime!.StatesOf(trigger).Single();
        Assert.Equal((TriggerConnection.Lost, true), (state.Connection, state.IsOn));

        // While away the input went off; the device's report after the reconnect brings that, and lost turns off.
        camera.SetState(Fixture.VirtualInput, Port3, Off);
        camera.Refuse = null;
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set lost {a.Id} off"), "connection back");
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {a.Id} off"), "the real change");
    }

    [Fact]
    public async Task AnEventThatIsNoLongerOnTheDeviceIsAProblem()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains("replace trigger []"), "the full report");

        // The app that offered the event was stopped: the next check of the event list misses it.
        rig.Cameras[a.Id].Declarations = Fixture.Recorded.Replace("<VirtualInput ", "<VirtualInputGone ", StringComparison.Ordinal).Replace("</VirtualInput>", "</VirtualInputGone>", StringComparison.Ordinal);
        rig.Cameras[a.Id].Drop();

        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set lost {a.Id} on reason=The event is no longer on the device"), "connection lost");
        Assert.Equal("The event is no longer on the device", rig.Plugin.Runtime!.StatesOf(trigger).Single().Problem);
    }

    [Fact]
    public async Task TagsAndAllDevicesFollowNewDevicesAndLeaveOutDevicesWithoutTheEvent()
    {
        await using var rig = new Rig();
        var tagged = rig.AddDevice("10.0.0.48", tags: ["Gate"]);
        var untagged = rig.AddDevice("10.0.0.49");
        var withoutEvent = rig.AddDevice("10.0.0.50", tags: ["gate"]);
        var noApi = rig.AddDevice("10.0.0.51", hasApi: false, tags: ["Gate"]);
        await rig.StartAsync();
        rig.Cameras[withoutEvent.Id].Declarations = Fixture.Recorded.Replace("<VirtualInput ", "<VirtualInputGone ", StringComparison.Ordinal).Replace("</VirtualInput>", "</VirtualInputGone>", StringComparison.Ordinal);

        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", []) with { Tags = ["Gate"] });
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the full report");

        var states = rig.Plugin.Runtime!.StatesOf(rig.Plugin.Resolved(trigger));
        Assert.Equal(new[] { tagged.Id, withoutEvent.Id }.Order(), states.Select(s => s.DeviceId).Order()); // not untagged, not without the API
        Assert.Equal(Oadm.Plugins.DeviceTriggers.Runtime.TriggerRuntime.NotOfferedText, states.Single(s => s.DeviceId == withoutEvent.Id).Problem);
        await Task.Delay(600); // longer than the lost delay
        Assert.DoesNotContain(rig.Publisher.Lines, l => l.Contains($"lost {withoutEvent.Id} on", StringComparison.Ordinal));

        var later = rig.AddDevice("10.0.0.52", tags: ["Gate"]);
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(rig.Plugin.Resolved(rig.Plugin.Triggers.Single())).Any(s => s.DeviceId == later.Id), "the newly tagged device joined");

        await rig.SaveAsync(rig.Plugin.Triggers.Single() with { Tags = [], AllDevices = true });
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(rig.Plugin.Resolved(rig.Plugin.Triggers.Single())).Any(s => s.DeviceId == untagged.Id), "all devices");
        Assert.DoesNotContain(rig.Plugin.Resolved(rig.Plugin.Triggers.Single()).DeviceIds, id => id == noApi.Id);
        Assert.Empty(rig.Plugin.Triggers.Single().DeviceIds); // the stored trigger keeps only the choice
    }

    [Fact]
    public async Task DevicesOadmCannotLogInToAreNotContacted()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48", status: DeviceStatus.CredentialsRequired);
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));

        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(trigger).Single().Problem == DeviceMessages.CredentialsRequired, "the status text");
        Assert.Equal(0, rig.Cameras[a.Id].Connections);
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains("replace trigger []"), "reported without that device");
    }

    [Fact]
    public async Task TurningATriggerOffTurnsEverythingOffAndCloses()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        rig.Cameras[a.Id].SetState(Fixture.VirtualInput, Port3, On);
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"replace trigger [{a.Id}]"), "the full report");
        var typesChanged = rig.TypesChanged;

        await rig.Plugin.InvokeAsync(DeviceTriggersMethods.SetEnabled, TriggerJson.Serialize(new SetEnabledRequest(trigger.Id, false)), CancellationToken.None);

        Assert.Equal(["replace trigger []", "replace lost []"], rig.Publisher.Lines.TakeLast(2));
        Assert.Equal(typesChanged, rig.TypesChanged); // its events stay (rules keep them)
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.ConnectionCount == 0, "connection closed");
        Assert.Equal(TriggerConnection.Off, rig.Plugin.Runtime!.StatesOf(rig.Plugin.Triggers.Single()).Single().Connection);
    }

    [Fact]
    public async Task EveryTriggerIsTwoEventsThatFollowSaveCopyAndDelete()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();

        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));
        Assert.Equal(1, rig.TypesChanged - 1); // start + save
        var types = rig.Plugin.EventTypes;
        Assert.Equal(
            [("Gate open", EventKind.Stateful), ("Gate open: connection lost", EventKind.Stateful)],
            types.Select(t => (t.DisplayName, t.Kind)));
        Assert.Equal("Device › Input ports › Virtual input · Port number: 3.\n\nValues:\n• {port}\n• {active}", types[0].Description);
        Assert.All(types, t => Assert.Equal("Device triggers", t.Group));

        var copy = (await rig.CallAsync<SaveReply>(DeviceTriggersMethods.Duplicate, new IdRequest(trigger.Id))).Trigger!;
        Assert.Equal(("Gate open (copy)", false), (copy.Name, copy.Enabled));
        Assert.Equal(4, rig.Plugin.EventTypes.Count);

        await rig.Plugin.InvokeAsync(DeviceTriggersMethods.Delete, TriggerJson.Serialize(new IdRequest(trigger.Id)), CancellationToken.None);
        Assert.Equal(["Gate open (copy)", "Gate open (copy): connection lost"], rig.Plugin.EventTypes.Select(t => t.DisplayName));
        Assert.Contains(DeviceTriggersPlugin.SettingsKey, rig.Settings.Values.Keys);
    }

    [Fact]
    public async Task SaveChecksNameEventAndDevices()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var old = rig.AddDevice("10.0.0.20", hasApi: false);
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));

        var reply = await rig.CallAsync<SaveReply>(DeviceTriggersMethods.Save, new SaveRequest(Fixture.VirtualInputTrigger(" gate OPEN ", []) with { Id = Guid.Empty, Topic = "" }));
        Assert.Equal("Another trigger has this name.", reply.Errors![TriggerValidation.NameField]);
        Assert.Equal("Choose an event.", reply.Errors[TriggerValidation.EventField]);
        Assert.Equal("Choose at least one device or tag.", reply.Errors[TriggerValidation.DevicesField]);

        reply = await rig.CallAsync<SaveReply>(DeviceTriggersMethods.Save, new SaveRequest(Fixture.VirtualInputTrigger("Old camera", [old.Id]) with { Id = Guid.Empty }));
        Assert.Equal("10.0.0.20: " + DeviceTriggersPluginInfo.NotSupportedReason + ".", reply.Errors![TriggerValidation.DevicesField]);
    }

    [Fact]
    public async Task ARemovedDeviceLeavesItsTriggers()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id, b.Id]));

        rig.Devices.Remove(b.Id);

        await Wait.UntilAsync(() => rig.Plugin.Triggers.Single().DeviceIds.SequenceEqual([a.Id]), "the device left the trigger");
    }

    [Fact]
    public async Task TheEditorReadsTheEventListsLive()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        var refused = rig.AddDevice("10.0.0.50", status: DeviceStatus.PasswordNotSet);
        rig.Cameras[b.Id].Declarations = Fixture.Recorded.Replace("<aev:Value>2</aev:Value><aev:Value>1</aev:Value>", "<aev:Value>2</aev:Value><aev:Value>1</aev:Value><aev:Value>3</aev:Value>", StringComparison.Ordinal);
        await rig.StartAsync();

        var started = await rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.StartRead, new StartReadRequest([a.Id, b.Id, refused.Id]));
        ReadStatusReply status = started;
        await Wait.UntilAsync(() =>
        {
            status = rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.ReadStatus, new ReadStatusRequest(started.JobId)).GetAwaiter().GetResult();
            return status.Finished;
        }, "the read");

        Assert.Equal((3, 3), (status.Total, status.Done));
        Assert.Equal(42, status.Events.Count);
        var manual = status.Events.Single(e => e.Topic == "tns1:Device/tnsaxis:IO/VirtualPort");
        Assert.Equal(["1", "2", "3"], manual.Sources[0].Values.Select(v => v.Value)); // values of both devices merged
        Assert.Equal(42, status.Devices.Single(d => d.DeviceId == a.Id).Events.Count);
        Assert.Equal(DeviceMessages.PasswordNotSet, status.Devices.Single(d => d.DeviceId == refused.Id).Error);
    }

    [Fact]
    public void OnlyAdministratorsChangeTriggers()
    {
        var plugin = new DeviceTriggersPlugin();
        Assert.All([DeviceTriggersMethods.Save, DeviceTriggersMethods.Delete, DeviceTriggersMethods.SetEnabled, DeviceTriggersMethods.Duplicate],
            m => Assert.Equal(UserRole.Admin, plugin.RequiredRole(m)));
        Assert.All([DeviceTriggersMethods.List, DeviceTriggersMethods.Get, DeviceTriggersMethods.StartRead, DeviceTriggersMethods.ReadStatus],
            m => Assert.Equal(UserRole.Operator, plugin.RequiredRole(m)));
    }

    [Fact]
    public void CopyNamesStayWithinTheLimit()
    {
        Assert.Equal("Gate (copy 2)", DeviceTriggersPlugin.CopyName("Gate", new HashSet<string>(["Gate (copy)"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal(DeviceTriggersPluginInfo.MaxNameLength, DeviceTriggersPlugin.CopyName(new string('x', 64), new HashSet<string>()).Length);
    }
}
