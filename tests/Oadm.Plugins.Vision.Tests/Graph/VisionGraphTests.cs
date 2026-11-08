using System.Diagnostics;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.Nodes;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Graph;

public sealed class VisionGraphTests(ITestOutputHelper output)
{
    private static GraphCheck Check(Pipeline p) => VisionGraph.Check(p, PipelineBuilder.Find);

    private static JsonObject Line() => PipelineBuilder.Shape((0.1, 0.5), (0.9, 0.5));

    private static JsonObject Area() => PipelineBuilder.Shape((0.1, 0.1), (0.9, 0.1), (0.9, 0.9));

    /// <summary>Camera > Detect > Filter > Track > Line > Count > Event, the pipeline of the spec's test.</summary>
    private static (PipelineBuilder B, string Camera, string Detect, string Track, string Line, string Count) Gate()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("person") });
        var track = b.After(filter, NodeKinds.Track);
        var line = b.After(track, NodeKinds.Line, Line());
        var count = b.After(line, NodeKinds.Count);
        b.After(count, NodeKinds.Event);
        return (b, camera, detect, track, line, count);
    }

    private static string ErrorOf(Pipeline p, string nodeId)
    {
        var check = Check(p);
        Assert.True(check.NodeErrors.TryGetValue(nodeId, out var error), $"no error on {nodeId}: {string.Join(", ", check.NodeErrors)}");
        return error;
    }

    [Fact]
    public void The_pipeline_of_the_spec_passes_with_its_input_kinds()
    {
        var (b, camera, detect, track, line, count) = Gate();
        var check = Check(b.Build());
        Assert.True(check.Ok, string.Join(", ", check.NodeErrors.Select(e => $"{e.Key}: {e.Value}").Concat(check.GraphErrors)));
        Assert.False(check.InputKinds.ContainsKey(camera));
        Assert.Equal(PortKind.Picture, check.InputKinds[detect]);
        Assert.Equal(PortKind.Objects, check.InputKinds[track]);
        Assert.Equal(PortKind.TrackedObjects, check.InputKinds[line]);
        Assert.Equal(PortKind.Crossings, check.InputKinds[count]);
    }

    [Fact]
    public void A_pipeline_without_a_camera_is_a_graph_error()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Detect, settings: PipelineBuilder.DetectSettings());
        Assert.Contains(GraphTexts.NoCamera, Check(b.Build()).GraphErrors);
    }

    [Fact]
    public void A_second_camera_has_an_error()
    {
        var (b, _, _, _, _, _) = Gate();
        var second = b.Add(NodeKinds.Camera);
        Assert.Equal(GraphTexts.SecondCamera, ErrorOf(b.Build(), second));
    }

    [Fact]
    public void A_camera_takes_no_input()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        b.Link(detect, camera);
        Assert.Equal(GraphTexts.CameraInput, ErrorOf(b.Build(), camera));
    }

    [Fact]
    public void A_node_without_input_says_so()
    {
        var (b, _, _, _, _, _) = Gate();
        var lone = b.Add(NodeKinds.Filter);
        Assert.Equal(GraphTexts.NoInput, ErrorOf(b.Build(), lone));
    }

    [Fact]
    public void A_loop_is_refused_on_its_nodes()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var f1 = b.Add(NodeKinds.Filter);
        var f2 = b.After(f1, NodeKinds.Filter);
        b.Link(f2, f1);
        var p = b.Build();
        Assert.Equal(GraphTexts.Loop, ErrorOf(p, f1));
        Assert.Equal(GraphTexts.Loop, ErrorOf(p, f2));
    }

    [Fact]
    public void A_node_linked_to_itself_is_a_loop()
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var f = b.Add(NodeKinds.Filter);
        b.Link(f, f);
        Assert.Equal(GraphTexts.Loop, ErrorOf(b.Build(), f));
    }

    [Fact]
    public void A_merge_of_two_branches_is_refused()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var a = b.After(detect, NodeKinds.Filter);
        var c = b.After(detect, NodeKinds.Filter);
        var count = b.After(a, NodeKinds.Count);
        b.Link(c, count);
        Assert.Equal(GraphTexts.Merge, ErrorOf(b.Build(), count));
    }

    [Fact]
    public void Branches_from_one_output_pass()
    {
        var (b, _, _, track, _, _) = Gate();
        var line2 = b.After(track, NodeKinds.Line, Line());
        b.After(line2, NodeKinds.Count);
        Assert.True(Check(b.Build()).Ok);
    }

    [Fact]
    public void A_link_to_a_node_that_does_not_exist_is_a_graph_error()
    {
        var (b, camera, _, _, _, _) = Gate();
        b.Link(camera, "nothere");
        Assert.Contains(GraphTexts.MissingLinkEnd, Check(b.Build()).GraphErrors);
    }

    [Theory]
    [InlineData(NodeKinds.Track, "This node takes objects.")]
    [InlineData(NodeKinds.Count, "This node takes objects or crossings.")]
    [InlineData(NodeKinds.Event, "This node takes objects, crossings or a number.")]
    public void A_picture_into_a_node_of_objects_is_refused(string kind, string text)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var node = b.After(camera, kind);
        Assert.Equal(text, ErrorOf(b.Build(), node));
    }

    [Fact]
    public void Objects_into_Detect_is_refused()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var d1 = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var d2 = b.After(d1, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        Assert.Equal("This node takes a picture.", ErrorOf(b.Build(), d2));
    }

    [Fact]
    public void A_node_after_an_Event_has_no_input()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var ev = b.After(detect, NodeKinds.Event);
        var count = b.After(ev, NodeKinds.Count);
        Assert.Equal(GraphTexts.NoOutputBefore, ErrorOf(b.Build(), count));
    }

    [Fact]
    public void A_line_needs_a_track_before_it()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var line = b.After(detect, NodeKinds.Line, Line());
        Assert.Equal("A line needs a Track node before it.", ErrorOf(b.Build(), line));
    }

    [Fact]
    public void Track_Line_and_counting_crossings_work_only_while_watching()
    {
        var (b, _, _, track, line, count) = Gate();
        b.Mode = PipelineMode.OnAction;
        var p = b.Build();
        Assert.Equal("Track works only while Watching.", ErrorOf(p, track));
        Assert.Equal("Line works only while Watching.", ErrorOf(p, line));
        Assert.Equal("Counting crossings works only while Watching.", ErrorOf(p, count));
    }

    [Fact]
    public void Counting_objects_works_on_action()
    {
        var b = new PipelineBuilder { Mode = PipelineMode.OnAction };
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var count = b.After(detect, NodeKinds.Count);
        b.After(count, NodeKinds.Event);
        b.After(detect, NodeKinds.Event);
        Assert.True(Check(b.Build()).Ok);
    }

    [Fact]
    public void More_than_32_nodes_is_a_graph_error()
    {
        var b = new PipelineBuilder();
        var last = b.Add(NodeKinds.Camera);
        last = b.After(last, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        for (var i = 0; i < 31; i++)
        {
            last = b.After(last, NodeKinds.Filter);
        }

        Assert.Contains("A pipeline has at most 32 nodes.", Check(b.Build()).GraphErrors);
    }

    [Fact]
    public void A_third_model_node_on_one_path_is_refused()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var c1 = b.After(detect, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        var c2 = b.After(c1, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        var p = b.Build();
        Assert.False(Check(p).NodeErrors.ContainsKey(c1));
        Assert.Equal("A path has at most 2 model nodes.", ErrorOf(p, c2));
    }

    [Fact]
    public void Model_nodes_on_two_paths_count_apart()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var d1 = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var d2 = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        b.After(d1, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        b.After(d2, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        Assert.True(Check(b.Build()).Ok);
    }

    [Fact]
    public void A_model_node_without_a_model_asks_for_one()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect);
        var classify = b.After(detect, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = "00000000000000000000000000000000" });
        var p = b.Build();
        Assert.Equal("Choose a detection model.", ErrorOf(p, detect));
        Assert.Equal("Choose a classification model.", ErrorOf(p, classify));
    }

    [Fact]
    public void A_model_of_the_wrong_kind_is_refused()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        Assert.Equal("Choose a detection model.", ErrorOf(b.Build(), detect));
    }

    [Fact]
    public void A_missing_model_asks_for_its_upload()
    {
        var missing = PipelineBuilder.Detector with { Name = "Gate people", Missing = true };
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var check = VisionGraph.Check(b.Build(), id => id == missing.Id ? missing : null);
        Assert.Equal("Upload the model Gate people again.", check.NodeErrors[detect]);
    }

    [Fact]
    public void Classes_must_exist_in_the_model_before_them()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings(50, "person", "horse"));
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("dog") });
        var p = b.Build();
        Assert.Equal("The model People has no class horse.", ErrorOf(p, detect));
        Assert.Equal("The model People has no class dog.", ErrorOf(p, filter));
    }

    [Fact]
    public void A_gap_in_the_class_ids_is_no_class()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings(50, VisionLimits.NoClassName));
        Assert.Equal("The model People has no class N/A.", ErrorOf(b.Build(), detect));
    }

    [Fact]
    public void The_class_of_Classify_needs_a_Classify_node_with_that_class()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var f1 = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.LabelKey] = "red" });
        var classify = b.After(detect, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        var f2 = b.After(classify, NodeKinds.Filter, new JsonObject { [NodeKinds.LabelKey] = "green" });
        var f3 = b.After(classify, NodeKinds.Filter, new JsonObject { [NodeKinds.LabelKey] = "blue" });
        var p = b.Build();
        Assert.Equal("Class of Classify needs a Classify node before it.", ErrorOf(p, f1));
        Assert.Equal("The model Colours has no class green.", ErrorOf(p, f2));
        Assert.False(Check(p).NodeErrors.ContainsKey(f3));
    }

    [Theory]
    [InlineData("\"high\"", "Enter a number.")]
    [InlineData("0", "Enter a number from 1 to 99.")]
    [InlineData("100", "Enter a number from 1 to 99.")]
    public void A_number_out_of_its_field_is_refused(string json, string text)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var settings = PipelineBuilder.DetectSettings();
        settings[NodeKinds.MinScoreKey] = JsonNode.Parse(json);
        var detect = b.After(camera, NodeKinds.Detect, settings);
        Assert.Equal(text, ErrorOf(b.Build(), detect));
    }

    [Fact]
    public void A_choice_must_be_one_of_its_options()
    {
        var (b, _, _, track, _, _) = Gate();
        var area = b.After(track, NodeKinds.Area, new JsonObject { [NodeKinds.PointsKey] = Area()[NodeKinds.PointsKey]!.DeepClone(), [NodeKinds.AnchorKey] = "top" });
        Assert.Equal("Choose one of the options.", ErrorOf(b.Build(), area));
    }

    [Fact]
    public void Shapes_need_their_points_inside_the_picture()
    {
        var (b, _, _, track, _, _) = Gate();
        var none = b.After(track, NodeKinds.Area);
        var two = b.After(track, NodeKinds.Area, PipelineBuilder.Shape((0.1, 0.1), (0.5, 0.5)));
        var outside = b.After(track, NodeKinds.Area, PipelineBuilder.Shape((0.1, 0.1), (1.2, 0.1), (0.5, 0.5)));
        var longLine = b.After(track, NodeKinds.Line, PipelineBuilder.Shape([.. Enumerable.Range(0, 9).Select(i => (i * 0.1, 0.5))]));
        var p = b.Build();
        Assert.Equal("Draw the area on the video.", ErrorOf(p, none));
        Assert.Equal("An area has 3 to 16 points.", ErrorOf(p, two));
        Assert.Equal("Keep the points inside the picture.", ErrorOf(p, outside));
        Assert.Equal("A line has 2 to 8 points.", ErrorOf(p, longLine));
    }

    [Fact]
    public void A_classify_area_is_optional_and_only_for_a_picture()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        b.After(camera, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());

        // The area of Classify is not shown on objects, so a bad one there is no error.
        var settings = new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id, [NodeKinds.PointsKey] = "bad" };
        b.After(detect, NodeKinds.Classify, settings);
        Assert.True(Check(b.Build()).Ok);
    }

    [Fact]
    public void A_daily_reset_needs_a_time()
    {
        var (b, _, _, _, line, _) = Gate();
        var count = b.After(line, NodeKinds.Count, new JsonObject { [NodeKinds.ResetAtKey] = "25:00" });
        var ok = b.After(line, NodeKinds.Count, new JsonObject { [NodeKinds.ResetAtKey] = "06:00" });
        var p = b.Build();
        Assert.Equal("Enter a time like 06:00.", ErrorOf(p, count));
        Assert.False(Check(p).NodeErrors.ContainsKey(ok));
    }

    [Fact]
    public void Classes_must_be_a_list_of_names()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var settings = PipelineBuilder.DetectSettings();
        settings[NodeKinds.ClassesKey] = "person";
        var detect = b.After(camera, NodeKinds.Detect, settings);
        Assert.Equal("Choose the classes.", ErrorOf(b.Build(), detect));
    }

    [Fact]
    public void A_minimum_size_above_the_maximum_is_refused()
    {
        var (b, _, detect, _, _, _) = Gate();
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.MinSizeKey] = 50, [NodeKinds.MaxSizeKey] = 10 });
        Assert.Equal("The minimum size is above the maximum size.", ErrorOf(b.Build(), filter));
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("Line\t1", "Use no control characters.")]
    public void Names_must_be_given_and_plain(string name, string text)
    {
        var (b, _, detect, _, _, _) = Gate();
        var filter = b.After(detect, NodeKinds.Filter, name: "x");
        var p = b.Build();
        p = p with { Nodes = [.. p.Nodes.Select(n => n.Id == filter ? n with { Name = name } : n)] };
        Assert.Equal(text, ErrorOf(p, filter));
    }

    [Fact]
    public void A_name_has_at_most_64_characters()
    {
        var (b, _, detect, _, _, _) = Gate();
        var filter = b.After(detect, NodeKinds.Filter, name: "x");
        var p = b.Build();
        p = p with { Nodes = [.. p.Nodes.Select(n => n.Id == filter ? n with { Name = new string('a', 65) } : n)] };
        Assert.Equal("Use at most 64 characters.", ErrorOf(p, filter));
    }

    [Fact]
    public void A_name_is_unique_whatever_its_case()
    {
        var (b, _, detect, _, _, _) = Gate();
        var p = b.Build();
        var copy = new PipelineNode { Id = "copy", Kind = NodeKinds.Filter, Name = "TRACK" };
        p = p with { Nodes = [.. p.Nodes, copy], Links = [.. p.Links, new PipelineLink(detect, "copy")] };
        Assert.Equal("Another node has this name.", ErrorOf(p, "copy"));
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc")]
    public void A_node_id_is_letters_and_digits_at_most_32(string id)
    {
        var (b, _, detect, _, _, _) = Gate();
        var p = b.Build();
        p = p with
        {
            Nodes = [.. p.Nodes, new PipelineNode { Id = id, Kind = NodeKinds.Filter, Name = "Odd" }],
            Links = [.. p.Links, new PipelineLink(detect, id)],
        };
        Assert.Equal("This node's id is not valid.", ErrorOf(p, id));
    }

    [Fact]
    public void Two_nodes_with_one_id_are_refused()
    {
        var (b, _, _, track, _, _) = Gate();
        var p = b.Build();
        p = p with { Nodes = [.. p.Nodes, new PipelineNode { Id = track, Kind = NodeKinds.Filter, Name = "Twin" }] };
        Assert.Equal("Two nodes have the same id.", ErrorOf(p, track));
    }

    [Fact]
    public void An_unknown_node_kind_needs_a_newer_version()
    {
        var (b, _, _, track, _, _) = Gate();
        var future = b.After(track, "heatmap");
        Assert.Equal("This node needs a newer Vision Center.", ErrorOf(b.Build(), future));
    }

    [Fact]
    public void A_newer_pipeline_format_is_a_graph_error()
    {
        var (b, _, _, _, _, _) = Gate();
        b.Version = Pipeline.CurrentVersion + 1;
        var check = Check(b.Build());
        Assert.Equal([GraphTexts.NewerVersion], check.GraphErrors);
        Assert.False(check.Ok);
    }

    [Fact]
    public void One_error_per_node_the_first_that_applies()
    {
        // No input and a bad score: the input comes first.
        var (b, _, _, _, _, _) = Gate();
        var settings = PipelineBuilder.DetectSettings();
        settings[NodeKinds.MinScoreKey] = 0;
        var detect = b.Add(NodeKinds.Detect, "Detect objects", settings);
        Assert.Equal(GraphTexts.NoInput, ErrorOf(b.Build(), detect));
    }

    [Fact]
    public void Errors_after_an_errored_node_still_show_on_their_own_node()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var settings = PipelineBuilder.DetectSettings();
        settings[NodeKinds.MinScoreKey] = 0;
        var detect = b.After(camera, NodeKinds.Detect, settings);
        var line = b.After(detect, NodeKinds.Line, Line());
        var p = b.Build();
        Assert.Equal("Enter a number from 1 to 99.", ErrorOf(p, detect));
        Assert.Equal(GraphTexts.LineNeedsTrack, ErrorOf(p, line));
    }

    [Fact]
    public void Setting_gives_the_value_or_the_fields_default()
    {
        var node = new PipelineNode { Id = "a", Kind = NodeKinds.Detect, Name = "D", Settings = new JsonObject { [NodeKinds.MinScoreKey] = 30 } };
        Assert.Equal(30, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinScoreKey)));
        Assert.Empty(NodeSettings.Texts(VisionGraph.Setting(node, NodeKinds.ClassesKey))!);
        var area = new PipelineNode { Id = "b", Kind = NodeKinds.Area, Name = "A" };
        Assert.Equal(NodeKinds.AnchorBottom, NodeSettings.Text(VisionGraph.Setting(area, NodeKinds.AnchorKey)));
        Assert.Null(VisionGraph.Setting(area, NodeKinds.PointsKey));
        Assert.Null(VisionGraph.Setting(area, "unknown"));
    }

    [Fact]
    public void The_check_is_fast_and_deterministic_for_32_nodes()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var track = b.After(detect, NodeKinds.Track);
        for (var i = 0; i < 9; i++)
        {
            var line = b.After(track, NodeKinds.Line, Line());
            b.After(b.After(line, NodeKinds.Count), NodeKinds.Event);
        }

        var p = b.Build();
        Assert.Equal(30, p.Nodes.Count);
        var first = Check(p);
        Assert.True(first.Ok);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            var again = Check(p);
            Assert.Equal(first.InputKinds, again.InputKinds);
        }

        output.WriteLine($"200 checks of 30 nodes: {watch.Elapsed.TotalMilliseconds:0.0} ms");
    }
}
