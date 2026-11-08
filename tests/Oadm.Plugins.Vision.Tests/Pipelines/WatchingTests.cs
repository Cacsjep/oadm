using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>Watching pipelines (docs/spec/vision-center.md "Running", "Load"): frame order, drops, emissions, problems, limits, stop.</summary>
public sealed class WatchingTests
{
    [Fact]
    public async Task AFrameWithoutRoomIsDroppedAndTheNewestWins()
    {
        await using var vision = await VisionHarness.StartAsync();
        using var gate = new SemaphoreSlim(0);
        vision.Evaluators.Gate = gate;
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        vision.Frames.Send(vision.Inside.Id, "A");
        await Eventually.TrueAsync(() => vision.Inference.Decoded >= 1, "the first frame is in work");
        vision.Frames.Send(vision.Inside.Id, "B");
        await Eventually.TrueAsync(() => vision.Frames.Taken >= 2, "the second frame waits in the slot");
        vision.Frames.Send(vision.Inside.Id, "C");
        await Eventually.TrueAsync(() => vision.Frames.Taken >= 3, "the third frame replaced it");

        gate.Release(10);
        await Eventually.TrueAsync(() => vision.Evaluators.Of(saved.Id)!.Frames.Count >= 2, "the frames ran");
        Assert.Equal(["A", "C"], vision.Evaluators.Of(saved.Id)!.Frames);
    }

    [Fact]
    public async Task WithRoomSeveralFramesAreInWorkAndRunInTheirOrder()
    {
        await using var vision = await VisionHarness.StartAsync();
        vision.Inference.HasRoom = true;
        using var gate = new SemaphoreSlim(0);
        vision.Evaluators.Gate = gate;
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        var sent = 0;
        foreach (var text in new[] { "A", "B", "C", "D" })
        {
            vision.Frames.Send(vision.Inside.Id, text);
            var count = ++sent;
            await Eventually.TrueAsync(() => vision.Inference.Decoded >= count, "the frame is in work beside the others");
        }
        gate.Release(10);
        await Eventually.TrueAsync(() => vision.Runtime.DeliveredFrames >= 4, "the frames were delivered");
        var evaluator = vision.Evaluators.Of(saved.Id)!;
        Assert.Equal(["A", "B", "C", "D"], evaluator.Frames);
        Assert.Equal(1, evaluator.MostAtOnce); // the evaluator takes one frame at a time
    }

    [Fact]
    public async Task EmissionsArePublishedOnTheCameraAndStatefulTypesReportFirst()
    {
        await using var vision = await VisionHarness.StartAsync();
        vision.Evaluators.Emit = (_, text) => text switch
        {
            "on" => [new Emission("ev", VisionEventKind.ObjectsPresent, EmissionState.On, new Dictionary<string, string> { [VisionInfo.CountField] = "2" })],
            "off" => [new Emission("ev", VisionEventKind.ObjectsPresent, EmissionState.Off, new Dictionary<string, string> { [VisionInfo.CountField] = "0" })],
            _ => [],
        };
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        var type = VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.ObjectsPresent);
        Assert.True(vision.Publisher.States.TryGetValue(type, out var ready) && ready.Count == 0, "the full report came first");

        await vision.SendAndWaitAsync(vision.Inside.Id, "on");
        await vision.SendAndWaitAsync(vision.Inside.Id, "off");
        var edges = vision.Publisher.Published.Where(e => e.TypeId == type).ToList();
        Assert.Equal([true, false], edges.Select(e => e.IsOn));
        Assert.All(edges, e => Assert.Equal(vision.Inside.Id, e.DeviceId));
        Assert.Equal("2", edges[0].Data[VisionInfo.CountField]);
    }

    [Fact]
    public async Task FailingModelsAreAProblemAfterThreeFramesUntilOneWorks()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        await vision.SendAndWaitAsync(vision.Inside.Id, "fail");
        await vision.SendAndWaitAsync(vision.Inside.Id, "throw");
        Assert.Null(vision.Runtime.ProblemOf(saved.Id));
        await vision.SendAndWaitAsync(vision.Inside.Id, "fail");
        Assert.Equal(PipelineRules.ModelsFail, vision.Runtime.ProblemOf(saved.Id));
        Assert.Contains(vision.Publisher.Published, e => e.TypeId == Oadm.Plugins.Vision.Events.PipelineProblemEvents.TypeIdOf(saved.Id) && e.IsOn == true && e.Data[VisionInfo.ProblemField] == PipelineRules.ModelsFail);

        await vision.SendAndWaitAsync(vision.Inside.Id, "fine");
        Assert.Null(vision.Runtime.ProblemOf(saved.Id));
    }

    [Fact]
    public async Task TheStreamsProblemShowsInTheList()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        vision.Frames.Tell(vision.Inside.Id, "The camera refuses the stream.");
        Assert.Equal("The camera refuses the stream.", (await vision.ListAsync()).Pipelines.Single().Problem);
        vision.Frames.Tell(vision.Inside.Id, null);
        Assert.Null(vision.Runtime.ProblemOf(saved.Id));
    }

    [Fact]
    public async Task APipelineWhoseCheckFailsNeverRuns()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        // The model goes (a restore without its file): the check fails, the loop stops, the node's error is the problem.
        await vision.Models.DeleteAsync("m1", [], CancellationToken.None);
        await Eventually.TrueAsync(() => vision.Frames.Open == 0, "the stream stopped");
        await Eventually.TrueAsync(() => vision.Runtime.ProblemOf(saved.Id) == "Choose a model.", "the node's error is the problem");
    }

    [Fact]
    public async Task WatchingIsLimitedByTheCoresAndThoseBeyondSayWhy()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 2);
        Assert.Equal(4, vision.Runtime.MaxWatching);
        var cameras = Enumerable.Range(0, 5).Select(i => vision.Context.Add(new FakeDevice { Address = "10.0.1." + i })).ToList();
        for (var i = 0; i < 4; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, device: cameras[i].Id));
        }

        var reply = await vision.TrySaveAsync(vision.NewPipeline("P4", device: cameras[4].Id));
        Assert.Equal(PipelineRules.TooManyWatching(4), reply.Errors[PipelineFields.Mode]);

        // Stored beyond the limit (another server with more cores): it reads nothing and says why.
        var extra = vision.NewPipeline("P4", device: cameras[4].Id) with { Id = new string('a', 32) };
        await vision.Context.PluginRecords.SetManyAsync(PipelineStore.Collection, [new(extra.Id, VisionJson.Serialize(extra with { Id = string.Empty }))], CancellationToken.None);
        await vision.Plugin.StopAsync(CancellationToken.None);
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);
        await vision.StreamingAsync(4);
        Assert.Equal(4, vision.Runtime.Watching.Count);
        Assert.Equal(PipelineRules.TooManyWatching(4), vision.Runtime.ProblemOf(vision.Plugin.AllPipelines.Single(p => !vision.Runtime.Watching.Contains(p.Id)).Id));
    }

    [Fact]
    public async Task AtMostFourWatchAtOneSite()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 32);
        var site = Guid.NewGuid();
        var cameras = Enumerable.Range(0, 5).Select(i => vision.Context.Add(new FakeDevice { Address = "10.0.2." + i, SiteId = site, SiteName = "Building B" })).ToList();
        for (var i = 0; i < 4; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, device: cameras[i].Id));
        }

        var reply = await vision.TrySaveAsync(vision.NewPipeline("P4", device: cameras[4].Id));
        Assert.Equal(PipelineRules.TooManyWatchingAtSite("Building B"), reply.Errors[PipelineFields.Mode]);
        Assert.NotNull(await vision.SaveAsync(vision.NewPipeline("P4", PipelineMode.OnAction, cameras[4].Id)));
    }

    [Fact]
    public async Task PipelinesWaitWhileTheCardIsTested()
    {
        await using var vision = await VisionHarness.StartAsync(startingCard: true);
        var saved = await vision.SaveAsync(vision.NewPipeline());
        Assert.Equal(PipelineRules.Starting, vision.Runtime.ProblemOf(saved.Id));
        Assert.Equal(0, vision.Frames.Opened);

        vision.CardTest.SetResult();
        await vision.StreamingAsync();
        await Eventually.TrueAsync(() => vision.Runtime.ProblemOf(saved.Id) is null, "the pipeline runs");
    }

    [Fact]
    public async Task ANewNameGoesOnAndAChangedGraphStartsAgain()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        var first = vision.Evaluators.Of(saved.Id);

        saved = await vision.SaveAsync(saved with { Name = "Gate renamed", Nodes = [.. saved.Nodes.Select(n => n with { X = n.X + 40 })] });
        Assert.Same(first, vision.Evaluators.Of(saved.Id));

        var changed = saved with { Nodes = [.. saved.Nodes.Select(n => n.Id == "det" ? n with { Settings = new JsonObject { [NodeKinds.ModelKey] = "m1", [NodeKinds.MinScoreKey] = 70 } } : n)] };
        await vision.SaveAsync(changed);
        Assert.NotSame(first, vision.Evaluators.Of(saved.Id));
        Assert.Single(vision.Runtime.Watching);
    }

    [Fact]
    public async Task AGroupThatLosesTheCameraStopsItsPipeline()
    {
        await using var vision = await VisionHarness.StartAsync();
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        var saved = await vision.SaveAsync(vision.NewPipeline(), group);
        await vision.StreamingAsync();

        vision.Context.FakeScopes.Delete(key);
        await Eventually.TrueAsync(() => vision.Frames.Open == 0, "the stream stopped");
        await Eventually.TrueAsync(() => vision.Runtime.ProblemOf(saved.Id) == PipelineRules.Off, "the pipeline is off");
    }

    [Fact]
    public async Task AStopCancelsTheFrameInWorkAndWaitsForIt()
    {
        var vision = await VisionHarness.StartAsync();
        using var gate = new SemaphoreSlim(0);
        vision.Evaluators.Gate = gate;
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        vision.Frames.Send(vision.Inside.Id, "A");
        await Eventually.TrueAsync(() => vision.Inference.Decoded >= 1, "the frame is in work");

        await vision.Plugin.StopAsync(CancellationToken.None);
        var evaluator = vision.Evaluators.Of(saved.Id)!;
        Assert.True(evaluator.Cancelled);
        Assert.Empty(evaluator.Frames);
        Assert.Equal(0, vision.Frames.Open);
        await vision.DisposeAsync();
    }

    [Fact]
    public async Task TheListShowsTheLiveLineOfTheFirstCounter()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline(tail: [VisionHarness.Node("cnt", NodeKinds.Count, "People"), VisionHarness.Node("ev", NodeKinds.Event, "Many")]));
        await vision.StreamingAsync();
        await vision.SendAndWaitAsync(vision.Inside.Id, "In 3 · Out 1");

        var row = (await vision.ListAsync()).Pipelines.Single();
        Assert.Equal("In 3 · Out 1", row.Last);
        Assert.Equal(saved.Id, row.Id);

        vision.Time.Advance(VisionLimits.LiveEvery);
        await Eventually.TrueAsync(() => vision.Context.PublishedEvents.Any(e => e.Topic == VisionMethods.PipelinesTopic && e.PayloadJson!.Contains("In 3", StringComparison.Ordinal)), "the pages heard of it");
    }
}

/// <summary>The runtime over the real graph check and evaluator (the fakes only for the models, pictures and inference).</summary>
public sealed class RealGraphTests
{
    [Fact]
    public async Task ASavedPipelineRunsThroughTheRealCheckAndEvaluator()
    {
        await using var vision = new VisionHarness(real: true);
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);
        var saved = await vision.SaveAsync(vision.NewPipeline(tail:
        [
            VisionHarness.Node("cnt", NodeKinds.Count, "People"),
            VisionHarness.Node("ev", NodeKinds.Event, "Many"),
        ]));
        Assert.Contains(vision.Plugin.EventTypes, t => t.Id == VisionInfo.EventTypeId(saved.Id, "ev", VisionEventKind.CountChanged));
        await vision.StreamingAsync();

        await vision.SendAndWaitAsync(vision.Inside.Id, "picture");
        Assert.Null(vision.Runtime.ProblemOf(saved.Id));
        Assert.NotNull(vision.Runtime.LastOf(saved.Id)?.Result);
    }
}
