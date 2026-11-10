using Avalonia;

using Oadm.Plugins.Shared.Vision.Client;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>
/// Import model (docs/spec/vision-center.md "Import model"): the wizard's upload and check, the family, the settings and class
/// list saved in one change, Cancel; the model settings' drawings; the + of an empty control input; Fit view near the top.
/// </summary>
public sealed class ModelImportTests
{
    private sealed class Dialogs : IVisionDialogs
    {
        public Queue<IModelFile?> Picks { get; } = new();

        public Task<bool> EditStreamAsync(StreamSettingsViewModel form) => Task.FromResult(false);

        public Task<bool> EditModelAsync(ModelEditViewModel form) => Task.FromResult(false);

        public Task<bool> EditClassListAsync(ClassListEditViewModel form) => Task.FromResult(false);

        public Task<IModelFile?> PickModelFileAsync(bool labels) => Task.FromResult(Picks.Count > 0 ? Picks.Dequeue() : null);
    }

    /// <summary>A context that takes an upload in chunks and checks it into <paramref name="checkedModel"/>.</summary>
    private static FakePageContext Server(VisionModel checkedModel, int chunkBytes = 1024)
    {
        var ctx = PageSamples.Context();
        ctx.Answers[VisionMethods.BeginUpload] = _ => VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = chunkBytes });
        ctx.Answers[VisionMethods.UploadChunk] = _ => "{}";
        ctx.Answers[VisionMethods.FinishUpload] = _ => VisionJson.Serialize(new FinishUploadReply { Model = checkedModel });
        ctx.Answers[VisionMethods.UpdateModel] = json => VisionJson.Serialize(VisionJson.Deserialize<UpdateModelRequest>(json).Model);
        ctx.Answers[VisionMethods.DeleteModel] = _ => "{}";
        return ctx;
    }

    private static VisionModel Checked => PageSamples.Detr with { Id = "m9", Name = "small", Format = ModelFormat.Yolo, Fit = ModelFit.Letterbox, InputWidth = 640, InputHeight = 640, FixedInput = false };

    private static async Task<(ModelsViewModel Models, ModelImportViewModel Wizard)> StartAsync(FakePageContext ctx)
    {
        var models = new ModelsViewModel(ctx);
        await models.LoadAsync(CancellationToken.None);
        return (models, new ModelImportViewModel(models, new Dialogs()));
    }

    [Fact]
    public async Task Next_sends_the_file_in_the_servers_chunks_then_shows_what_the_check_found()
    {
        var ctx = Server(Checked, chunkBytes: 4);
        var chunks = new List<UploadChunkRequest>();
        ctx.Answers[VisionMethods.UploadChunk] = json =>
        {
            chunks.Add(VisionJson.Deserialize<UploadChunkRequest>(json));
            return "{}";
        };
        var (models, wizard) = await StartAsync(ctx);
        var progress = new List<double>();
        wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelImportViewModel.Progress))
            {
                progress.Add(wizard.Progress);
            }
        };

        Assert.False(wizard.NextCommand.CanExecute(null));
        Assert.Equal(VisionTexts.ChooseModelFileFirst, wizard.StepProblem);
        wizard.SetFile(new MemoryModelFile("small.onnx", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Null(VisionJson.Deserialize<BeginUploadRequest>(ctx.LastPayloadOf(VisionMethods.BeginUpload)!).Labels);
        Assert.Equal([0L, 4L, 8L], chunks.Select(c => c.Offset));
        Assert.Equal([4, 4, 2], chunks.Select(c => c.Data.Length));
        Assert.Equal([0.4, 0.8, 1.0], progress.Where(p => p > 0));
        Assert.True(wizard.IsSettingsStep);
        Assert.Equal("Detection · 640x640 · 6 classes", wizard.Found);
        Assert.Equal("small", wizard.Name); // the server's name for it
        Assert.Equal("YOLOv8, YOLO11, YOLO12", wizard.Family!.Name);
        Assert.All(wizard.Families, f => Assert.Equal(ModelKind.Detection, f.Kind));
        Assert.True(wizard.Settings!.IsGuided);

        // Every select holds what the check found: a check mark, nothing below it.
        Assert.True(wizard.FamilyFound && wizard.Settings.FormatFound && wizard.Settings.FitFound && wizard.Settings.ScalingFound);
        Assert.False(wizard.FamilyChanged || wizard.Settings.FitChanged);
        Assert.Null(wizard.Settings.FitHint);

        // Another fit: a warning and what the check found below it; back again, the check mark.
        wizard.Settings.Fit = ModelSettingsViewModel.Fits.First(f => f.Value == ModelFit.Stretch);
        Assert.True(wizard.Settings.FitChanged);
        Assert.False(wizard.Settings.FitFound);
        Assert.Equal(VisionTexts.CheckFound("Letterbox"), wizard.Settings.FitHint);
        wizard.Settings.Fit = ModelSettingsViewModel.Fits.First(f => f.Value == ModelFit.Letterbox);
        Assert.True(wizard.Settings.FitFound);
        Assert.Null(wizard.Settings.FitHint);

        // Another family: its settings, the family marked as changed.
        var other = wizard.Families.First(f => f != wizard.FoundFamily);
        wizard.Family = other;
        Assert.True(wizard.FamilyChanged);
        Assert.Equal(VisionTexts.CheckFound(wizard.FoundFamily!.Name), wizard.FamilyHint);
        Assert.Equal(other.Format, wizard.Settings.Format.Value);
        Assert.Contains(models.Rows.OfType<ModelRow>(), r => r.Model.Id == "m9");
    }

    [Fact]
    public async Task A_refusal_of_the_check_shows_below_the_file_and_the_wizard_stays()
    {
        var ctx = Server(Checked);
        ctx.Answers[VisionMethods.FinishUpload] = _ => VisionJson.Serialize(new FinishUploadReply { Refusal = "This model cannot run here: it needs a custom operator." });
        var (_, wizard) = await StartAsync(ctx);
        wizard.SetFile(new MemoryModelFile("bad.onnx", [1, 2, 3]));
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsModelStep);
        Assert.Equal("This model cannot run here: it needs a custom operator.", wizard.FileProblem);
        Assert.False(wizard.NextCommand.CanExecute(null));

        // Another file clears it.
        wizard.SetFile(new MemoryModelFile("good.onnx", [1]));
        Assert.Null(wizard.FileProblem);
    }

    [Fact]
    public async Task A_refused_begin_shows_below_the_file()
    {
        var ctx = Server(Checked);
        ctx.Answers[VisionMethods.BeginUpload] = _ => throw new InvalidOperationException("At most 20 models.");
        var (_, wizard) = await StartAsync(ctx);
        wizard.SetFile(new MemoryModelFile("one.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal("At most 20 models.", wizard.FileProblem);
    }

    [Fact]
    public async Task Cancel_during_the_upload_ends_it_on_the_server()
    {
        var ctx = Server(Checked, chunkBytes: 2);
        var gate = new TaskCompletionSource<string?>();
        ctx.AsyncAnswers[VisionMethods.UploadChunk] = _ => gate.Task;
        ctx.Answers[VisionMethods.CancelUpload] = _ => "{}";
        var (_, wizard) = await StartAsync(ctx);
        var closed = (bool?)null;
        wizard.CloseRequested += (_, ok) => closed = ok;
        wizard.SetFile(new MemoryModelFile("big.onnx", new byte[10]));
        var next = wizard.NextCommand.ExecuteAsync(null);
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.UploadChunk) == 1, "the first chunk on its way");
        Assert.True(wizard.IsWorking);
        Assert.False(wizard.BackCommand.CanExecute(null));
        var cancel = wizard.CancelCommand.ExecuteAsync(null);
        gate.SetResult("{}");
        await next;
        await cancel;
        Assert.False(closed);
        Assert.Equal("u1", VisionJson.Deserialize<UploadRequest>(ctx.LastPayloadOf(VisionMethods.CancelUpload)!).UploadId);
        Assert.Equal(0, ctx.CallsOf(VisionMethods.FinishUpload));
        Assert.Equal(0, ctx.CallsOf(VisionMethods.DeleteModel));
    }

    [Fact]
    public async Task Another_family_sets_its_settings_and_a_format_that_does_not_fit_shows_below_it_on_Import()
    {
        var ctx = Server(Checked);
        var (_, wizard) = await StartAsync(ctx);
        wizard.SetFile(new MemoryModelFile("small.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Family = ModelFamilies.All.Single(f => f.Name == "RF-DETR");
        Assert.Equal((ModelFormat.RfDetr, ModelFit.Stretch, ModelScaling.ImageNet), (wizard.Settings!.Format.Value, wizard.Settings.Fit.Value, wizard.Settings.Scaling.Value));

        await wizard.NextCommand.ExecuteAsync(null);
        ctx.Answers[VisionMethods.UpdateModel] = _ => throw new InvalidOperationException(ModelTexts.OutputsDoNotFit);
        await wizard.ImportCommand.ExecuteAsync(null);
        Assert.True(wizard.IsSettingsStep); // back where the format is, its refusal below it
        Assert.Equal(ModelTexts.OutputsDoNotFit, wizard.Settings.ErrorOf(nameof(ModelSettingsViewModel.Format)));
        Assert.Equal(1, ctx.CallsOf(VisionMethods.BeginUpload));
    }

    [Fact]
    public async Task A_labels_file_fills_the_own_names_and_an_unreadable_one_says_so()
    {
        var (_, wizard) = await StartAsync(Server(Checked));
        wizard.SetFile(new MemoryModelFile("small.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsLabelsStep);
        await wizard.LoadLabelsAsync(new MemoryModelFile("labels.txt", "a\r\nb\r\nc\r\nd\r\ne\r\nf\r\n"u8.ToArray()));
        Assert.True(wizard.ClassList!.IsOwn);
        Assert.Equal(["a", "b", "c", "d", "e", "f"], wizard.ClassList.ClassNames);
        Assert.True(wizard.ImportCommand.CanExecute(null));

        await wizard.LoadLabelsAsync(new MemoryModelFile("short.txt", "a\nb"u8.ToArray()));
        Assert.NotNull(wizard.ClassList.Error); // 2 names for 6 classes
        Assert.False(wizard.ImportCommand.CanExecute(null));

        await wizard.LoadLabelsAsync(new UnreadableFile("labels.txt"));
        Assert.Equal("The file is in use.", wizard.LabelsError);
    }

    [Fact]
    public async Task Import_saves_name_settings_and_class_list_in_one_change()
    {
        var ctx = Server(Checked);
        var (models, wizard) = await StartAsync(ctx);
        var closed = (bool?)null;
        wizard.CloseRequested += (_, ok) => closed = ok;
        wizard.SetFile(new MemoryModelFile("small.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Name = "Gate people";
        wizard.Settings!.Scaling = ModelSettingsViewModel.Scalings.Single(s => s.Value == ModelScaling.ImageNet);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsLabelsStep);
        wizard.ClassList!.SelectedChoice = wizard.ClassList.Choices.Single(c => c.Id == "gate6");
        Assert.True(wizard.ImportCommand.CanExecute(null));
        await wizard.ImportCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = VisionJson.Deserialize<UpdateModelRequest>(ctx.LastPayloadOf(VisionMethods.UpdateModel)!).Model!;
        Assert.Equal(("m9", "Gate people", ModelScaling.ImageNet, "gate6"), (saved.Id, saved.Name, saved.Scaling, saved.ClassListId));
        Assert.Equal(1, ctx.CallsOf(VisionMethods.UpdateModel));
        Assert.Contains(models.Rows.OfType<ModelRow>(), r => r.Name == "Gate people");

        // Closing the window after Import deletes nothing.
        await wizard.AbandonAsync();
        Assert.Equal(0, ctx.CallsOf(VisionMethods.DeleteModel));
    }

    [Fact]
    public async Task Cancel_after_the_check_deletes_the_stored_model_but_keeps_a_missing_model_that_came_back()
    {
        var ctx = Server(Checked);
        var (models, wizard) = await StartAsync(ctx);
        wizard.SetFile(new MemoryModelFile("small.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.CancelCommand.ExecuteAsync(null);
        Assert.Equal("m9", VisionJson.Deserialize<ModelRequest>(ctx.LastPayloadOf(VisionMethods.DeleteModel)!).ModelId);
        Assert.DoesNotContain(models.Rows.OfType<ModelRow>(), r => r.Model.Id == "m9");

        // The file of a Missing model brings it back: Cancel keeps it.
        var back = Server(PageSamples.Detr);
        back.Answers[VisionMethods.ListModels] = _ => VisionJson.Serialize(new ModelList { Models = [PageSamples.Detr with { Missing = true }] });
        var (_, again) = await StartAsync(back);
        again.SetFile(new MemoryModelFile("detr.onnx", [1]));
        await again.NextCommand.ExecuteAsync(null);
        Assert.True(again.IsSettingsStep);
        await again.CancelCommand.ExecuteAsync(null);
        Assert.Equal(0, back.CallsOf(VisionMethods.DeleteModel));
    }

    [Fact]
    public async Task Another_file_after_Back_replaces_the_stored_model()
    {
        var ctx = Server(Checked);
        var (_, wizard) = await StartAsync(ctx);
        wizard.SetFile(new MemoryModelFile("first.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.BackCommand.Execute(null);
        Assert.True(wizard.IsModelStep);
        await wizard.NextCommand.ExecuteAsync(null); // the same file: no second upload
        Assert.Equal(1, ctx.CallsOf(VisionMethods.BeginUpload));
        wizard.BackCommand.Execute(null);
        wizard.SetFile(new MemoryModelFile("second.onnx", [2]));
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(2, ctx.CallsOf(VisionMethods.BeginUpload));
        Assert.Equal(1, ctx.CallsOf(VisionMethods.DeleteModel));
    }

    [Fact]
    public async Task The_name_must_be_free()
    {
        var (_, wizard) = await StartAsync(Server(Checked));
        wizard.SetFile(new MemoryModelFile("small.onnx", [1]));
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Name = PageSamples.Detr.Name;
        Assert.False(wizard.NextCommand.CanExecute(null));
        Assert.Equal(VisionTexts.ModelNameTaken, wizard.StepProblem);
    }

    [Fact]
    public void The_drawings_show_every_choice_of_the_setting()
    {
        Assert.Equal(7, ModelSettingDiagram.FormatsOf(ModelKind.Detection).Count);
        Assert.Equal([ModelFormat.Classification], ModelSettingDiagram.FormatsOf(ModelKind.Classification).Select(f => f.Format));
        Assert.Equal([("R", "1.00"), ("G", "0.50"), ("B", "0.00")], ModelSettingDiagram.PixelOf(ModelScaling.ZeroToOne));
        Assert.Equal([("R", "2.25"), ("G", "0.21"), ("B", "-1.80")], ModelSettingDiagram.PixelOf(ModelScaling.ImageNet));
        Assert.Equal([("B", "0"), ("G", "128"), ("R", "255")], ModelSettingDiagram.PixelOf(ModelScaling.ZeroTo255Bgr));

        // Every family names a format of its kind.
        Assert.All(ModelFamilies.All, f => Assert.Contains(ModelSettingDiagram.FormatsOf(f.Kind), x => x.Format == f.Format));

        // The family the check found follows the format; a classifier its scaling.
        Assert.Equal("RF-DETR", ModelFamilies.Of(PageSamples.RfDetr).Name);
        Assert.Equal("ImageNet classifier", ModelFamilies.Of(PageSamples.Helmet with { Scaling = ModelScaling.ImageNet }).Name);
        Assert.Equal("YOLO classifier", ModelFamilies.Of(PageSamples.Helmet with { Scaling = ModelScaling.ZeroToOne }).Name);
    }

    [Fact]
    public void Sizes_read_like_the_filter_counts_them_and_25_fps_is_a_choice()
    {
        Assert.Equal("9 %", VisionTexts.Size(0.094));
        Assert.Equal(" · Sizes 6 to 12 %", VisionTexts.Sizes([0.12, 0.06, 0.09]));
        Assert.Equal(" · Size 9 %", VisionTexts.Sizes([0.09]));
        Assert.Equal(string.Empty, VisionTexts.Sizes([]));
        Assert.Contains(20, VisionLimits.FpsChoices);
        Assert.Equal(25, VisionLimits.FpsChoices.Max());
    }

    // ---- The canvas ----

    [Fact]
    public void The_plus_of_an_empty_control_input_offers_what_fits_and_links_the_new_node_into_it()
    {
        var canvas = new CanvasViewModel(PageSamples.GatePeople.Nodes, PageSamples.GatePeople.Links);
        var count = canvas.Nodes.First(n => n.Kind == NodeKinds.Count);
        canvas.SelectedNode = count;
        Assert.True(canvas.ShowsControlPlus(count));
        var plus = CanvasGeometry.ControlPlusRect(count.X, count.Y, 0);
        Assert.Equal((count, NodeKinds.ResetPort), canvas.EmptyControlAt(plus.Center));

        canvas.OpenPickerInto(count, NodeKinds.ResetPort);
        Assert.Contains(NodeKinds.TimerKind, canvas.Picker.ShownKinds); // and Throttle, which passes a pulse on
        Assert.All(canvas.Picker.ShownKinds, k => Assert.True(NodePickerViewModel.Gives(k, PortKind.Pulse)));
        Assert.DoesNotContain(NodeKinds.CountKind, canvas.Picker.ShownKinds);
        var timer = canvas.Choose(NodeKinds.TimerKind)!;
        Assert.Contains(canvas.Links, l => l.From == timer && l.To == count && l.ToPort == NodeKinds.ResetPort);
        Assert.True(timer.IsSelected);
        Assert.Null(canvas.EmptyControlAt(CanvasGeometry.ControlPort(count.X, count.Y, 0))); // the input has its link now

        // A yes-or-no input offers the nodes that give one.
        canvas.OpenPickerInto(count, NodeKinds.WhilePort);
        Assert.Contains(NodeKinds.TimeWindowKind, canvas.Picker.ShownKinds);
        Assert.DoesNotContain(NodeKinds.TimerKind, canvas.Picker.ShownKinds);
    }

    [Fact]
    public void Fit_view_puts_a_new_pipelines_camera_near_the_top()
    {
        var canvas = new CanvasViewModel([new PipelineNode { Id = "c", Kind = NodeKinds.Camera, Name = "Camera", X = 300, Y = 200 }], [])
        {
            ViewportSize = new Size(900, 700),
        };
        canvas.FitView();
        var top = canvas.ToScreen(new Point(300, 200)).Y;
        Assert.InRange(top, 0, 80);
        var centre = canvas.ToScreen(new Point(300 + (CanvasGeometry.NodeWidth / 2), 200)).X;
        Assert.Equal(450, centre, 1);
    }

    private sealed class UnreadableFile(string name) : IModelFile
    {
        public string Name => name;

        public long Length => 10;

        public Task<Stream> OpenReadAsync() => Task.FromException<Stream>(new IOException("The file is in use."));
    }
}
