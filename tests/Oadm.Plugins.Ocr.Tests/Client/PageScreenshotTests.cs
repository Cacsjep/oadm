using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>Renders the page offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class PageScreenshotTests
{
    /// <summary>Dark by default; OADM_TEST_APPEARANCE=Light writes the light files in a second run.</summary>
    [Fact]
    public async Task Page_renders_every_state()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            // The empty page.
            var empty = Samples.Context();
            empty.Answers[OcrMethods.ListReaders] = _ => "{}";
            using (var vm = new OcrViewModel(empty))
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm; // after attaching: no live watch, deterministic content
                await vm.LoadAsync(CancellationToken.None);
                Headless.Pump();
                Assert.True(vm.ShowEmpty);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == OcrInfo.EmptyText);
                Headless.Capture(window, $"ocr-empty-{suffix}.png");
                window.Close();
            }

            // The live video shows a still frame of the gate (the reader's 16:9 picture).
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            ctx.Answers[OcrMethods.GetReading] = json => OcrJson.Deserialize<ReaderRequest>(json).ReaderId == "r1"
                ? Samples.ReplyJson(Samples.GateReading(), [.. Samples.GateExtras.Select(d => d with { TooSmall = false })], null, Samples.WithCutOuts(Samples.Reads(9)))
                : "{}";
            using (var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode })
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm;
                await vm.LoadAsync(CancellationToken.None);
                var editor = vm.Editor!;
                Headless.Pump();

                // Watching: the live video with the areas and the lines over it; the form, the areas and the results right of it.
                var host = view.FindControl<ContentControl>("VideoHost")!;
                Assert.Same(ctx.Video.Created[^1].View, host.Content);
                Assert.True(host.IsEffectivelyVisible);
                Assert.Equal(9, editor.Results.Count);
                Assert.All(editor.Results, r => Assert.True(r.HasCutOut)); // the server's cut-out of each read
                var picture = view.PictureControl;
                Assert.True(picture.PictureRect.Width > 100);
                Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
                Assert.Equal(2, editor.Lines.Count);
                Assert.Equal("Configure · 1920x1080 @ 1 fps", editor.StreamSummary);
                var draw = view.FindControl<ToolbarButton>("NewAreaButton")!;
                Assert.True(draw.IsEffectivelyVisible);
                Assert.Equal("New", draw.Text);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<ToolbarButton>(), b => b.Text == "Read now");

                // The runtime stats below Settings: green, the results below the video.
                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));
                Headless.Pump();
                Assert.True(view.FindControl<StackPanel>("StatsPanel")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<DataGrid>("ResultsGrid")!.IsEffectivelyVisible);
                Headless.Capture(window, $"ocr-watching-{suffix}.png");

                // Orange: a reader reads slower than set (its chip too); red: overloaded.
                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Busy, ReaderRules.BusySlow(1), 81,
                    new ReaderRate { ReaderId = "r1", Fps = 1, Reads = 0.8, Slow = true }));
                Headless.Pump();
                Assert.Contains(view.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == "Watching · 1 fps (reads 0.8)" && c.IsWarning);
                Headless.Capture(window, $"ocr-stats-busy-{suffix}.png");
                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Overloaded, ReaderRules.Overloaded, 99,
                    new ReaderRate { ReaderId = "r1", Fps = 1, Reads = 0.4, Slow = true }));
                Headless.Pump();
                Assert.Contains(view.FindControl<StackPanel>("StatsPanel")!.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == ReaderRules.Overloaded && c.IsError);
                Headless.Capture(window, $"ocr-stats-overloaded-{suffix}.png");
                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));

                // Moving an area: the lines are hidden while the drag lasts.
                picture.BeginEdit(editor.Areas[1], AreaHandle.Move, new Point(0.53, 0.44));
                picture.UpdateEdit(new Point(0.58, 0.60));
                Headless.Pump();
                Assert.Empty(editor.ShownLines);
                Headless.Capture(window, $"ocr-dragging-{suffix}.png");
                picture.CancelEdit();
                Headless.Pump();
                Assert.Equal(2, editor.ShownLines.Count);

                // A selected area: solid, with its handles; a drag on its corner resizes it (Esc cancels a drag).
                editor.SelectedArea = editor.Areas[0];
                Headless.Pump();
                var rect = picture.PictureRect;
                var corner = new Point(rect.X + ((0.47 + 0.27) * rect.Width), rect.Y + ((0.27 + 0.12) * rect.Height));
                Assert.Equal((editor.Areas[0], AreaHandle.BottomRight), picture.HitTest(corner));
                Assert.Equal((editor.Areas[1], AreaHandle.Move), picture.HitTest(new Point(rect.X + (0.5 * rect.Width), rect.Y + (0.44 * rect.Height))));
                Assert.Equal(((AreaViewModel?)null, AreaHandle.None), picture.HitTest(new Point(rect.X + (0.1 * rect.Width), rect.Y + (0.1 * rect.Height))));
                Headless.Capture(window, $"ocr-area-selected-{suffix}.png");
                var before = editor.Areas[0].Box;
                picture.BeginEdit(editor.Areas[0], AreaHandle.BottomRight, new Point(0.74, 0.39));
                picture.UpdateEdit(new Point(0.80, 0.45));
                Assert.Equal(before, editor.Areas[0].Box); // nothing changes before the drag ends
                picture.Focus();
                picture.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(picture.IsDragging);
                Assert.Equal(before, editor.Areas[0].Box);
                picture.BeginEdit(editor.Areas[0], AreaHandle.BottomRight, new Point(0.74, 0.39));
                picture.UpdateEdit(new Point(0.80, 0.45));
                picture.EndEdit();
                Assert.Equal(0.33, editor.Areas[0].Box.Width, 6);
                Assert.True(editor.IsDirty);

                // An arrow key nudges the selected area.
                var x = editor.Areas[0].Box.X;
                picture.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.Shift });
                Assert.Equal(x - AreaGeometry.BigNudge, editor.Areas[0].Box.X, 6);
                editor.Areas[0].Box = before;

                // Drawing an area: the hint and the rectangle being dragged over the live video.
                editor.SelectedArea = null;
                draw.Command!.Execute(null);
                Assert.True(editor.IsDrawing);
                picture.ShowDrag(new Point(0.12, 0.62), new Point(0.38, 0.76));
                Headless.Pump();
                Headless.Capture(window, $"ocr-drawing-{suffix}.png");
                RelativeBox? drawn = null;
                picture.AreaDrawn += (_, box) => drawn = box;
                picture.FinishDrag(new Point(0.5, 0.5), new Point(0.505, 0.6)); // too narrow: not an area
                Assert.Null(drawn);
                picture.FinishDrag(new Point(0.38, 0.76), new Point(0.12, 0.62));
                Assert.NotNull(drawn);
                Assert.Equal(0.12, drawn!.Value.X, 6);
                Assert.Equal(0.14, drawn.Value.Height, 6);
                Assert.Equal(4, editor.Areas.Count); // the view handed it to the reader
                Assert.False(editor.IsDrawing);
                Assert.Equal("Area 1", editor.Areas[^1].Name); // the lowest free number
                editor.RemoveAreaCommand.Execute(editor.Areas[^1]);

                // A wrong format: the error below the format field, Save off.
                var lane = editor.Areas[2];
                editor.SelectedArea = lane;
                lane.FormatText = "12";
                Headless.Pump();
                view.FindControl<ListBox>("AreaList")!.ContainerFromItem(lane)!.BringIntoView();
                Headless.Pump();
                Assert.Equal("A format needs at least one #, @ or *.", lane.ErrorOf(nameof(AreaViewModel.FormatText)));
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "A format needs at least one #, @ or *.");
                Assert.False(editor.SaveCommand.CanExecute(null));
                Headless.Capture(window, $"ocr-format-error-{suffix}.png");

                // A reader without an area: the video dimmed with the start hint, no results.
                await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
                Headless.Pump();
                Assert.True(vm.Editor!.ShowStartHint);
                Assert.True(view.PictureControl.ShowStartHint);
                Headless.Capture(window, $"ocr-no-area-{suffix}.png");

                // An On action reader without a reading: no frame rate.
                ctx.ConfirmAnswer = true;
                lane.FormatText = "@#";
                await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
                Headless.Pump();
                Assert.Equal("Water meter", vm.Editor!.Name);
                Assert.True(vm.Editor.IsOnAction);
                Assert.Equal("Configure · 1920x1080", ((Oadm.Plugins.Ocr.Client.ReaderEditorViewModel)view.FindControl<Grid>("SettingsColumn")!.DataContext!).StreamSummary); // no frame rate
                Headless.Capture(window, $"ocr-on-action-{suffix}.png");

                // A refusal of the server for the resolution: in the summary's place below Stream settings.
                vm.Editor.ShowFieldError(new FieldError(OcrFields.Resolution, ReaderRules.ResolutionMissing));
                Headless.Pump();
                Assert.True(view.FindControl<FormField>("StreamField")!.HasError);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == ReaderRules.ResolutionMissing);
                Headless.Capture(window, $"ocr-stream-error-{suffix}.png");
                window.Close();
            }

            // The Stream settings dialog of a watching reader (frames per second shown), the resolution refused by the server.
            var form = new StreamSettingsViewModel(
                Samples.StreamSources, Samples.StreamSources[0], Samples.CameraOptions.Resolutions, new Resolution(1920, 1080),
                [1, 2, 4, 8, 12, 15], 4, showFps: true, isLoading: false,
                new Dictionary<string, string?> { [nameof(StreamSettingsViewModel.SelectedResolution)] = ReaderRules.ResolutionMissing });
            var stream = new StreamSettingsWindow { DataContext = form };
            Oadm.Client.App.ApplyCrispText(stream);
            stream.Show();
            Headless.Pump();
            Assert.True(stream.FindControl<ComboBox>("FpsBox")!.IsEffectivelyVisible);
            Assert.Contains(stream.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == ReaderRules.ResolutionMissing);
            Assert.DoesNotContain(stream.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));
            Headless.Capture(stream, $"ocr-stream-settings-{suffix}.png");
            stream.Close();

            // The Rename dialog, with a name another area has.
            var rename = new RenameAreaViewModel("Area 2", name => name.Equals("Lane", StringComparison.OrdinalIgnoreCase)) { Name = "Lane" };
            var renameWindow = new RenameAreaWindow { DataContext = rename };
            Oadm.Client.App.ApplyCrispText(renameWindow);
            renameWindow.Show();
            Headless.Pump();
            Assert.Contains(renameWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == ReaderRules.AreaNameTaken("Lane"));
            Headless.Capture(renameWindow, $"ocr-rename-area-{suffix}.png");
            rename.Name = "Container number 2";
            Headless.Pump();
            Headless.Capture(renameWindow, $"ocr-rename-area-ok-{suffix}.png");
            renameWindow.Close();

            // The short message when a reader is set to On action (the host shows it with MessageWindow).
            var notice = new MessageWindow { Heading = OcrTexts.OnActionNoticeTitle, Message = OcrTexts.OnActionNotice, ConfirmText = "OK", CancelText = null };
            Oadm.Client.App.ApplyCrispText(notice);
            notice.Show();
            Headless.Pump();
            Assert.False(notice.CancelButton.IsVisible);
            Headless.Capture(notice, $"ocr-on-action-message-{suffix}.png");
            notice.Close();

            // The short message after Use read text as format made a format that checks a container number.
            var check = new MessageWindow { Heading = OcrTexts.CheckFormatTitle, Message = OcrTexts.CheckFormatNotice, ConfirmText = "OK", CancelText = null };
            Oadm.Client.App.ApplyCrispText(check);
            check.Show();
            Headless.Pump();
            Headless.Capture(check, $"ocr-check-format-message-{suffix}.png");
            check.Close();

            // A value: the async lambda is then awaited to its end (Dispatch has no overload for a plain Task, so its
            // assertions after the first await would otherwise never reach the test).
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Problems_refusals_and_loading_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            // The list with a short chip per kind of problem, the area list refused below its rows.
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            ctx.Answers[OcrMethods.ListReaders] = _ => OcrJson.Serialize(new
            {
                readers = new object[]
                {
                    new { id = "r1", name = "Container gate", deviceId = Samples.Gate.Id, mode = "Watching", fps = 1, lastText = "MSKU 123456 7", lastTime = Samples.Time },
                    new { id = "r3", name = "Dock 4 sign", deviceId = Samples.Dock.Id, mode = "Watching", fps = 2, lastText = "DOCK 4", lastTime = Samples.Time, problem = ReaderRules.TooManyWatching(16) },
                    new { id = "r2", name = "Water meter", deviceId = Samples.Plant.Id, mode = "OnAction", lastText = "", problem = Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired },
                    new { id = "r4", name = "Lane sign", deviceId = Samples.Plant.Id, mode = "Watching", fps = 4, lastText = "", problem = Oadm.Plugins.Ocr.Frames.FrameTexts.ResolutionNotOffered },
                    new { id = "r5", name = "Gate 2 plate", deviceId = Samples.Gate.Id, mode = "Watching", fps = 2, lastText = "", problem = Oadm.Sdk.Devices.DeviceMessages.NotAnswering },
                },
                fpsChoices = new[] { 1, 2, 4, 8, 12, 15 },
            });
            using (var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode })
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm;
                await vm.LoadAsync(CancellationToken.None);
                var editor = vm.Editor!;
                editor.ShowFieldError(new FieldError(OcrFields.Areas, ReaderRules.TooManyAreas));
                Headless.Pump();
                Assert.Equal([OcrTexts.WatchingAt(1), OcrTexts.LimitReached, OcrTexts.LoginRefused, OcrTexts.StreamError, OcrTexts.NotReachable], vm.Readers.Select(r => r.ModeText));
                foreach (var chip in new[] { OcrTexts.LimitReached, OcrTexts.LoginRefused, OcrTexts.StreamError, OcrTexts.NotReachable })
                {
                    Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == chip);
                }

                var error = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AreasError");
                Assert.True(error.IsEffectivelyVisible);
                Assert.Equal(ReaderRules.TooManyAreas, error.Text);
                Headless.Capture(window, $"ocr-problems-{suffix}.png");
                window.Close();
            }

            // The list and a reader still being read: the loading overlays.
            var slow = Samples.Context();
            var list = new TaskCompletionSource<string?>();
            var reader = new TaskCompletionSource<string?>();
            slow.AsyncAnswers[OcrMethods.ListReaders] = _ => list.Task;
            slow.AsyncAnswers[OcrMethods.GetReader] = _ => reader.Task;
            using (var vm = new OcrViewModel(slow) { DecodePicture = Samples.Decode })
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm;
                var load = vm.LoadAsync(CancellationToken.None);
                Headless.Pump();
                Assert.True(vm.IsLoading);
                Assert.Contains(view.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible && o.Text == OcrTexts.ReadingReaders);
                Headless.Capture(window, $"ocr-loading-list-{suffix}.png");

                list.SetResult(Samples.ListJson());
                Headless.Pump();
                Assert.True(vm.IsOpening);
                Assert.Contains(view.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible && o.Text == OcrTexts.ReadingReader);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible && o.Text == OcrTexts.ReadingReaders);
                Headless.Capture(window, $"ocr-loading-reader-{suffix}.png");

                reader.SetResult(OcrJson.Serialize(Samples.ContainerGate));
                await load;
                Headless.Pump();
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible);
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
