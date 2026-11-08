using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.Nodes;

namespace Oadm.Plugins.Vision.Tests.Graph;

/// <summary>The check of the sources besides the Camera, the control inputs and the nodes of logic, time and motion.</summary>
public sealed class ControlLinkTests
{
    private static GraphCheck Check(Pipeline p) => VisionGraph.Check(p, PipelineBuilder.Find);

    private static string ErrorOf(Pipeline p, string nodeId)
    {
        var check = Check(p);
        Assert.True(check.NodeErrors.TryGetValue(nodeId, out var error), $"no error on {nodeId}: {string.Join(", ", check.NodeErrors)}");
        return error;
    }

    private static void Passes(Pipeline p)
    {
        var check = Check(p);
        Assert.True(check.Ok, string.Join(", ", check.NodeErrors.Select(e => $"{e.Key}: {e.Value}").Concat(check.GraphErrors)));
    }

    /// <summary>Camera > Detect > Count, the Count's id.</summary>
    private static (PipelineBuilder B, string Detect, string Count) Counting()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        return (b, detect, b.After(detect, NodeKinds.Count));
    }

    [Fact]
    public void A_timer_into_reset_and_send_now_passes()
    {
        var (b, _, count) = Counting();
        var ev = b.After(count, NodeKinds.Event);
        var timer = b.Add(NodeKinds.Timer);
        b.Control(timer, count, NodeKinds.ResetPort).Control(timer, ev, NodeKinds.SendPort);
        Passes(b.Build());
    }

    [Fact]
    public void A_timer_into_a_data_input_is_refused()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var timer = b.Add(NodeKinds.Timer);
        var count = b.After(timer, NodeKinds.Count);
        Assert.Equal("A Timer links only to Reset, Send now or Throttle.", ErrorOf(b.Build(), count));
    }

    [Fact]
    public void A_timer_into_throttle_passes()
    {
        var (b, _, count) = Counting();
        var timer = b.Add(NodeKinds.Timer);
        var throttle = b.After(timer, NodeKinds.Throttle);
        b.Control(throttle, count, NodeKinds.ResetPort);
        Passes(b.Build());
    }

    [Fact]
    public void A_control_link_into_an_unknown_port_is_refused()
    {
        var (b, _, count) = Counting();
        var timer = b.Add(NodeKinds.Timer);
        b.Control(timer, count, "start");
        Assert.Equal("This node has no such input.", ErrorOf(b.Build(), count));
    }

    [Fact]
    public void A_control_link_must_carry_its_ports_kind()
    {
        var (b, detect, count) = Counting();
        var ev = b.After(count, NodeKinds.Event);
        var timer = b.Add(NodeKinds.Timer);
        b.Control(detect, count, NodeKinds.ResetPort).Control(timer, ev, NodeKinds.WhilePort);
        var p = b.Build();
        Assert.Equal("Reset takes a pulse.", ErrorOf(p, count));
        Assert.Equal("Only while takes a condition.", ErrorOf(p, ev));
    }

    [Fact]
    public void A_control_input_takes_one_link()
    {
        var (b, _, count) = Counting();
        var t1 = b.Add(NodeKinds.Timer);
        var t2 = b.Add(NodeKinds.Timer);
        b.Control(t1, count, NodeKinds.ResetPort).Control(t2, count, NodeKinds.ResetPort);
        Assert.Equal("Reset takes one link.", ErrorOf(b.Build(), count));
    }

    [Fact]
    public void A_condition_into_only_while_passes()
    {
        var (b, _, count) = Counting();
        var window = b.Add(NodeKinds.TimeWindow);
        b.Control(window, count, NodeKinds.WhilePort);
        Passes(b.Build());
    }

    [Fact]
    public void And_Or_needs_its_second_input()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var w1 = b.Add(NodeKinds.TimeWindow);
        var logic = b.After(w1, NodeKinds.Logic);
        b.After(logic, NodeKinds.Event);
        Assert.Equal("Link the second input.", ErrorOf(b.Build(), logic));

        var w2 = b.Add(NodeKinds.TimeWindow);
        b.Control(w2, logic, NodeKinds.SecondPort);
        Passes(b.Build());
    }

    [Fact]
    public void Control_links_in_a_circle_are_a_loop()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var w1 = b.Add(NodeKinds.TimeWindow);
        var w2 = b.Add(NodeKinds.TimeWindow);
        var a = b.After(w1, NodeKinds.Logic);
        var c = b.After(w2, NodeKinds.Logic);
        b.Control(a, c, NodeKinds.SecondPort).Control(c, a, NodeKinds.SecondPort);
        var p = b.Build();
        Assert.Equal(GraphTexts.Loop, ErrorOf(p, a));
        Assert.Equal(GraphTexts.Loop, ErrorOf(p, c));
    }

    [Fact]
    public void A_timer_takes_no_input_and_works_only_while_watching()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var timer = b.After(camera, NodeKinds.Timer);
        Assert.Equal("The Timer node takes no input.", ErrorOf(b.Build(), timer));

        var onAction = new PipelineBuilder { Mode = PipelineMode.OnAction };
        onAction.Add(NodeKinds.Camera);
        var t = onAction.Add(NodeKinds.Timer);
        Assert.Equal("Timer works only while Watching.", ErrorOf(onAction.Build(), t));
    }

    [Fact]
    public void Several_sources_reach_an_event()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var window = b.Add(NodeKinds.TimeWindow);
        var device = b.Add(NodeKinds.DeviceEvent, settings: new JsonObject { [NodeKinds.EventKey] = new JsonObject { ["type"] = "door", ["device"] = "" } });
        var hold = b.After(window, NodeKinds.Hold);
        var logic = b.After(hold, NodeKinds.Logic);
        b.Control(device, logic, NodeKinds.SecondPort);
        b.After(logic, NodeKinds.Event);
        var p = b.Build();
        Passes(p);
        Assert.Equal(PortKind.Condition, Check(p).InputKinds[logic]);
    }

    [Fact]
    public void A_device_event_needs_its_event_and_a_window_valid_days()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var device = b.Add(NodeKinds.DeviceEvent);
        var bad = b.Add(NodeKinds.DeviceEvent, settings: new JsonObject { [NodeKinds.EventKey] = new JsonObject { ["type"] = "door", ["device"] = "nope" } });
        var window = b.Add(NodeKinds.TimeWindow, settings: new JsonObject { [NodeKinds.DaysKey] = new JsonArray("mon", "funday") });
        var p = b.Build();
        Assert.Equal("Choose an event.", ErrorOf(p, device));
        Assert.Equal("Choose an event.", ErrorOf(p, bad));
        Assert.Equal("Choose the days.", ErrorOf(p, window));
    }

    [Fact]
    public void Dwell_needs_an_area_before_it()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var track = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track);
        var lone = b.After(track, NodeKinds.Dwell);
        var area = b.After(track, NodeKinds.Area, PipelineBuilder.Shape((0, 0), (1, 0), (1, 1)));
        var dwell = b.After(b.After(area, NodeKinds.Filter), NodeKinds.Dwell);
        var p = b.Build();
        Assert.Equal("Dwell needs an Area node before it.", ErrorOf(p, lone));
        Assert.False(Check(p).NodeErrors.ContainsKey(dwell));
    }

    [Fact]
    public void Between_needs_its_bounds_in_order()
    {
        var (b, _, count) = Counting();
        var compare = b.After(count, NodeKinds.Compare, new JsonObject { [NodeKinds.OperatorKey] = NodeKinds.OpBetween, [NodeKinds.ValueKey] = 20, [NodeKinds.Value2Key] = 10 });
        Assert.Equal("And must not be below Value.", ErrorOf(b.Build(), compare));
    }

    [Fact]
    public void A_hidden_field_is_not_checked()
    {
        // Value2 shows only for Between, Minutes only for Few minutes.
        var (b, _, count) = Counting();
        b.After(count, NodeKinds.Compare, new JsonObject { [NodeKinds.Value2Key] = "x" });
        b.Add(NodeKinds.Timer, settings: new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryDay, [NodeKinds.MinutesKey] = 500 });
        Passes(b.Build());
    }
}
