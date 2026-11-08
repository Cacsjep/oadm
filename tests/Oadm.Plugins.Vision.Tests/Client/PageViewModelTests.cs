using System.Text.Json.Nodes;

using Avalonia;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>The page's view models without a window: the node form from its fields, the preview, uploads, the overlay, the list.</summary>
public sealed class PageViewModelTests
{
    private static PipelineEditorViewModel Editor(FakePageContext ctx, Pipeline pipeline, bool isNew = false) =>
        new(ctx, pipeline, isNew, new EngineInfo(), id => id == PageSamples.Gate.Id ? "AXIS Q1798-LE · 10.0.0.48" : "AXIS P1468-LE · 10.0.0.202",
            (_, _) => false, () => PageSamples.Models, PageSamples.Decode, PageSamples.Check);

    // ---- The form from the fields ----

    [Fact]
    public void Every_field_type_draws_its_field_and_an_untouched_form_shows_no_errors()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        foreach (var node in editor.Canvas.Nodes.Where(n => !n.IsCamera))
        {
            editor.Canvas.SelectedNode = node;
            var form = editor.Form!;
            var fields = node.Info!.Fields.Where(f => VisionGraph.Applies(f, node.InputKind)).ToList();
            Assert.Equal(fields.Select(f => f.Key), form.Fields.Select(f => f.Field.Key));
            foreach (var field in form.Fields)
            {
                var expected = field.Field.Type switch
                {
                    NodeFieldType.Number => typeof(NumberFieldViewModel),
                    NodeFieldType.Choice => typeof(ChoiceFieldViewModel),
                    NodeFieldType.Model => typeof(ModelFieldViewModel),
                    NodeFieldType.Classes => typeof(ClassesFieldViewModel),
                    NodeFieldType.Shape => typeof(ShapeFieldViewModel),
                    NodeFieldType.TimeOfDay => typeof(TimeFieldViewModel),
                    NodeFieldType.Bool => typeof(BoolFieldViewModel),
                    _ => typeof(TextFieldViewModel),
                };
                Assert.IsType(expected, field);
                Assert.False(field.HasErrors, $"{node.Name}: {field.Label} shows no error before an edit");
            }

            Assert.False(form.HasErrors);
        }
    }

    [Fact]
    public void Fields_follow_the_nodes_input_kind()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        editor.Canvas.SelectedNode = editor.Canvas.Find("f"); // Count: no fields (a Timer resets it)
        Assert.Empty(editor.Form!.Fields);
        Assert.False(editor.Form.HasFields);
        editor.Canvas.SelectedNode = editor.Canvas.Find("i"); // Event on objects: Minimum objects, At least, Off after
        Assert.Equal([NodeKinds.MinObjectsKey, NodeKinds.AtLeastKey, NodeKinds.OffAfterKey], editor.Form!.Fields.Select(f => f.Field.Key));
        editor.Canvas.SelectedNode = editor.Canvas.Find("g"); // Event on a number: none
        Assert.Empty(editor.Form!.Fields);

        using var lobby = Editor(PageSamples.Context(), PageSamples.LobbyCount);
        lobby.Canvas.SelectedNode = lobby.Canvas.Find("f"); // Event on a yes or no: At least, Off after
        Assert.Equal([NodeKinds.AtLeastKey, NodeKinds.OffAfterKey], lobby.Form!.Fields.Select(f => f.Field.Key));
    }

    [Fact]
    public void An_edit_writes_the_node_and_a_wrong_value_shows_its_error_below_the_field()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        var filter = editor.Canvas.Find("c")!;
        editor.Canvas.SelectedNode = filter;
        var score = Assert.IsType<NumberFieldViewModel>(editor.Form!.Fields.Single(f => f.Field.Key == NodeKinds.MinScoreKey));
        Assert.Equal("60", score.Text);
        Assert.Equal("%", score.Unit);

        score.Text = "75";
        Assert.Equal(75, NodeValues.Number(filter.Settings[NodeKinds.MinScoreKey]));
        Assert.True(editor.IsDirty);

        score.Text = "150";
        Assert.Equal(GraphTexts.NumberRange(1, 99), score.ErrorOf(nameof(NumberFieldViewModel.Text)));
        Assert.Equal(75, NodeValues.Number(filter.Settings[NodeKinds.MinScoreKey])); // the node keeps its value
        Assert.NotNull(editor.SaveBlockedReason);
        Assert.False(editor.SaveCommand.CanExecute(null));

        var maxSize = Assert.IsType<NumberFieldViewModel>(editor.Form.Fields.Single(f => f.Field.Key == NodeKinds.MaxSizeKey));
        Assert.Equal(VisionTexts.Optional, maxSize.Placeholder);
        maxSize.Text = string.Empty;
        Assert.False(maxSize.HasErrors);

        var classes = Assert.IsType<ClassesFieldViewModel>(editor.Form.Fields.Single(f => f.Field.Key == NodeKinds.ClassesKey));
        Assert.Equal(PageSamples.Coco, classes.Names); // the classes of the detection model before it
        Assert.Equal("person", classes.Summary);
        classes.Choices.Items.Single(i => i.Name == "car").Chosen = true;
        Assert.Equal(["person", "car"], NodeValues.Strings(filter.Settings[NodeKinds.ClassesKey]));

        editor.Form.Name = string.Empty;
        Assert.Equal(GraphTexts.EnterName, editor.Form.ErrorOf(nameof(NodeFormViewModel.Name)));
        editor.Form.Name = "Track";
        Assert.Equal(GraphTexts.NameTaken, editor.Form.ErrorOf(nameof(NodeFormViewModel.Name)));
        editor.Form.Name = "Adults";
        Assert.Equal("Adults", filter.Name);
    }

    [Fact]
    public void Model_choice_and_time_fields_check_their_values()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        editor.Canvas.SelectedNode = editor.Canvas.Find("b");
        var model = Assert.IsType<ModelFieldViewModel>(editor.Form!.Fields[0]);
        Assert.Equal(["m1", "m2"], model.Models.Select(m => m.Id)); // detection models only
        Assert.Equal("m1", model.Selected!.Id);
        model.Selected = null;
        Assert.Equal(GraphTexts.ChooseModel, model.ErrorOf(nameof(ModelFieldViewModel.Selected)));
        Assert.Equal(GraphTexts.ChooseDetectionModel, editor.Canvas.Find("b")!.Error);

        editor.Canvas.SelectedNode = editor.Canvas.Find("h");
        var anchor = Assert.IsType<ChoiceFieldViewModel>(editor.Form!.Fields.Single(f => f.Field.Key == NodeKinds.AnchorKey));
        Assert.Equal(NodeKinds.AnchorBottom, anchor.Selected!.Value); // the default
        anchor.Selected = anchor.Choices.Single(c => c.Value == NodeKinds.AnchorCentre);
        Assert.Equal(NodeKinds.AnchorCentre, NodeValues.Text(editor.Canvas.Find("h")!.Settings[NodeKinds.AnchorKey]));
        var shape = Assert.IsType<ShapeFieldViewModel>(editor.Form.Fields.Single(f => f.Field.Key == NodeKinds.PointsKey));
        Assert.True(shape.HasShape);
        Assert.Equal(4, shape.PointCount);

        using var lobby = Editor(PageSamples.Context(), PageSamples.LobbyCount);
        lobby.Canvas.SelectedNode = lobby.Canvas.Find("t1"); // the Timer Midnight: every day At 00:00
        var at = Assert.IsType<TimeFieldViewModel>(lobby.Form!.Fields.Single(f => f.Field.Key == NodeKinds.AtKey));
        Assert.Equal("00:00", at.Text);
        at.Text = "25:00";
        Assert.Equal(GraphTexts.EnterTime, at.ErrorOf(nameof(TimeFieldViewModel.Text)));
        at.Text = "7:30";
        Assert.Equal("07:30", NodeValues.Text(lobby.Canvas.Find("t1")!.Settings[NodeKinds.AtKey]));
    }

    [Fact]
    public void A_shape_drawn_on_the_video_goes_to_its_node()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        var area = editor.Canvas.Add(NodeKinds.AreaKind, editor.Canvas.Find("d"))!;
        Assert.Same(area, editor.DrawingNode);
        Assert.Equal(NodeKinds.Area, editor.DrawKind);
        Assert.False(Assert.IsType<ShapeFieldViewModel>(editor.Form!.Fields[0]).HasShape);

        editor.ShapeDrawn([new Point(0.1, 0.1), new Point(0.4, 0.1), new Point(0.4, 0.5), new Point(0.1, 0.5)]);
        Assert.Null(editor.DrawKind);
        Assert.Equal(4, NodeValues.Points(area.Settings[NodeKinds.PointsKey]).Count);
        Assert.True(Assert.IsType<ShapeFieldViewModel>(editor.Form.Fields[0]).HasShape);
        Assert.Null(area.Error);
        Assert.Contains(editor.Shapes, s => ReferenceEquals(s.Node, area) && s.IsSelected);

        editor.NudgeSelected(0.05, 0);
        Assert.Equal(0.15, NodeValues.Points(area.Settings[NodeKinds.PointsKey])[0].X, 6);
    }

    // ---- The preview ----

    [Fact]
    public async Task The_preview_starts_with_a_page_made_id_keeps_alive_follows_changes_and_stops()
    {
        var ctx = PageSamples.Context();
        var time = new FakeTimeProvider(PageSamples.Time);
        using var editor = Editor(ctx, PageSamples.GatePeople);
        editor.PreviewTime = time;
        editor.StartPreview();
        var id = editor.PreviewId!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 1, "the first start");
        var sent = VisionJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(VisionMethods.StartPreview));
        Assert.Equal(id, sent.PreviewId);
        Assert.Equal(9, sent.Pipeline!.Nodes.Count);

        time.Advance(VisionLimits.PreviewKeepAlive);
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 2, "the keep-alive");

        editor.Canvas.ToggleEye(editor.Canvas.Find("b")!); // any change of the draft
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 3, "the change");
        Assert.True(VisionJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(VisionMethods.StartPreview)).Pipeline!.Nodes.Single(n => n.Id == "b").ShowOnVideo);

        editor.StopPreview();
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StopPreview) == 1, "the stop");
        Assert.Equal(id, VisionJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(VisionMethods.StopPreview)).PreviewId);
        Assert.Null(editor.PreviewId);
        time.Advance(VisionLimits.PreviewKeepAlive * 3);
        Assert.Equal(3, ctx.CallsOf(VisionMethods.StartPreview));

        editor.StartPreview();
        Assert.NotEqual(id, editor.PreviewId); // new per start
        editor.StopPreview();
    }

    [Fact]
    public void A_preview_event_fills_the_live_lines_the_overlay_and_the_selected_nodes_output()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.GatePeople);
        editor.StartPreview();
        editor.ApplyPreview(PageSamples.Preview("someone else's")); // another preview: ignored
        Assert.Empty(editor.OverlayItems);
        editor.ApplyPreview(PageSamples.Preview(editor.PreviewId!));
        Assert.Equal("3 tracked", editor.Canvas.Find("d")!.Live);
        Assert.Contains(editor.OverlayItems, i => i.Kind == OverlayItemKind.Trail);

        editor.Canvas.SelectedNode = editor.Canvas.Find("b");
        Assert.True(editor.Output.IsObjects);
        Assert.Equal(5, editor.Output.Objects.Count);
        Assert.Equal("5 in this picture · 18 ms per picture", editor.Output.Summary);

        editor.Canvas.SelectedNode = editor.Canvas.Find("e");
        Assert.True(editor.Output.IsCrossings);
        Assert.Equal("12", editor.Output.In);
        Assert.Equal(2, editor.Output.Crossings.Count);
        Assert.Equal("In", editor.Output.Crossings[0].Direction);

        editor.Canvas.SelectedNode = editor.Canvas.Find("f");
        Assert.True(editor.Output.IsNumber);
        Assert.Equal("12", editor.Output.Value);

        editor.Canvas.SelectedNode = editor.Canvas.Find("g");
        Assert.True(editor.Output.IsEvent);
        Assert.Single(editor.Output.Fires);
        editor.Canvas.SelectedNode = editor.Canvas.Find("i");
        Assert.True(editor.Output.HasState);
        Assert.False(editor.Output.IsOn);
        editor.StopPreview();
    }

    [Fact]
    public void Save_is_off_with_errors_on_nodes_and_says_why()
    {
        using var editor = Editor(PageSamples.Context(), PageSamples.DockTrucks);
        Assert.Equal(VisionTexts.FixErrors(editor.Canvas.ErrorCount), editor.SaveBlockedReason);
        editor.Name = "Dock 4";
        Assert.True(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_stores_the_draft_and_undo_starts_again()
    {
        var ctx = PageSamples.Context();
        using var editor = Editor(ctx, PageSamples.GatePeople);
        Assert.False(editor.IsDirty);
        Assert.Equal(VisionTexts.Saved, editor.SaveStatus);
        editor.Canvas.ToggleEye(editor.Canvas.Find("b")!);
        editor.Canvas.TidyUpCommand.Execute(null);
        Assert.Equal(VisionTexts.NotSaved, editor.SaveStatus);
        Pipeline? saved = null;
        editor.Saved += (_, p) => saved = p;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(saved);
        Assert.False(editor.IsDirty);
        Assert.False(editor.Canvas.UndoCommand.CanExecute(null));
        var stored = VisionJson.Deserialize<SavePipelineRequest>(ctx.LastPayloadOf(VisionMethods.SavePipeline)).Pipeline!;
        Assert.Equal(editor.Canvas.Find("e")!.X, stored.Nodes.Single(n => n.Id == "e").X); // places are stored
    }

    // ---- Uploads ----

    [Fact]
    public async Task An_upload_sends_chunks_of_the_servers_size_with_progress_then_the_check()
    {
        var ctx = PageSamples.Context();
        var chunks = new List<UploadChunkRequest>();
        ctx.Answers[VisionMethods.BeginUpload] = json =>
        {
            Assert.Equal("person\ncar", VisionJson.Deserialize<BeginUploadRequest>(json).Labels);
            return VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = 4 });
        };
        var progress = new List<double>();
        var models = new ModelsViewModel(ctx);
        ctx.Answers[VisionMethods.UploadChunk] = json =>
        {
            chunks.Add(VisionJson.Deserialize<UploadChunkRequest>(json));
            return "{}";
        };
        ctx.Answers[VisionMethods.FinishUpload] = _ =>
        {
            Assert.True(models.Rows.OfType<UploadRow>().Single().IsChecking);
            return VisionJson.Serialize(new FinishUploadReply { Model = PageSamples.Helmet with { Id = "m9", Name = "Small" } });
        };
        models.Rows.CollectionChanged += (_, _) =>
        {
            foreach (var row in models.Rows.OfType<UploadRow>())
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(UploadRow.Progress))
                    {
                        progress.Add(row.Progress);
                    }
                };
            }
        };

        await models.UploadFilesAsync([new MemoryModelFile("small.onnx", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), new MemoryModelFile("labels.txt", "person\ncar"u8.ToArray())]);
        Assert.Equal([0L, 4L, 8L], chunks.Select(c => c.Offset));
        Assert.Equal([4, 4, 2], chunks.Select(c => c.Data.Length));
        Assert.All(chunks, c => Assert.Equal("u1", c.UploadId));
        Assert.Equal([0.4, 0.8, 1.0], progress);
        Assert.Empty(models.Rows.OfType<UploadRow>());
        Assert.Contains(models.Rows.OfType<ModelRow>(), r => r.Name == "Small");
    }

    [Fact]
    public async Task A_refusal_stays_in_the_row_in_the_servers_words()
    {
        var ctx = PageSamples.Context();
        ctx.Answers[VisionMethods.BeginUpload] = _ => VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = 1024 });
        ctx.Answers[VisionMethods.UploadChunk] = _ => "{}";
        ctx.Answers[VisionMethods.FinishUpload] = _ => VisionJson.Serialize(new FinishUploadReply { Refusal = "This model cannot run here: it needs a custom operator." });
        var models = new ModelsViewModel(ctx);
        await models.UploadFilesAsync([new MemoryModelFile("bad.onnx", [1, 2, 3])]);
        var row = Assert.Single(models.Rows.OfType<UploadRow>());
        Assert.Equal("This model cannot run here: it needs a custom operator.", row.Refusal);
        Assert.False(row.IsChecking);
        Assert.False(row.CancelCommand.CanExecute(null));
        row.DismissCommand.Execute(null);
        Assert.Empty(models.Rows);
    }

    [Fact]
    public async Task Cancel_ends_the_upload_on_the_server()
    {
        var ctx = PageSamples.Context();
        var models = new ModelsViewModel(ctx);
        var gate = new TaskCompletionSource<string?>();
        ctx.Answers[VisionMethods.BeginUpload] = _ => VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = 2 });
        ctx.AsyncAnswers[VisionMethods.UploadChunk] = _ => gate.Task;
        ctx.Answers[VisionMethods.CancelUpload] = _ => "{}";
        var upload = models.UploadFilesAsync([new MemoryModelFile("big.onnx", new byte[10])]);
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.UploadChunk) == 1, "the first chunk on its way");
        var row = Assert.Single(models.Rows.OfType<UploadRow>());
        Assert.True(row.CancelCommand.CanExecute(null));
        row.CancelCommand.Execute(null);
        gate.SetResult("{}");
        await upload;
        Assert.Equal(1, ctx.CallsOf(VisionMethods.UploadChunk));
        Assert.Equal("u1", VisionJson.Deserialize<UploadRequest>(ctx.LastPayloadOf(VisionMethods.CancelUpload)).UploadId);
        Assert.Equal(0, ctx.CallsOf(VisionMethods.FinishUpload));
        Assert.Empty(models.Rows.OfType<UploadRow>());
    }

    [Fact]
    public async Task A_refused_begin_shows_the_reason_in_the_row()
    {
        var ctx = PageSamples.Context();
        ctx.Answers[VisionMethods.BeginUpload] = _ => throw new InvalidOperationException("At most 20 models.");
        var models = new ModelsViewModel(ctx);
        await models.UploadFilesAsync([new MemoryModelFile("one.onnx", [1])]);
        Assert.Equal("At most 20 models.", Assert.Single(models.Rows.OfType<UploadRow>()).Refusal);
    }

    // ---- The overlay ----

    [Fact]
    public void The_overlay_draws_the_outputs_of_the_nodes_whose_eye_is_on_and_the_selected_node()
    {
        var canvas = new CanvasViewModel(PageSamples.GatePeople.Nodes, PageSamples.GatePeople.Links);
        var preview = PageSamples.Preview("x");
        var outputs = preview.Nodes.ToDictionary(o => o.NodeId);
        var (items, shapes) = VideoOverlay.Build(canvas.Nodes, null, outputs);
        // Eyes on: Track (boxes with ids and trails), Gate line, Waiting zone.
        Assert.Equal(3, items.Count(i => i.Kind == OverlayItemKind.Trail));
        Assert.Contains(items, i => i.NodeId == "d" && i.Kind == OverlayItemKind.Box && i.Label == "#3");
        Assert.DoesNotContain(items, i => i.NodeId == "b"); // the eye is off
        Assert.Contains(items, i => i.NodeId == "h" && i.Filled && i.Label == "4 s");
        var line = Assert.Single(shapes, s => s.Node.Id == "e");
        Assert.False(line.Closed);
        Assert.Equal("Gate line · In 12 · Out 7", line.Label);
        Assert.Equal("Waiting zone · 2 inside", Assert.Single(shapes, s => s.Node.Id == "h").Label);

        // Selected nodes show too: Detect (class and score), Filter (the left out dashed), Count (the number).
        (items, _) = VideoOverlay.Build(canvas.Nodes, canvas.Find("b"), outputs);
        Assert.Contains(items, i => i.NodeId == "b" && i.Label == "person 88 %");
        (items, _) = VideoOverlay.Build(canvas.Nodes, canvas.Find("c"), outputs);
        Assert.Single(items, i => i.NodeId == "c" && i.Dashed);
        (items, _) = VideoOverlay.Build(canvas.Nodes, canvas.Find("f"), outputs);
        Assert.Contains(items, i => i.NodeId == "f" && i.Kind == OverlayItemKind.Badge && i.Value == "12");

        // A node with an error draws no output.
        canvas.Find("d")!.Error = GraphTexts.NoInput;
        (items, _) = VideoOverlay.Build(canvas.Nodes, null, outputs);
        Assert.DoesNotContain(items, i => i.NodeId == "d");
    }

    [Fact]
    public void Classify_on_a_picture_shows_its_class_in_the_corner()
    {
        var node = new NodeViewModel(new PipelineNode { Id = "k", Kind = NodeKinds.Classify, Name = "Helmet", ShowOnVideo = true }) { InputKind = PortKind.Picture };
        var output = new NodeOutput { NodeId = "k", Kind = PortKind.Picture, Label = new ClassResult(0, "helmet", 0.91) };
        var (items, _) = VideoOverlay.Build([node], null, new Dictionary<string, NodeOutput> { ["k"] = output });
        Assert.Equal("helmet 91 %", Assert.Single(items).Value);
    }

    [Fact]
    public void The_in_arrow_points_right_of_the_drawn_direction()
    {
        var (_, right) = VideoOverlay.InArrow([new Point(0, 0.5), new Point(1, 0.5)]); // drawn left to right: In is down
        Assert.Equal(new Vector(0, 1), right);
    }

    // ---- The page ----

    [Fact]
    public async Task The_page_reads_the_lists_opens_the_first_pipeline_and_searches()
    {
        var ctx = PageSamples.Context();
        using var vm = new VisionViewModel(ctx) { Check = PageSamples.Check, DecodePicture = PageSamples.Decode };
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal(3, vm.Pipelines.Count);
        Assert.Equal("p1", vm.Editor!.Id);
        Assert.Same(vm.Pipelines[0], vm.SelectedPipeline);
        Assert.True(vm.Pipelines[0].IsOk);
        Assert.Equal("Watching · 8 fps", vm.Pipelines[0].ChipText);
        Assert.True(vm.Pipelines[2].IsWarning);
        Assert.Equal("Watching · 15 fps (reads 11.2)", vm.Pipelines[2].ChipText);
        Assert.Equal(VisionTexts.OnAction, vm.Pipelines[1].ChipText);
        Assert.Equal(3, vm.Models.Rows.Count);
        Assert.Equal("CPU 24 cores · RTX 5070 Ti", vm.RunsOnShortText);

        vm.SearchText = "dock";
        Assert.Single(vm.Pipelines);
        vm.SearchText = "nothing";
        Assert.True(vm.ShowNoMatch);
    }

    [Fact]
    public async Task New_starts_from_the_camera_and_Delete_asks_first()
    {
        var ctx = PageSamples.Context();
        using var vm = new VisionViewModel(ctx) { Check = PageSamples.Check };
        await vm.LoadAsync(CancellationToken.None);
        vm.PickCamera = _ => Task.FromResult<Guid?>(PageSamples.Dock.Id);
        await vm.NewCommand.ExecuteAsync(null);
        Assert.True(vm.Editor!.IsNew);
        Assert.Equal(PageSamples.Dock.Id, vm.Editor.DeviceId);
        Assert.Equal("Pipeline 4", vm.Editor.Name);
        Assert.Single(vm.Editor.Canvas.Nodes, n => n.IsCamera);

        ctx.ConfirmAnswer = false;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        Assert.Equal(VisionTexts.DeletePipeline, ctx.Confirmations[^1].Title);
        ctx.ConfirmAnswer = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("p1", vm.Editor!.Id); // the new one only closed, the first one shows
        Assert.Equal(0, ctx.CallsOf(VisionMethods.DeletePipeline));
    }

    [Fact]
    public void Live_events_update_the_list_and_the_shown_pipeline()
    {
        var ctx = PageSamples.Context();
        using var vm = new VisionViewModel(ctx) { Check = PageSamples.Check };
        vm.ApplyList(PageSamples.List);
        var editor = vm.ShowEditor(PageSamples.GatePeople, isNew: false);
        editor.StartPreview();
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(VisionMethods.PreviewTopic, VisionJson.Serialize(PageSamples.Preview(editor.PreviewId!))));
        Assert.Equal("2 inside", editor.Canvas.Find("h")!.Live);
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(VisionMethods.PipelinesTopic, VisionJson.Serialize(new PipelineList
        {
            Pipelines = [PageSamples.List.Pipelines[0] with { Problem = "The camera does not answer." }],
        })));
        var row = Assert.Single(vm.Pipelines);
        Assert.True(row.IsError);
        Assert.Equal("The camera does not answer.", row.Problem);
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(VisionMethods.StatsTopic, VisionJson.Serialize(new VisionStats { State = LoadState.Busy, StateText = "Busy", Load = 81 })));
        Assert.True(vm.Stats.IsBusy);
        Assert.Equal("81 %", vm.Stats.LoadText);
        editor.StopPreview();
    }

    [Fact]
    public void The_models_menu_has_Rename_Settings_Class_list_and_Delete_in_red_last()
    {
        var models = new ModelsViewModel(PageSamples.Context());
        models.Apply(PageSamples.ModelList);
        var row = models.Rows.OfType<ModelRow>().First(r => r.Model.Id == "m2");
        Assert.EndsWith("2 pipelines", row.Line);
        var actions = models.ActionsOf(row);
        Assert.True(actions[^1].IsDanger);

        Assert.Equal([VisionTexts.Rename, VisionTexts.ModelSettings, VisionTexts.ClassListTitle, "-", VisionTexts.Delete], actions.Select(a => a.Text));
        var rename = ModelEditViewModel.ForRename(PageSamples.Helmet, name => name == "DETR R50");
        rename.Name = "DETR R50";
        Assert.Equal(VisionTexts.ModelNameTaken, rename.ErrorOf(nameof(ModelEditViewModel.Name)));
        Assert.False(rename.OkCommand.CanExecute(null));
    }

    [Fact]
    public void Node_settings_round_trip_as_json()
    {
        var points = NodeValues.ToJson([new Point(0.123456, 0.5)]);
        Assert.Equal("[{\"x\":0.1235,\"y\":0.5}]", points.ToJsonString());
        Assert.Equal(new Point(0.1235, 0.5), NodeValues.Points(points)[0]);
        Assert.Empty(NodeValues.Points(JsonValue.Create(3)));
    }
}
