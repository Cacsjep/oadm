using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.Tracking;

namespace Oadm.Plugins.Vision.Tests.Nodes;

public sealed class PipelineEvaluatorTests
{
    private static JsonObject VerticalLine() => PipelineBuilder.Shape((0.5, 0.0), (0.5, 1.0));

    private static JsonObject HorizontalLine(string directions = NodeKinds.DirectionsBoth)
    {
        var o = PipelineBuilder.Shape((0.0, 0.6), (1.0, 0.6));
        o[NodeKinds.DirectionsKey] = directions;
        return o;
    }

    /// <summary>Camera > Detect > Filter (person) > Track > Line > Count > Event.</summary>
    private static (Pipeline P, Ids Ids) Gate(JsonObject line, JsonObject? track = null)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("person") });
        var tr = b.After(filter, NodeKinds.Track, track);
        var ln = b.After(tr, NodeKinds.Line, line);
        var count = b.After(ln, NodeKinds.Count);
        var crossings = b.After(ln, NodeKinds.Event, name: "Crossed");
        var ev = b.After(count, NodeKinds.Event, name: "People");
        return (b.Build(), new Ids(camera, detect, filter, tr, ln, count, crossings, ev));
    }

    private sealed record Ids(string Camera, string Detect, string Filter, string Track, string Line, string Count, string Crossed, string Number);

    [Fact]
    public async Task Two_people_crossing_count_once_each_with_their_directions()
    {
        var (p, ids) = Gate(VerticalLine());
        var h = new EvaluatorHarness(p);
        var last = await h.RunAsync(TrackFixture.Load("two-people-crossing.json"));

        // Drawn from top to bottom, the line's right side is the picture's left: A (left to right) goes Out, B comes In.
        Assert.Equal(1, last.Nodes[ids.Line].In);
        Assert.Equal(1, last.Nodes[ids.Line].Out);
        Assert.Equal("In 1 · Out 1", last.Nodes[ids.Line].Live);
        Assert.Equal(1, last.Nodes[ids.Count].Value);
        var crossed = h.Of(VisionEventKind.LineCrossed).ToList();
        Assert.Equal(2, crossed.Count);
        Assert.All(crossed, c => Assert.Equal(EmissionState.Fired, c.State));
        Assert.Equal(["In", "Out"], crossed.Select(c => c.Values[VisionInfo.DirectionField]).Order());
        Assert.All(crossed, c => Assert.Equal("person", c.Values[VisionInfo.ClassField]));
        Assert.Equal(["1", "2"], crossed.Select(c => c.Values[VisionInfo.ObjectField]).Order());
        Assert.All(crossed, c => Assert.InRange(int.Parse(c.Values[VisionInfo.ScoreField], System.Globalization.CultureInfo.InvariantCulture), 70, 99));
    }

    [Fact]
    public async Task A_crossing_while_hidden_counts_once_when_the_object_comes_back()
    {
        var (p, ids) = Gate(VerticalLine());
        var h = new EvaluatorHarness(p);
        var last = await h.RunAsync(TrackFixture.Load("short-occlusion.json"));
        Assert.Equal(0, last.Nodes[ids.Line].In);
        Assert.Equal(1, last.Nodes[ids.Line].Out);
        Assert.Single(h.Of(VisionEventKind.LineCrossed));
    }

    [Fact]
    public async Task Back_and_forth_counts_In_and_Out()
    {
        var (p, ids) = Gate(HorizontalLine());
        var h = new EvaluatorHarness(p);
        var last = await h.RunAsync(TrackFixture.Load("back-and-forth.json"));

        // Drawn from left to right: walking down is In, back up is Out.
        Assert.Equal(1, last.Nodes[ids.Line].In);
        Assert.Equal(1, last.Nodes[ids.Line].Out);
        Assert.Equal(["In", "Out"], h.Of(VisionEventKind.LineCrossed).Select(c => c.Values[VisionInfo.DirectionField]));
        var changed = h.Of(VisionEventKind.CountChanged).Last();
        Assert.Equal("1", changed.Values[VisionInfo.CountField]);
        Assert.Equal("1", changed.Values[VisionInfo.InField]);
        Assert.Equal("1", changed.Values[VisionInfo.OutField]);
    }

    [Fact]
    public async Task A_line_for_In_only_counts_and_shows_In_only()
    {
        var (p, ids) = Gate(HorizontalLine(NodeKinds.DirectionsIn));
        var h = new EvaluatorHarness(p);
        var last = await h.RunAsync(TrackFixture.Load("back-and-forth.json"));
        Assert.Equal(1, last.Nodes[ids.Line].In);
        Assert.Equal(0, last.Nodes[ids.Line].Out);
        Assert.Equal("In 1", last.Nodes[ids.Line].Live);
        Assert.Single(h.Of(VisionEventKind.LineCrossed));
    }

    [Fact]
    public async Task A_count_on_a_line_for_Out_only_gives_Out()
    {
        var (p, ids) = Gate(HorizontalLine(NodeKinds.DirectionsOut));
        var h = new EvaluatorHarness(p);
        var last = await h.RunAsync(TrackFixture.Load("back-and-forth.json"));
        Assert.Equal(1, last.Nodes[ids.Count].Value);
        Assert.Equal("1", last.Nodes[ids.Count].Live);
    }

    [Fact]
    public async Task Reset_counter_starts_the_count_and_its_line_again()
    {
        var (p, ids) = Gate(HorizontalLine());
        var h = new EvaluatorHarness(p);
        var fixture = TrackFixture.Load("back-and-forth.json");
        await h.RunAsync(fixture with { Frames = [.. fixture.Frames.Take(20)] });

        h.Evaluator.ResetCounter(ids.Count);
        var after = await h.RunAsync(fixture with { Frames = [.. fixture.Frames.Skip(20)] });
        Assert.Equal(0, after.Nodes[ids.Line].In);
        Assert.Equal(1, after.Nodes[ids.Line].Out);
        Assert.Equal(0, after.Nodes[ids.Count].In);
        Assert.Equal(1, after.Nodes[ids.Count].Out);

        h.Evaluator.ResetCounter("nothere");
        h.Evaluator.ResetCounter(ids.Line);
        var reset = await h.FrameAsync(10);
        Assert.Equal(0, reset.Nodes[ids.Line].Out);
    }

    [Fact]
    public async Task The_bottom_centre_decides_inside_by_default()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var top = PipelineBuilder.Shape((0, 0), (1, 0), (1, 0.5), (0, 0.5));
        var bottom = b.After(detect, NodeKinds.Area, top, name: "Bottom", show: true);
        var centreSettings = PipelineBuilder.Shape((0, 0), (1, 0), (1, 0.5), (0, 0.5));
        centreSettings[NodeKinds.AnchorKey] = NodeKinds.AnchorCentre;
        var centre = b.After(detect, NodeKinds.Area, centreSettings, name: "Centre", show: true);
        var overlapSettings = PipelineBuilder.Shape((0, 0), (1, 0), (1, 0.32), (0, 0.32));
        overlapSettings[NodeKinds.AnchorKey] = NodeKinds.AnchorOverlap;
        var overlap = b.After(detect, NodeKinds.Area, overlapSettings, name: "Overlap", show: true);
        var h = new EvaluatorHarness(b.Build());

        // Box from y 0.3 to 0.6: its centre (0.45) is in the top half, its bottom (0.6) is not.
        var r = await h.RunAsync(0, Det.Person(0.5, 0.6));
        Assert.Empty(r.Nodes[bottom].Objects);
        Assert.Equal("0 inside", r.Nodes[bottom].Live);
        Assert.Single(r.Nodes[centre].Objects);
        Assert.Single(r.Nodes[overlap].Objects);
    }

    [Fact]
    public async Task An_area_gives_tracked_objects_their_time_inside()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var track = b.After(detect, NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        var area = b.After(track, NodeKinds.Area, PipelineBuilder.Shape((0, 0.5), (1, 0.5), (1, 1), (0, 1)), show: true);
        var h = new EvaluatorHarness(b.Build());
        await h.RunAsync(0, Det.Person(0.5, 0.4));
        await h.RunAsync(0.5, Det.Person(0.5, 0.7));
        var r = await h.RunAsync(2.0, Det.Person(0.5, 0.71));
        Assert.Equal(1.5, Assert.Single(r.Nodes[area].Objects).InsideSeconds!.Value, 3);
        Assert.Equal("1 inside", r.Nodes[area].Live);
    }

    [Fact]
    public async Task Filter_keeps_by_class_score_and_size_and_lists_the_dropped()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings(30));
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject
        {
            [NodeKinds.ClassesKey] = new JsonArray("person"),
            [NodeKinds.MinScoreKey] = 60,
            [NodeKinds.MinSizeKey] = 20,
            [NodeKinds.MaxSizeKey] = 50,
        }, show: true);
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0,
            Det.Person(0.2, 0.9, 0.9, h: 0.3),
            Det.Person(0.4, 0.9, 0.5, h: 0.3),  // score
            Det.Person(0.6, 0.9, 0.9, h: 0.1),  // too small
            Det.Person(0.8, 0.9, 0.9, h: 0.6),  // too large
            Det.Car(0.5, 0.5));                 // class
        Assert.Equal(0.3, h.Inference.MinScores[0], 9);
        Assert.Single(r.Nodes[filter].Objects);
        Assert.Equal(4, r.Nodes[filter].Dropped.Count);
        Assert.Equal("1 object", r.Nodes[filter].Live);
    }

    [Fact]
    public async Task Detect_keeps_its_classes_and_shows_count_and_time()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings(50, "person"), show: true);
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0, Det.Person(0.2, 0.9), Det.Person(0.4, 0.9), Det.Car(0.6, 0.5));
        Assert.Equal(2, r.Nodes[detect].Objects.Count);
        Assert.Equal("2 objects · 18 ms", r.Nodes[detect].Live);
        Assert.Equal(18, r.Nodes[detect].Milliseconds!.Value, 3);
        Assert.Equal("1280x720", r.Nodes[camera].Live);
    }

    [Fact]
    public async Task Classify_on_objects_works_on_at_most_16_and_on_tracked_ones_once_every_2_s()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var classify = b.After(detect, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id }, show: true);
        var track = b.After(detect, NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        var classifyTracked = b.After(track, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id }, name: "Tracked", show: true);
        var h = new EvaluatorHarness(b.Build());
        var people = Enumerable.Range(0, 20).Select(i => Det.Person(0.025 + i * 0.05, 0.9, w: 0.04)).ToArray();

        var r = await h.RunAsync(0, people);
        Assert.Equal(16, r.Nodes[classify].Objects.Count(o => o.Label is not null));
        Assert.Equal(20, r.Nodes[classify].Objects.Count);
        Assert.Equal("red", r.Nodes[classify].Objects[0].Label!.ClassName);
        Assert.Equal([16, 16], h.Inference.ClassifiedRegions);

        // Tracked ones keep their class without a call until 2 s passed; the 4 left over get theirs now.
        await h.RunAsync(0.5, people);
        Assert.Equal([16, 16, 16, 4], h.Inference.ClassifiedRegions);
        var later = await h.RunAsync(1.0, people);
        Assert.Equal(20, later.Nodes[classifyTracked].Objects.Count(o => o.Label is not null));
        Assert.Equal(5, h.Inference.ClassifiedRegions.Count);
        Assert.Equal("20 objects", later.Nodes[classifyTracked].Live);
        await h.RunAsync(2.0, people);
        Assert.Equal(16, h.Inference.ClassifiedRegions[^1]);
    }

    [Fact]
    public async Task Classify_on_a_picture_gives_its_class()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var settings = PipelineBuilder.Shape((0.1, 0.1), (0.5, 0.1), (0.5, 0.5));
        settings[NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id;
        var classify = b.After(camera, NodeKinds.Classify, settings, show: true);
        var h = new EvaluatorHarness(b.Build());
        RelativeBox? asked = null;
        h.Inference.Classify = box =>
        {
            asked = box;
            return new ClassResult(1, "blue", 0.874);
        };
        var r = await h.FrameAsync(0);
        Assert.Equal(PortKind.Picture, r.Nodes[classify].Kind);
        Assert.Equal("blue", r.Nodes[classify].Label!.ClassName);
        Assert.Equal("blue 87 % · 18 ms", r.Nodes[classify].Live);
        Assert.Equal(new RelativeBox(0.1, 0.1, 0.4, 0.4), asked);
    }

    [Fact]
    public async Task Two_lines_after_one_track_share_one_model_call()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var track = b.After(detect, NodeKinds.Track);
        b.After(b.After(track, NodeKinds.Line, VerticalLine()), NodeKinds.Count);
        b.After(b.After(track, NodeKinds.Line, HorizontalLine()), NodeKinds.Count);
        var h = new EvaluatorHarness(b.Build());
        await h.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Equal(1, h.Inference.DetectCalls);
    }

    [Fact]
    public async Task A_branch_that_nothing_needs_costs_nothing_unless_shown_or_previewed()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var filter = b.After(detect, NodeKinds.Filter);
        var p = b.Build();

        var plain = new EvaluatorHarness(p);
        var r = await plain.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Equal(0, plain.Inference.DetectCalls);
        Assert.Null(r.Nodes[detect].Kind);
        Assert.Null(r.Nodes[filter].Kind);
        Assert.Equal(3, r.Nodes.Count);

        var shown = new EvaluatorHarness(p with { Nodes = [.. p.Nodes.Select(n => n.Id == filter ? n with { ShowOnVideo = true } : n)] });
        r = await shown.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Equal(1, shown.Inference.DetectCalls);
        Assert.Single(r.Nodes[filter].Objects);

        var preview = new EvaluatorHarness(p, allNodes: true);
        r = await preview.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Single(r.Nodes[filter].Objects);
    }

    [Fact]
    public async Task A_failing_model_ends_the_frame_without_events_and_the_next_frame_works()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var ev = b.After(detect, NodeKinds.Event, new JsonObject { [NodeKinds.AtLeastKey] = 0 });
        var h = new EvaluatorHarness(b.Build());
        h.Inference.FailNext = new InvalidOperationException("out of memory");
        var failed = await h.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Equal("The model People failed: out of memory", failed.ModelFailure);
        Assert.Empty(failed.Emissions);
        Assert.Null(failed.Nodes[detect].Kind);
        Assert.Equal(string.Empty, failed.Nodes[ev].Live);

        var ok = await h.RunAsync(1, Det.Person(0.5, 0.5));
        Assert.Null(ok.ModelFailure);
        Assert.Equal(VisionEventKind.ObjectsPresent, Assert.Single(ok.Emissions).Kind);
    }

    [Fact]
    public async Task A_branch_with_an_error_does_not_run_and_the_others_do()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var bad = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.MinScoreKey] = 0 }, name: "Bad");
        var afterBad = b.After(bad, NodeKinds.Count, name: "After bad");
        var good = b.After(detect, NodeKinds.Count, name: "Good");
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0, Det.Person(0.5, 0.5), Det.Person(0.2, 0.5));
        Assert.Null(r.Nodes[bad].Kind);
        Assert.Null(r.Nodes[afterBad].Kind);
        Assert.Equal(2, r.Nodes[good].Value);
    }

    [Fact]
    public async Task A_pipeline_with_a_graph_error_runs_nothing()
    {
        var b = new PipelineBuilder { Version = Pipeline.CurrentVersion + 1 };
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0, Det.Person(0.5, 0.5));
        Assert.Equal(0, h.Inference.DetectCalls);
        Assert.Null(r.Nodes[camera].Kind);
        Assert.Null(r.Nodes[count].Value);
    }

    [Fact]
    public async Task The_frame_result_carries_its_time()
    {
        var (p, _) = Gate(VerticalLine());
        var h = new EvaluatorHarness(p);
        var r = await h.RunAsync(2.5);
        Assert.Equal(EvaluatorHarness.T0.AddSeconds(2.5), r.At);
    }
}
