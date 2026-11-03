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
                ? Samples.ReplyJson(Samples.GateReading(), Samples.WithCutout(Samples.GateExtras, Samples.ContainerPicture()), null)
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
                Assert.True(editor.Results[0].HasCutOut); // the server's cut-out of the match
                Assert.False(editor.Results[1].HasCutOut); // Any text: none
                var picture = view.PictureControl;
                Assert.True(picture.PictureRect.Width > 100);
                Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
                Assert.Equal(3, editor.Lines.Count);
                var fps = view.FindControl<ComboBox>("FpsBox")!;
                Assert.True(fps.IsEffectivelyVisible);
                Assert.Equal(1, fps.SelectedItem);
                Assert.Equal(6, fps.ItemCount);
                var draw = view.FindControl<ToolbarButton>("DrawAreaButton")!;
                Assert.True(draw.IsEffectivelyVisible);
                Assert.Equal("Draw area", draw.Text);
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
                Assert.Contains(view.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == ReaderRules.Overloaded && c.IsError);
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
                Assert.Equal(3, editor.ShownLines.Count);

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
                Assert.Equal("Area 4", editor.Areas[^1].Name);
                editor.RemoveAreaCommand.Execute(editor.Areas[^1]);

                // A wrong format: the error below the format field, Save off.
                var lane = editor.Areas[2];
                editor.SelectedArea = lane;
                lane.FormatText = "12";
                Headless.Pump();
                view.FindControl<ItemsControl>("AreaList")!.ContainerFromItem(lane)!.BringIntoView();
                Headless.Pump();
                Assert.Equal("A format needs at least one #, A or *.", lane.ErrorOf(nameof(AreaViewModel.FormatText)));
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "A format needs at least one #, A or *.");
                Assert.False(editor.SaveCommand.CanExecute(null));
                Headless.Capture(window, $"ocr-format-error-{suffix}.png");

                // A reader without an area: the video dimmed with the start hint, no results.
                await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
                vm.Editor!.Problem = null;
                Headless.Pump();
                Assert.True(vm.Editor.ShowStartHint);
                Assert.True(view.PictureControl.ShowStartHint);
                Headless.Capture(window, $"ocr-no-area-{suffix}.png");

                // An On action reader without a reading: no frame rate.
                ctx.ConfirmAnswer = true;
                lane.FormatText = "A#";
                await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
                Headless.Pump();
                Assert.Equal("Water meter", vm.Editor!.Name);
                Assert.True(vm.Editor.IsOnAction);
                Assert.False(view.FindControl<ComboBox>("FpsBox")!.IsEffectivelyVisible);
                Headless.Capture(window, $"ocr-on-action-{suffix}.png");
                window.Close();
            }

            // The short message when a reader is set to On action (the host shows it with MessageWindow).
            var notice = new MessageWindow { Heading = OcrTexts.OnActionNoticeTitle, Message = OcrTexts.OnActionNotice, ConfirmText = "OK", CancelText = null };
            Oadm.Client.App.ApplyCrispText(notice);
            notice.Show();
            Headless.Pump();
            Assert.False(notice.CancelButton.IsVisible);
            Headless.Capture(notice, $"ocr-on-action-message-{suffix}.png");
            notice.Close();

            // A value: the async lambda is then awaited to its end (Dispatch has no overload for a plain Task, so its
            // assertions after the first await would otherwise never reach the test).
            return true;
        }, CancellationToken.None);
    }
}
