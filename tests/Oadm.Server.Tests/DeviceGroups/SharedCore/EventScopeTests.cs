using Grpc.Core;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Events;
using Oadm.Sdk.Events;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups.SharedCore;

/// <summary>
/// Event rules are for users with all devices (docs/spec/device-scope.md, decision 2026-10-23): every EventService method
/// refuses a user of a device group, rules have no owner and a deleted group changes no rule.
/// </summary>
public sealed class EventScopeTests
{
    private static RuleDefinition Rule(string name) => new()
    {
        Name = name,
        Enabled = true,
        Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
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
    public async Task AGroupUserGetsNoEventRulesAndAUserWithAllDevicesSeesEveryRule()
    {
        await using var host = await TestServerHost.StartAsync();
        Quiet.Register(host);
        var device = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var admin = new Proto.EventService.EventServiceClient(host.Invoker);
        var rule = await admin.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Everything").ToJson() });
        var events = new Proto.EventService.EventServiceClient(user.Invoker);
        var id = new Proto.RuleId { Id = rule.Summary.Id };

        foreach (var call in new Func<Task>[]
        {
            async () => await events.GetCatalogAsync(new Proto.Empty()),
            async () => await events.ListRulesAsync(new Proto.Empty()),
            async () =>
            {
                using var watch = events.WatchRules(new Proto.Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
                await watch.ResponseStream.MoveNext(CancellationToken.None);
            },
            async () => await events.GetRuleAsync(id),
            async () => await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Lobby").ToJson() }),
            async () => await events.DeleteRuleAsync(id),
            async () => await events.SetRuleEnabledAsync(new Proto.SetRuleEnabledRequest { Id = rule.Summary.Id }),
            async () => await events.DuplicateRuleAsync(id),
            async () => await events.ListActionChoicesAsync(new Proto.ActionChoicesRequest { ActionId = Quiet.ActionId, FieldKey = "x" }),
            async () => await events.TestActionAsync(new Proto.TestActionRequest { ActionId = Quiet.ActionId, DeviceId = device.Id.ToString() }),
            async () => await events.ListLogAsync(new Proto.ListEventLogRequest()),
            async () =>
            {
                using var watch = events.WatchLog(new Proto.Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
                await watch.ResponseStream.MoveNext(CancellationToken.None);
            },
            async () => await events.ClearLogAsync(new Proto.Empty()),
        })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal((StatusCode.PermissionDenied, DeviceGroupTexts.AllDevicesOnly), (refused.StatusCode, refused.Status.Detail));
        }

        Assert.Single(await host.Get<EventRulesService>().ListAsync(CancellationToken.None)); // nothing changed

        // A user with all devices (not an administrator) sees the administrator's rule as before device groups.
        var all = new Proto.EventService.EventServiceClient(await GroupCallers.AllDevicesUserAsync(host, "tech"));
        Assert.Equal([rule.Summary.Id], (await all.ListRulesAsync(new Proto.Empty())).Rules.Select(r => r.Id));
    }

    [Fact]
    public async Task DeletingADeviceGroupChangesNoRule()
    {
        await using var host = await TestServerHost.StartAsync();
        Quiet.Register(host);
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var admin = new Proto.EventService.EventServiceClient(host.Invoker);
        var rule = await admin.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Everything").ToJson() });

        var deleted = await new Proto.DeviceGroupService.DeviceGroupServiceClient(host.Invoker).DeleteAsync(new Proto.DeviceGroupIdRequest { Id = user.GroupId.ToString() });

        Assert.DoesNotContain(deleted.Disabled, d => d.Contains("event rule", StringComparison.Ordinal));
        var after = Assert.Single((await admin.ListRulesAsync(new Proto.Empty())).Rules);
        Assert.Equal(rule.Summary.Id, after.Id);
        Assert.True(after.Enabled);
    }
}
