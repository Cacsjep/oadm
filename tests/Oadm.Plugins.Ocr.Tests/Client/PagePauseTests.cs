using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// Pause on the page (docs/spec/ocr.md "Pause"): the button in the picture's corner, the held frame in the live video's place
/// with the lines of the server's reading, Resume, and what a real pointer press on the button leaves alone.
/// </summary>
public sealed class PagePauseTests
{
    /// <summary>The fake server of a preview that can pause: <paramref name="held"/> is the frame it answers.</summary>
    private static PauseServer Server(FakePageContext ctx, byte[] held)
    {
        var server = new PauseServer();
        ctx.Answers[OcrMethods.StartPreview] = json =>
        {
            var id = OcrJson.Deserialize<PreviewRequest>(json).PreviewId;
            return OcrJson.Serialize(new PreviewReply { PreviewId = id, Paused = server.Paused, Rereading = server.Rereading });
        };
        ctx.Answers[OcrMethods.PausePreview] = _ =>
        {
            server.Paused = true;
            return OcrJson.Serialize(new PauseReply { Paused = true, Picture = held, Reading = Samples.GateReading(), Details = Samples.GateExtras });
        };
        ctx.Answers[OcrMethods.ResumePreview] = json =>
        {
            server.Paused = false;
            return OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId });
        };
        return server;
    }

    private sealed class PauseServer
    {
        public bool Paused { get; set; }

        public bool Rereading { get; set; }
    }

    /// <summary>The page's editor with a running preview and its first reading.</summary>
    private static async Task<ReaderEditorViewModel> ReadingAsync(OcrViewModel vm)
    {
        var editor = vm.Editor!;
        editor.PreviewTime = new FakeTimeProvider();
        editor.StartPreview();
        await Eventually.TrueAsync(() => editor.PreviewId is not null, "the preview started");
        await editor.RefreshPreviewAsync(CancellationToken.None);
        return editor;
    }

    private static async Task<OcrViewModel> LoadedAsync(FakePageContext ctx)
    {
        var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    private static byte[] Pixel()
    {
        using var target = new RenderTargetBitmap(new PixelSize(16, 9));
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    [Fact]
    public async Task Pause_shows_the_ring_while_the_server_reads_the_held_frame_again()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Paused = true, Picture = Pixel(), Reading = Samples.GateReading(), Details = Samples.GateExtras, Rereading = true });
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);

            await editor.TogglePauseCommand.ExecuteAsync(null);
            Assert.True(editor.IsRereading);

            await editor.RefreshPreviewAsync(CancellationToken.None);
            Assert.False(editor.IsRereading);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_event_that_overtakes_the_answer_leaves_no_ring_on()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);
            await editor.TogglePauseCommand.ExecuteAsync(null);
            Assert.False(editor.IsRereading);

            // The server's answer was made before its reading ended, and reaches the page after that reading's event.
            var overtaken = new TaskCompletionSource<string?>();
            ctx.Answers[OcrMethods.StartPreview] = json => OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId, Paused = true, Rereading = true, HeldRead = 0 });
            editor.Areas[0].FormatText = "@@@U ###### *";
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) >= 2, "the draft sent");
            await Eventually.TrueAsync(() => editor.IsRereading, "the first answer");
            vm.HandleEvent(new PluginEvent(OcrMethods.PreviewTopic, OcrJson.Serialize(new PreviewEvent { PreviewId = editor.PreviewId!, HeldRead = 1, Reading = Samples.GateReading() })));
            await Eventually.TrueAsync(() => !editor.IsRereading, "the reading arrived");

            await editor.SendPreviewAsync(CancellationToken.None); // the late answer

            Assert.False(editor.IsRereading);

            // A newer draft after that reading announces its own: the ring again.
            ctx.Answers[OcrMethods.StartPreview] = json => OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId, Paused = true, Rereading = true, HeldRead = 1 });
            await editor.SendPreviewAsync(CancellationToken.None);
            Assert.True(editor.IsRereading);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_draft_the_server_never_took_leaves_no_ring_on()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);
            await editor.TogglePauseCommand.ExecuteAsync(null);

            ctx.Answers[OcrMethods.StartPreview] = _ => throw new InvalidOperationException("The server has gone.");
            editor.Areas[0].FormatText = "@@@U ###### *";

            await Eventually.TrueAsync(() => editor.PreviewProblem is not null && !editor.IsRereading, "the ring went");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Pause_shows_the_servers_frame_and_Resume_goes_back_live()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var server = Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = vm.Editor!;

            // The saved reader's last reading alone is no frame of this preview.
            Assert.False(editor.TogglePauseCommand.CanExecute(null));
            await ReadingAsync(vm);
            Assert.True(editor.TogglePauseCommand.CanExecute(null));
            Assert.Equal(OcrTexts.Pause, editor.PauseTip);
            Assert.True(editor.ShowLiveVideo);

            await editor.TogglePauseCommand.ExecuteAsync(null);

            Assert.True(editor.IsPaused);
            Assert.NotNull(editor.HeldPicture);
            Assert.False(editor.ShowLiveVideo); // hidden, the video stops streaming by itself
            Assert.Equal(OcrTexts.Resume, editor.PauseTip);
            Assert.Equal(2, editor.Lines.Count); // the lane's format has no match: no box

            // An edit goes to the server as always: the ring until its reading arrives.
            server.Rereading = true;
            editor.Areas[0].FormatText = "@@@U ###### *";
            await Eventually.TrueAsync(() => editor.IsRereading, "the ring");
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) >= 2, "the draft sent");
            Assert.True(editor.IsPauseWorking);
            server.Rereading = false;
            ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading("TCLU 000000 1"), Samples.GateExtras, null);
            await editor.RefreshPreviewAsync(CancellationToken.None);
            Assert.False(editor.IsRereading);
            Assert.Equal("TCLU 000000 1", editor.HeldAreas[0].Text);
            Assert.True(editor.IsPaused);

            // Use read text as format works on the held frame's reading.
            editor.UseReadTextAsFormatCommand.Execute(editor.Areas[0]);
            Assert.Equal("@@@@ ###### #", editor.Areas[0].FormatText);

            ctx.Answers[OcrMethods.GetPreview] = _ => "{}"; // the frame's reading goes with Resume
            await editor.TogglePauseCommand.ExecuteAsync(null);

            Assert.False(editor.IsPaused);
            Assert.Null(editor.HeldPicture);
            Assert.True(editor.ShowLiveVideo);
            Assert.False(editor.TogglePauseCommand.CanExecute(null)); // the next reading of the live preview first
            Assert.Equal(1, ctx.CallsOf(OcrMethods.ResumePreview));
            editor.StopPreview();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_note_says_per_area_with_a_format_what_the_held_frame_gives_only_while_paused()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);
            Assert.Null(editor.ReadingNote); // live: the alert waits, see PageNoteTests

            await editor.TogglePauseCommand.ExecuteAsync(null);

            // Text too small stays, a match gives the value, no match says so, an area without a format has no line.
            Assert.Equal(
                ["Lane: " + Oadm.Plugins.Ocr.Readers.ReaderRules.TooSmall, "Container number: MSKU 123456 7", "Lane: no match"],
                editor.ReadingNote!.Split('\n'));
            Assert.Equal(6, editor.NoteMaxLines); // the first line may wrap, none is cut

            // The held frame's list is by area, the matched box stays on the frame.
            Assert.Equal(["Container number", "Type", "Lane"], editor.HeldAreas.Select(a => a.Area));
            Assert.True(editor.HeldAreas[2].TooSmall);
            var match = Assert.Single(editor.Lines, l => l.Text == "MSKU 123456 7");
            Assert.Equal(Samples.GateExtras[0].MatchBox, match.Box);

            ctx.Answers[OcrMethods.GetPreview] = _ => "{}";
            await editor.TogglePauseCommand.ExecuteAsync(null);

            Assert.Empty(editor.HeldAreas);
            Assert.False(editor.HasReadingNote); // the values go with the frame; the next live reading brings its own note
            editor.StopPreview();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_page_leaves_the_pause_when_the_server_no_longer_holds_the_frame_or_the_page_hides()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var server = Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);
            await editor.TogglePauseCommand.ExecuteAsync(null);

            // Another resolution: the server ends the pause, the page follows.
            server.Paused = false;
            await editor.SendPreviewAsync(CancellationToken.None);
            Assert.False(editor.IsPaused);
            Assert.Null(editor.HeldPicture);

            // An answer that started before the Pause says nothing about it.
            await editor.RefreshPreviewAsync(CancellationToken.None);
            var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.AsyncAnswers[OcrMethods.StartPreview] = _ => late.Task;
            var sending = editor.SendPreviewAsync(CancellationToken.None);
            await editor.TogglePauseCommand.ExecuteAsync(null);
            ctx.AsyncAnswers.Remove(OcrMethods.StartPreview);
            late.SetResult(OcrJson.Serialize(new PreviewReply { PreviewId = editor.PreviewId! }));
            await sending;
            Assert.True(editor.IsPaused);

            // The page hides: the preview ends, and the frame with it.
            editor.StopPreview();
            Assert.False(editor.IsPaused);
            Assert.Null(editor.HeldPicture);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_refusal_stays_in_the_hosts_message_and_a_picture_that_does_not_decode_goes_back_live()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            using var vm = await LoadedAsync(ctx);
            var editor = await ReadingAsync(vm);

            ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Problem = "Too large." });
            await editor.TogglePauseCommand.ExecuteAsync(null);
            Assert.False(editor.IsPaused);
            Assert.Equal((OcrTexts.Pause, "Too large."), ctx.Messages[^1]);

            await editor.RefreshPreviewAsync(CancellationToken.None);
            ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Paused = true, Picture = [1, 2, 3], Reading = Samples.GateReading() });
            using var failing = new OcrViewModel(ctx) { DecodePicture = _ => Task.FromResult<Bitmap?>(null) };
            await failing.LoadAsync(CancellationToken.None);
            var other = await ReadingAsync(failing);
            await other.TogglePauseCommand.ExecuteAsync(null);
            Assert.False(other.IsPaused);
            Assert.Equal(1, ctx.CallsOf(OcrMethods.ResumePreview));
            other.StopPreview();
            editor.StopPreview();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Without_Live_view_Pause_is_not_offered_and_a_lost_right_ends_the_pause()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Pixel());
            ctx.Denied.Add(HostPermissionKeys.DevicesLiveView);
            using var denied = await LoadedAsync(ctx);
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = denied;
            Headless.Pump();
            var editor = await ReadingAsync(denied);
            Assert.False(editor.MayPause);
            Assert.False(editor.TogglePauseCommand.CanExecute(null));
            Assert.False(view.FindControl<Button>("PauseButton")!.IsEffectivelyVisible);
            editor.StopPreview();
            window.Close();

            ctx.Denied.Clear();
            using var vm = await LoadedAsync(ctx);
            var paused = await ReadingAsync(vm);
            await paused.TogglePauseCommand.ExecuteAsync(null);
            Assert.True(paused.IsPaused);
            ctx.Denied.Add(HostPermissionKeys.DevicesLiveView);
            ctx.RaisePermissionsChanged();
            await Eventually.TrueAsync(() => !paused.IsPaused, "the page left the pause");
            Assert.False(paused.MayPause);
            paused.StopPreview();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_real_press_on_the_button_touches_no_area_and_leaves_the_keys_with_the_picture()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            Server(ctx, Samples.ContainerPicture());
            using var vm = await LoadedAsync(ctx);
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            Headless.Pump();
            var editor = await ReadingAsync(vm);
            Headless.Pump();
            var picture = view.PictureControl;
            var button = view.FindControl<Button>("PauseButton")!;

            // An area over the whole bottom left corner of the picture, selected, the keys on the picture.
            var under = editor.AddArea(new RelativeBox(0, 0.6, 0.4, 0.4))!;
            Headless.Pump();
            var box = under.Box;
            picture.Focus();
            Headless.Pump();
            Assert.Same(picture, window.FocusManager!.GetFocusedElement());

            // The button sits inside the picture, also with black bars left and right of it.
            var rect = picture.PictureRect;
            var corner = button.TranslatePoint(default, picture)!.Value;
            Assert.True(new Rect(rect.Size).Translate(new Vector(rect.X, rect.Y)).Contains(new Rect(corner, button.Bounds.Size)));

            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(centre);
            window.MouseDown(centre, MouseButton.Left);
            Headless.Pump();
            Assert.False(picture.IsDragging);
            Assert.False(editor.IsAdjustingArea);
            window.MouseUp(centre, MouseButton.Left);
            await Eventually.TrueAsync(() => editor.IsPaused, "the press paused");
            Headless.Pump();

            Assert.Equal(box, under.Box);
            Assert.Same(under, editor.SelectedArea);
            Assert.Same(picture, window.FocusManager!.GetFocusedElement());

            // The arrow keys still move the area.
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Headless.Pump();
            Assert.Equal(box.X + AreaGeometry.Nudge, under.Box.X, 6);

            // While drawing a press on the button draws nothing and Esc still stops the drawing.
            editor.IsDrawing = true;
            Headless.Pump();
            var areas = editor.Areas.Count;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseMove(centre + new Point(60, -60));
            window.MouseUp(centre + new Point(60, -60), MouseButton.Left); // released outside: no click either
            Headless.Pump();
            Assert.True(editor.IsPaused);
            window.MouseMove(centre);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            await Eventually.TrueAsync(() => !editor.IsPaused, "the press resumed");
            Headless.Pump();
            Assert.Equal(areas, editor.Areas.Count);
            Assert.True(editor.IsDrawing);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Headless.Pump();
            Assert.False(editor.IsDrawing);

            // An area can still be drawn across the corner: the drag starts beside the button and ends on it.
            await editor.RefreshPreviewAsync(CancellationToken.None);
            editor.RemoveAreaCommand.Execute(under);
            editor.IsDrawing = true;
            Headless.Pump();
            var side = button.TranslatePoint(new Point(button.Bounds.Width + 40, -40), window)!.Value;
            window.MouseMove(side);
            window.MouseDown(side, MouseButton.Left);
            window.MouseMove(centre);
            window.MouseUp(centre, MouseButton.Left);
            Headless.Pump();
            Assert.Equal(areas, editor.Areas.Count);
            editor.StopPreview();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Eight_areas_with_a_format_fit_in_the_note_of_a_short_window()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var areas = Enumerable.Range(1, OcrLimits.MaxAreas).Select(i => new Area
            {
                Name = "Area " + i.ToString(CultureInfo.InvariantCulture),
                Box = new RelativeBox(0.05 + (0.1 * (i - 1)), 0.4, 0.08, 0.1),
                Expected = ExpectedText.WithFormat("##"),
            }).ToList();
            ctx.Answers[OcrMethods.GetReader] = _ => OcrJson.Serialize(Samples.ContainerGate with { Areas = areas });
            var reading = Samples.GateReading() with
            {
                Areas = [.. areas.Select((a, i) => new AreaResult { Area = a.Name, Text = i % 2 == 0 ? "12" : string.Empty, TextRead = "12", MatchesFormat = i % 2 == 0, Confidence = 90 })],
            };
            IReadOnlyList<AreaDetails> details = [.. areas.Select(a => new AreaDetails { Area = a.Name })];
            ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(reading, details, null);
            ctx.Answers[OcrMethods.GetPreview] = ctx.Answers[OcrMethods.GetReading];
            Server(ctx, Samples.ContainerPicture());
            ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Paused = true, Picture = Samples.ContainerPicture(), Reading = reading, Details = details });
            using var vm = await LoadedAsync(ctx);
            var view = new OcrView();
            var window = Headless.Host(view, 1500, 640);
            window.Show();
            view.DataContext = vm;
            Headless.Pump();
            var editor = await ReadingAsync(vm);
            await editor.TogglePauseCommand.ExecuteAsync(null);
            Headless.Pump();

            var chip = view.FindControl<StatusChip>("NoteChip")!;
            var card = view.FindControl<Border>("SettingsCard")!;
            var results = view.FindControl<Border>("ResultsCard")!;
            var save = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"));
            Assert.Equal(OcrLimits.MaxAreas, editor.ReadingNote!.Split('\n').Length);
            Assert.True(chip.TranslatePoint(new Point(0, chip.Bounds.Height), save)!.Value.Y <= 0, "the note stays above Save");
            Assert.True(chip.TranslatePoint(new Point(0, 0), card)!.Value.Y >= 0, "the whole note stays in the card");
            Assert.True(save.TranslatePoint(new Point(0, save.Bounds.Height), card)!.Value.Y <= card.Bounds.Height, "Save stays in the card");
            Assert.True(view.PictureControl.PictureRect.Height > 0);
            Assert.True(results.Bounds.Height >= OcrView.ResultsDragMinHeight - 0.5);
            Headless.Capture(window, $"ocr-pause-eight-areas-{suffix}.png");
            editor.StopPreview();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>A picture of the reader's shape with a magenta block exactly on <paramref name="box"/> (measured in the screenshots).</summary>
    private static byte[] MarkerPicture(int width, int height, RelativeBox box)
    {
        using var target = new RenderTargetBitmap(new PixelSize(width, height));
        using (var dc = target.CreateDrawingContext())
        {
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#3a5a40")), new Rect(0, 0, width, height));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#ff00ff")), new Rect(box.X * width, box.Y * height, box.Width * width, box.Height * height));
        }

        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    [Fact]
    public async Task Pause_renders_live_paused_and_after_an_edit()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            var server = Server(ctx, Samples.ContainerPicture());
            using var vm = await LoadedAsync(ctx);
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            Headless.Pump();
            var editor = await ReadingAsync(vm);
            Headless.Pump();
            var chip = view.FindControl<StatusChip>("PausedChip")!;
            Assert.True(view.FindControl<ContentControl>("VideoHost")!.IsEffectivelyVisible);
            Assert.False(chip.IsEffectivelyVisible);
            Headless.Capture(window, $"ocr-pause-live-{suffix}.png");

            await editor.TogglePauseCommand.ExecuteAsync(null);
            Headless.Pump();
            Assert.False(view.FindControl<ContentControl>("VideoHost")!.IsEffectivelyVisible);
            Assert.True(chip.IsEffectivelyVisible);
            Assert.Equal(OcrTexts.Paused, chip.Text);
            Assert.NotNull(view.PictureControl.HeldPicture);
            Headless.Capture(window, $"ocr-pause-paused-{suffix}.png");

            // Another format on the held frame: the ring, then the new reading's lines and results.
            server.Rereading = true;
            editor.Areas[2].FormatText = "@@";
            await Eventually.TrueAsync(() => editor.IsRereading, "the ring");
            Headless.Pump();
            Assert.True(view.FindControl<ProgressRing>("PauseRing")!.IsEffectivelyVisible);
            Headless.Capture(window, $"ocr-pause-rereading-{suffix}.png");
            server.Rereading = false;
            var edited = Samples.GateReading();
            edited = edited with
            {
                Areas =
                [
                    edited.Areas[0],
                    edited.Areas[1],
                    edited.Areas[2] with { Text = "G3", Confidence = 71, MatchesFormat = true },
                ],
            };
            ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(edited, Samples.GateExtras, null);
            await editor.RefreshPreviewAsync(CancellationToken.None);
            Headless.Pump();
            Assert.Equal("G3", editor.HeldAreas[2].Text);
            Assert.True(editor.HeldAreas[2].IsMatch);
            Assert.Contains("Lane: G3", editor.ReadingNote, StringComparison.Ordinal);
            Headless.Capture(window, $"ocr-pause-after-edit-{suffix}.png");
            editor.StopPreview();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// The frames to measure (the file names say what): the held picture and the overlay on one rectangle, 16:9 in a wide window
    /// (black bars left and right) and a 4:3 reader. The sizes go to a text file for the measurement, never into an assertion.
    /// </summary>
    [Fact]
    public async Task The_held_picture_lines_up_with_the_overlay()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var box = new RelativeBox(0.25, 0.25, 0.5, 0.4);
            foreach (var (name, resolution, width, height) in new[]
            {
                ("wide", new Resolution(1920, 1080), 2600, 900),
                ("tall", new Resolution(1920, 1080), 1500, 1000),
                ("four-three", new Resolution(1280, 960), 2000, 900),
            })
            {
                var ctx = Samples.Context();
                var reader = Samples.ContainerGate with { Resolution = resolution, Areas = [new Area { Name = "Box", Box = box }] };
                ctx.Answers[OcrMethods.GetReader] = _ => OcrJson.Serialize(reader);
                ctx.Answers[OcrMethods.GetCameraOptions] = _ => OcrJson.Serialize(new CameraOptions
                {
                    VideoSources = Samples.CameraOptions.VideoSources,
                    Resolutions = [resolution],
                });
                var reading = new Reading
                {
                    ReaderId = "r1",
                    Time = Samples.Time,
                    Resolution = resolution,
                    Areas = [new AreaResult { Area = "Box", Text = "BOX", TextRead = "BOX", Confidence = 99, Lines = [new TextLine("BOX", box, 99)] }],
                };
                Server(ctx, MarkerPicture(resolution.Width, resolution.Height, box));
                ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Paused = true, Picture = MarkerPicture(resolution.Width, resolution.Height, box), Reading = reading, Details = [new AreaDetails { Area = "Box" }] });
                ctx.Answers[OcrMethods.GetPreview] = _ => OcrJson.Serialize(new ReadingReply { Reading = reading, Details = [new AreaDetails { Area = "Box" }] });
                using var vm = await LoadedAsync(ctx);
                var view = new OcrView();
                var window = Headless.Host(view, width, height);
                window.Show();
                view.DataContext = vm;
                Headless.Pump();
                var editor = await ReadingAsync(vm);
                await editor.TogglePauseCommand.ExecuteAsync(null);
                Headless.Pump();
                var picture = view.PictureControl;
                var at = picture.TranslatePoint(new Point(picture.PictureRect.X, picture.PictureRect.Y), window)!.Value;
                var button = view.FindControl<Button>("PauseButton")!;
                var buttonAt = button.TranslatePoint(default, window)!.Value;
                Headless.Capture(window, $"ocr-pause-align-{name}-{suffix}.png");
                if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is { Length: > 0 } dir)
                {
                    File.WriteAllText(
                        Path.Combine(dir, $"ocr-pause-align-{name}-{suffix}.txt"),
                        string.Create(CultureInfo.InvariantCulture, $"{at.X} {at.Y} {picture.PictureRect.Width} {picture.PictureRect.Height} {box.X} {box.Y} {box.Width} {box.Height} {buttonAt.X} {buttonAt.Y} {button.Bounds.Width} {button.Bounds.Height}"));
                }

                editor.StopPreview();
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
