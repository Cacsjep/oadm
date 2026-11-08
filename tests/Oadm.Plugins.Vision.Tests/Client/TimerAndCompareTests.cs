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
        Assert.Equal([NodeKinds.Count, NodeKinds.Statistics, NodeKinds.Event, NodeKinds.Throttle], canvas.Picker.ShownKinds.Select(k => k.Id));
        canvas.Picker.SearchText = "Count";
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
        Assert.Equal(CanvasGeometry.ControlPort(count.X, count.Y, 0), link.End); // Reset: the first row below the live line
        Assert.True(link.End.Y > count.Y + CanvasGeometry.NodeHeight - 4 && link.End.Y < count.Y + count.Height);
        Assert.Same(link, canvas.LinkAt(CanvasGeometry.OnCurve(link.Start, link.End, 0.5, intoSide: true)));
        canvas.SelectedLink = link;
        canvas.DeleteSelectedCommand.Execute(null);
        Assert.Null(canvas.ControlLinkOf(count, NodeKinds.ResetPort));
        Assert.NotNull(canvas.InputOf(count));
    }

    [Fact]
    public void Tidy_up_puts_the_timers_left_of_the_nodes_they_pulse_top_aligned()
    {
        var canvas = Canvas(PageSamples.LobbyCount);
        canvas.TidyUpCommand.Execute(null);
        var midnight = canvas.Find("t1")!;
        var count = canvas.Find("d")!;
        Assert.Equal(count.Y, midnight.Y); // top-aligned with the Count it resets, left of it
        Assert.Equal(count.X - CanvasGeometry.ColumnStep, midnight.X);
        foreach (var node in canvas.Nodes) // no card on another
        {
            Assert.DoesNotContain(canvas.Nodes, other => !ReferenceEquals(other, node) && other.Rect.Intersects(node.Rect));
        }
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

    [Fact]
    public void Condition_inputs_take_only_a_yes_or_no_and_a_drop_on_a_dot_takes_that_input()
    {
        var canvas = Canvas(PageSamples.Entrance);
        var door = canvas.Find("h")!;
        var both = canvas.Find("g")!;
        var alert = canvas.Find("i")!;
        var hours = canvas.Find("w")!;
        var midnight = canvas.Find("t")!;
        Assert.Equal(PortKind.Condition, door.OutputKind);
        Assert.Same(door, canvas.ControlLinkOf(both, NodeKinds.SecondPort)!.From);
        Assert.Same(hours, canvas.ControlLinkOf(alert, NodeKinds.WhilePort)!.From);
        Assert.Equal(CanvasGeometry.HeightFor(2), alert.Height); // Send now and Only while

        // A pulse fits Reset and Send now, never Only while or Second.
        Assert.True(canvas.FitsPort(midnight, alert, NodeKinds.SendPort));
        Assert.False(canvas.FitsPort(midnight, alert, NodeKinds.WhilePort));
        Assert.False(canvas.FitsPort(midnight, both, NodeKinds.SecondPort));

        // A yes or no from the Time window: onto the Only while dot it goes there, elsewhere on the card into the data input.
        canvas.SelectedLink = canvas.ControlLinkOf(alert, NodeKinds.WhilePort);
        canvas.DeleteSelectedCommand.Execute(null);
        canvas.BeginLinkDrag(hours, new Point(0, 0));
        Assert.Equal(LinkFit.Fits, alert.Fit);
        Assert.True(canvas.EndLinkDrag(alert, alert.ControlPortAt(NodeKinds.WhilePort)));
        Assert.Same(hours, canvas.ControlLinkOf(alert, NodeKinds.WhilePort)!.From);
        Assert.Same(both, canvas.InputOf(alert)!.From); // the data input keeps And / Or

        // The + of a yes or no lists the kinds that take one, in the data input or a control input.
        canvas.OpenPickerAfter(hours);
        Assert.Contains(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Hold);
        Assert.Contains(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Count); // its Only while
        Assert.DoesNotContain(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Statistics);
    }

    [Fact]
    public void Days_chips_and_the_event_type_field()
    {
        var ctx = PageSamples.Context();
        var dialogs = new EventDialogs();
        using var editor = new PipelineEditorViewModel(ctx, PageSamples.Entrance, false, new Oadm.Plugins.Vision.Api.EngineInfo(),
            id => id == PageSamples.Dock.Id ? "AXIS P1468-LE · 10.0.0.202" : "AXIS Q1798-LE · 10.0.0.48",
            (_, _) => false, () => PageSamples.Models, PageSamples.Decode, PageSamples.CheckData) { Dialogs = dialogs };

        editor.Canvas.SelectedNode = editor.Canvas.Find("w");
        var days = Assert.IsType<DaysFieldViewModel>(editor.Form!.Fields[0]);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], days.Days.Select(d => d.Text));
        Assert.Equal(["mon", "tue", "wed", "thu", "fri"], days.Chosen);
        days.Days[5].IsChecked = true;
        Assert.Equal(["mon", "tue", "wed", "thu", "fri", "sat"], NodeValues.Strings(editor.Canvas.Find("w")!.Settings[NodeKinds.DaysKey]));
        foreach (var day in days.Days)
        {
            day.IsChecked = false;
        }

        Assert.Empty(NodeValues.Strings(editor.Canvas.Find("w")!.Settings[NodeKinds.DaysKey])); // none: every day

        editor.Canvas.SelectedNode = editor.Canvas.Find("h");
        var field = Assert.IsType<EventTypeFieldViewModel>(editor.Form!.Fields[0]);
        dialogs.Event = form =>
        {
            // Vision Center's own events never show; a type of a plugin that is off shows with "(turned off)".
            Assert.Equal(["Device status", "Input ports", "Server", "Text recognition / Container gate"], form.Rows.OfType<EventPickerFolder>().Select(f => f.Name));
            var off = form.Rows.OfType<EventPickerType>().Single(r => r.Type.Id == "oadm.ocr.text");
            Assert.False(off.IsAvailable);
            Assert.Equal("Text read (turned off)", off.Name);
            Assert.Equal("io.port.door", form.Chosen!.Id); // the stored one selected
            form.SearchText = "tamper";
            Assert.Single(form.Rows.OfType<EventPickerType>());
            form.Selected = form.Rows.OfType<EventPickerType>().Single();
            return true;
        };
        dialogs.Device = _ => PageSamples.Gate.Id; // a per-device event asks for the device
        field.ChooseCommand.Execute(null);
        Assert.Equal(new EventTypeValue("io.port.tamper", PageSamples.Gate.Id), field.Value);
        Assert.Equal("Tamper alarm · AXIS Q1798-LE · 10.0.0.48", field.Text);
        Assert.Equal("io.port.tamper", NodeValues.Text(editor.Canvas.Find("h")!.Settings[NodeKinds.EventKey]!["type"]));

        dialogs.Event = form =>
        {
            form.Selected = form.Rows.OfType<EventPickerType>().Single(r => r.Type.Id == "server.backup");
            return true;
        };
        field.ChooseCommand.Execute(null);
        Assert.Equal(new EventTypeValue("server.backup", null), field.Value); // a server event has no device
        Assert.Equal("Backup failed", field.Text);
    }

    [Fact]
    public void Direction_is_an_arrow_with_its_tolerance_and_dwell_like_nodes_mark_their_objects()
    {
        var canvas = new CanvasViewModel(PageSamples.Entrance.Nodes, PageSamples.Entrance.Links);
        var outputs = PageSamples.EntrancePreview("x").Nodes.ToDictionary(o => o.NodeId);
        var (items, shapes) = VideoOverlay.Build(canvas.Nodes, null, outputs);
        var arrow = Assert.Single(shapes, s => s.Node.Id == "k");
        Assert.True(arrow.IsArrow);
        Assert.False(arrow.Closed);
        Assert.Equal(30, arrow.Tolerance);
        Assert.Equal("Going in · 2", arrow.Label);
        Assert.Contains(items, i => i.NodeId == "k" && i.Filled && i.Label == "3 s");
    }

    private sealed class EventDialogs : IVisionDialogs
    {
        public Func<EventTypePickerViewModel, bool> Event { get; set; } = _ => false;

        public Func<Guid?, Guid?> Device { get; set; } = _ => null;

        public Task<bool> ChooseEventTypeAsync(EventTypePickerViewModel form) => Task.FromResult(Event(form));

        public Task<Guid?> ChooseDeviceAsync(Guid? selected) => Task.FromResult(Device(selected));

        public Task<bool> EditStreamAsync(Oadm.Plugins.Shared.Vision.Client.StreamSettingsViewModel form) => Task.FromResult(false);

        public Task<bool> EditModelAsync(ModelEditViewModel form) => Task.FromResult(false);

        public Task<bool> EditClassListAsync(ClassListEditViewModel form) => Task.FromResult(false);

        public Task<IReadOnlyList<IModelFile>?> PickModelFilesAsync() => Task.FromResult<IReadOnlyList<IModelFile>?>(null);
    }

    [Fact]
    public void Tidy_up_stacks_rows_by_their_real_heights_with_one_gap()
    {
        var canvas = Canvas(PageSamples.Entrance);
        canvas.TidyUpCommand.Execute(null);
        var count = canvas.Find("e")!; // two control rows: a taller card
        var below = canvas.Find("f")!;
        Assert.Equal(count.Y + count.Height + CanvasGeometry.RowGap, below.Y);
        var line = canvas.Find("d")!;
        var going = canvas.Find("k")!;
        Assert.Equal(line.Y, going.Y);
        Assert.True(Math.Abs(going.X - line.X) >= CanvasGeometry.NodeWidth + CanvasGeometry.ColumnGap); // side by side, one gap at least (its subtree may be wider)
        foreach (var node in canvas.Nodes)
        {
            Assert.DoesNotContain(canvas.Nodes, other => !ReferenceEquals(other, node) && other.Rect.Intersects(node.Rect));
        }

        // Every start sits directly left of the node it feeds, top-aligned: its link only crosses the gap.
        foreach (var link in canvas.Links.Where(l => l.IsControl))
        {
            Assert.Equal(link.To.Y, link.From.Y);
            Assert.True(link.To.X - link.From.X >= CanvasGeometry.ColumnStep);
            Assert.DoesNotContain(canvas.Nodes, n => n.Y == link.To.Y && n.X > link.From.X && n.X < link.To.X); // nothing in between
        }

        // Each data link goes from one row to the next: never under a card.
        foreach (var link in canvas.Links.Where(l => !l.IsControl))
        {
            Assert.True(link.To.Y > link.From.Y + link.From.Height, $"{link.From.Name} > {link.To.Name}");
        }
    }
}
