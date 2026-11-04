using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>The pictures of the user docs: the page and parts of it cut out of the same frame (website/scripts/screens.json).</summary>
public sealed class PageDocsScreenshotTests
{
    private const string BothOnCard = "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti";

    [Fact]
    public async Task Pictures_of_the_user_docs_render()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            ctx.Answers[OcrMethods.GetEngine] = _ => OcrJson.Serialize(Samples.Engine with { RunsOn = BothOnCard });
            ctx.Answers[OcrMethods.GetReading] = json => OcrJson.Deserialize<ReaderRequest>(json).ReaderId == "r1"
                ? Samples.ReplyJson(Samples.GateReading(), [.. Samples.GateExtras.Select(d => d with { TooSmall = false })], null, Samples.WithCutOuts(Samples.Reads(9)))
                : "{}";
            ctx.Answers[OcrMethods.ListReaders] = _ => OcrJson.Serialize(new
            {
                readers = new object[]
                {
                    new { id = "r1", name = "Container gate", deviceId = Samples.Gate.Id, mode = "Watching", fps = 1, lastText = "MSKU 123456 7", lastTime = Samples.Time },
                    new { id = "r2", name = "Water meter", deviceId = Samples.Plant.Id, mode = "OnAction", lastText = "" },
                    new { id = "r3", name = "Dock 4 sign", deviceId = Samples.Dock.Id, mode = "Watching", fps = 2, lastText = "DOCK 4", lastTime = Samples.Time, problem = ReaderRules.TooManyWatching(16) },
                },
                fpsChoices = new[] { 1, 2, 4, 8, 12, 15 },
                maxWatching = 16,
            });

            using (var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode })
            {
                var view = new OcrView();
                var window = Headless.Host(view);
                window.Show();
                view.DataContext = vm;
                await vm.LoadAsync(CancellationToken.None);
                Headless.Pump();
                var stats = view.FindControl<StackPanel>("StatsPanel")!;
                var engine = view.FindControl<StackPanel>("EnginePanel")!;
                Rect Header() => Union(Place(stats, window), Place(engine, window));

                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));
                Headless.Pump();
                Assert.True(stats.IsEffectivelyVisible);
                Headless.Capture(window, $"ocr-docs-page-{suffix}.png");
                Crop(window, Header(), $"ocr-docs-status-ok-{suffix}.png");

                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Busy, ReaderRules.BusySlow(1), 81,
                    new ReaderRate { ReaderId = "r1", Fps = 1, Reads = 0.8, Slow = true }));
                Headless.Pump();
                Crop(window, Header(), $"ocr-docs-status-busy-{suffix}.png");
                var list = view.FindControl<ListBox>("ReaderList")!;
                var readers = Place(view.FindControl<Grid>("ReadersColumn")!, window);
                var lastRow = Place((Control)list.ContainerFromIndex(vm.Readers.Count - 1)!, window);
                Crop(window, new Rect(readers.X, readers.Y, readers.Width, lastRow.Bottom - readers.Y), $"ocr-docs-readers-{suffix}.png");

                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Overloaded, ReaderRules.Overloaded, 99,
                    new ReaderRate { ReaderId = "r1", Fps = 1, Reads = 0.4, Slow = true }));
                Headless.Pump();
                Assert.Contains(stats.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.IsError);
                Crop(window, Header(), $"ocr-docs-status-overloaded-{suffix}.png");
                vm.ApplyStats(PageAreasAndStatsTests.Stats(LoadState.Ok, ReaderRules.KeepUp, 42));

                // Drawing an area: the video card alone.
                var editor = vm.Editor!;
                view.FindControl<ToolbarButton>("NewAreaButton")!.Command!.Execute(null);
                Assert.True(editor.IsDrawing);
                view.PictureControl.ShowDrag(new Point(0.12, 0.62), new Point(0.38, 0.76));
                Headless.Pump();
                Crop(window, Place(view.FindControl<Border>("VideoCard")!, window), $"ocr-docs-drawing-{suffix}.png", 0);
                window.Close();
            }

            // The settings: Moving objects on, a note above Save, in a low window so the card holds no gap.
            using (var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode })
            {
                ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, Samples.Reads(3));
                ctx.Answers[OcrMethods.GetPreview] = ctx.Answers[OcrMethods.GetReading];
                var view = new OcrView();
                var window = Headless.Host(view, 1500, 640);
                window.Show();
                view.DataContext = vm;
                await vm.LoadAsync(CancellationToken.None);
                var editor = vm.Editor!;
                editor.MovingObjects = true;
                editor.StartPreview();
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Headless.Pump();
                Assert.True(editor.HasReadingNote);
                Crop(window, Place(view.FindControl<Border>("SettingsCard")!, window), $"ocr-docs-settings-{suffix}.png", 0);
                editor.StopPreview();
                window.Close();
            }

            // The Stream settings dialog without a refusal.
            var form = new StreamSettingsViewModel(
                Samples.CameraOptions.VideoSources, Samples.CameraOptions.VideoSources[0], Samples.CameraOptions.Resolutions, new Resolution(1920, 1080),
                [1, 2, 4, 8, 12, 15], 2, showFps: true, isLoading: false, new Dictionary<string, string?>());
            var stream = new StreamSettingsWindow { DataContext = form };
            Oadm.Client.App.ApplyCrispText(stream);
            stream.Show();
            Headless.Pump();
            Assert.True(stream.FindControl<ComboBox>("FpsBox")!.IsEffectivelyVisible);
            Headless.Capture(stream, $"ocr-docs-stream-settings-{suffix}.png");
            stream.Close();
            return true;
        }, CancellationToken.None);
    }

    private static Rect Place(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static Rect Union(Rect a, Rect b) => a.Union(b);

    /// <summary>Writes a part of the window's frame, with a margin, when OADM_SCREENSHOT_DIR is set.</summary>
    private static void Crop(Window window, Rect part, string name, double margin = 8)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        var rect = part.Inflate(margin).Intersect(new Rect(window.ClientSize));
        var size = new PixelSize((int)rect.Width, (int)rect.Height);
        using var target = new RenderTargetBitmap(size);
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(frame, rect, new Rect(0, 0, size.Width, size.Height));
        }

        Directory.CreateDirectory(outDir);
        target.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
    }
}
