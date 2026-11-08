using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Events;

/// <summary>The rule events and actions (docs/spec/vision-center.md "Events and the rule actions").</summary>
public sealed class VisionEventTests
{
    private static JsonElement Settings(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task EachEventNodeHasTheTypesOfItsInputInThePipelinesFolder()
    {
        await using var vision = await VisionHarness.StartAsync();
        var changes = 0;
        vision.Plugin.EventTypesChanged += (_, _) => changes++;
        var saved = await vision.SaveAsync(vision.NewPipeline(tail:
        [
            VisionHarness.Node("trk", NodeKinds.Track, "Track"),
            VisionHarness.Node("ev", NodeKinds.Event, "Gate people"),
        ]));
        Assert.True(changes > 0);

        var mine = vision.Plugin.EventTypes.Where(t => t.Id.Contains(saved.Id, StringComparison.Ordinal)).ToList();
        var present = mine.Single(t => t.Id == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.ObjectsPresent));
        Assert.Equal("Gate people", present.DisplayName);
        Assert.Equal(EventKind.Stateful, present.Kind);
        Assert.Equal([VisionInfo.DisplayName, "Gate 1"], present.Path);
        Assert.Equal(vision.Inside.Id, present.FixedDeviceId);
        Assert.Equal(EventValueKind.Number, present.DataFields.Single(f => f.Name == VisionInfo.CountField).Kind);
        Assert.Equal(EventValueKind.Text, present.DataFields.Single(f => f.Name == VisionInfo.ClassesField).Kind);

        var left = mine.Single(t => t.Id == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.ObjectLeft));
        Assert.Equal("Gate people: object left", left.DisplayName);
        Assert.Equal(EventKind.Stateless, left.Kind);
        Assert.Equal(EventValueKind.Number, left.DataFields.Single(f => f.Name == VisionInfo.TimeInsideField).Kind);
        Assert.Contains(mine, t => t.Id == PipelineProblemEvents.TypeIdOf(saved.Id));
        Assert.Contains(vision.Plugin.EventTypes, t => t.Id == VisionInfo.AnyPipelineProblemTypeId);

        await vision.CallAsync(VisionMethods.DeletePipeline, new PipelineRequest { PipelineId = saved.Id });
        Assert.DoesNotContain(vision.Plugin.EventTypes, t => t.Id.Contains(saved.Id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PortKind.Objects, PipelineMode.Watching, false, new[] { VisionEventKind.ObjectsPresent })]
    [InlineData(PortKind.Objects, PipelineMode.OnAction, false, new[] { VisionEventKind.ObjectsFound, VisionEventKind.NoObjectsFound })]
    [InlineData(PortKind.Crossings, PipelineMode.Watching, false, new[] { VisionEventKind.LineCrossed })]
    [InlineData(PortKind.Number, PipelineMode.Watching, false, new[] { VisionEventKind.CountChanged })]
    [InlineData(PortKind.Number, PipelineMode.Watching, true, new[] { VisionEventKind.CountChanged, VisionEventKind.Report })]
    [InlineData(PortKind.Condition, PipelineMode.Watching, false, new[] { VisionEventKind.ConditionHolds })]
    public void TheKindsFollowTheInput(PortKind input, PipelineMode mode, bool limit, VisionEventKind[] kinds) =>
        Assert.Equal(kinds, VisionEventTypes.KindsOf(input, mode, limit));

    [Fact]
    public void CrossingsCarryDirectionClassObjectAndScore()
    {
        var fields = VisionEventTypes.FieldsOf(VisionEventKind.LineCrossed);
        Assert.Equal([VisionInfo.DirectionField, VisionInfo.ClassField, VisionInfo.ObjectField, VisionInfo.ScoreField], fields.Select(f => f.Name));
        Assert.Equal(["In", "Out"], fields[0].Values);
        Assert.Equal(EventValueKind.Number, fields[3].Kind);
        Assert.Equal([VisionInfo.CountField, VisionInfo.InField, VisionInfo.OutField], VisionEventTypes.FieldsOf(VisionEventKind.CountChanged).Select(f => f.Name));
    }

    [Fact]
    public async Task RunPipelineOnActionPublishesItsEventsAndRefusesASecondRun()
    {
        await using var vision = await VisionHarness.StartAsync();
        vision.Evaluators.Emit = (_, _) => [new Emission("ev", VisionEventKind.ObjectsFound, EmissionState.Fired, new Dictionary<string, string> { [VisionInfo.CountField] = "1" })];
        var saved = await vision.SaveAsync(vision.NewPipeline(mode: PipelineMode.OnAction));
        var action = new RunPipelineAction(vision.Plugin);
        var log = new FakeActionContext();
        using var gate = new SemaphoreSlim(0);
        vision.Evaluators.Gate = gate;

        var first = action.ExecuteAsync(log, Invocation(new() { [VisionInfo.PipelineActionField] = saved.Id }), CancellationToken.None);
        await Eventually.TrueAsync(() => vision.Inference.Decoded >= 1, "the first run is in work");
        var second = new FakeActionContext();
        await action.ExecuteAsync(second, Invocation(new() { [VisionInfo.PipelineActionField] = saved.Id }), CancellationToken.None);
        Assert.Equal([PipelineRules.AlreadyRunning], second.Lines);

        gate.Release(5);
        await first;
        Assert.Single(vision.Publisher.Published, e => e.TypeId == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.ObjectsFound) && e.DeviceId == vision.Inside.Id);
        Assert.Equal(["Gate people: single"], log.Lines);
    }

    [Fact]
    public async Task RunPipelineOfAWatchingOneAnswersWithItsFreshFrameElseAPictureOfItsOwn()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        await vision.SendAndWaitAsync(vision.Inside.Id, "live");

        var fresh = await vision.Plugin.RunOnceAsync(saved, CancellationToken.None);
        Assert.Equal("live", fresh.Nodes["ev"].Live);
        Assert.Equal(0, vision.Frames.Singles);

        vision.Time.Advance(TimeSpan.FromSeconds(2));
        var own = await vision.Plugin.RunOnceAsync(saved, CancellationToken.None);
        Assert.Equal("single", own.Nodes["ev"].Live);
        Assert.Equal(1, vision.Frames.Singles);
        Assert.DoesNotContain(vision.Publisher.Published, e => e.TypeId.StartsWith(VisionInfo.PluginId + ".event.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPipelineOfAWatchingOneWaitsForItsFrameInWork()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        using var gate = new SemaphoreSlim(0);
        vision.Evaluators.Gate = gate;
        vision.Frames.Send(vision.Inside.Id, "in work");
        await Eventually.TrueAsync(() => vision.Inference.Decoded >= 1, "the frame is in work");

        var run = vision.Plugin.RunOnceAsync(saved, CancellationToken.None);
        gate.Release(5);
        Assert.Equal("in work", (await run).Nodes["ev"].Live);
        Assert.Equal(0, vision.Frames.Singles);
    }

    [Fact]
    public async Task ActionsSayWhatToChooseAtTheRulesSave()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline(tail: [VisionHarness.Node("cnt", NodeKinds.Count, "People"), VisionHarness.Node("ev", NodeKinds.Event, "Many")]));
        var other = await vision.SaveAsync(vision.NewPipeline("Lane 2", tail: [VisionHarness.Node("cnt", NodeKinds.Count, "Cars")]));
        var run = new RunPipelineAction(vision.Plugin);
        var reset = new ResetCounterAction(vision.Plugin);

        Assert.Equal(VisionInfo.ChoosePipeline, run.Validate(Settings(new { }), false)[VisionInfo.PipelineActionField]);
        Assert.Empty(run.Validate(Settings(new Dictionary<string, string> { [VisionInfo.PipelineActionField] = saved.Id }), false));

        var errors = reset.Validate(Settings(new Dictionary<string, string> { [VisionInfo.PipelineActionField] = saved.Id }), false);
        Assert.Equal(VisionInfo.ChooseCounter, errors[VisionInfo.CounterActionField]);
        errors = reset.Validate(Settings(new Dictionary<string, string> { [VisionInfo.PipelineActionField] = saved.Id, [VisionInfo.CounterActionField] = ResetCounterAction.CounterValue(other.Id, "cnt") }), false);
        Assert.Equal(VisionInfo.ChooseCounter, errors[VisionInfo.CounterActionField]);
        Assert.Empty(reset.Validate(Settings(new Dictionary<string, string> { [VisionInfo.PipelineActionField] = saved.Id, [VisionInfo.CounterActionField] = ResetCounterAction.CounterValue(saved.Id, "cnt") }), false));

        var counters = await reset.ListChoicesAsync(null!, VisionInfo.CounterActionField, CancellationToken.None);
        Assert.Equal(["Gate 1: People", "Lane 2: Cars"], counters.Select(c => c.Label));
    }

    [Fact]
    public async Task ResetCounterResetsTheRunningCountNode()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline(tail: [VisionHarness.Node("cnt", NodeKinds.Count, "People")]));
        await vision.StreamingAsync();
        var log = new FakeActionContext();

        await new ResetCounterAction(vision.Plugin).ExecuteAsync(log, Invocation(new()
        {
            [VisionInfo.PipelineActionField] = saved.Id,
            [VisionInfo.CounterActionField] = ResetCounterAction.CounterValue(saved.Id, "cnt"),
        }), CancellationToken.None);

        Assert.Equal(["cnt"], vision.Evaluators.Of(saved.Id)!.Resets);
        Assert.Equal(["People of Gate 1 is 0 again."], log.Lines);
    }

    [Fact]
    public async Task TheRuntimeNeverResetsACounterByTheClock()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline(tail: [VisionHarness.Node("cnt", NodeKinds.Count, "People", new JsonObject { [NodeKinds.ResetAtKey] = "06:00" })]));
        await vision.StreamingAsync();

        vision.Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(vision.Evaluators.Of(saved.Id)!.Resets); // the Timer node pulses Count's Reset (the evaluator's)
    }

    [Fact]
    public async Task ATimersSettingsRestartThePipelineLikeAnyOther()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(TimedPipeline(vision, "15"));
        await vision.StreamingAsync();
        var first = vision.Evaluators.Of(saved.Id);

        await vision.SaveAsync(TimedPipeline(vision, "30") with { Id = saved.Id });
        Assert.NotSame(first, vision.Evaluators.Of(saved.Id));
    }

    [Fact]
    public async Task AConditionHoldsWhileItsCompareSaysYesAndATimerAddsAReport()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline(tail:
        [
            VisionHarness.Node("cnt", NodeKinds.Count, "People"),
            VisionHarness.Node("cmp", NodeKinds.Compare, "Crowded"),
            VisionHarness.Node("ev", NodeKinds.Event, "Lobby crowded"),
        ]));
        var holds = vision.Plugin.EventTypes.Single(t => t.Id == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.ConditionHolds));
        Assert.Equal("Lobby crowded", holds.DisplayName);
        Assert.Equal(EventKind.Stateful, holds.Kind);
        Assert.Equal([VisionInfo.CountField], holds.DataFields.Select(f => f.Name));
        Assert.DoesNotContain(vision.Plugin.EventTypes, t => t.Id == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.Report));
        await Eventually.TrueAsync(() => vision.Publisher.States.ContainsKey(holds.Id), "the condition's type reported first");

        var timed = await vision.SaveAsync(TimedPipeline(vision, "15") with { Name = "Counted" });
        var report = vision.Plugin.EventTypes.Single(t => t.Id == VisionInfo.EventTypeId(timed.Id, "ev", VisionEventKind.Report));
        Assert.Equal("Counts: report", report.DisplayName);
        Assert.Equal(EventKind.Stateless, report.Kind);
        Assert.Equal([VisionInfo.CountField, VisionInfo.InField, VisionInfo.OutField], report.DataFields.Select(f => f.Name));
        Assert.Contains(vision.Plugin.EventTypes, t => t.Id == VisionInfo.EventTypeId(timed.Id, "ev", VisionEventKind.CountChanged));
    }

    [Theory]
    [InlineData(PortKind.Objects, new[] { VisionInfo.CountField, VisionInfo.ClassesField })]
    [InlineData(PortKind.TrackedObjects, new[] { VisionInfo.CountField, VisionInfo.ClassesField })]
    [InlineData(PortKind.Crossings, new[] { VisionInfo.CountField, VisionInfo.InField, VisionInfo.OutField })]
    [InlineData(PortKind.Condition, new[] { VisionInfo.CountField })]
    public void AReportCarriesTheValuesOfItsInput(PortKind input, string[] fields) =>
        Assert.Equal(fields, VisionEventTypes.FieldsOf(VisionEventKind.Report, input).Select(f => f.Name));

    /// <summary>Camera > detect > count > event "Counts", a timer pulsing the event's Send now.</summary>
    private static Pipeline TimedPipeline(VisionHarness vision, string minutes)
    {
        var pipeline = vision.NewPipeline("Timed", tail:
        [
            VisionHarness.Node("cnt", NodeKinds.Count, "People"),
            VisionHarness.Node("ev", NodeKinds.Event, "Counts"),
        ]);
        var timer = VisionHarness.Node("tmr", NodeKinds.Timer, "Every 15 minutes", new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryMinutes, [NodeKinds.MinutesKey] = int.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture) });
        return pipeline with
        {
            Nodes = [.. pipeline.Nodes, timer],
            Links = [.. pipeline.Links, new PipelineLink("tmr", "ev") { ToPort = NodeKinds.SendPort }],
        };
    }

    private static Oadm.Sdk.Actions.ActionInvocation Invocation(Dictionary<string, string> settings) =>
        new(
            Guid.NewGuid(),
            "Gate opened",
            Oadm.Sdk.Actions.ActionEdge.Fired,
            Settings(settings),
            null,
            new Oadm.Sdk.Actions.ActionTrigger("oadm.test", "Test", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
}
