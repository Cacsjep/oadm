using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Plugins.Ocr.Tests.Recognition;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>The note above Save and the setting Moving objects on the page (docs/spec/ocr.md "Page", "Moving objects").</summary>
public sealed class PageNoteTests
{
    private static readonly IReadOnlyList<AreaDetails> Fine = [new AreaDetails { Area = "Container number" }];

    private sealed class Page(ReaderEditorViewModel editor, OcrViewModel vm, OcrView view, Window window, FakePageContext ctx, FakeTimeProvider clock) : IDisposable
    {
        public ReaderEditorViewModel Editor => editor;

        public OcrView View => view;

        public Window Window => window;

        public FakeTimeProvider Clock => clock;

        public async Task ReadingAsync(Reading reading, IReadOnlyList<AreaDetails> details)
        {
            ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(reading, details, null, Samples.Reads(3));
            await editor.RefreshPreviewAsync(CancellationToken.None);
            Headless.Pump();
        }

        public void Dispose()
        {
            editor.StopPreview();
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The page in a window with a live preview on a fake clock, no note yet.</summary>
    private static async Task<Page> OpenAsync(FakePageContext ctx, int width = 2000, int height = 870)
    {
        ctx.Video!.Still = Samples.ContainerPicture();
        ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Fine, null, Samples.Reads(3));
        ctx.Answers[OcrMethods.GetPreview] = ctx.Answers[OcrMethods.GetReading];
        var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
        var view = new OcrView();
        var window = Headless.Host(view, width, height);
        window.Show();
        view.DataContext = vm;
        await vm.LoadAsync(CancellationToken.None);
        var clock = new FakeTimeProvider();
        var editor = vm.Editor!;
        editor.PreviewTime = clock;
        editor.StartPreview();
        await Eventually.TrueAsync(() => editor.PreviewId is not null, "the preview started");
        await editor.RefreshPreviewAsync(CancellationToken.None);
        Headless.Pump();
        return new Page(editor, vm, view, window, ctx, clock);
    }

    [Fact]
    public async Task The_video_keeps_its_height_when_a_note_comes_and_goes()
    {
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context());
            var card = page.View.FindControl<Border>("VideoCard")!;
            var panel = page.View.FindControl<Panel>("VideoPanel")!;
            var button = page.View.FindControl<Button>("PauseButton")!;
            var at = button.TranslatePoint(default, page.View)!.Value;
            var (cardHeight, panelHeight) = (card.Bounds.Height, panel.Bounds.Height);
            Assert.False(page.Editor.HasReadingNote);

            await page.ReadingAsync(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Samples.GateExtras);
            Assert.True(page.View.FindControl<StatusChip>("NoteChip")!.IsEffectivelyVisible);
            Assert.Equal(cardHeight, card.Bounds.Height);
            Assert.Equal(panelHeight, panel.Bounds.Height);
            Assert.Equal(at, button.TranslatePoint(default, page.View)!.Value);

            page.Clock.Advance(OcrLimits.NoteHold);
            await Eventually.TrueAsync(() => !page.Editor.HasReadingNote, "the note went");
            Headless.Pump();
            Assert.Equal(cardHeight, card.Bounds.Height);
            Assert.Equal(panelHeight, panel.Bounds.Height);
            Assert.Equal(at, button.TranslatePoint(default, page.View)!.Value);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_live_alert_stays_five_seconds_after_the_last_reading_that_had_it()
    {
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context());
            var editor = page.Editor;
            var small = "Lane: " + ReaderRules.TooSmall;

            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            Assert.Equal(small, editor.ReadingNote);

            // Readings without it: it stays. One with it starts the time again.
            page.Clock.Advance(TimeSpan.FromSeconds(4));
            await page.ReadingAsync(Samples.GateReading(), Fine);
            Assert.Equal(small, editor.ReadingNote);
            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            page.Clock.Advance(TimeSpan.FromSeconds(4.9));
            await page.ReadingAsync(Samples.GateReading(), Fine);
            Assert.Equal(small, editor.ReadingNote);

            // The time ends without another reading.
            page.Clock.Advance(TimeSpan.FromSeconds(0.1));
            await Eventually.TrueAsync(() => !editor.HasReadingNote, "the note went");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_alert_joins_the_note_and_nothing_changes_while_nothing_changed()
    {
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context());
            var editor = page.Editor;
            var changes = 0;
            editor.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(ReaderEditorViewModel.ReadingNote) ? 1 : 0;

            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            Assert.Equal(1, changes);
            foreach (var details in new[] { Fine, Samples.GateExtras, Fine, Samples.GateExtras })
            {
                await page.ReadingAsync(Samples.GateReading(), details); // an alert that comes and goes per frame does not flicker
            }

            Assert.Equal(1, changes);

            await page.ReadingAsync(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Fine);
            Assert.Equal(2, changes);
            Assert.Equal(ReaderRules.TooManyTexts + " Lane: " + ReaderRules.TooSmall, editor.ReadingNote);

            // Each alert goes by its own time.
            page.Clock.Advance(OcrLimits.NoteHold / 2);
            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            page.Clock.Advance(OcrLimits.NoteHold / 2);
            await Eventually.TrueAsync(() => editor.ReadingNote == "Lane: " + ReaderRules.TooSmall, "the older alert went");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_held_frames_lines_show_at_once_and_go_with_it()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var picture = Samples.ContainerPicture();
            ctx.Answers[OcrMethods.PausePreview] = _ => OcrJson.Serialize(new PauseReply { Paused = true, Picture = picture, Reading = Samples.GateReading(), Details = Fine });
            ctx.Answers[OcrMethods.ResumePreview] = _ => OcrJson.Serialize(new PreviewReply { PreviewId = "x" });
            using var page = await OpenAsync(ctx);
            var editor = page.Editor;
            await page.ReadingAsync(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Samples.GateExtras);
            await Eventually.TrueAsync(() => editor.TogglePauseCommand.CanExecute(null), "pause is possible");

            await editor.TogglePauseCommand.ExecuteAsync(null);

            // No live alert stays over the frame's own lines, and no hold on them.
            Assert.Equal("Container number: MSKU 123456 7\nLane: no match", editor.ReadingNote);
            editor.ApplyReading(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Fine);
            Assert.Equal(ReaderRules.TooManyTexts + "\nContainer number: MSKU 123456 7\nLane: no match", editor.ReadingNote);
            editor.ApplyReading(Samples.GateReading(), Fine);
            Assert.Equal("Container number: MSKU 123456 7\nLane: no match", editor.ReadingNote);

            ctx.Answers[OcrMethods.GetPreview] = _ => "{}";
            await editor.TogglePauseCommand.ExecuteAsync(null);
            Assert.False(editor.HasReadingNote);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_note_goes_with_the_reader()
    {
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context());
            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            Assert.True(page.Editor.HasReadingNote);

            page.Editor.StopPreview();

            Assert.False(page.Editor.HasReadingNote);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task In_a_short_window_the_note_and_Save_stay_in_reach()
    {
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context(), 1500, 640);
            await page.ReadingAsync(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Samples.GateExtras);

            var card = page.View.FindControl<Border>("SettingsCard")!;
            var chip = page.View.FindControl<StatusChip>("NoteChip")!;
            var save = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"));
            Assert.True(chip.IsEffectivelyVisible);
            Assert.True(chip.TranslatePoint(new Point(0, chip.Bounds.Height), save)!.Value.Y <= 0, "the note sits above Save");
            Assert.True(save.TranslatePoint(new Point(0, save.Bounds.Height), card)!.Value.Y <= card.Bounds.Height, "Save stays in the card");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_checkbox_shows_only_while_watching_and_goes_with_the_draft()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            using var page = await OpenAsync(ctx);
            var editor = page.Editor;
            var box = page.View.FindControl<CheckBox>("MovingObjectsBox")!;
            Assert.True(box.IsEffectivelyVisible);
            Assert.Equal(OcrTexts.MovingObjects, box.Content);
            Assert.Equal(OcrTexts.MovingObjectsTip, ToolTip.GetTip(box));
            Assert.False(box.IsChecked);
            Assert.False(editor.IsDirty);

            box.IsChecked = true;
            Headless.Pump();
            Assert.True(editor.MovingObjects);
            Assert.True(editor.ToReader().MovingObjects);
            Assert.True(editor.IsDirty);
            await Eventually.TrueAsync(() => OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview)).Reader!.MovingObjects, "the preview follows");

            editor.Mode = ReaderMode.OnAction;
            Headless.Pump();
            Assert.False(box.IsEffectivelyVisible);
            editor.Mode = ReaderMode.Watching;
            Headless.Pump();
            Assert.True(box.IsEffectivelyVisible);
            Assert.True(box.IsChecked);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void A_saved_reader_opens_with_its_setting()
    {
        var ctx = Samples.Context();
        using var editor = new ReaderEditorViewModel(ctx, Samples.ContainerGate with { MovingObjects = true }, false, new EngineInfo(), _ => string.Empty, (_, _) => false, Samples.Decode);

        Assert.True(editor.MovingObjects);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task Screenshots_of_the_note_and_the_checkbox()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            using var page = await OpenAsync(Samples.Context());
            page.Editor.MovingObjects = true;
            await page.ReadingAsync(Samples.GateReading(), Samples.GateExtras);
            Headless.Capture(page.Window, $"ocr-note-{suffix}.png");

            var box = page.View.FindControl<CheckBox>("MovingObjectsBox")!;
            var stream = page.View.FindControl<Button>("StreamButton")!;
            var chip = page.View.FindControl<StatusChip>("NoteChip")!;
            var save = page.View.FindControl<Border>("SettingsCard")!.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"));
            var left = stream.TranslatePoint(default, page.View)!.Value.X;
            Assert.Equal(left, box.TranslatePoint(default, page.View)!.Value.X);
            Assert.Equal(left, chip.TranslatePoint(default, page.View)!.Value.X);
            Assert.True(save.TranslatePoint(default, chip)!.Value.Y >= chip.Bounds.Height);

            var long2 = Samples.GateReading() with { Note = ReaderRules.TooManyTexts };
            await page.ReadingAsync(long2, Samples.GateExtras);
            Headless.Capture(page.Window, $"ocr-note-long-{suffix}.png");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Screenshot_of_the_cut_out_of_a_label_upside_down()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var jpeg = UprightCutOutTests.Label(180);
            var cut = new PictureCutter().Cut(jpeg, new RelativeBox(0.32, 0.45, 0.36, 0.1), new LineTurn(640, 360, 430, 60, MathF.PI));
            var picture = await Samples.Decode(cut!);
            using (picture)
            {
                var row = new ResultRow(new RecentRead { Id = 1, Time = Samples.Time, Area = "Parcel", Text = "AT-4020-12", Confidence = 97, HasFormat = true, MatchesFormat = true, Cutout = cut }, picture);
                var window = new CutOutWindow { DataContext = new CutOutViewModel(row, picture!) };
                window.Show();
                Headless.Pump();
                Headless.Capture(window, $"ocr-cutout-upside-down-{suffix}.png");
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
