using Grpc.Core;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups.SharedCore;

/// <summary>EventService, the allowed addresses and the server log for users of a device group.</summary>
public sealed class EventScopeTests
{
    private static RuleDefinition Rule(string name, DeviceSelector? devices = null) => new()
    {
        Name = name,
        Enabled = true,
        Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = devices ?? new DeviceSelector { Mode = DeviceSelectorMode.All } }],
        Actions = [new RuleAction { ActionId = Quiet.ActionId }],
    };

    /// <summary>An action without devices that does nothing (a rule needs one).</summary>
    private sealed class Quiet : Sdk.Actions.IActionPlugin
    {
        public const string ActionId = "test.quiet";

        public string Id => ActionId;

        public string DisplayName => "Quiet";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<Sdk.Actions.ActionField> Fields => [];

        public Task ExecuteAsync(Sdk.Actions.IActionContext ctx, Sdk.Actions.ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;

        public static void Register(TestServerHost host) =>
            host.Get<Core.Plugins.PluginRegistry>().RegisterActionPlugin(new Quiet(), new Core.Plugins.PluginOrigin("test.quiet", "1.0", null) { DisplayName = "Quiet" });
    }

    [Fact]
    public async Task AGroupUserSeesSavesAndTestsOnlyWithinTheGroup()
    {
        await using var host = await TestServerHost.StartAsync();
        Quiet.Register(host);
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(host, 2, "Yard");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var admin = new Proto.EventService.EventServiceClient(host.Invoker);
        var events = new Proto.EventService.EventServiceClient(user.Invoker);
        var theirs = await admin.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Everything").ToJson() });

        var mine = await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Lobby").ToJson() });
        Assert.Equal(user.GroupId.ToString(), mine.Summary.Owner.GroupId);
        Assert.Equal("Group Lobby", mine.Summary.Owner.GroupName);
        Assert.True(theirs.Summary.Owner.AllDevices);
        Assert.Equal([mine.Summary.Id], (await events.ListRulesAsync(new Proto.Empty())).Rules.Select(r => r.Id));
        Assert.Equal(2, (await admin.ListRulesAsync(new Proto.Empty())).Rules.Count);

        foreach (var call in new Func<Task>[]
        {
            async () => await events.GetRuleAsync(new Proto.RuleId { Id = theirs.Summary.Id }),
            async () => await events.DeleteRuleAsync(new Proto.RuleId { Id = theirs.Summary.Id }),
            async () => await events.SetRuleEnabledAsync(new Proto.SetRuleEnabledRequest { Id = theirs.Summary.Id }),
            async () => await events.DuplicateRuleAsync(new Proto.RuleId { Id = theirs.Summary.Id }),
            async () => await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = RuleDefinition.FromJson(theirs.DefinitionJson).ToJson() }),
        })
        {
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(call)).StatusCode);
        }

        var outsideRule = Rule("Outside", new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [inside.Id, outside.Id] });
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = outsideRule.ToJson() }));
        Assert.Equal((StatusCode.NotFound, DeviceMessages.NoLongerAvailable(1)), (refused.StatusCode, refused.Status.Detail));
        var other = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRuleAsync(new Proto.SaveRuleRequest
        {
            DefinitionJson = Rule("Mine").ToJson(), Owner = new Proto.DeviceOwner { AllDevices = true },
        }));
        Assert.Equal(StatusCode.PermissionDenied, other.StatusCode);

        var test = await Assert.ThrowsAsync<RpcException>(async () => await events.TestActionAsync(new Proto.TestActionRequest { ActionId = "x", DeviceId = outside.Id.ToString() }));
        Assert.Equal(StatusCode.NotFound, test.StatusCode);
    }

    [Fact]
    public async Task TheEventLogOfAGroupUserCarriesOnlyItsRulesAndDevices()
    {
        await using var host = await TestServerHost.StartAsync();
        Quiet.Register(host);
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(host, 2, "Yard");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var admin = new Proto.EventService.EventServiceClient(host.Invoker);
        var events = new Proto.EventService.EventServiceClient(user.Invoker);
        await admin.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Everything").ToJson() });
        var mine = await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Lobby").ToJson() });

        foreach (var device in new[] { inside, outside })
        {
            await host.Get<DeviceRepository>().UpdateAsync(device.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        }

        await TestHelpers.WaitUntilAsync(async () => (await admin.ListLogAsync(new Proto.ListEventLogRequest())).Entries.Count(e => e.What == "Rule turned on") == 3, "the rules turned on", TimeSpan.FromSeconds(30));
        var page = await events.ListLogAsync(new Proto.ListEventLogRequest());
        Assert.NotEmpty(page.Entries);
        Assert.Equal(page.Entries.Count, page.TotalCount);
        Assert.All(page.Entries, e => Assert.NotEqual(outside.Id.ToString(), e.DeviceId));
        Assert.All(page.Entries, e => Assert.True(e.RuleId.Length == 0 || e.RuleId == mine.Summary.Id, e.ToString()));
    }

    [Fact]
    public async Task TheAllowedAddressesAreSettingsOfUsersWithAllDevices()
    {
        await using var host = await TestServerHost.StartAsync();
        var settings = host.Settings;
        var saved = await settings.SetAsync(new Proto.ServerSettings { ActionDestinations = new Proto.ActionDestinationList { Entries = { " vms.example.com ", "10.0.0.5:8080", "[fe80::1]:80", "vms.example.com" } } });
        Assert.Equal(["vms.example.com", "10.0.0.5:8080", "[fe80::1]:80"], saved.ActionDestinations.Entries);
        Assert.Equal(saved.ActionDestinations.Entries, (await settings.GetAsync(new Proto.Empty())).ActionDestinations.Entries);
        Assert.True(host.Get<Core.Settings.ActionDestinations>().Allows("10.0.0.5", 8080));
        Assert.False(host.Get<Core.Settings.ActionDestinations>().Allows("10.0.0.5", 80));
        Assert.True(host.Get<Core.Settings.ActionDestinations>().Allows("VMS.example.com", 443));

        // Unset keeps the list; a bad entry changes nothing.
        Assert.Equal(3, (await settings.SetAsync(new Proto.ServerSettings())).ActionDestinations.Entries.Count);
        var bad = await Assert.ThrowsAsync<RpcException>(async () => await settings.SetAsync(new Proto.ServerSettings { ActionDestinations = new Proto.ActionDestinationList { Entries = { "http://x/y" } } }));
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);
        Assert.Equal(3, (await settings.GetAsync(new Proto.Empty())).ActionDestinations.Entries.Count);
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == Core.Auth.AuditActions.SettingsChanged && e.Detail.Contains("10.0.0.5:8080", StringComparison.Ordinal));

        // A user of a group never gets the list and never reads the server log.
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        Assert.Null((await new Proto.SettingsService.SettingsServiceClient(user.Invoker).GetAsync(new Proto.Empty())).ActionDestinations);
        var log = await Assert.ThrowsAsync<RpcException>(async () => await new Proto.ServerLogService.ServerLogServiceClient(user.Invoker).ListAsync(new Proto.ListServerLogRequest()));
        Assert.Equal((StatusCode.PermissionDenied, DeviceGroupTexts.AllDevicesOnly), (log.StatusCode, log.Status.Detail));
    }
}
