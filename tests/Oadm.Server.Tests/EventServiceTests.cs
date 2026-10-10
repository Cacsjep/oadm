using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>gRPC EventService through the whole server: catalog, roles, a built-in event running an action, the log.</summary>
public sealed class EventServiceTests
{
    [Fact]
    public async Task ARuleOnABuiltInEventRunsItsActionAndEverythingIsLoggedAndAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        var action = new NotifyAction();
        host.Get<PluginRegistry>().RegisterActionPlugin(action, new PluginOrigin("test.notify", "1.0", null) { DisplayName = "Notify" });
        var events = new Proto.EventService.EventServiceClient(host.Invoker);
        var device = await host.Get<DeviceRepository>().AddAsync(new Device
        {
            Serial = "ACCC8E000123", Address = "10.0.0.48", Model = "P3265-V", Status = DeviceStatus.Ok, Category = DeviceCategory.Camera,
        }, CancellationToken.None);

        var catalog = await events.GetCatalogAsync(new Proto.Empty());
        Assert.Contains(catalog.EventTypes, t => t.Id == BuiltInEvents.Unreachable && t.Stateful && t.SourceName == "Built in");
        var entry = Assert.Single(catalog.Actions, a => a.Id == action.Id);
        Assert.Equal(Proto.ActionFieldKind.ActionFieldPassword, entry.Fields.Single(f => f.Key == "token").Kind);

        var rule = new RuleDefinition
        {
            Name = "Camera offline",
            Enabled = true,
            Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
            Actions = [new RuleAction { ActionId = action.Id, Settings = new JsonObject { ["message"] = "{device} is {state}", ["token"] = "t0ken" } }],
        };
        var saved = await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = rule.ToJson() });
        Assert.Equal("Camera offline", saved.Summary.Name);
        Assert.DoesNotContain("t0ken", saved.DefinitionJson, StringComparison.Ordinal);
        Assert.Equal(["token"], RuleDefinition.FromJson(saved.DefinitionJson).Actions[0].StoredSecretFields);

        await host.Get<DeviceRepository>().UpdateAsync(device.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(action.Messages.Count == 1), "the action ran", TimeSpan.FromSeconds(10));
        Assert.Equal("10.0.0.48 (P3265-V) is on|t0ken", action.Messages.Single());

        await host.Get<EventLog>().FlushAsync(CancellationToken.None);
        var log = await events.ListLogAsync(new Proto.ListEventLogRequest());
        Assert.Contains(log.Entries, e => e.Kind == Proto.EventLogKind.EventLogEvent && e.What == "Device unreachable");
        Assert.Contains(log.Entries, e => e.Kind == Proto.EventLogKind.EventLogRule && e.What == "Rule turned on");
        Assert.Contains(log.Entries, e => e.Kind == Proto.EventLogKind.EventLogAction && e.Result == Proto.EventLogResult.EventLogOk);
        var list = await events.ListRulesAsync(new Proto.Empty());
        Assert.Equal("On", Assert.Single(list.Rules).StateText);
        Assert.Contains((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.RuleCreated && e.Target == "Camera offline");
    }

    [Fact]
    public async Task OperatorsReadRulesButOnlyAdministratorsChangeThem()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.EventService.EventServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));

        Assert.Empty((await op.ListRulesAsync(new Proto.Empty())).Rules);
        Assert.NotEmpty((await op.GetCatalogAsync(new Proto.Empty())).EventTypes);
        await op.ListLogAsync(new Proto.ListEventLogRequest());
        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await op.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = new RuleDefinition { Name = "x" }.ToJson() }));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Fact]
    public async Task AnIncompleteRuleIsRefusedWithItsFirstProblem()
    {
        await using var host = await TestServerHost.StartAsync();
        var events = new Proto.EventService.EventServiceClient(host.Invoker);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = new RuleDefinition { Name = "Nothing" }.ToJson() }));
        Assert.Equal((StatusCode.InvalidArgument, "The rule needs an event as its first condition."), (refused.StatusCode, refused.Status.Detail));
        var unreadable = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = "{nope" }));
        Assert.Equal(StatusCode.InvalidArgument, unreadable.StatusCode);
    }

    private sealed class NotifyAction : IActionPlugin
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public string Id => "test.notify";

        public string DisplayName => "Notify";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Text("message", "Message", required: true), ActionField.Password("token", "Token")];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            Messages.Enqueue(ctx.Text(invocation.Get("message")) + "|" + invocation.Get("token"));
            return Task.CompletedTask;
        }
    }
}
