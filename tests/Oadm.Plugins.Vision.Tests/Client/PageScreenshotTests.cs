using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;
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
                var before = view.RightWidth;
                Assert.True(before >= PageLayout.RightMinWidth - 1, $"the right column starts at {before}, at least its minimum");
                view.SetRightWidth(before + 300);
                window.UpdateLayout();
                Headless.Pump();
                Assert.True(view.RightWidth > before, $"the right column is wider: {view.RightWidth} after {before}");
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
                Assert.Equal("5 in this picture · 3 below 50 % · 18 ms per picture", editor.Output.Summary);
                await editor.ModelView.ReadAsync();
                Headless.Pump();
                Assert.True(view.FindControl<Border>("ModelViewPicture")!.IsEffectivelyVisible);
                Assert.Equal("576x576 · Letterbox", editor.ModelView.Caption);
                Headless.Capture(window, $"vision-page-detect-{suffix}.png");

                // A wide right column: the cards side by side, the fields at their width instead of grown.
                var narrow = view.RightWidth;
                view.SetRightWidth(narrow + 700);
                window.UpdateLayout();
                Headless.Pump();
                var formCard = view.FindControl<Border>("NodeFormCard")!;
                var outputCard = view.FindControl<Border>("OutputCard")!;
                Assert.True(view.FindControl<Border>("ModelViewCard")!.Bounds.X > formCard.Bounds.X || outputCard.Bounds.X > formCard.Bounds.X, "a card sits beside the form");
                Headless.Capture(window, $"vision-page-detect-wide-{suffix}.png");
                view.SetRightWidth(narrow);
                window.UpdateLayout();
                Headless.Pump();

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

                // The Event node's empty Send now: its + and the list of the nodes that give a pulse.
                var send = editor.Canvas.Find("e")!;
                editor.Canvas.SelectedNode = send;
                editor.Canvas.OpenPickerInto(send, Oadm.Plugins.Vision.Model.NodeKinds.SendPort);
                Headless.Pump();
                Assert.Contains(Oadm.Plugins.Vision.Model.NodeKinds.TimerKind, editor.Canvas.Picker.ShownKinds);
                Headless.Capture(window, $"vision-node-picker-send-{suffix}.png");
                editor.Canvas.Choose(Oadm.Plugins.Vision.Model.NodeKinds.TimerKind);
                Headless.Pump();
                Headless.Capture(window, $"vision-timer-into-send-{suffix}.png");
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_import_wizard_renders_each_step_and_the_empty_page()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var gate = new TaskCompletionSource<string?>();
            ctx.Answers[VisionMethods.BeginUpload] = _ => VisionJson.Serialize(new BeginUploadReply { UploadId = "u1", ChunkBytes = 40 });
            var chunks = 0;
            ctx.AsyncAnswers[VisionMethods.UploadChunk] = _ => ++chunks < 5 ? Task.FromResult<string?>("{}") : gate.Task;
            var stored = PageSamples.RfDetr with { Id = "m9", Name = "rf-detr-small", ClassListId = null, Fit = ModelFit.Stretch, Scaling = ModelScaling.ImageNet };
            ctx.Answers[VisionMethods.FinishUpload] = _ => VisionJson.Serialize(new FinishUploadReply { Model = stored });
            ctx.Answers[VisionMethods.UpdateModel] = json => VisionJson.Serialize(VisionJson.Deserialize<UpdateModelRequest>(json).Model);
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                Assert.Equal(VisionTexts.ImportModel, view.FindControl<ToolbarButton>("ImportButton")!.Text);
                using var wizard = new ModelImportViewModel(vm.Models, view);
                var dialog = new ModelImportWindow { DataContext = wizard };
                Oadm.Client.App.ApplyCrispText(dialog);
                dialog.Show();
                Headless.Pump();
                Headless.Capture(dialog, $"vision-import-start-{suffix}.png");

                wizard.SetFile(new MemoryModelFile("rf-detr-small.onnx", new byte[400]));
                var next = wizard.NextCommand.ExecuteAsync(null);
                Headless.Pump();
                Assert.Equal(0.4, wizard.Progress, 3);
                Assert.Equal("Upload 40 %", wizard.ProgressText);
                Headless.Capture(dialog, $"vision-import-upload-{suffix}.png");
                gate.SetResult("{}");
                await next;
                Headless.Pump();
                Assert.True(wizard.IsSettingsStep);
                Assert.DoesNotContain(dialog.FindControl<ModelSettingsForm>("SettingsForm")!.GetVisualDescendants().OfType<ModelSettingDiagram>(), d => d.IsEffectivelyVisible); // the drawings sit in the info icons, as in the Settings dialog
                Headless.Capture(dialog, $"vision-import-settings-{suffix}.png");

                // Another fit than the check found: a warning in its select and what was found below it.
                var found = wizard.Settings!.Fit;
                wizard.Settings.Fit = ModelSettingsViewModel.Fits.First(f => f != found);
                Headless.Pump();
                Headless.Capture(dialog, $"vision-import-changed-{suffix}.png");
                wizard.Settings.Fit = found;
                Headless.Pump();

                await wizard.NextCommand.ExecuteAsync(null);
                Headless.Pump();
                Headless.Capture(dialog, $"vision-import-labels-{suffix}.png");
                dialog.Close();
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
                Assert.Equal(3, vm.Models.ClassLists.Count);
                vm.Editor!.Canvas.SelectedNode = vm.Editor.Canvas.Find("b"); // Detect objects does not repeat its class list
                Headless.Pump();
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Class list: ", StringComparison.Ordinal) == true);
                Headless.Capture(window, $"vision-class-lists-{suffix}.png");
            }

            window.Close();

            // A model's Class list: the lists that fit, then its own names to edit.
            var form = ModelEditViewModel.ForClassList(PageSamples.RfDetr, PageSamples.ClassLists.Lists);
            var dialog = new ModelEditWindow { DataContext = form };
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Headless.Pump();
            Assert.False(dialog.GetVisualDescendants().OfType<ClassNamesField>().Single(f => f.Name == "OwnNames").IsEffectivelyVisible);
            Headless.Capture(dialog, $"vision-class-list-dialog-{suffix}.png");
            form.SelectedChoice = form.Choices[0];
            form.Own.Text = "person\ncar";
            Headless.Pump();
            Assert.True(dialog.GetVisualDescendants().OfType<ClassNamesField>().Single(f => f.Name == "OwnNames").IsEffectivelyVisible);
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
    public async Task Problem_chips_and_the_model_settings_dialog_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            var list = PageSamples.List;
            ctx.Answers[VisionMethods.ListPipelines] = _ => VisionJson.Serialize(list with
            {
                Pipelines =
                [
                    list.Pipelines[0] with { Problem = Oadm.Plugins.Vision.Pipelines.PipelineRules.TooManyWatching(24) },
                    list.Pipelines[1] with { Problem = Oadm.Sdk.Devices.DeviceMessages.TimedOut },
                    list.Pipelines[2],
                ],
            });
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                Assert.Contains(view.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == Oadm.Plugins.Shared.Vision.Client.ProblemChips.LimitReached);
                Assert.Contains(view.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == Oadm.Plugins.Shared.Vision.Client.ProblemChips.NotReachable);
                Headless.Capture(window, $"vision-problem-chips-{suffix}.png");
            }

            window.Close();

            // A model's Settings: format, fit, scaling and the input size, a refusal below its field.
            var form = new ModelSettingsViewModel(PageSamples.Detr) { InputWidth = "650" };
            form.ShowRefusal(Oadm.Plugins.Vision.Models.ModelTexts.OutputsDoNotFit);
            form.OkCommand.Execute(null);
            var dialog = new ModelSettingsWindow { DataContext = form };
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Headless.Pump();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Oadm.Plugins.Vision.Models.ModelTexts.InputSizeRule);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Oadm.Plugins.Vision.Models.ModelTexts.OutputsDoNotFit);
            Headless.Capture(dialog, $"vision-model-settings-errors-{suffix}.png");
            dialog.Close();

            var plain = new ModelSettingsWindow { DataContext = new ModelSettingsViewModel(PageSamples.Detr) };
            Oadm.Client.App.ApplyCrispText(plain);
            plain.Show();
            Headless.Pump();
            Headless.Capture(plain, $"vision-model-settings-{suffix}.png");
            plain.Close();

            var fixedInput = new ModelSettingsWindow { DataContext = new ModelSettingsViewModel(PageSamples.RfDetr) };
            Oadm.Client.App.ApplyCrispText(fixedInput);
            fixedInput.Show();
            Headless.Pump();
            Assert.False(fixedInput.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WidthBox").IsEffectivelyVisible);
            Headless.Capture(fixedInput, $"vision-model-settings-fixed-{suffix}.png");
            fixedInput.Close();
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
            vm.ApplyStats(new Oadm.Plugins.Vision.Api.VisionStats { State = Oadm.Plugins.Vision.Api.LoadState.Ok, StateText = "All pipelines keep up", Load = 38 });
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

                // A wrong number: the red border stays while the input has the focus and under the pointer.
                slider.Box.Text = "500";
                slider.Box.Focus();
                Headless.Pump();
                Assert.True(slider.Box.IsFocused);
                Assert.Contains("error", slider.Classes);
                var border = slider.Box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                var red = (Avalonia.Media.ISolidColorBrush)Avalonia.Application.Current!.FindResource("Oadm.StatusErrorBrush")!;
                Assert.Equal(red.Color, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(border.BorderBrush).Color);
                Headless.Capture(window, $"vision-slider-error-focus-{suffix}.png");
                slider.Box.Text = "50";
                Headless.Pump();

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

    [Fact]
    public async Task Logic_time_and_device_events_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = PageSamples.Context();
            ctx.Video!.Still = PageSamples.GatePicture();
            var vm = new VisionViewModel(ctx) { Check = PageSamples.CheckData, DecodePicture = PageSamples.Decode };
            var view = new VisionView();
            var window = Headless.Host(view, Width, Height);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            vm.ApplyStats(new Oadm.Plugins.Vision.Api.VisionStats { State = Oadm.Plugins.Vision.Api.LoadState.Ok, StateText = "All pipelines keep up", Load = 38 });
            using (vm)
            {
                vm.ApplyList(new Oadm.Plugins.Vision.Api.PipelineList
                {
                    Pipelines = [.. PageSamples.List.Pipelines, new() { Id = "p4", Name = "Entrance", DeviceId = PageSamples.Gate.Id, Fps = 8, Last = "In 41 · Out 33" }],
                });
                await vm.OpenAsync(vm.Pipelines.Single(p => p.Id == "p4"), CancellationToken.None);
                var editor = vm.Editor!;
                editor.StartPreview();
                editor.ApplyPreview(PageSamples.EntrancePreview(editor.PreviewId!));
                await editor.EventTypesAsync(); // the names of the chosen events
                editor.Canvas.FitView();
                Headless.Pump();
                Assert.Equal("Highest 14", editor.Canvas.Find("s")!.Live);
                Headless.Capture(window, $"vision-logic-{suffix}.png");

                // Time window: the shared day chips.
                editor.Canvas.SelectedNode = editor.Canvas.Find("w");
                Headless.Pump();
                Assert.True(view.GetVisualDescendants().OfType<DayChips>().Single().IsEffectivelyVisible);
                Headless.Capture(window, $"vision-days-{suffix}.png");

                // Device event: the chosen event with its device on the button.
                editor.Canvas.SelectedNode = editor.Canvas.Find("h");
                Headless.Pump();
                Assert.Contains(view.GetVisualDescendants().OfType<IconLabel>(), l => l.IsEffectivelyVisible && l.Text == "Door open · AXIS P1468-LE · 10.0.0.202");
                Headless.Capture(window, $"vision-device-event-{suffix}.png");
                editor.StopPreview();
            }

            window.Close();

            // The Choose event dialog.
            var picker = new EventTypePickerViewModel(PageSamples.EventTypes.Types, "io.port.door");
            var dialog = new EventTypeWindow { DataContext = picker };
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            Headless.Pump();
            Assert.True(picker.OkCommand.CanExecute(null));
            Headless.Capture(dialog, $"vision-event-type-dialog-{suffix}.png");
            dialog.SizeToContent = SizeToContent.Manual; // room for the panel below the field in the picture
            dialog.Height = 520;
            var select = dialog.FindControl<EventPicker>("EventPicker")!;
            select.SetOpen(true); // the event rules' select: its folders, search and description
            Headless.Pump();
            Assert.True(select.IsOpen);
            Headless.Capture(dialog, $"vision-event-type-open-{suffix}.png");
            select.SetOpen(false);
            dialog.Close();

            // While the list is read: the ring at once; a failure below the field.
            var reading = new EventTypePickerViewModel("io.port.door");
            var waiting = new EventTypeWindow { DataContext = reading };
            Oadm.Client.App.ApplyCrispText(waiting);
            waiting.Show();
            Headless.Pump();
            Assert.True(waiting.GetVisualDescendants().OfType<LoadingOverlay>().Single().IsEffectivelyVisible);
            Assert.False(reading.OkCommand.CanExecute(null));
            Headless.Capture(waiting, $"vision-event-type-loading-{suffix}.png");
            await reading.LoadAsync(_ => Task.FromException<IReadOnlyList<EventTypeChoice>>(new InvalidOperationException("The server does not answer.")), CancellationToken.None);
            Headless.Pump();
            Assert.Contains(waiting.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "The server does not answer.");
            Headless.Capture(waiting, $"vision-event-type-failed-{suffix}.png");
            waiting.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Copy_to_cameras_renders_the_picker_and_the_copies()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = CopyPipelineTests.Context();
            ctx.Video!.Still = PageSamples.GatePicture();
            var (vm, view, window) = await ShowAsync(ctx);
            using (vm)
            {
                // The shared picker of several devices: every camera but the pipeline's own.
                var row = vm.Pipelines.Single(p => p.Id == "p1");
                var picker = new DevicePickerWindow(new DevicePickerViewModel(vm.CopyTargetsOf(row))) { Heading = VisionTexts.ChooseCameras };
                Oadm.Client.App.ApplyCrispText(picker);
                picker.Show();
                Headless.Pump();
                Headless.Capture(picker, $"vision-copy-picker-{suffix}.png");
                picker.Close();

                vm.PickCameras = _ => Task.FromResult<IReadOnlyList<Guid>?>([.. PageSamples.MoreCameras.Select(c => c.Id)]);
                await vm.CopyCommand.ExecuteAsync(row);
                Headless.Pump();
                Assert.Equal(8, vm.Pipelines.Count);
                Assert.Empty(ctx.Messages);

                // The ⋯ menu of a copy's row open: Copy to cameras, then Delete in red.
                var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PipelineActions" && b.DataContext is PipelineListRow { Id: "p11" });
                RowActions.Show(button, vm.ActionsOf((PipelineListRow)button.DataContext!));
                Headless.Pump();
                Headless.Capture(window, $"vision-copies-{suffix}.png");
            }

            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
