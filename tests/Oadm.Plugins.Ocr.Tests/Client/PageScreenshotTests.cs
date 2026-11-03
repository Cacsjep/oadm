using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;

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

            var ctx = Samples.Context(Samples.ContainerPicture());
            using (var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode })
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm;
                await vm.LoadAsync(CancellationToken.None);
                var editor = vm.Editor!;
                editor.SelectedArea = editor.Areas[0];
                Headless.Pump();

                // Watching, with the areas, the lines and the results with the match's cut-out.
                Assert.NotNull(editor.Picture);
                Assert.True(editor.Results[0].HasCutOut);
                Assert.False(editor.Results[1].HasCutOut); // Any text: no cut-out
                Assert.IsType<Avalonia.Media.Imaging.CroppedBitmap>(editor.Results[0].CutOut); // cut from the frame by the match box

                // The server's own cut-out wins over cutting the frame.
                var served = Samples.GateExtras.ToList();
                served[0] = served[0] with { Cutout = Samples.ContainerPicture() };
                ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), served, Samples.ContainerPicture());
                await editor.RefreshReadingAsync(CancellationToken.None);
                Assert.IsType<Avalonia.Media.Imaging.Bitmap>(editor.Results[0].CutOut);
                ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, Samples.ContainerPicture());
                await editor.RefreshReadingAsync(CancellationToken.None);
                Headless.Pump();
                var picture = view.PictureControl;
                Assert.True(picture.PictureRect.Width > 100);
                Assert.Equal(16.0 / 9, picture.PictureRect.Width / picture.PictureRect.Height, 2);
                Headless.Capture(window, $"ocr-watching-{suffix}.png");

                // Drawing an area: the hint and the rectangle being dragged.
                editor.IsDrawing = true;
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

                // An On demand reader without a reading.
                ctx.ConfirmAnswer = true;
                lane.FormatText = "A#";
                await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r2"), CancellationToken.None);
                Headless.Pump();
                Assert.Equal("Water meter", vm.Editor!.Name);
                Assert.False(vm.Editor.IsWatching);
                Headless.Capture(window, $"ocr-on-demand-{suffix}.png");
                window.Close();
            }
        }, CancellationToken.None);
    }
}
