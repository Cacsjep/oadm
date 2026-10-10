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

        Assert.Equal(
            [(ActionEdge.TurnedOn, a.Id), (ActionEdge.TurnedOn, b.Id), (ActionEdge.TurnedOff, a.Id)],
            rig.OnDevice.Edges.Select(e => (e.Edge, e.Device!.Value)).ToList());
        var rule = Assert.Single((await rig.Rules.ListAsync(CancellationToken.None)));
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
        Assert.Contains(await rig.LogLinesAsync(), l => l.Kind == EventLogKind.Rule && l.Result == EventLogResult.Skipped && l.Message.Contains("less than 10 s", StringComparison.Ordinal));
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

        await rig.Rules.SetEnabledAsync(rule.Id, false, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOff, rig.OnDevice.Edges[^1].Edge);

        await rig.Rules.SetEnabledAsync(rule.Id, true, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOn, rig.OnDevice.Edges[^1].Edge);

        rule.Name = "Renamed";
        rule.Enabled = true;
        await rig.SaveAsync(rule); // off with the old rule, then fresh: on again
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff, ActionEdge.TurnedOn, ActionEdge.TurnedOff, ActionEdge.TurnedOn], rig.OnDevice.Edges.Select(e => e.Edge));

        await rig.Rules.DeleteAsync(rule.Id, CancellationToken.None);
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
        Assert.StartsWith("Waiting for Test source", Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).StateText, StringComparison.Ordinal);

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
        Assert.Equal("Not available: Test source is turned off", Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).Problem);

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
