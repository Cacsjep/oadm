using Avalonia;

using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>Compare, Timer and the control inputs on the canvas; sliders and VisibleWhen in the node form.</summary>
public sealed class TimerAndCompareTests
{
    private static CanvasViewModel Canvas(Pipeline pipeline)
    {
        var canvas = new CanvasViewModel(pipeline.Nodes, pipeline.Links) { ViewportSize = new Size(900, 700) };
        canvas.Checker = (nodes, links) => PageSamples.CheckData(pipeline with { Nodes = nodes, Links = links });
        canvas.Recheck();
        return canvas;
    }

    [Fact]
    public void A_timer_has_no_input_its_pulse_goes_only_into_control_inputs()
    {
        var canvas = Canvas(PageSamples.LobbyCount);
        var midnight = canvas.Find("t1")!;
        var count = canvas.Find("d")!;
        var compare = canvas.Find("e")!;
        var report = canvas.Find("g")!;
        Assert.False(midnight.HasInputPort);
        Assert.Equal(PortKind.Pulse, midnight.OutputKind);
        Assert.Equal(PortKind.Number, count.OutputKind); // a control link changes nothing of the data
        Assert.Equal(PortKind.Condition, compare.OutputKind);
        Assert.Same(canvas.Find("c"), canvas.InputOf(count)!.From);
        Assert.Same(midnight, canvas.ControlLinkOf(count, NodeKinds.ResetPort)!.From);

        // While a pulse is dragged only the nodes with a control input light up.
        canvas.BeginLinkDrag(midnight, new Point(0, 0));
        Assert.Equal(LinkFit.Fits, report.Fit);
        Assert.Equal(LinkFit.Dimmed, compare.Fit);
        Assert.Equal(LinkFit.Dimmed, canvas.Find("b")!.Fit);
        Assert.True(canvas.EndLinkDrag(report));
        Assert.Single(canvas.Links, l => ReferenceEquals(l.To, report) && l.ToPort == NodeKinds.SendPort && ReferenceEquals(l.From, midnight));
        Assert.Same(canvas.Find("d"), canvas.InputOf(report)!.From); // the data input stays

        // A data output never goes into a control input, a pulse never into a data input.
        Assert.False(canvas.CanLink(midnight, compare));
        Assert.True(canvas.TryPort(compare, report, out var port)); // a condition into the data input of an Event
        Assert.Null(port);
        Assert.Equal(NodeKinds.SendPort, canvas.ToLinks().Single(l => l.From == "t1" && l.To == "g").ToPort);
    }

    [Fact]
    public void The_plus_of_a_timer_lists_count_and_event_and_links_into_their_control_input()
    {
        var canvas = Canvas(PageSamples.LobbyCount);
        var hourly = canvas.Find("t2")!;
        canvas.OpenPickerAfter(hourly);
        Assert.Equal([NodeKinds.Count, NodeKinds.Event], canvas.Picker.ShownKinds.Select(k => k.Id));
        var count = canvas.Choose(NodeKinds.CountKind)!;
        var link = Assert.Single(canvas.Links, l => ReferenceEquals(l.To, count));
        Assert.Equal(NodeKinds.ResetPort, link.ToPort);
        Assert.Null(canvas.InputOf(count));

        canvas.OpenPickerAfter(canvas.Find("d")!);
        Assert.Contains(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Compare);
        Assert.DoesNotContain(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Timer); // a timer takes no input
    }

    [Fact]
    public void A_control_link_ends_on_the_left_edge_and_can_be_selected()
    {
        var canvas = Canvas(PageSamples.LobbyCount);
        var count = canvas.Find("d")!;
        var link = canvas.ControlLinkOf(count, NodeKinds.ResetPort)!;
        Assert.Equal(count.X, link.End.X);
        Assert.Equal(count.Y + (CanvasGeometry.NodeHeight / 2), link.End.Y);
        Assert.Same(link, canvas.LinkAt(CanvasGeometry.OnCurve(link.Start, link.End, 0.5, intoSide: true)));
        canvas.SelectedLink = link;
        canvas.DeleteSelectedCommand.Execute(null);
        Assert.Null(canvas.ControlLinkOf(count, NodeKinds.ResetPort));
        Assert.NotNull(canvas.InputOf(count));
    }

    [Fact]
    public void Tidy_up_puts_the_timers_left_of_the_graph_above_the_nodes_they_pulse()
    {
        var canvas = Canvas(PageSamples.LobbyCount);
        canvas.TidyUpCommand.Execute(null);
        var midnight = canvas.Find("t1")!;
        Assert.True(midnight.Y < canvas.Find("d")!.Y); // above the Count it resets, its link comes into the left edge
        Assert.True(midnight.X < canvas.Nodes.Where(n => n.Kind != NodeKinds.Timer).Min(n => n.X));
    }

    [Fact]
    public void Slider_numbers_and_fields_shown_by_another_field()
    {
        using var editor = new PipelineEditorViewModel(PageSamples.Context(), PageSamples.LobbyCount, false, new Oadm.Plugins.Vision.Api.EngineInfo(), _ => "AXIS P1468-LE · 10.0.0.202",
            (_, _) => false, () => PageSamples.Models, PageSamples.Decode, PageSamples.CheckData);

        editor.Canvas.SelectedNode = editor.Canvas.Find("b");
        var score = Assert.IsType<NumberFieldViewModel>(editor.Form!.Fields.Single(f => f.Field.Key == NodeKinds.MinScoreKey));
        Assert.True(score.IsSlider);
        Assert.Equal(1, score.Minimum);
        Assert.Equal(99, score.Maximum);
        score.Text = "120";
        Assert.True(score.HasTextError);
        Assert.Equal(score.ErrorOf(nameof(NumberFieldViewModel.Text)), score.TextError);

        editor.Canvas.SelectedNode = editor.Canvas.Find("e"); // Compare: And only for Between
        var form = editor.Form!;
        var and = form.Fields.Single(f => f.Field.Key == NodeKinds.Value2Key);
        Assert.False(and.IsShown);
        Assert.True(Assert.IsType<NumberFieldViewModel>(form.Fields.Single(f => f.Field.Key == NodeKinds.ValueKey)).IsPlain);
        var op = Assert.IsType<ChoiceFieldViewModel>(form.Fields.Single(f => f.Field.Key == NodeKinds.OperatorKey));
        Assert.Equal(NodeKinds.OpAtLeast, op.Selected!.Value);
        op.Selected = op.Choices.Single(c => c.Value == NodeKinds.OpBetween);
        Assert.True(and.IsShown);

        editor.Canvas.SelectedNode = editor.Canvas.Find("t2"); // Timer every hour: no Minutes, no At, no Day
        form = editor.Form!;
        bool Shown(string key) => form.Fields.Single(f => f.Field.Key == key).IsShown;
        Assert.False(Shown(NodeKinds.MinutesKey));
        Assert.False(Shown(NodeKinds.AtKey));
        Assert.False(Shown(NodeKinds.DayKey));
        var every = Assert.IsType<ChoiceFieldViewModel>(form.Fields.Single(f => f.Field.Key == NodeKinds.EveryKey));
        every.Selected = every.Choices.Single(c => c.Value == NodeKinds.EveryWeek);
        Assert.True(Shown(NodeKinds.AtKey));
        Assert.True(Shown(NodeKinds.DayKey));
        every.Selected = every.Choices.Single(c => c.Value == NodeKinds.EveryMinutes);
        Assert.True(Shown(NodeKinds.MinutesKey));
        Assert.True(Assert.IsType<NumberFieldViewModel>(form.Fields.Single(f => f.Field.Key == NodeKinds.MinutesKey)).IsSlider);
    }

    [Fact]
    public void Live_lines_of_compare_and_timer_show_in_their_output()
    {
        using var editor = new PipelineEditorViewModel(PageSamples.Context(), PageSamples.LobbyCount, false, new Oadm.Plugins.Vision.Api.EngineInfo(), _ => "AXIS P1468-LE · 10.0.0.202",
            (_, _) => false, () => PageSamples.Models, PageSamples.Decode, PageSamples.CheckData);
        editor.StartPreview();
        editor.ApplyPreview(PageSamples.LobbyPreview(editor.PreviewId!));
        Assert.Equal("Next 00:00", editor.Canvas.Find("t1")!.Live);
        editor.Canvas.SelectedNode = editor.Canvas.Find("e");
        Assert.True(editor.Output.IsNote);
        Assert.Equal("9 ≥ 10 · No", editor.Output.Summary);
        editor.StopPreview();
    }
}
