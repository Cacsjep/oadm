using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Events;

/// <summary>
/// Event rules with an owner (docs/spec/device-scope.md, Shared objects): a rule of a group sees and acts only on the group's
/// devices ("All devices" and the "Any device" counts are the group's), follows the group, is hidden from other groups, refuses
/// devices, tags and addresses outside the group on save, and is turned off when its group is deleted.
/// </summary>
public sealed class RuleOwnerTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static async Task<DeviceView> GroupAsync(EventRulesRig rig, string tag)
    {
        var id = (await rig.Tags.ListDefinitionsAsync(Ct)).FirstOrDefault(t => t.Name == tag)?.Id ?? (await rig.Tags.CreateAsync(tag, TagColor.None, Ct)).Id;
        var group = await rig.Groups.SaveAsync(null, "Group " + tag, "", new DeviceGroupRules(false, [], [id], [], []), 0, Ct);
        return await rig.Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator " + tag, Ct);
    }

    private static RuleAction OnAll(EventRulesRig rig) => new() { ActionId = rig.OnDevice.Id, Targets = new DeviceSelector { Mode = DeviceSelectorMode.All } };

    [Fact]
    public async Task ARuleOfAGroupSeesAndActsOnlyOnTheGroupsDevices()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var outside = await rig.AddDeviceAsync("10.0.0.2", "Yard");
        var view = await GroupAsync(rig, "Lobby");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var (rule, summary, _) = await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice(), OnAll(rig)), view, null, false, Ct);
        Assert.Equal(view.GroupId, summary.Owner.GroupId);
        await rig.SettleAsync();

        rig.Publisher.SetState(EventRulesRig.Blur, outside.Id, true);
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges); // "All devices" of the trigger are the group's

        rig.Publisher.SetState(EventRulesRig.Blur, inside.Id, true);
        await rig.SettleAsync();
        Assert.All(rig.OnDevice.Edges, e => Assert.Equal(inside.Id, e.Device)); // the event's device and "All devices" of the action
        Assert.Equal(2, rig.OnDevice.Edges.Count);

        // The device leaves the group (an administrator removes the tag): the rule turns off for it, and its off actions
        // no longer reach it (it is not the group's).
        await rig.Tags.SetDeviceTagsAsync(await rig.AllView.GetAsync([inside.Id], Ct), [], ["Lobby"], Ct);
        await rig.SettleAsync();
        Assert.DoesNotContain(rig.OnDevice.Edges, e => e.Edge == ActionEdge.TurnedOff);
        Assert.Contains(await rig.LogLinesAsync(), l => l.Kind == EventLogKind.Action && l.Result == EventLogResult.Skipped && l.Message == ActionRunner.OutsideOwner);
        Assert.Equal(0, (await rig.Rules.ListAsync(rig.AllView, Ct)).Single(r => r.Id == rule.Id).ActiveCount);
    }

    [Fact]
    public async Task TheAnyDeviceCountOfAGroupsRuleIsTheGroups()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var outside = await rig.AddDeviceAsync("10.0.0.2", "Yard");
        var view = await GroupAsync(rig, "Lobby");
        var any = new RuleDefinition
        {
            Name = "Any unreachable",
            Enabled = true,
            CooldownSeconds = 0,
            Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.AnyUnreachable }],
            Actions = [rig.WithoutDevice(new JsonObject { ["text"] = "{count}" })],
        };
        var (groupRule, _, _) = await rig.Rules.SaveAsync(any, view, null, false, Ct);
        var allRule = await rig.SaveAsync(new RuleDefinition { Name = "All", Enabled = true, CooldownSeconds = 0, Conditions = any.Conditions, Actions = [rig.WithoutDevice(new JsonObject { ["text"] = "{count}" })] });

        await rig.Devices.UpdateAsync(outside.Id, d => d.Status = DeviceStatus.Unreachable, Ct);
        await rig.SettleAsync();
        Assert.Equal([(allRule.Id, "1|")], rig.NoDevice.Runs.Select(r => (r.RuleId, r.Text))); // the group has no unreachable device

        await rig.Devices.UpdateAsync(inside.Id, d => d.Status = DeviceStatus.Unreachable, Ct);
        await rig.SettleAsync();
        Assert.Equal([(groupRule.Id, "1|")], rig.NoDevice.Runs.Where(r => r.RuleId == groupRule.Id).Select(r => (r.RuleId, r.Text))); // its own device only
    }

    [Fact]
    public async Task UsersOfAGroupSeeAndChangeOnlyTheirGroupsRules()
    {
        await using var rig = await EventRulesRig.StartAsync();
        await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        await rig.AddDeviceAsync("10.0.0.2", "Yard");
        var lobby = await GroupAsync(rig, "Lobby");
        var yard = await GroupAsync(rig, "Yard");
        var (mine, _, _) = await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()), lobby, null, false, Ct);
        var all = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Input, rig.OnEventDevice()));

        Assert.Equal([mine.Id], (await rig.Rules.ListAsync(lobby, Ct)).Select(r => r.Id));
        Assert.Empty(await rig.Rules.ListAsync(yard, Ct));
        Assert.Equal(2, (await rig.Rules.ListAsync(rig.AllView, Ct)).Count);
        Assert.Null(await rig.Rules.GetAsync(all.Id, lobby, Ct));
        Assert.Null(await rig.Rules.SetEnabledAsync(mine.Id, false, yard, Ct));
        Assert.Null(await rig.Rules.DeleteAsync(all.Id, lobby, Ct));
        Assert.Null(await rig.Rules.DuplicateAsync(mine.Id, yard, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Rules.SaveAsync(mine, yard, null, false, Ct));

        // An administrator's edit keeps the owner; only an administrator chooses another one; a copy belongs to who copies.
        var (kept, keptSummary, _) = await rig.Rules.SaveAsync(mine, rig.AllView, null, true, Ct);
        Assert.Equal(lobby.GroupId, keptSummary.Owner.GroupId);
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Rules.SaveAsync(kept, lobby, DeviceScope.All, false, Ct));
        Assert.Equal(OwnerRules.OnlyAdministrators, refused.Message);
        var copy = await rig.Rules.DuplicateAsync(mine.Id, rig.AllView, Ct);
        Assert.Equal(DeviceScope.All, copy!.Value.Summary.Owner);
        var groupCopy = await rig.Rules.DuplicateAsync(mine.Id, lobby, Ct);
        Assert.Equal(lobby.GroupId, groupCopy!.Value.Summary.Owner.GroupId);
    }

    [Fact]
    public async Task SavingARuleOfAGroupRefusesDevicesTagsAndAddressesOutsideIt()
    {
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterActionPlugin(new SendsTo(), r.ActionOrigin));
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var outside = await rig.AddDeviceAsync("10.0.0.2", "Yard");
        await rig.Devices.UpdateAsync(outside.Id, d => d.Tags = ["Yard", "Hidden"], Ct);
        var view = await GroupAsync(rig, "Lobby");

        foreach (var id in new[] { outside.Id, Guid.NewGuid() })
        {
            var onDevices = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnDevices(inside.Id, id));
            var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.Rules.SaveAsync(onDevices, view, null, false, Ct));
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Message);
            var trigger = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
            trigger.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [id] };
            await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.Rules.SaveAsync(trigger, view, null, false, Ct));
        }

        var hidden = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        hidden.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Hidden"] };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Rules.SaveAsync(hidden, view, null, false, Ct));

        var send = EventRulesRig.Rule(EventRulesRig.Blur, new RuleAction { ActionId = SendsTo.ActionId, Settings = new JsonObject { ["host"] = "vms.example.com", ["port"] = 8000 } });
        var notAllowed = await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(send, view, null, false, Ct));
        Assert.Equal(ActionDestination.NotAllowedText, notAllowed.Message);
        await rig.SaveAsync(send); // users with all devices send anywhere

        await rig.Destinations.SetAsync(["VMS.example.com:8000"], Ct);
        var (saved, _, _) = await rig.Rules.SaveAsync(send, view, null, false, Ct);
        Assert.Equal(view.GroupId, (await rig.Rules.GetAsync(saved.Id, view, Ct))!.Value.Summary.Owner.GroupId);
    }

    [Fact]
    public async Task ARuleOfAGroupSendsOnlyToAllowedAddresses()
    {
        var action = new SendsTo();
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterActionPlugin(action, r.ActionOrigin));
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var view = await GroupAsync(rig, "Lobby");
        await rig.Destinations.SetAsync(["vms.example.com"], Ct);
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        // Placeholders can change the address after the save: the send checks it again.
        await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, new RuleAction { ActionId = SendsTo.ActionId, Settings = new JsonObject { ["host"] = "vms.example.com", ["sendTo"] = "other.example.com" } }), view, null, false, Ct);

        rig.Publisher.SetState(EventRulesRig.Blur, inside.Id, true);
        await rig.SettleAsync();
        Assert.Equal([("other.example.com", false)], action.Checked);
        var line = (await rig.LogLinesAsync()).Last(l => l.Kind == EventLogKind.Action);
        Assert.Equal(EventLogResult.Failed, line.Result);
        Assert.Contains(ActionDestination.NotAllowedText, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheGroupTurnsItsRulesOffWithoutAnOwner()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var view = await GroupAsync(rig, "Lobby");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var (rule, _, _) = await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()), view, null, false, Ct);
        rig.Publisher.SetState(EventRulesRig.Blur, inside.Id, true);
        await rig.SettleAsync();
        Assert.Single(rig.OnDevice.Edges);

        await rig.Groups.DeleteAsync(view.GroupId!.Value, Ct);
        await Eventually.TrueAsync(async () => (await rig.Rules.GetAsync(rule.Id, rig.AllView, Ct))!.Value.Summary is { Enabled: false, Owner.IsNone: true }, "the rule is off without an owner");
        await Eventually.TrueAsync(async () => (await rig.Rules.ListAsync(rig.AllView, Ct)).Single().ActiveCount == 0, "the engine dropped it");
        var refused = await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SetEnabledAsync(rule.Id, true, rig.AllView, Ct));
        Assert.Equal(DeviceGroupTexts.ChooseDevices, refused.Message);
    }

    [Fact]
    public async Task TheEventLogOfAGroupShowsOnlyItsRulesAndDevices()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", "Lobby");
        var outside = await rig.AddDeviceAsync("10.0.0.2", "Yard");
        var view = await GroupAsync(rig, "Lobby");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var (mine, _, _) = await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()), view, null, false, Ct);
        var all = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));
        rig.Publisher.SetState(EventRulesRig.Blur, inside.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, outside.Id, true);
        await rig.SettleAsync();
        await rig.Log.FlushAsync(Ct);

        var scope = new EventLogScope(await view.IdsAsync(Ct), await rig.Rules.VisibleIdsAsync(view, Ct));
        var (lines, total) = await rig.Log.ListAsync(0, 0, null, scope, Ct);
        Assert.NotEmpty(lines);
        Assert.Equal(lines.Count, total);
        Assert.All(lines, l => Assert.NotEqual(outside.Id, l.DeviceId));
        Assert.All(lines, l => Assert.True(l.RuleId is null || l.RuleId == mine.Id));
        Assert.All(lines, l => Assert.True(scope.Covers(l)));
        var (everything, _) = await rig.Log.ListAsync(0, 0, null, Ct);
        Assert.Contains(everything, l => l.RuleId == all.Id);
        Assert.Contains(everything, l => l.DeviceId == outside.Id);
    }

    [Fact]
    public async Task TheCatalogOfAGroupHasItsSitesAndDevices()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var view = await GroupAsync(rig, "Lobby");
        var group = DeviceScope.Group(view.GroupId!.Value);
        var device = Guid.NewGuid();
        CatalogEventType Type(string id, IReadOnlyList<Guid>? devices = null) =>
            new(new EventTypeInfo(id, id, "Test", EventKind.Stateful, EventScope.Server) { DeviceIds = devices }, "s", "S", true);

        Assert.False(rig.Owners.Sees(Type(SiteEventSource.OfflineTypeId(Guid.NewGuid())), group, _ => false)); // not a site of the group
        Assert.True(rig.Owners.Sees(Type(SiteEventSource.OfflineTypeId(Guid.NewGuid())), DeviceScope.All, _ => false));
        Assert.True(rig.Owners.Sees(Type(SiteEventSource.AnyOfflineTypeId), group, _ => false)); // counted over the group's sites
        Assert.True(rig.Owners.Sees(Type("trigger", [device]), group, id => id == device));
        Assert.False(rig.Owners.Sees(Type("trigger", [Guid.NewGuid()]), group, id => id == device));
        Assert.True(rig.Owners.Sees(Type(BuiltInEvents.Unreachable), group, _ => false));
        Assert.False(rig.Owners.Sees(Type(BuiltInEvents.Unreachable), DeviceScope.None, _ => true));
        Assert.Equal(rig.Catalog.EventTypes.Count, (await rig.Rules.EventTypesAsync(view, Ct)).Count); // no site, check or trigger here
    }

    /// <summary>An action without devices that declares the address it sends to and checks it again when it sends.</summary>
    private sealed class SendsTo : IActionPlugin
    {
        public const string ActionId = "test.sends-to";

        public List<(string Host, bool Allowed)> Checked { get; } = [];

        public string Id => ActionId;

        public string DisplayName => "Sends to";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Text("host", "Host"), ActionField.Number("port", "Port", 0, 65535, 0) with { Required = false }, ActionField.Text("sendTo", "Send to")];

        public IReadOnlyList<ActionDestination> Destinations(JsonElement settings) =>
            [new ActionDestination(settings.GetProperty("host").GetString()!, settings.TryGetProperty("port", out var port) ? port.GetInt32() : 0)];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            var host = invocation.Get("sendTo") ?? invocation.Get("host") ?? string.Empty;
            var allowed = ctx.IsDestinationAllowed(host, 0);
            Checked.Add((host, allowed));
            return allowed ? Task.CompletedTask : throw new InvalidOperationException(ActionDestination.NotAllowedText);
        }
    }
}
