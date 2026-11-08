using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Nodes;

public sealed class EventNodeTests
{
    /// <summary>Camera > Detect > [Track] > Event.</summary>
    private static (EvaluatorHarness H, string Event) Objects(JsonObject? settings = null, bool tracked = false, PipelineMode mode = PipelineMode.Watching, double keepLost = 0.5)
    {
        var b = new PipelineBuilder { Mode = mode };
        var camera = b.Add(NodeKinds.Camera);
        var last = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        if (tracked)
        {
            last = b.After(last, NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1, [NodeKinds.KeepLostKey] = keepLost });
        }

        var ev = b.After(last, NodeKinds.Event, settings, name: "Lobby");
        return (new EvaluatorHarness(b.Build()), ev);
    }

    /// <summary>Camera > Detect > Count > Event.</summary>
    private static (EvaluatorHarness H, string Event) Number(JsonObject? settings = null)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var ev = b.After(count, NodeKinds.Event, settings, name: "Queue");
        return (new EvaluatorHarness(b.Build()), ev);
    }

    private static Detection[] People(int n) => [.. Enumerable.Range(0, n).Select(i => Det.Person(0.05 + i * 0.09, 0.9, w: 0.08))];

    [Fact]
    public async Task Objects_present_goes_on_after_At_least_and_off_after_Off_after()
    {
        var (h, ev) = Objects(); // defaults: Minimum objects 1, At least 1 s, Off after 2 s
        h.Inference.Always = [Det.Person(0.5, 0.9)];
        for (var t = 0.0; t < 1.0; t += 0.25)
        {
            var r = await h.FrameAsync(t);
            Assert.Empty(r.Emissions);
            Assert.Equal("Off", r.Nodes[ev].Live);
            Assert.False(r.Nodes[ev].On);
        }

        var on = await h.FrameAsync(1.0);
        var edge = Assert.Single(on.Emissions);
        Assert.Equal((VisionEventKind.ObjectsPresent, EmissionState.On), (edge.Kind, edge.State));
        Assert.Equal("1", edge.Values[VisionInfo.CountField]);
        Assert.Equal("person", edge.Values[VisionInfo.ClassesField]);
        Assert.Equal("On", on.Nodes[ev].Live);
        Assert.True(on.Nodes[ev].On);

        // Gone: on until 2 s without objects, a single frame with one in between starts that again.
        h.Inference.Always = [];
        await h.FrameAsync(2.0);
        h.Inference.Enqueue(Det.Person(0.5, 0.9));
        await h.FrameAsync(3.0);
        Assert.Empty((await h.FrameAsync(4.0)).Emissions);
        Assert.Empty((await h.FrameAsync(5.9)).Emissions);
        Assert.Single(h.Evaluator.CurrentStates);
        var off = Assert.Single((await h.FrameAsync(6.0)).Emissions);
        Assert.Equal(EmissionState.Off, off.State);
        Assert.Equal("0", off.Values[VisionInfo.CountField]);
        Assert.Empty(h.Evaluator.CurrentStates);
        Assert.Equal("Fired 5 s ago", (await h.FrameAsync(6.0)).Nodes[ev].Live);
    }

    [Fact]
    public async Task A_short_presence_never_turns_it_on()
    {
        var (h, _) = Objects();
        await h.RunAsync(0, Det.Person(0.5, 0.9));
        await h.RunAsync(0.5, Det.Person(0.5, 0.9));
        await h.RunAsync(0.75);
        await h.RunAsync(1.5, Det.Person(0.5, 0.9));
        Assert.Empty(h.Emitted);
    }

    [Fact]
    public async Task Minimum_objects_counts_the_objects_and_the_values_follow_while_on()
    {
        var (h, ev) = Objects(new JsonObject { [NodeKinds.MinObjectsKey] = 3, [NodeKinds.AtLeastKey] = 0 });
        Assert.Empty((await h.RunAsync(0, People(2))).Emissions);
        var on = Assert.Single((await h.RunAsync(0.5, [.. People(3), Det.Car(0.5, 0.5)])).Emissions);
        Assert.Equal("4", on.Values[VisionInfo.CountField]);
        Assert.Equal("car person", on.Values[VisionInfo.ClassesField]);

        await h.RunAsync(1, People(5));
        var state = Assert.Single(h.Evaluator.CurrentStates);
        Assert.Equal((ev, VisionEventKind.ObjectsPresent, EmissionState.On), (state.NodeId, state.Kind, state.State));
        Assert.Equal("5", state.Values[VisionInfo.CountField]);
        Assert.Equal("person", state.Values[VisionInfo.ClassesField]);
    }

    [Fact]
    public async Task Tracked_objects_fire_entered_and_left_with_their_time()
    {
        var (h, _) = Objects(new JsonObject { [NodeKinds.AtLeastKey] = 0, [NodeKinds.OffAfterKey] = 0 }, tracked: true, keepLost: 0.5);
        await h.RunAsync(0, Det.Person(0.5, 0.9));
        await h.RunAsync(1, Det.Person(0.51, 0.9), Det.Car(0.1, 0.4));
        await h.RunAsync(1.25, Det.Car(0.1, 0.4)); // the person is lost for 0.25 s: kept
        await h.RunAsync(2, Det.Car(0.1, 0.4)); // lost for 1 s: left

        var entered = h.Of(VisionEventKind.ObjectEntered).ToList();
        Assert.Equal(["1", "2"], entered.Select(e => e.Values[VisionInfo.ObjectField]));
        Assert.Equal(["person", "car"], entered.Select(e => e.Values[VisionInfo.ClassField]));
        Assert.All(entered, e => Assert.Equal("0", e.Values[VisionInfo.TimeInsideField]));
        var left = Assert.Single(h.Of(VisionEventKind.ObjectLeft));
        Assert.Equal("1", left.Values[VisionInfo.ObjectField]);
        Assert.Equal("person", left.Values[VisionInfo.ClassField]);
        Assert.Equal("1", left.Values[VisionInfo.TimeInsideField]);
        Assert.Equal(2, h.Emitted.Single(e => e.Emission.Kind == VisionEventKind.ObjectLeft).At);

        // Objects present stayed on: the car is there.
        Assert.Single(h.Of(VisionEventKind.ObjectsPresent));
    }

    [Fact]
    public async Task On_action_fires_objects_found_or_no_objects_found_once_per_run()
    {
        var (h, ev) = Objects(new JsonObject { [NodeKinds.MinObjectsKey] = 2 }, mode: PipelineMode.OnAction);
        var found = Assert.Single((await h.RunAsync(0, [.. People(2), Det.Car(0.5, 0.5)])).Emissions);
        Assert.Equal((VisionEventKind.ObjectsFound, EmissionState.Fired), (found.Kind, found.State));
        Assert.Equal("3", found.Values[VisionInfo.CountField]);
        Assert.Equal("car person", found.Values[VisionInfo.ClassesField]);

        var none = await h.RunAsync(10, People(1));
        Assert.Equal(VisionEventKind.NoObjectsFound, Assert.Single(none.Emissions).Kind);
        Assert.Equal("Fired now", none.Nodes[ev].Live);
        Assert.Null(none.Nodes[ev].On);
        Assert.Empty(h.Evaluator.CurrentStates);
        Assert.Equal(VisionEventKind.ObjectsFound, Assert.Single((await h.RunAsync(11, People(4))).Emissions).Kind);
    }

    [Fact]
    public async Task Count_changed_fires_at_most_once_a_second_with_the_latest_value()
    {
        var (h, ev) = Number();
        Assert.Empty((await h.RunAsync(0)).Emissions); // a count starts at 0
        var first = Assert.Single((await h.RunAsync(0.1, People(1))).Emissions);
        Assert.Equal((VisionEventKind.CountChanged, "1"), (first.Kind, first.Values[VisionInfo.CountField]));
        Assert.False(first.Values.ContainsKey(VisionInfo.InField));

        Assert.Empty((await h.RunAsync(0.4, People(2))).Emissions);
        Assert.Empty((await h.RunAsync(0.8, People(3))).Emissions);
        var latest = Assert.Single((await h.RunAsync(1.1, People(4))).Emissions);
        Assert.Equal("4", latest.Values[VisionInfo.CountField]);
        Assert.Equal("Fired now", (await h.RunAsync(1.2, People(4))).Nodes[ev].Live);

        // Back to the value last sent before a second passed: nothing changed.
        await h.RunAsync(1.5, People(2));
        Assert.Empty((await h.RunAsync(1.9, People(4))).Emissions);
        Assert.Empty((await h.RunAsync(2.5, People(4))).Emissions);
        Assert.Equal(2, h.Of(VisionEventKind.CountChanged).Count());
        Assert.Equal("Fired 2 s ago", (await h.RunAsync(3.1, People(4))).Nodes[ev].Live);
    }

    [Fact]
    public async Task A_stateless_event_that_never_fired_shows_nothing()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var track = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track);
        var ev = b.After(b.After(track, NodeKinds.Line, PipelineBuilder.Shape((0.5, 0), (0.5, 1))), NodeKinds.Event);
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0, Det.Person(0.2, 0.9));
        Assert.Equal(string.Empty, r.Nodes[ev].Live);
        Assert.Null(r.Nodes[ev].On);
    }
}
