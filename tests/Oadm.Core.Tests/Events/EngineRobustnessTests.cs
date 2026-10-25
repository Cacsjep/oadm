using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

/// <summary>Ordering, start-up, persistence and lookup rules of the rule engine and the action runner.</summary>
public sealed class EngineRobustnessTests
{
    [Fact]
    public async Task ADeviceThatDoesNotAnswerNeverDelaysAnotherDeviceAndItsOffWaitsForItsOn()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var slow = await rig.AddDeviceAsync("10.0.0.1");
        var fast = await rig.AddDeviceAsync("10.0.0.2");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()));
        var release = new TaskCompletionSource();
        rig.OnDevice.WaitFor[slow.Id] = release.Task;

        rig.Publisher.SetState(EventRulesRig.Blur, slow.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, fast.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, slow.Id, false);
        await rig.Engine.DrainAsync(CancellationToken.None);

        await WaitUntilAsync(() => rig.OnDevice.Edges.Contains((ActionEdge.TurnedOn, fast.Id)));
        Assert.DoesNotContain(rig.OnDevice.Edges, e => e.Device == slow.Id); // still waiting for the slow device

        release.SetResult();
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Where(e => e.Device == slow.Id).Select(e => e.Edge));
        await WaitUntilAsync(() => rig.Runner.PendingChains == 0); // finished chains are removed
    }

    [Fact]
    public async Task EdgesWithActionsOnChosenDevicesStayInOrder()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var first = await rig.AddDeviceAsync("10.0.0.1");
        var second = await rig.AddDeviceAsync("10.0.0.2");
        var box = await rig.AddDeviceAsync("10.0.0.9");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnDevices(box.Id)));
        rig.OnDevice.Delay = TimeSpan.FromMilliseconds(50);

        rig.Publisher.SetState(EventRulesRig.Blur, first.Id, true); // first on: chosen devices on
        rig.Publisher.SetState(EventRulesRig.Blur, second.Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, first.Id, false);
        rig.Publisher.SetState(EventRulesRig.Blur, second.Id, false); // last off: chosen devices off
        await rig.SettleAsync();

        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)box.Id), (ActionEdge.TurnedOff, box.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task ActionsWaitUntilTheCorePluginsStarted()
    {
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.HoldActions = true);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        await rig.Rules.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice()), CancellationToken.None);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => rig.Runner.PendingChains == 1, "the edge waits for the plugins");

        Assert.Equal(1, Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).ActiveCount); // evaluated
        Assert.Empty(rig.OnDevice.Edges); // but nothing ran yet

        rig.Runner.PluginsStarted();
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task ALateStateOfARemovedDeviceCannotTurnARuleOn()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [camera.Id] };
        await rig.SaveAsync(rule);

        await rig.Devices.RemoveAsync(camera.Id, CancellationToken.None);
        await rig.SettleAsync();
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await rig.SettleAsync();

        Assert.Empty(rig.NoDevice.Runs);
        Assert.Equal(0, Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).ActiveCount);
    }

    [Fact]
    public async Task RuleStatesSurviveAFailedWriteAndAreWrittenAtStop()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var other = await rig.AddDeviceAsync("10.0.0.2");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice()));

        await SqlAsync(rig, "ALTER TABLE EventRuleStates RENAME TO EventRuleStatesAway");
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await rig.SettleAsync(); // the write fails
        await SqlAsync(rig, "ALTER TABLE EventRuleStatesAway RENAME TO EventRuleStates");

        rig.Publisher.SetState(EventRulesRig.Blur, other.Id, true);
        await rig.SettleAsync(); // the next write takes the kept change along
        var states = await rig.Store.LoadStatesAsync(CancellationToken.None);
        Assert.Equal(new[] { camera.Id, other.Id }.Order(), states[rule.Id].Keys.Order());

        // A change whose write fails just before the stop is written by the last flush of the stop.
        await SqlAsync(rig, "ALTER TABLE EventRuleStates RENAME TO EventRuleStatesAway");
        rig.Publisher.SetState(EventRulesRig.Blur, other.Id, false);
        await rig.SettleAsync();
        await SqlAsync(rig, "ALTER TABLE EventRuleStatesAway RENAME TO EventRuleStates");
        await rig.StopEngineAsync();

        await using var db = await rig.DbFactory.CreateDbContextAsync();
        Assert.Equal([camera.Id], await db.EventRuleStates.Where(s => s.RuleId == rule.Id).Select(s => s.SubjectId).ToListAsync());
    }

    [Fact]
    public async Task ARuleChangeIsAppliedEvenWhenTheEventQueueIsFull()
    {
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.QueueCapacity = 4);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice()));

        // Hold the engine loop (status changes are raised on it) and fill its queue.
        var entered = new TaskCompletionSource();
        using var hold = new ManualResetEventSlim(false);
        var armed = 1;
        rig.Engine.StatusChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.SetResult();
                hold.Wait(TimeSpan.FromSeconds(30));
            }
        };
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 10; i++)
        {
            rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, i % 2 == 0);
        }

        var turnOff = rig.Rules.SetEnabledAsync(rule.Id, false, CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(async () => (await rig.Store.GetAsync(rule.Id, CancellationToken.None))?.Definition.Enabled == false, "the change stored");
        // Then it waits for the held engine: a full queue never makes it fail.
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(() => !turnOff.IsFaulted, "the change waits", TimeSpan.FromMilliseconds(300));

        hold.Set();
        var summary = await turnOff.WaitAsync(TimeSpan.FromSeconds(30));
        await rig.SettleAsync();
        Assert.False(summary!.Enabled);
        Assert.False(rig.Engine.Status.ContainsKey(rule.Id)); // the engine applied it
        Assert.Equal(ActionEdge.TurnedOff, rig.NoDevice.Edges[^1].Edge);
    }

    [Fact]
    public async Task ACopyKeepsThePasswordsOfTwoActionsOfOneTypeAndEmptiesUnreadableOnes()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var rule = EventRulesRig.Rule(
            EventRulesRig.Pulse,
            rig.WithoutDevice(new JsonObject { ["text"] = "a", ["secret"] = "one" }),
            rig.WithoutDevice(new JsonObject { ["text"] = "b", ["secret"] = "two" }));
        var original = await rig.SaveAsync(rule);

        var copy = await rig.Rules.DuplicateAsync(original.Id, CancellationToken.None);
        Assert.NotNull(copy);
        Assert.All(copy.Value.Definition.Actions, a => Assert.Equal(["secret"], a.StoredSecretFields));
        await rig.Rules.SetEnabledAsync(original.Id, false, CancellationToken.None);
        await rig.Rules.SetEnabledAsync(copy.Value.Definition.Id, true, CancellationToken.None);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Equal(["a|one", "b|two"], rig.NoDevice.Runs.Select(r => r.Text));

        // A value of an older master key: the copy has the field empty.
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            var row = await db.EventRules.SingleAsync(r => r.Id == original.Id);
            var definition = RuleDefinition.FromJson(row.DefinitionJson);
            definition.Actions[0].Settings["secret"] = RuleStore.SecretPrefix + "not-readable";
            row.DefinitionJson = definition.ToJson();
            await db.SaveChangesAsync();
        }

        var second = await rig.Rules.DuplicateAsync(original.Id, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Empty(second.Value.Definition.Actions[0].StoredSecretFields);
        Assert.Equal(["secret"], second.Value.Definition.Actions[1].StoredSecretFields);
    }

    [Fact]
    public async Task CatalogLookupsUseOneSnapshotUntilTheRegistryChanges()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var catalog = new EventCatalog(rig.Registry);
        Assert.NotNull(catalog.FindEventType(EventRulesRig.Blur));
        var builds = catalog.Builds;
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(catalog.FindEventType(EventRulesRig.Input)!.Available);
            Assert.NotNull(catalog.FindAction(rig.OnDevice.Id));
            Assert.NotNull(catalog.FindEventType(BuiltInEvents.Unreachable));
        }

        Assert.Equal(builds, catalog.Builds);

        rig.Registry.SetEnabled(rig.SourceOrigin.PackageId, false);
        Assert.False(catalog.FindEventType(EventRulesRig.Input)!.Available); // rebuilt after the change
        Assert.Equal(builds + 1, catalog.Builds);

        // A plugin registered later (no registry event) is found by its id.
        var late = new RecordingAction("test.late", needsDevice: false, supportsTurnOff: false);
        rig.Registry.RegisterActionPlugin(late, rig.ActionOrigin);
        Assert.Same(late, catalog.FindAction("test.late")!.Plugin);
    }

    [Fact]
    public async Task ARuleWithAnActionOfAPluginThatIsOffCanBeSaved()
    {
        var refusing = new RefusingAction();
        var origin = new PluginOrigin("test.off", "1.0", null) { DisplayName = "Off package", EnabledByDefault = false };
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterActionPlugin(refusing, origin));
        var rule = EventRulesRig.Rule(EventRulesRig.Pulse, new RuleAction { ActionId = refusing.Id, Settings = new JsonObject { ["command"] = "x" } });

        var saved = await rig.SaveAsync(rule);

        Assert.Equal("Not available: Off package is turned off", Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).Problem);
        Assert.Equal("x", saved.Actions[0].Settings["command"]!.GetValue<string>());

        // Its field checks still apply.
        rule.Actions[0].Settings = [];
        var refused = await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(rule, CancellationToken.None));
        Assert.Contains("Command", refused.Message, StringComparison.Ordinal);
    }

    private static async Task SqlAsync(EventRulesRig rig, string sql)
    {
        await using var db = await rig.DbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out.");
            await Task.Delay(10);
        }
    }

    /// <summary>An action whose own check refuses everything (like Run VAPIX command without its command list).</summary>
    private sealed class RefusingAction : IActionPlugin
    {
        public string Id => "test.refusing";

        public string DisplayName => "Refusing";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public bool SupportsTurnOff => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Text("command", "Command", required: true)];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) =>
            new Dictionary<string, string> { ["command"] = "Unknown command." };

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
    }
}
