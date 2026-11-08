using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>Renders the page offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs (OADM_TEST_APPEARANCE=Light for light).</summary>
public sealed class PageScreenshotTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static async Task<(VisionViewModel Vm, VisionView View, Window Window)> ShowAsync(FakePageContext ctx)
    {
        var vm = new VisionViewModel(ctx) { Check = PageSamples.Check, DecodePicture = PageSamples.Decode };
        var view = new VisionView();
        var window = Headless.Host(view, Width, Height);
        window.Show();
        view.DataContext = vm; // after attaching: no live watch, deterministic content
        await vm.LoadAsync(CancellationToken.None);
        vm.ApplyStats(new VisionStats { State = LoadState.Ok, StateText = "All pipelines keep up", Load = 38 });
        Headless.Pump();
        return (vm, view, window);
    }

    [Fact]
    public async Task Page_renders_the_example_pipeline_with_its_live_outputs()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            ctx.Video!.Still = PageSamples.GatePicture();
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                var editor = vm.Editor!;
                editor.StartPreview();
                editor.Canvas.FitView();
                editor.ApplyPreview(PageSamples.Preview(editor.PreviewId!));
                editor.Canvas.SelectedNode = editor.Canvas.Find("e");
                Headless.Pump();

                Assert.Equal("In 12 · Out 7", editor.Canvas.Find("e")!.Live);
                Assert.True(view.FindControl<Border>("ModelsCard")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<DataGrid>("CrossingsGrid")!.IsEffectivelyVisible);
                Assert.Contains(view.GetVisualDescendants().OfType<StatCard>(), c => c.IsEffectivelyVisible && c.Value == "12");
                Assert.Contains(editor.Shapes, s => s.Node.Id == "e" && s.IsSelected);
                Assert.False(view.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled); // saved, nothing to save
                Headless.Capture(window, $"vision-page-{suffix}.png");

                // The splitter dragged left: the right column wider, the video with it; never the canvas below its minimum.
                Assert.Equal(PageLayout.RightMinWidth, view.RightWidth, 0);
                view.SetRightWidth(760);
                window.UpdateLayout();
                Headless.Pump();
                Assert.Equal(760, view.RightWidth, 0);
                Headless.Capture(window, $"vision-page-wide-right-{suffix}.png");
                view.SetRightWidth(5000);
                window.UpdateLayout();
                Headless.Pump();
                var canvasCard = view.FindControl<Border>("CanvasCard")!;
                Assert.True(canvasCard.Bounds.Width >= PageLayout.CanvasMinWidth - 1, $"the canvas card is {canvasCard.Bounds.Width} wide");
                var toolbar = view.FindControl<ToolbarOverflowPanel>("CanvasToolbar")!;
                Assert.True(toolbar.OverflowItems.Count == 0, $"the toolbar fits at the canvas minimum: {toolbar.OverflowItems.Count} left out, card {canvasCard.Bounds.Width}, toolbar {toolbar.Bounds.Width}");
                Assert.True(view.FindControl<ToolbarButton>("TidyUpButton")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<ToolbarButton>("DeleteNodeButton")!.IsEffectivelyVisible);
                Headless.Capture(window, $"vision-page-canvas-min-{suffix}.png");
                view.SetRightWidth(PageLayout.RightMinWidth);
                Headless.Pump();

                // A model node selected: its form drawn from its fields, its objects of the frame.
                editor.Canvas.SelectedNode = editor.Canvas.Find("b");
                Headless.Pump();

                // The splitter between the video and the node card dragged down: the video taller, the node card at its least
                // still shows its heading and two fields.
                var first = view.VideoHeight;
                view.SetVideoHeight(10000);
                window.UpdateLayout();
                Headless.Pump();
                Assert.True(view.VideoHeight > first);
                var nodeCard = view.FindControl<Border>("NodeCard")!;
                Assert.True(nodeCard.Bounds.Height >= PageLayout.NodeMinHeight - 1, $"the node card is {nodeCard.Bounds.Height} high");
                var fields = view.FindControl<ItemsControl>("Fields")!;
                var modelField = fields.GetVisualDescendants().OfType<FormField>().First();
                var bottom = Avalonia.VisualExtensions.TranslatePoint(modelField, new Avalonia.Point(0, modelField.Bounds.Height), nodeCard)!.Value.Y;
                Assert.True(bottom <= nodeCard.Bounds.Height, $"the second field ends at {bottom} in a card of {nodeCard.Bounds.Height}");
                Headless.Capture(window, $"vision-page-video-taller-{suffix}.png");
                view.SetVideoHeight(first);
                window.UpdateLayout();
                Headless.Pump();
                Assert.True(view.FindControl<DataGrid>("ObjectsGrid")!.IsEffectivelyVisible);
                Assert.Equal(5, editor.Output.Objects.Count);
                Headless.Capture(window, $"vision-page-detect-{suffix}.png");

                // Nothing selected: the pipeline's own settings.
                editor.Canvas.ClearSelection();
                Headless.Pump();
                Assert.True(view.FindControl<StackPanel>("PipelineForm")!.IsEffectivelyVisible);
                Headless.Capture(window, $"vision-page-pipeline-{suffix}.png");
                editor.StopPreview();
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Page_renders_errors_below_the_nodes_and_the_node_list()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                await vm.OpenAsync(vm.Pipelines.Single(p => p.Id == "p2"), CancellationToken.None);
                var editor = vm.Editor!;
                editor.Canvas.FitView();
                Headless.Pump();
                Assert.NotNull(editor.Canvas.Find("d")!.Error);
                var save = view.FindControl<Button>("SaveButton")!;
                editor.Name = "Dock 4"; // Not saved, Save still off: its tooltip says why
                Headless.Pump();
                Assert.False(save.IsEffectivelyEnabled);
                Assert.Equal(editor.SaveBlockedReason, ToolTip.GetTip(save));
                Headless.Capture(window, $"vision-page-errors-{suffix}.png");

                // The + of Detect objects: only the kinds that take objects.
                editor.Canvas.OpenPickerAfter(editor.Canvas.Find("b")!);
                Headless.Pump();
                Assert.True(view.FindControl<Border>("NodePicker")!.IsEffectivelyVisible);
                Assert.DoesNotContain(editor.Canvas.Picker.ShownKinds, k => k.Id == Oadm.Plugins.Vision.Model.NodeKinds.Detect);
                Headless.Capture(window, $"vision-node-picker-{suffix}.png");
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Page_renders_an_upload_in_the_models_card_and_the_empty_page()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var gate = new TaskCompletionSource<string?>();
            ctx.Answers[VisionMethods.BeginUpload] = _ => VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = 40 });
            var chunks = 0;
            ctx.AsyncAnswers[VisionMethods.UploadChunk] = _ => ++chunks < 5 ? Task.FromResult<string?>("{}") : gate.Task;
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                var upload = vm.Models.UploadFilesAsync([new MemoryModelFile("rf-detr-small.onnx", new byte[400])]);
                Headless.Pump();
                var row = Assert.Single(vm.Models.Rows.OfType<UploadRow>());
                Assert.Equal(0.4, row.Progress, 3);
                Assert.Equal("Upload 40 %", row.StateText);
                Headless.Capture(window, $"vision-models-upload-{suffix}.png");
                ctx.Answers[VisionMethods.CancelUpload] = _ => "{}";
                row.CancelCommand.Execute(null);
                gate.SetResult("{}");
                await upload;
            }

            window.Close();

            var empty = PageSamples.Context();
            empty.Answers[VisionMethods.ListPipelines] = _ => "{}";
            empty.Answers[VisionMethods.ListModels] = _ => "{}";
            (vm, view, window) = await ShowAsync(empty);
            using (vm)
            {
                Assert.True(vm.ShowEmpty);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == VisionInfo.EmptyText);
                Headless.Capture(window, $"vision-empty-{suffix}.png");
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Class_lists_render_in_the_models_card_and_their_dialogs()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                Assert.True(view.FindControl<ListBox>("ClassListList")!.IsEffectivelyVisible);
                Assert.Equal(4, vm.Models.ClassLists.Count);
                vm.Editor!.Canvas.SelectedNode = vm.Editor.Canvas.Find("b"); // Detect objects names its class list
                Headless.Pump();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Class list: Gate classes");
                Headless.Capture(window, $"vision-class-lists-{suffix}.png");
            }

            window.Close();

            // A model's Class list: the lists that fit, then its own names to edit.
            var form = ModelEditViewModel.ForClassList(PageSamples.RfDetr, PageSamples.ClassLists.Lists);
            var dialog = new ModelEditWindow { DataContext = form };
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Headless.Pump();
            Assert.False(dialog.FindControl<ClassNamesField>("OwnNames")!.IsEffectivelyVisible);
            Headless.Capture(dialog, $"vision-class-list-dialog-{suffix}.png");
            form.SelectedChoice = form.Choices[0];
            form.Own.Text = "person\ncar";
            Headless.Pump();
            Assert.True(dialog.FindControl<ClassNamesField>("OwnNames")!.IsEffectivelyVisible);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == VisionTexts.WrongClassCount);
            Headless.Capture(dialog, $"vision-class-list-own-{suffix}.png");
            dialog.Close();

            // No list has as many names as the model has classes: one line below the select says why.
            var none = ModelEditViewModel.ForClassList(PageSamples.Helmet, PageSamples.ClassLists.Lists);
            var noneWindow = new ModelEditWindow { DataContext = none };
            Oadm.Client.App.ApplyCrispText(noneWindow);
            noneWindow.Show();
            Headless.Pump();
            Assert.Contains(noneWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == none.NoFittingList);
            Headless.Capture(noneWindow, $"vision-class-list-none-{suffix}.png");
            noneWindow.Close();

            // New class list: Name and the names, one per line.
            var edit = new ClassListEditViewModel(PageSamples.GateClasses, _ => false);
            var editWindow = new ClassListWindow { DataContext = edit };
            Oadm.Client.App.ApplyCrispText(editWindow);
            editWindow.Show();
            Headless.Pump();
            Assert.DoesNotContain(editWindow.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));
            Headless.Capture(editWindow, $"vision-class-list-edit-{suffix}.png");
            editWindow.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Timers_compare_and_sliders_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var vm = new VisionViewModel(ctx) { Check = PageSamples.CheckData, DecodePicture = PageSamples.Decode };
            var view = new VisionView();
            var window = Headless.Host(view, Width, Height);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            using (vm)
            {
                await vm.OpenAsync(vm.Pipelines.Single(p => p.Id == "p3"), CancellationToken.None);
                var editor = vm.Editor!;
                editor.StartPreview();
                editor.ApplyPreview(PageSamples.LobbyPreview(editor.PreviewId!));
                editor.Canvas.FitView();
                Headless.Pump();
                Assert.Equal("Next 00:00", editor.Canvas.Find("t1")!.Live);
                Assert.All(editor.Canvas.Nodes, n => Assert.Null(n.Error));
                Headless.Capture(window, $"vision-timers-{suffix}.png");

                // A form with a slider: the minimum score of Detect objects.
                editor.Canvas.SelectedNode = editor.Canvas.Find("b");
                Headless.Pump();
                var slider = view.GetVisualDescendants().OfType<NumberSlider>().First(s => s.IsEffectivelyVisible);
                Assert.Equal("50", slider.Box.Text);
                Assert.Equal(50, slider.Slider.Value);
                Headless.Capture(window, $"vision-slider-form-{suffix}.png");

                // Compare with Between: And shows.
                editor.Canvas.SelectedNode = editor.Canvas.Find("e");
                var op = Assert.IsType<ChoiceFieldViewModel>(editor.Form!.Fields.Single(f => f.Field.Key == Oadm.Plugins.Vision.Model.NodeKinds.OperatorKey));
                op.Selected = op.Choices.Single(c => c.Value == Oadm.Plugins.Vision.Model.NodeKinds.OpBetween);
                Headless.Pump();
                Assert.Contains(view.GetVisualDescendants().OfType<FormField>(), f => f.IsEffectivelyVisible && f.Label == "And");
                Headless.Capture(window, $"vision-compare-between-{suffix}.png");
                editor.StopPreview();
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
