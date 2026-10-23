using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

public sealed class RuleEngineTests
{
    [Fact]
    public async Task AStatefulRuleTurnsOnAndOffPerDevice()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var a = await rig.AddDeviceAsync("10.0.0.1");
        var b = await rig.AddDeviceAsync("10.0.0.2");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));

        rig.Publisher.SetState(EventRulesRig.Blur, a.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, b.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, a.Id, false);
        await rig.SettleAsync();

        // In order per device; devices do not wait for each other.
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Where(e => e.Device == a.Id).Select(e => e.Edge));
        Assert.Equal([ActionEdge.TurnedOn], rig.OnDevice.Edges.Where(e => e.Device == b.Id).Select(e => e.Edge));
        var rule = Assert.Single((await rig.Rules.ListAsync(rig.AllView, CancellationToken.None)));
        Assert.Equal(1, rule.ActiveCount);
        Assert.Equal("On", rule.StateText);
    }

    [Fact]
    public async Task ActionsOnChosenDevicesFollowTheFirstOnAndTheLastOff()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera1 = await rig.AddDeviceAsync("10.0.0.1");
        var camera2 = await rig.AddDeviceAsync("10.0.0.2");
        var ioBox = await rig.AddDeviceAsync("10.0.0.70");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnDevices(ioBox.Id)));

        rig.Publisher.SetState(EventRulesRig.Blur, camera1.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, camera2.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, camera1.Id, false); // camera 2 still on: the relay stays
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)ioBox.Id)], rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Blur, camera2.Id, false);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)ioBox.Id), (ActionEdge.TurnedOff, ioBox.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task FurtherConditionsMustHoldOnTheSameOrAnyDevice()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var io = await rig.AddDeviceAsync("10.0.0.70");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions.Add(new RuleCondition
        {
            Kind = RuleConditionKind.Event,
            EventTypeId = EventRulesRig.Input,
            Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [io.Id] },
            Data = new Dictionary<string, string> { ["port"] = "1" },
        });
        await rig.SaveAsync(rule);

        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges); // input on the I/O box not active

        rig.Publisher.SetState(EventRulesRig.Input, io.Id, true, new Dictionary<string, string> { ["port"] = "2" });
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges); // another port

        rig.Publisher.SetState(EventRulesRig.Input, io.Id, true, new Dictionary<string, string> { ["port"] = "1" });
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Input, io.Id, false);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id), (ActionEdge.TurnedOff, camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task SameDeviceConditionsJoinOnTheTriggerDevice()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var a = await rig.AddDeviceAsync("10.0.0.1");
        var b = await rig.AddDeviceAsync("10.0.0.2");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = EventRulesRig.Input, Devices = new DeviceSelector { Mode = DeviceSelectorMode.EventDevice } });
        await rig.SaveAsync(rule);

        rig.Publisher.SetState(EventRulesRig.Blur, a.Id, true);
        rig.Publisher.SetState(EventRulesRig.Input, b.Id, true);
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Input, a.Id, true);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)a.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task AScheduleTurnsTheRuleOnAndOffWithTheClock()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        var local = rig.Time.GetLocalNow();
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions.Add(new RuleCondition
        {
            Kind = RuleConditionKind.Schedule,
            Schedule = new RuleSchedule
            {
                Days = [.. Enum.GetValues<DayOfWeek>()],
                From = local.AddHours(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                To = local.AddHours(2).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        await rig.SaveAsync(rule);
        Assert.Empty(rig.OnDevice.Edges);

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        rig.Engine.Tick();
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);

        rig.Time.Advance(TimeSpan.FromMinutes(60));
        rig.Engine.Tick();
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id), (ActionEdge.TurnedOff, camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task AOneShotTriggerFiresWhileTheOtherConditionsHold()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Pulse, rig.OnEventDevice(), rig.WithoutDevice());
        rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = EventRulesRig.Input, Devices = new DeviceSelector { Mode = DeviceSelectorMode.EventDevice } });
        await rig.SaveAsync(rule);

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.Fired, (Guid?)camera.Id)], rig.OnDevice.Edges);
        Assert.Equal([(ActionEdge.Fired, (Guid?)null)], rig.NoDevice.Edges);
    }

    [Fact]
    public async Task TheCooldownSkipsAQuickSecondRunAndItsOff()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.CooldownSeconds = 10;
        await rig.SaveAsync(rule);

        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, false);
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true); // within 10 s: skipped
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, false); // its off is skipped too
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id), (ActionEdge.TurnedOff, camera.Id)], rig.OnDevice.Edges);

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await rig.SettleAsync();
        Assert.Equal(3, rig.OnDevice.Edges.Count);
        Assert.Contains(await rig.LogLinesAsync(), l => l.Kind == EventLogKind.Rule && l.Result == EventLogResult.Skipped && l.Message.Contains("Dead time (10 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRunsPerMinuteCapPausesTheRuleAndLogsItOnce()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var rule = EventRulesRig.Rule(EventRulesRig.Pulse, rig.OnEventDevice());
        rule.MaxRunsPerMinute = 3;
        await rig.SaveAsync(rule);

        for (var i = 0; i < 10; i++)
        {
            rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        }

        await rig.SettleAsync();
        Assert.Equal(3, rig.OnDevice.Edges.Count);
        Assert.Single(await rig.LogLinesAsync(), l => l.What == "Rule paused");

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Equal(4, rig.OnDevice.Edges.Count);
    }

    [Fact]
    public async Task TurningOffChangingOrDeletingARuleThatIsOnRunsItsOffActions()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        var rule = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);

        await rig.Rules.SetEnabledAsync(rule.Id, false, rig.AllView, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOff, rig.OnDevice.Edges[^1].Edge);

        await rig.Rules.SetEnabledAsync(rule.Id, true, rig.AllView, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOn, rig.OnDevice.Edges[^1].Edge);

        rule.Name = "Renamed";
        rule.Enabled = true;
        await rig.SaveAsync(rule); // off with the old rule, then fresh: on again
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff, ActionEdge.TurnedOn, ActionEdge.TurnedOff, ActionEdge.TurnedOn], rig.OnDevice.Edges.Select(e => e.Edge));

        await rig.Rules.DeleteAsync(rule.Id, rig.AllView, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOff, rig.OnDevice.Edges[^1].Edge);
    }

    [Fact]
    public async Task BuiltInDeviceStatusEventsComeFromTheDeviceTable()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        await rig.SaveAsync(EventRulesRig.Rule(BuiltInEvents.Unreachable, rig.WithoutDevice(new JsonObject { ["text"] = "{event} on {device}, {state}" })));

        await rig.Devices.UpdateAsync(camera.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await rig.SettleAsync();
        await rig.Devices.UpdateAsync(camera.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);
        await rig.SettleAsync();

        Assert.Equal(["Device unreachable on 10.0.0.1 (P3265-V), on|", "Device unreachable on 10.0.0.1 (P3265-V), off|"], rig.NoDevice.Runs.Select(r => r.Text));
        var lines = await rig.LogLinesAsync();
        Assert.Contains(lines, l => l.Kind == EventLogKind.Event && l.What == "Device unreachable" && l.Message == "On");
    }

    [Fact]
    public async Task AnyDeviceEventsAreOnWhileAnyDeviceHasTheCondition()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var a = await rig.AddDeviceAsync("10.0.0.1");
        var b = await rig.AddDeviceAsync("10.0.0.2");
        var events = new List<PublishedEvent>();
        using var subscription = rig.Engine.SubscriptionsFor("test").Subscribe(e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        await rig.Engine.DrainAsync(CancellationToken.None);

        await rig.Devices.UpdateAsync(a.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await rig.SettleAsync();
        await rig.Devices.UpdateAsync(b.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await rig.SettleAsync();
        await rig.Devices.UpdateAsync(a.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);
        await rig.SettleAsync();
        await rig.Devices.RemoveAsync(b.Id, CancellationToken.None);
        await rig.SettleAsync();

        List<PublishedEvent> AnyEvents()
        {
            lock (events)
            {
                return [.. events.Where(e => e.TypeId == BuiltInEvents.AnyUnreachable)];
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(30); // the subscription's own loop hands them over
        while (AnyEvents().Count < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var any = AnyEvents();
        lock (events)
        {
            Assert.Contains(events, e => e.TypeId == BuiltInEvents.Unreachable && e.DeviceId == b.Id && e.Kind == PublishedEventKind.Off); // removed: off
        }

        Assert.Equal(
            [(PublishedEventKind.On, "1"), (PublishedEventKind.On, "2"), (PublishedEventKind.On, "1"), (PublishedEventKind.Off, "")],
            any.Select(e => (e.Kind, e.Data.GetValueOrDefault(BuiltInEvents.CountField) ?? "")));
        Assert.All(any, e => Assert.Null(e.DeviceId)); // one state for the server
        var type = rig.Catalog.FindEventType(BuiltInEvents.AnyUnreachable)!;
        Assert.False(type.DeviceScope);
        Assert.Equal(EventValueKind.Number, Assert.Single(type.Info.DataFields).Kind);
    }

    [Fact]
    public async Task CertificateExpiryEventsFollowTheClock()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        await rig.Devices.UpdateAsync(camera.Id, d => d.CertNotAfterUtc = rig.Time.GetUtcNow().UtcDateTime.AddDays(31), CancellationToken.None);
        await rig.SaveAsync(EventRulesRig.Rule(BuiltInEvents.CertificateExpiresSoon, rig.OnEventDevice()));
        Assert.Empty(rig.OnDevice.Edges);

        rig.Time.Advance(TimeSpan.FromDays(2));
        rig.Engine.Tick();
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task ARemovedDeviceTurnsItsRulesOff()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice(), rig.OnEventDevice()));

        await rig.Devices.RemoveAsync(camera.Id, CancellationToken.None);
        await rig.SettleAsync();

        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.NoDevice.Edges.Select(e => e.Edge));
        Assert.Contains(await rig.LogLinesAsync(), l => l.Kind == EventLogKind.Action && l.Result == EventLogResult.Skipped && l.Message == Oadm.Sdk.Devices.DeviceMessages.Removed);
    }

    [Fact]
    public async Task APluginEventTypeWaitsForTheFirstFullReportAndTimesOut()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true); // a single change is not the full report
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));
        Assert.Empty(rig.OnDevice.Edges);
        Assert.StartsWith("Waiting for Test source", Assert.Single(await rig.Rules.ListAsync(rig.AllView, CancellationToken.None)).StateText, StringComparison.Ordinal);

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        rig.Engine.Tick();
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges); // what was reported counts after the timeout
    }

    [Fact]
    public async Task AfterARestartOnlyRealChangesRunActions()
    {
        var folder = Persistence.TestDatabase.NewTempDirectory();
        Guid on;
        Guid cleared;
        var first = await EventRulesRig.StartAsync(folder);
        on = (await first.AddDeviceAsync("10.0.0.1")).Id;
        cleared = (await first.AddDeviceAsync("10.0.0.2")).Id;
        first.Publisher.ReplaceStates(EventRulesRig.Blur, [on, cleared]);
        await first.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, first.OnEventDevice()));
        Assert.Equal(2, first.OnDevice.Edges.Count);
        await first.CloseKeepDataAsync(); // like a server stop: the database stays

        await using var second = await EventRulesRig.StartAsync(folder);
        await second.SettleAsync();
        var states = await second.Store.LoadStatesAsync(CancellationToken.None);
        Assert.Equal(2, Assert.Single(states).Value.Count);
        Assert.Empty(second.OnDevice.Edges); // nothing runs before the source reported

        second.Publisher.ReplaceStates(EventRulesRig.Blur, [on]); // the second camera cleared while the server was down
        await second.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOff, (Guid?)cleared)], second.OnDevice.Edges);
    }

    [Fact]
    public async Task APluginTurnedOffTurnsItsEventsOff()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));

        rig.Registry.SetEnabled(rig.SourceOrigin.PackageId, false);
        rig.Engine.SourcesChanged();
        await rig.SettleAsync();

        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Select(e => e.Edge));
        Assert.Equal("Not available: Test source is turned off", Assert.Single(await rig.Rules.ListAsync(rig.AllView, CancellationToken.None)).Problem);

        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true); // ignored while off
        await rig.SettleAsync();
        Assert.Equal(2, rig.OnDevice.Edges.Count);
    }

    [Fact]
    public async Task ActionsRunInOrderAndAFailureDoesNotStopTheNext()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.OnDevice.FailWith = "Boom";
        rig.OnDevice.Delay = TimeSpan.FromMilliseconds(50);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, rig.OnEventDevice(), rig.WithoutDevice()));

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Single(rig.OnDevice.Runs);
        Assert.Single(rig.NoDevice.Runs);
        var actions = (await rig.LogLinesAsync()).Where(l => l.Kind == EventLogKind.Action).ToList();
        Assert.Equal([EventLogResult.Failed, EventLogResult.Ok], actions.Select(a => a.Result));
        Assert.Equal("Boom", actions[0].Message);

        // The failure is in the server log too (Logs page), with the rule, the device and the reason.
        var warning = Assert.Single(rig.ServerLog.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Matches(@"^Event rule .+: action .+ on 10\.0\.0\.1.* failed: Boom$", warning.Text);
    }

    [Fact]
    public async Task DevicesThatRefuseTheLoginAreSkippedWithoutARequest()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        await rig.Devices.UpdateAsync(camera.Id, d => d.Status = DeviceStatus.CredentialsRequired, CancellationToken.None);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, rig.OnEventDevice()));

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Empty(rig.OnDevice.Runs);
        Assert.Contains(await rig.LogLinesAsync(), l => l.Kind == EventLogKind.Action && l.Result == EventLogResult.Skipped && l.Message == DeviceMessages.CredentialsRequired);
    }

    [Fact]
    public async Task AServerWideEventTurnsTheRuleOnOnce()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var target = await rig.AddDeviceAsync("10.0.0.70");
        rig.Publisher.ReplaceStates(EventRulesRig.ServerFlag, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.ServerFlag, rig.OnDevices(target.Id), rig.WithoutDevice()));

        rig.Publisher.SetState(EventRulesRig.ServerFlag, null, true);
        rig.Publisher.SetState(EventRulesRig.ServerFlag, null, false);
        await rig.SettleAsync();

        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)target.Id), (ActionEdge.TurnedOff, target.Id)], rig.OnDevice.Edges);
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.NoDevice.Edges.Select(e => e.Edge));
    }

    [Fact]
    public async Task TagsSelectDevicesAndFollowTagChanges()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Tags, Tags = ["Building A"] };
        await rig.SaveAsync(rule);
        Assert.Empty(rig.OnDevice.Edges);

        await rig.Devices.UpdateAsync(camera.Id, d => d.Tags = ["Building A"], CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task AChoiceOfTagsAndDevicesSelectsBoth()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var tagged = await rig.AddDeviceAsync("10.0.0.1");
        var listed = await rig.AddDeviceAsync("10.0.0.2");
        var other = await rig.AddDeviceAsync("10.0.0.3");
        await rig.Devices.UpdateAsync(tagged.Id, d => d.Tags = ["Building A"], CancellationToken.None);
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [tagged.Id, listed.Id, other.Id]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [listed.Id], Tags = ["building a"] };
        await rig.SaveAsync(rule);

        Assert.Equal(new[] { tagged.Id, listed.Id }.Order(), rig.OnDevice.Edges.Select(e => e.Device!.Value).Order()); // not the other device
        Assert.True(new DeviceSelector { Mode = DeviceSelectorMode.Devices }.IsEmptyChoice);
        Assert.True(rule.Conditions[0].Devices.ChoiceMatches(tagged.Id, ["BUILDING A"]));
    }

    [Fact]
    public async Task EventsNoRuleUsesAreNotLogged()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Empty(await rig.LogLinesAsync());
        Assert.False(rig.Publisher.IsWatched(EventRulesRig.Blur));
    }
}
