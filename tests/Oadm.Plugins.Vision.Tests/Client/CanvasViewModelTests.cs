using Avalonia;

using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>The canvas's view model (docs/spec/vision-center.md "Page"), without Avalonia: adding, linking, undo, copy, tidy, fit.</summary>
public sealed class CanvasViewModelTests
{
    private static CanvasViewModel Canvas(Pipeline pipeline, PipelineMode? mode = null)
    {
        var canvas = new CanvasViewModel(pipeline.Nodes, pipeline.Links) { ViewportSize = new Size(900, 500) };
        canvas.Checker = (nodes, links) => PageSamples.Check(pipeline with { Mode = mode ?? pipeline.Mode, Nodes = nodes, Links = links });
        canvas.Recheck();
        return canvas;
    }

    private static Pipeline CameraOnly => new()
    {
        Name = "New", DeviceId = PageSamples.Gate.Id,
        Nodes = [new PipelineNode { Id = "n1", Kind = NodeKinds.Camera, Name = "Camera", X = 40, Y = 40 }],
    };

    [Fact]
    public void The_plus_lists_only_the_kinds_that_take_the_output_and_links_the_new_node()
    {
        var canvas = Canvas(CameraOnly);
        var camera = canvas.Nodes[0];
        canvas.OpenPickerAfter(camera);
        Assert.True(canvas.Picker.IsOpen);
        Assert.Equal([NodeKinds.Detect, NodeKinds.Classify], canvas.Picker.ShownKinds.Select(k => k.Id));

        var detect = canvas.Choose(NodeKinds.DetectKind)!;
        Assert.False(canvas.Picker.IsOpen);
        Assert.Same(detect, canvas.SelectedNode);
        Assert.Equal("Detect objects", detect.Name);
        Assert.Single(canvas.Links, l => ReferenceEquals(l.From, camera) && ReferenceEquals(l.To, detect));
        Assert.Equal(PortKind.Objects, detect.OutputKind);
        Assert.Equal(GraphTexts.ChooseDetectionModel, detect.Error);

        canvas.OpenPickerAfter(detect);
        Assert.DoesNotContain(canvas.Picker.ShownKinds, k => k.Id is NodeKinds.Detect or NodeKinds.Camera);
        Assert.Contains(canvas.Picker.ShownKinds, k => k.Id == NodeKinds.Track);
        canvas.Picker.SearchText = "tra";
        Assert.Equal([NodeKinds.Track], canvas.Picker.ShownKinds.Select(k => k.Id));
        canvas.Picker.SearchText = "nothing like it";
        Assert.True(canvas.Picker.IsEmpty);
    }

    [Fact]
    public void Add_node_lists_every_kind_of_a_group_by_group()
    {
        var canvas = Canvas(CameraOnly);
        canvas.AddNodeCommand.Execute(null);
        // Every kind with a group, grouped in the order the groups first come in the catalogue.
        Assert.Equal(
            NodeKinds.All.Where(k => k.Group is not null).GroupBy(k => k.Group).SelectMany(g => g).Select(k => k.Id),
            canvas.Picker.ShownKinds.Select(k => k.Id));
        Assert.Equal(["Models", "Objects", "Geometry", "Output", "Logic", "Time"], canvas.Picker.Entries.OfType<PickerGroup>().Select(g => g.Name));
        canvas.AddNodeCommand.Execute(null);
        Assert.False(canvas.Picker.IsOpen); // the button again closes it
    }

    [Fact]
    public void A_link_that_does_not_fit_is_refused_and_a_drag_dims_the_nodes_that_do_not_take_it()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        var camera = canvas.Find("a")!;
        var track = canvas.Find("d")!;
        var line = canvas.Find("e")!;
        var count = canvas.Find("f")!;

        Assert.False(canvas.CanLink(camera, track)); // a picture into Track
        Assert.False(canvas.Link(camera, track));
        Assert.False(canvas.CanLink(line, canvas.Find("b")!)); // crossings into Detect

        canvas.SelectedLink = canvas.InputOf(canvas.Find("b")!);
        canvas.DeleteSelectedCommand.Execute(null);
        canvas.BeginLinkDrag(camera, new Point(0, 0));
        Assert.Equal(LinkFit.Fits, canvas.Find("b")!.Fit);
        Assert.Equal(LinkFit.Dimmed, track.Fit);
        Assert.Equal(LinkFit.None, camera.Fit);
        var links = canvas.Links.Count;
        Assert.False(canvas.EndLinkDrag(track)); // a drop on a node that does not fit does nothing
        Assert.Equal(links, canvas.Links.Count);
        Assert.All(canvas.Nodes, n => Assert.Equal(LinkFit.None, n.Fit));

        canvas.BeginLinkDrag(camera, new Point(0, 0));
        Assert.True(canvas.EndLinkDrag(canvas.Find("b"))); // a drop on a node that fits links it

        // Crossings into Count fit (it already has an input): the old one is replaced, one input per node.
        Assert.False(canvas.CanLink(line, count)); // already linked from the line
        Assert.True(canvas.Link(track, count));
        Assert.Single(canvas.Links, l => ReferenceEquals(l.To, count));
        Assert.Same(track, canvas.InputOf(count)!.From);
    }

    [Fact]
    public void A_link_never_makes_a_loop()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        var filter = canvas.Find("c")!;
        var track = canvas.Find("d")!;
        var area = canvas.Find("h")!;
        Assert.True(canvas.IsBefore(filter, area));
        Assert.False(canvas.CanLink(area, filter)); // Area gives objects, Filter takes them, but Filter is before it
        Assert.False(canvas.CanLink(track, track));
    }

    [Fact]
    public void Delete_removes_the_node_with_its_links_and_never_the_camera()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        canvas.SelectedNode = canvas.Find("a");
        Assert.False(canvas.DeleteSelectedCommand.CanExecute(null));

        canvas.SelectedNode = canvas.Find("d");
        canvas.DeleteSelectedCommand.Execute(null);
        Assert.Null(canvas.Find("d"));
        Assert.DoesNotContain(canvas.Links, l => l.From.Id == "d" || l.To.Id == "d");
        Assert.Equal(GraphTexts.NoInput, canvas.Find("e")!.Error); // the error directly below the node
        Assert.True(canvas.ErrorCount > 0);

        canvas.SelectedLink = canvas.Links.First(l => l.To.Id == "c");
        canvas.DeleteSelectedCommand.Execute(null);
        Assert.Null(canvas.InputOf(canvas.Find("c")!));
    }

    [Fact]
    public void Undo_and_redo_step_through_the_draft()
    {
        var canvas = Canvas(CameraOnly);
        Assert.False(canvas.UndoCommand.CanExecute(null));
        var detect = canvas.Add(NodeKinds.DetectKind, canvas.Nodes[0])!;
        canvas.SetSetting(detect, NodeKinds.MinScoreKey, 60);
        canvas.SetSetting(detect, NodeKinds.MinScoreKey, 65); // the same field again: one step
        Assert.Equal(65, NodeValues.Number(detect.Settings[NodeKinds.MinScoreKey]));

        canvas.UndoCommand.Execute(null);
        var restored = canvas.Find(detect.Id)!;
        Assert.Null(restored.Settings[NodeKinds.MinScoreKey]);
        canvas.UndoCommand.Execute(null);
        Assert.Single(canvas.Nodes);
        Assert.False(canvas.UndoCommand.CanExecute(null));

        canvas.RedoCommand.Execute(null);
        Assert.Equal(2, canvas.Nodes.Count);
        Assert.Single(canvas.Links);
        canvas.RedoCommand.Execute(null);
        Assert.Equal(65, NodeValues.Number(canvas.Find(detect.Id)!.Settings[NodeKinds.MinScoreKey]));

        // A move is one step; a new change clears what Redo had.
        var node = canvas.Find(detect.Id)!;
        canvas.BeginMove();
        canvas.MoveNode(node, 400, 300);
        canvas.MoveNode(node, 420, 310);
        canvas.EndMove();
        canvas.UndoCommand.Execute(null);
        Assert.Equal(detect.X, canvas.Find(detect.Id)!.X);
        canvas.ForgetHistory();
        Assert.False(canvas.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Copy_and_paste_make_a_free_named_copy_without_links()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        canvas.SelectedNode = canvas.Find("a");
        Assert.False(canvas.CopyCommand.CanExecute(null)); // one camera per pipeline
        canvas.SelectedNode = canvas.Find("h");
        canvas.CopyCommand.Execute(null);
        canvas.PasteCommand.Execute(null);
        var copy = canvas.SelectedNode!;
        Assert.NotEqual("h", copy.Id);
        Assert.Equal(NodeKinds.Area, copy.Kind);
        Assert.Equal("Area 1", copy.Name);
        Assert.Equal(canvas.Find("h")!.Settings.ToJsonString(), copy.Settings.ToJsonString());
        Assert.Null(canvas.InputOf(copy));
        Assert.Equal(canvas.Find("h")!.X + 40, copy.X);
        canvas.PasteCommand.Execute(null);
        Assert.Equal("Area 2", canvas.SelectedNode!.Name);
    }

    [Fact]
    public void Names_take_the_lowest_free_number()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        Assert.Equal("Track 2", canvas.FreeName("Track"));
        Assert.Equal("Detect objects 2", canvas.FreeName("Detect objects"));
        Assert.Equal("Count 1", canvas.FreeName("Count"));
    }

    [Fact]
    public void An_area_or_line_added_on_the_canvas_asks_to_draw_at_once()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        NodeViewModel? asked = null;
        canvas.DrawRequested += (_, node) => asked = node;
        var area = canvas.Add(NodeKinds.AreaKind, canvas.Find("d"))!;
        Assert.Same(area, asked);
        Assert.True(area.ShowOnVideo);
        Assert.NotNull(area.Error); // no shape yet
    }

    [Fact]
    public void Tidy_up_lays_out_from_top_to_bottom_with_the_branches_side_by_side()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        foreach (var node in canvas.Nodes)
        {
            node.X = 5;
            node.Y = 5;
        }

        canvas.TidyUpCommand.Execute(null);
        double Row(string id) => canvas.Find(id)!.Y;
        double Col(string id) => canvas.Find(id)!.X;
        Assert.True(Row("a") < Row("b") && Row("b") < Row("c") && Row("c") < Row("d") && Row("d") < Row("e") && Row("e") < Row("f") && Row("f") < Row("g"));
        Assert.Equal(Row("e"), Row("h")); // the two branches after Track in one row, side by side
        Assert.NotEqual(Col("e"), Col("h"));
        Assert.Equal(Col("e"), Col("g")); // each branch a column
        Assert.Equal((Col("e") + Col("h")) / 2, Col("d"), 0); // the parent centred above its children
        Assert.Equal(Col("d"), Col("a"));
        Assert.True(canvas.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void A_new_node_goes_below_its_parent_and_a_second_child_beside_the_first()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        var gate = canvas.Find("g")!;
        var count = canvas.Find("f")!;
        var second = canvas.Add(NodeKinds.EventKind, count)!;
        Assert.True(second.Y > count.Y);
        Assert.NotEqual(gate.X, second.X); // the first child already below
        Assert.True(second.Y >= count.Y + count.Height); // below its parent
        var below = canvas.Add(NodeKinds.EventKind, canvas.Find("h")!)!;
        Assert.True(below.Y > canvas.Find("h")!.Y);
    }

    [Fact]
    public void Links_run_from_the_bottom_of_an_output_to_the_top_of_an_input()
    {
        var node = new NodeViewModel(new PipelineNode { Id = "x", Kind = NodeKinds.Track, X = 100, Y = 200 });
        Assert.Equal(new Point(100 + (CanvasGeometry.NodeWidth / 2), 200), CanvasGeometry.InputPort(node.X, node.Y));
        Assert.Equal(new Point(100 + (CanvasGeometry.NodeWidth / 2), 200 + CanvasGeometry.NodeHeight), CanvasGeometry.OutputPort(node.X, node.Y));
        var plus = CanvasGeometry.PlusRect(node.X, node.Y);
        Assert.True(plus.Top > 200 + CanvasGeometry.NodeHeight);
        Assert.Equal(CanvasGeometry.OutputPort(node.X, node.Y).X, plus.Center.X);
        Assert.True(CanvasGeometry.PlusRect(node.X, node.Y, belowError: true).Top > plus.Top);
        var (c1, c2) = CanvasGeometry.Curve(new Point(0, 0), new Point(0, 200));
        Assert.True(c1.Y > 0 && c2.Y < 200); // vertical curve
    }

    [Fact]
    public void Fit_view_shows_every_node_and_the_wheel_zooms_from_25_to_200_percent()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        canvas.FitView();
        foreach (var node in canvas.Nodes)
        {
            var topLeft = canvas.ToScreen(new Point(node.X, node.Y));
            var bottomRight = canvas.ToScreen(new Point(node.X + CanvasGeometry.NodeWidth, node.Y + CanvasGeometry.NodeHeight));
            Assert.True(topLeft.X >= 0 && topLeft.Y >= 0 && bottomRight.X <= 900 && bottomRight.Y <= 500, $"{node.Name} in the view");
        }

        for (var i = 0; i < 40; i++)
        {
            canvas.ZoomAt(new Point(100, 100), 1.1);
        }

        Assert.Equal(CanvasGeometry.MaxZoom, canvas.Zoom);
        var under = canvas.ToWorld(new Point(300, 200));
        canvas.ZoomAt(new Point(300, 200), 1 / 1.1);
        Assert.Equal(under.X, canvas.ToWorld(new Point(300, 200)).X, 6); // the point under the pointer stays
        for (var i = 0; i < 60; i++)
        {
            canvas.ZoomAt(new Point(100, 100), 1 / 1.1);
        }

        Assert.Equal(CanvasGeometry.MinZoom, canvas.Zoom);
        Assert.Equal("25 %", canvas.ZoomText);
    }

    [Fact]
    public void Hit_tests_find_ports_the_plus_the_eye_and_links()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        var track = canvas.Find("d")!;
        Assert.Same(track, canvas.NodeAt(new Point(track.X + 50, track.Y + 20)));
        Assert.Same(track, canvas.OutputPortAt(CanvasGeometry.OutputPort(track.X, track.Y)));
        Assert.Same(track, canvas.EyeAt(CanvasGeometry.EyeRect(track.X, track.Y).Center));
        Assert.Null(canvas.PlusAt(canvas.PlusOf(track).Center)); // its output leads on, not selected
        canvas.SelectedNode = track;
        Assert.Same(track, canvas.PlusAt(canvas.PlusOf(track).Center));
        var gate = canvas.Find("g")!;
        Assert.Null(canvas.OutputPortAt(CanvasGeometry.OutputPort(gate.X, gate.Y))); // Event has no output

        var link = canvas.Links.First(l => l.From.Id == "b");
        var middle = CanvasGeometry.OnCurve(CanvasGeometry.OutputPort(link.From.X, link.From.Y), CanvasGeometry.InputPort(link.To.X, link.To.Y), 0.5);
        Assert.Same(link, canvas.LinkAt(middle));
        canvas.ToggleEye(track);
        Assert.False(track.ShowOnVideo);
    }

    [Fact]
    public void Live_outputs_update_the_cards_and_links_in_place()
    {
        var canvas = Canvas(PageSamples.GatePeople);
        var nodes = canvas.Nodes.ToList();
        var version = canvas.Version;
        canvas.ApplyOutputs(PageSamples.Preview("x").Nodes);
        Assert.Equal(nodes, canvas.Nodes); // the same node view models: nothing rebuilt
        Assert.True(canvas.Version > version);
        Assert.Equal("5 objects · 18 ms", canvas.Find("b")!.Live);
        Assert.Equal("In 12 · Out 7", canvas.Find("e")!.Live);
        Assert.Equal("5", canvas.Links.First(l => l.From.Id == "b").Count);
        Assert.Equal("3", canvas.Links.First(l => l.From.Id == "d" && l.To.Id == "e").Count);
        Assert.Null(canvas.Links.First(l => l.From.Id == "e").Count); // crossings carry no object count
    }

    [Fact]
    public void On_action_the_graph_check_puts_its_errors_on_the_nodes()
    {
        var canvas = Canvas(PageSamples.DockTrucks);
        Assert.Equal(GraphTexts.LineNeedsTrack, canvas.Find("d")!.Error);
        Assert.True(canvas.ErrorCount > 0);
        Assert.Null(canvas.Find("b")!.Error);
        Assert.Equal(VisionTexts.NotRunning, canvas.Find("d")!.Live);
    }
}

/// <summary>The labels on the video never cover each other.</summary>
public sealed class OverlayLabelTests
{
    private static readonly Rect Picture = new(0, 0, 640, 360);

    private static Size Measure(string label) => new(label.Length * 6, 12);

    private static OverlayItem Box(string label, string? shortLabel) =>
        new(OverlayItemKind.Box, "b", "Oadm.Tag.TealBrush", [], label, ShortLabel: shortLabel);

    [Fact]
    public void Close_boxes_put_their_labels_inside_shorten_them_or_leave_them_out_never_overlapping()
    {
        var taken = new List<Rect>();
        var placed = new List<(string Label, Rect At)>();
        // Five people side by side, 20 px apart: the labels "person 88 %" are wider than the gap.
        for (var i = 0; i < 5; i++)
        {
            var box = new Rect(100 + (i * 20), 100 + (i % 2 * 4), 18, 80);
            if (VideoOverlay.PlaceLabel(Box("person 88 %", "88 %"), box, Picture, Measure, taken) is { } p)
            {
                placed.Add(p);
                taken.Add(p.At);
            }
        }

        Assert.Equal("person 88 %", placed[0].Label); // the first one whole, above its box
        Assert.Contains(placed, p => p.At.Top > 100); // another inside its box's top edge
        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                Assert.False(placed[i].At.Intersects(placed[j].At), $"{i} and {j} overlap");
            }

            Assert.True(Picture.Contains(placed[i].At));
        }
    }

    [Fact]
    public void A_label_without_room_keeps_only_its_score()
    {
        var box = new Rect(100, 100, 40, 80);
        var taken = new List<Rect> { new(140, 80, 160, 45) };
        Assert.Equal("88 %", VideoOverlay.PlaceLabel(Box("person 88 %", "88 %"), box, Picture, Measure, taken)!.Value.Label);
    }

    [Fact]
    public void A_label_without_a_short_form_is_left_out_when_it_has_no_room()
    {
        var box = new Rect(100, 100, 40, 80);
        var taken = new List<Rect> { new(80, 80, 120, 60) };
        Assert.Null(VideoOverlay.PlaceLabel(Box("person", null), box, Picture, Measure, taken));
        Assert.NotNull(VideoOverlay.PlaceLabel(Box("person", null), box, Picture, Measure, []));
    }
}

/// <summary>The resizer between the canvas and the right column, and the pointer over the canvas.</summary>
public sealed class PageLayoutTests
{
    [Fact]
    public void The_right_column_starts_at_its_minimum_and_a_drag_never_takes_the_canvas_below_its_own()
    {
        var wide = PageLayout.LimitsOf(1920, 16);
        Assert.Equal(PageLayout.CanvasMinWidth, wide.CanvasMin);
        Assert.Equal(PageLayout.RightMinWidth, wide.RightMin);
        Assert.Equal(1920 - PageLayout.PipelinesWidth - 16 - PageLayout.CanvasMinWidth, wide.RightMax);
    }

    [Fact]
    public void A_small_window_keeps_the_canvas_minimum_first_then_the_right_column()
    {
        // Room for the canvas and part of the right column: the canvas keeps its minimum, the right column the rest.
        var narrow = PageLayout.LimitsOf(PageLayout.PipelinesWidth + 16 + PageLayout.CanvasMinWidth + 200, 16);
        Assert.Equal(PageLayout.CanvasMinWidth, narrow.CanvasMin);
        Assert.Equal(200, narrow.RightMin);
        Assert.Equal(200, narrow.RightMax);

        // Not even room for the canvas: it takes all, the right column nothing; never below zero.
        var tiny = PageLayout.LimitsOf(600, 16);
        Assert.Equal(600 - PageLayout.PipelinesWidth - 16, tiny.CanvasMin);
        Assert.Equal(0, tiny.RightMin);
        Assert.Equal(0, PageLayout.LimitsOf(100, 16).CanvasMin);
    }

    [Fact]
    public void Everything_a_click_acts_on_shows_the_hand_and_an_output_dot_the_crosshair()
    {
        var pipeline = PageSamples.GatePeople;
        var canvas = new CanvasViewModel(pipeline.Nodes, pipeline.Links);
        var track = canvas.Find("d")!;
        canvas.SelectedNode = track;
        Assert.Equal(Avalonia.Input.StandardCursorType.Hand, canvas.CursorAt(canvas.PlusOf(track).Center));
        Assert.Equal(Avalonia.Input.StandardCursorType.Hand, canvas.CursorAt(CanvasGeometry.EyeRect(track.X, track.Y).Center));
        Assert.Equal(Avalonia.Input.StandardCursorType.Hand, canvas.CursorAt(new Point(track.X + 40, track.Y + 40)));
        Assert.Equal(Avalonia.Input.StandardCursorType.Cross, canvas.CursorAt(CanvasGeometry.OutputPort(track.X, track.Y)));
        Assert.Null(canvas.CursorAt(new Point(-500, -500)));
        canvas.CanEdit = false;
        Assert.NotEqual(Avalonia.Input.StandardCursorType.Cross, canvas.CursorAt(CanvasGeometry.OutputPort(track.X, track.Y)));
    }
}

/// <summary>The resizer between the video card and the node card of the right column.</summary>
public sealed class RightColumnLayoutTests
{
    [Fact]
    public void The_video_starts_at_its_16_by_9_height_and_a_drag_keeps_both_minimums()
    {
        var rows = PageLayout.RowsOf(1000, 420, 16, 32);
        Assert.Equal(PageLayout.VideoMinHeight + 32, rows.VideoMin);
        Assert.Equal(PageLayout.NodeMinHeight, rows.NodeMin);
        Assert.Equal(1000 - 16 - PageLayout.NodeMinHeight, rows.VideoMax);
        Assert.Equal(rows.VideoMin, rows.VideoFirst); // a narrow column: the least picture
        Assert.Equal(Math.Round((600 - 32) * 9 / 16.0) + 32, PageLayout.RowsOf(1000, 600, 16, 32).VideoFirst);

        // A wide column: the first height follows the width, never beyond what leaves the node card its minimum.
        var wide = PageLayout.RowsOf(800, 1400, 16, 32);
        Assert.Equal(wide.VideoMax, wide.VideoFirst);
    }

    [Fact]
    public void A_short_column_keeps_the_video_first_then_the_node_card()
    {
        var shortColumn = PageLayout.RowsOf(PageLayout.VideoMinHeight + 32 + 16 + 100, 420, 16, 32);
        Assert.Equal(PageLayout.VideoMinHeight + 32, shortColumn.VideoMin);
        Assert.Equal(100, shortColumn.NodeMin);
        var tiny = PageLayout.RowsOf(200, 420, 16, 32);
        Assert.Equal(184, tiny.VideoMin); // the cards never overflow the column
        Assert.Equal(0, tiny.NodeMin);
    }
}
