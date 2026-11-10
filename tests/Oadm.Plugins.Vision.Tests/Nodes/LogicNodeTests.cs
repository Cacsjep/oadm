using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Nodes;

public sealed class LogicNodeTests
{
    private static Detection[] People(int n) => [.. Enumerable.Range(0, n).Select(i => Det.Person(0.05 + i * 0.09, 0.9, w: 0.08))];

    /// <summary>Camera > Detect > Count > [node], the node shown.</summary>
    private static (EvaluatorHarness H, string Node, string Count, PipelineBuilder B) AfterCount(string kind, JsonObject? settings = null)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var node = b.After(count, kind, settings, show: true);
        return (new EvaluatorHarness(b.Build()), node, count, b);
    }

    [Theory]
    [InlineData(NodeKinds.OpGreater, 10, 0, 10, false)]
    [InlineData(NodeKinds.OpGreater, 10, 0, 11, true)]
    [InlineData(NodeKinds.OpAtLeast, 10, 0, 10, true)]
    [InlineData(NodeKinds.OpAtLeast, 10, 0, 9, false)]
    [InlineData(NodeKinds.OpEqual, 3, 0, 3, true)]
    [InlineData(NodeKinds.OpEqual, 3, 0, 4, false)]
    [InlineData(NodeKinds.OpAtMost, 3, 0, 3, true)]
    [InlineData(NodeKinds.OpAtMost, 3, 0, 4, false)]
    [InlineData(NodeKinds.OpLess, 3, 0, 2, true)]
    [InlineData(NodeKinds.OpLess, 3, 0, 3, false)]
    [InlineData(NodeKinds.OpBetween, 2, 4, 2, true)]
    [InlineData(NodeKinds.OpBetween, 2, 4, 4, true)]
    [InlineData(NodeKinds.OpBetween, 2, 4, 1, false)]
    [InlineData(NodeKinds.OpBetween, 2, 4, 5, false)]
    public async Task Compare_holds_by_its_condition(string op, double value, double value2, int count, bool holds)
    {
        var (h, compare, _, _) = AfterCount(NodeKinds.Compare, new JsonObject
        {
            [NodeKinds.OperatorKey] = op, [NodeKinds.ValueKey] = value, [NodeKinds.Value2Key] = Math.Max(value, value2),
        });
        var r = await h.RunAsync(0, People(count));
        Assert.Equal(PortKind.Condition, r.Nodes[compare].Kind);
        Assert.Equal(holds, r.Nodes[compare].On);
        Assert.Equal(count, r.Nodes[compare].Value);
    }

    [Fact]
    public async Task Compare_shows_the_comparison()
    {
        var (h, compare, _, _) = AfterCount(NodeKinds.Compare);
        Assert.Equal("4 > 10 · No", (await h.RunAsync(0, People(4))).Nodes[compare].Live);
        var (h2, between, _, _) = AfterCount(NodeKinds.Compare, new JsonObject { [NodeKinds.OperatorKey] = NodeKinds.OpBetween, [NodeKinds.ValueKey] = 2, [NodeKinds.Value2Key] = 6 });
        Assert.Equal("2 ≤ 4 ≤ 6 · Yes", (await h2.RunAsync(0, People(4))).Nodes[between].Live);
    }

    [Fact]
    public async Task An_event_on_a_condition_holds_after_At_least_and_goes_off_after_Off_after()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var compare = b.After(count, NodeKinds.Compare, new JsonObject { [NodeKinds.OperatorKey] = NodeKinds.OpAtLeast, [NodeKinds.ValueKey] = 3 });
        var ev = b.After(compare, NodeKinds.Event); // At least 1 s, Off after 2 s
        var h = new EvaluatorHarness(b.Build());
        Assert.Empty((await h.RunAsync(0, People(3))).Emissions);
        var on = Assert.Single((await h.RunAsync(1, People(4))).Emissions);
        Assert.Equal((VisionEventKind.ConditionHolds, EmissionState.On, "4"), (on.Kind, on.State, on.Values[VisionInfo.CountField]));
        Assert.True((await h.RunAsync(2, People(5))).Nodes[ev].On);
        Assert.Equal("5", Assert.Single(h.Evaluator.CurrentStates).Values[VisionInfo.CountField]);
        Assert.Empty((await h.RunAsync(3, People(1))).Emissions);
        var off = Assert.Single((await h.RunAsync(5, People(1))).Emissions);
        Assert.Equal((VisionEventKind.ConditionHolds, EmissionState.Off), (off.Kind, off.State));
        Assert.Empty(h.Evaluator.CurrentStates);
    }

    [Fact]
    public async Task Hold_says_yes_after_Yes_after_and_no_after_No_after()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var compare = b.After(count, NodeKinds.Compare, new JsonObject { [NodeKinds.ValueKey] = 0 });
        var hold = b.After(compare, NodeKinds.Hold, new JsonObject { [NodeKinds.OnAfterKey] = 5, [NodeKinds.OffAfterKey] = 2 }, show: true);
        var h = new EvaluatorHarness(b.Build());
        var r = await h.RunAsync(0, People(1));
        Assert.Equal((false, "Yes in 5 s"), (r.Nodes[hold].On!.Value, r.Nodes[hold].Live));
        r = await h.RunAsync(2, People(1));
        Assert.Equal("Yes in 3 s", r.Nodes[hold].Live);
        r = await h.RunAsync(5, People(1));
        Assert.Equal((true, "Yes"), (r.Nodes[hold].On!.Value, r.Nodes[hold].Live));
        r = await h.RunAsync(6, People(0));
        Assert.Equal((true, "No in 2 s"), (r.Nodes[hold].On!.Value, r.Nodes[hold].Live));
        r = await h.RunAsync(7, People(1)); // back before No after: stays yes, the wait starts again
        Assert.True(r.Nodes[hold].On);
        await h.RunAsync(8, People(0));
        Assert.True((await h.RunAsync(9.5, People(0))).Nodes[hold].On);
        Assert.False((await h.RunAsync(10, People(0))).Nodes[hold].On);
    }

    [Fact]
    public async Task And_Or_combine_the_input_and_the_second_input()
    {
        foreach (var (op, a, c, yes) in new[] { (NodeKinds.OpAnd, 1, 1, true), (NodeKinds.OpAnd, 1, 0, false), (NodeKinds.OpOr, 0, 1, true), (NodeKinds.OpOr, 0, 0, false) })
        {
            var b = new PipelineBuilder();
            var camera = b.Add(NodeKinds.Camera);
            var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
            var people = b.After(b.After(b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("person") }), NodeKinds.Count), NodeKinds.Compare, new JsonObject { [NodeKinds.ValueKey] = 0 });
            var cars = b.After(b.After(b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("car") }), NodeKinds.Count), NodeKinds.Compare, new JsonObject { [NodeKinds.ValueKey] = 0 });
            var logic = b.After(people, NodeKinds.Logic, new JsonObject { [NodeKinds.OperatorKey] = op }, show: true);
            b.Control(cars, logic, NodeKinds.SecondPort);
            var h = new EvaluatorHarness(b.Build());
            var r = await h.RunAsync(0, [.. People(a), .. Enumerable.Range(0, c).Select(_ => Det.Car(0.5, 0.5))]);
            Assert.Equal(yes, r.Nodes[logic].On);
            Assert.Equal(yes ? "Yes" : "No", r.Nodes[logic].Live);
        }
    }

    [Fact]
    public async Task Throttle_passes_one_crossing_per_period()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var track = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        var line = b.After(track, NodeKinds.Line, PipelineBuilder.Shape((0, 0.5), (1, 0.5)));
        var throttle = b.After(line, NodeKinds.Throttle, new JsonObject { [NodeKinds.EveryNSecondsKey] = 10 });
        b.After(throttle, NodeKinds.Event);
        var h = new EvaluatorHarness(b.Build());

        // Three people cross at once, then a fourth 5 s later, a fifth 11 s after the first.
        await h.RunAsync(0, Det.Person(0.1, 0.4), Det.Person(0.4, 0.4), Det.Person(0.7, 0.4));
        var r = await h.RunAsync(0.2, Det.Person(0.1, 0.6), Det.Person(0.4, 0.6), Det.Person(0.7, 0.6));
        Assert.Single(r.Nodes[throttle].Crossings);
        await h.RunAsync(5, Det.Person(0.9, 0.4));
        await h.RunAsync(5.2, Det.Person(0.9, 0.6));
        await h.RunAsync(11, Det.Person(0.25, 0.4));
        r = await h.RunAsync(11.2, Det.Person(0.25, 0.6));
        Assert.Equal("2 passed · 3 held back", r.Nodes[throttle].Live);
        Assert.Equal(2, h.Of(VisionEventKind.LineCrossed).Count());
    }

    [Fact]
    public async Task Statistics_gives_highest_lowest_average_and_sum_and_starts_again_on_reset()
    {
        foreach (var (fn, live, value) in new[] { (NodeKinds.FnMax, "Highest 6", 6L), (NodeKinds.FnMin, "Lowest 1", 1L), (NodeKinds.FnAverage, "Average 3.7", 4L), (NodeKinds.FnSum, "Sum 11", 11L) })
        {
            var (h, stats, _, _) = AfterCount(NodeKinds.Statistics, new JsonObject { [NodeKinds.FunctionKey] = fn });
            await h.RunAsync(0, People(4));
            await h.RunAsync(1, People(1));
            var r = await h.RunAsync(2, People(6));
            Assert.Equal((live, value), (r.Nodes[stats].Live, r.Nodes[stats].Value!.Value));
        }

        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var count = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Count);
        var s = b.After(count, NodeKinds.Statistics, show: true);
        var timer = b.Add(NodeKinds.Timer, settings: new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryMinutes, [NodeKinds.MinutesKey] = 1 });
        b.Control(timer, s, NodeKinds.ResetPort);
        var hr = new EvaluatorHarness(b.Build());
        await hr.RunAsync(0, People(9));
        Assert.Equal("Highest 9", (await hr.RunAsync(60, People(2))).Nodes[s].Live); // the pulse: this frame still counts
        Assert.Equal("Highest 3", (await hr.RunAsync(61, People(3))).Nodes[s].Live);
    }

    [Fact]
    public async Task A_device_event_says_yes_while_its_event_is_on()
    {
        var states = new FakeEventStates();
        var device = Guid.NewGuid();
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var node = b.Add(NodeKinds.DeviceEvent, settings: new JsonObject { [NodeKinds.EventKey] = new JsonObject { ["type"] = "door", ["device"] = device.ToString() } }, show: true);
        var h = new EvaluatorHarness(b.Build(), events: states);
        var off = (await h.FrameAsync(0)).Nodes[node];
        Assert.Equal((false, "No"), (off.On!.Value, off.Live));
        states.On.Add(("door", device));
        Assert.Equal("Yes", (await h.FrameAsync(1)).Nodes[node].Live);

        var none = new EvaluatorHarness(b.Build());
        Assert.False((await none.FrameAsync(0)).Nodes[node].On);
    }

    [Fact]
    public async Task Count_counts_and_Event_sends_only_while_yes()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var track = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        var line = b.After(track, NodeKinds.Line, PipelineBuilder.Shape((0, 0.5), (1, 0.5)));
        var count = b.After(line, NodeKinds.Count);
        var ev = b.After(line, NodeKinds.Event);
        var window = b.Add(NodeKinds.TimeWindow, settings: new JsonObject { [NodeKinds.FromKey] = "12:00", [NodeKinds.ToKey] = "12:01" });
        b.Control(window, count, NodeKinds.WhilePort).Control(window, ev, NodeKinds.WhilePort);
        var h = new EvaluatorHarness(b.Build()); // frames from 12:00 UTC

        await h.RunAsync(0, Det.Person(0.2, 0.4));
        var r = await h.RunAsync(1, Det.Person(0.2, 0.6));
        Assert.Equal(1, r.Nodes[count].Value);
        await h.RunAsync(70, Det.Person(0.6, 0.4));
        r = await h.RunAsync(71, Det.Person(0.6, 0.6)); // 12:01:11, outside: counted nowhere, the value stays
        Assert.Equal(1, r.Nodes[count].Value);
        Assert.Single(h.Of(VisionEventKind.LineCrossed));
    }

    [Fact]
    public async Task Send_now_reports_the_current_values_and_a_reset_of_the_same_pulse_follows()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var track = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        var line = b.After(track, NodeKinds.Line, PipelineBuilder.Shape((0, 0.5), (1, 0.5)));
        var count = b.After(line, NodeKinds.Count);
        var ev = b.After(count, NodeKinds.Event);
        var timer = b.Add(NodeKinds.Timer, settings: new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryHour });
        b.Control(timer, count, NodeKinds.ResetPort).Control(timer, ev, NodeKinds.SendPort);
        var h = new EvaluatorHarness(b.Build()); // 12:00:00 UTC: the next pulse is 13:00

        // In over the line, then back Out (tall boxes, so the steps keep the track).
        await h.RunAsync(0, Det.Person(0.2, 0.45, h: 0.6));
        await h.RunAsync(0.5, Det.Person(0.2, 0.55, h: 0.6));
        await h.RunAsync(1, Det.Person(0.2, 0.45, h: 0.6));
        var r = await h.RunAsync(3600, Det.Person(0.2, 0.4));
        var report = Assert.Single(r.Emissions, e => e.Kind == VisionEventKind.Report);
        Assert.Equal(EmissionState.Fired, report.State);
        Assert.Equal(("1", "1", "1"), (report.Values[VisionInfo.CountField], report.Values[VisionInfo.InField], report.Values[VisionInfo.OutField]));
        Assert.Equal(0, (await h.RunAsync(3601, Det.Person(0.2, 0.4))).Nodes[count].Value);
    }

    [Fact]
    public async Task A_report_on_objects_carries_count_and_classes()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var ev = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Event);
        var timer = b.Add(NodeKinds.Timer, settings: new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryMinutes, [NodeKinds.MinutesKey] = 5 });
        b.Control(timer, ev, NodeKinds.SendPort);
        var h = new EvaluatorHarness(b.Build());
        await h.RunAsync(0, People(2));
        var r = await h.RunAsync(300, [.. People(2), Det.Car(0.5, 0.5)]);
        var report = Assert.Single(r.Emissions, e => e.Kind == VisionEventKind.Report);
        Assert.Equal(("3", "car person"), (report.Values[VisionInfo.CountField], report.Values[VisionInfo.ClassesField]));
    }

    private sealed class FakeEventStates : IEventStates
    {
        public HashSet<(string, Guid?)> On { get; } = [];

        public bool IsOn(string typeId, Guid? deviceId) => On.Contains((typeId, deviceId));
    }
}
