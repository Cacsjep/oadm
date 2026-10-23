using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Events;

/// <summary>Event types that change while the server runs (<see cref="IDynamicEventSource"/>, e.g. device triggers).</summary>
public sealed class DynamicEventTypesTests
{
    private const string TriggerType = "test.dynamic.trigger";

    [Fact]
    public async Task ANewTypeWaitsForItsFirstReportThenRuns()
    {
        var source = new DynamicSource();
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterCorePlugin(source, new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        var camera = await rig.AddDeviceAsync("10.0.0.1");

        source.Set(new EventTypeInfo(TriggerType, "Virtual input 3", "Device triggers", EventKind.Stateful));
        await rig.SettleAsync();
        Assert.NotNull(rig.Catalog.FindEventType(TriggerType));
        await rig.SaveAsync(EventRulesRig.Rule(TriggerType, rig.OnEventDevice()));
        Assert.StartsWith("Waiting for Dynamic", Assert.Single(await rig.Rules.ListAsync(rig.AllView, CancellationToken.None)).StateText, StringComparison.Ordinal);

        var publisher = rig.Engine.PublisherFor(source.Id);
        publisher.ReplaceStates(TriggerType, [camera.Id]);
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task ARemovedTypeTurnsItsRulesOff()
    {
        var source = new DynamicSource(new EventTypeInfo(TriggerType, "Virtual input 3", "Device triggers", EventKind.Stateful));
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterCorePlugin(source, new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Engine.PublisherFor(source.Id).ReplaceStates(TriggerType, [camera.Id]);
        await rig.SaveAsync(EventRulesRig.Rule(TriggerType, rig.OnEventDevice()));
        Assert.Single(rig.OnDevice.Edges);

        source.Set();
        await rig.SettleAsync();

        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Select(e => e.Edge));
        Assert.Equal("Not available: an event of this rule no longer exists", Assert.Single(await rig.Rules.ListAsync(rig.AllView, CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task ATypeDeclaredAfterTheEngineStartedKeepsTheStoredStateUntilItReports()
    {
        var folder = Persistence.TestDatabase.NewTempDirectory();
        var declared = new DynamicSource(new EventTypeInfo(TriggerType, "Virtual input 3", "Device triggers", EventKind.Stateful));
        Guid camera;
        var first = await EventRulesRig.StartAsync(folder, r => r.Registry.RegisterCorePlugin(declared, new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        camera = (await first.AddDeviceAsync("10.0.0.1")).Id;
        first.Engine.PublisherFor(declared.Id).ReplaceStates(TriggerType, [camera]);
        await first.SaveAsync(EventRulesRig.Rule(TriggerType, first.OnEventDevice()));
        await first.CloseKeepDataAsync();

        // After the restart the plugin declares its types only once it runs (it reads its triggers on start).
        var late = new DynamicSource();
        await using var second = await EventRulesRig.StartAsync(folder, r => r.Registry.RegisterCorePlugin(late, new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        await second.SettleAsync();
        Assert.Equal("Waiting for its events", Assert.Single(await second.Rules.ListAsync(second.AllView, CancellationToken.None)).StateText);

        late.Set(new EventTypeInfo(TriggerType, "Virtual input 3", "Device triggers", EventKind.Stateful));
        second.Engine.PublisherFor(late.Id).ReplaceStates(TriggerType, [camera]);
        await second.SettleAsync();
        Assert.Empty(second.OnDevice.Edges); // still on: nothing to do
        Assert.Equal("On", Assert.Single(await second.Rules.ListAsync(second.AllView, CancellationToken.None)).StateText);
    }

    [Fact]
    public async Task ARuleWhoseEventNeverComesBackTurnsOffAfterTheWait()
    {
        var folder = Persistence.TestDatabase.NewTempDirectory();
        var declared = new DynamicSource(new EventTypeInfo(TriggerType, "Virtual input 3", "Device triggers", EventKind.Stateful));
        var first = await EventRulesRig.StartAsync(folder, r => r.Registry.RegisterCorePlugin(declared, new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        var camera = (await first.AddDeviceAsync("10.0.0.1")).Id;
        first.Engine.PublisherFor(declared.Id).ReplaceStates(TriggerType, [camera]);
        await first.SaveAsync(EventRulesRig.Rule(TriggerType, first.OnEventDevice()));
        await first.CloseKeepDataAsync();

        await using var second = await EventRulesRig.StartAsync(folder, r => r.Registry.RegisterCorePlugin(new DynamicSource(), new PluginOrigin("test.dynamic", "1.0", null) { DisplayName = "Dynamic" }));
        second.Time.Advance(TimeSpan.FromMinutes(2));
        second.Engine.Tick();
        await second.SettleAsync();

        Assert.Equal([(ActionEdge.TurnedOff, (Guid?)camera)], second.OnDevice.Edges);
    }

    [Fact]
    public async Task EventValuesAreActionPlaceholders()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Input, rig.WithoutDevice(new JsonObject { ["text"] = "{device} port {port} is {state}, {missing}" })));

        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true, new Dictionary<string, string> { ["port"] = "3", ["state"] = "1" });
        await rig.SettleAsync();

        Assert.Equal("10.0.0.1 (P3265-V) port 3 is on, {missing}|", Assert.Single(rig.NoDevice.Runs).Text); // fixed names win
    }

    private sealed class DynamicSource(params EventTypeInfo[] types) : ICorePlugin, IDynamicEventSource
    {
        private IReadOnlyList<EventTypeInfo> _types = types;

        public event EventHandler? EventTypesChanged;

        public string Id => "test.dynamic-source";

        public string DisplayName => "Dynamic";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public IReadOnlyList<EventTypeInfo> EventTypes => _types;

        public void Set(params EventTypeInfo[] types)
        {
            _types = types;
            EventTypesChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
