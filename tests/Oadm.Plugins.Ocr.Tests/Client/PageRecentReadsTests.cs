using System.Collections.Specialized;

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
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>The last 20 reads on the page (docs/spec/ocr.md "The last 20 reads").</summary>
public sealed class PageRecentReadsTests
{
    /// <summary>Counts the decoded pictures and keeps them, so a test sees which went.</summary>
    private sealed class Decoder
    {
        public List<Bitmap> Made { get; } = [];

        public async Task<Bitmap?> DecodeAsync(byte[] bytes)
        {
            var bitmap = await Samples.Decode(bytes);
            Made.Add(bitmap!);
            return bitmap;
        }
    }

    private static bool IsGone(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return false;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            return true;
        }
    }

    private sealed class Opened : IReaderDialogs
    {
        public CutOutViewModel? CutOut { get; private set; }

        public Func<StreamSettingsViewModel, bool> Stream { get; } = _ => false;

        public Task<bool> EditStreamAsync(StreamSettingsViewModel form) => Task.FromResult(false);

        public Task<bool> RenameAreaAsync(RenameAreaViewModel form) => Task.FromResult(false);

        public Task ShowCutOutAsync(CutOutViewModel form)
        {
            CutOut = form;
            return Task.CompletedTask;
        }
    }

    /// <summary>The page with a preview and its fake server of last reads: each fetch answers <paramref name="reads"/>.</summary>
    private static async Task<(OcrViewModel Vm, ReaderEditorViewModel Editor)> ReadingAsync(FakePageContext ctx, Func<IReadOnlyList<RecentRead>> reads, Decoder? decoder = null)
    {
        ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads());
        ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads());
        var vm = new OcrViewModel(ctx) { DecodePicture = decoder is null ? Samples.Decode : decoder.DecodeAsync };
        await vm.LoadAsync(CancellationToken.None);
        var editor = vm.Editor!;
        editor.PreviewTime = new FakeTimeProvider();
        editor.StartPreview();
        await Eventually.TrueAsync(() => editor.PreviewId is not null, "the preview started");
        await editor.RefreshPreviewAsync(CancellationToken.None);
        return (vm, editor);
    }

    [Fact]
    public async Task The_table_shows_the_reads_newest_on_top_and_one_reset_per_change()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var reads = Samples.Reads(OcrLimits.RecentReads);
            var ctx = Samples.Context();
            var (vm, editor) = await ReadingAsync(ctx, () => reads);
            using (vm)
            {
                Assert.Equal(reads.Select(r => r.Text), editor.Results.Select(r => r.Text));
                Assert.Equal(OcrTexts.Time(reads[0].Time), editor.Results[0].Time);
                Assert.Matches(@"^\d\d:\d\d:\d\d$", editor.Results[0].Time); // seconds tell the rows apart

                var resets = 0;
                NotifyCollectionChangedEventHandler count = (_, _) => resets++;
                editor.Results.CollectionChanged += count;

                // The same rows: the grid is left alone, whatever the frame rate.
                await editor.RefreshPreviewAsync(CancellationToken.None);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(0, resets);

                // A new read: one reset, the oldest gone.
                reads = Samples.Reads(OcrLimits.RecentReads, newest: 1001);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(1, resets);
                Assert.Equal(1001, editor.Results[0].ReadId);
                Assert.Equal(OcrLimits.RecentReads, editor.Results.Count);
                Assert.DoesNotContain(editor.Results, r => r.ReadId == 981);
                editor.Results.CollectionChanged -= count;
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_row_decodes_its_picture_once_and_gives_it_back_when_it_leaves()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var picture = Samples.CutOutPicture();
            var decoder = new Decoder();
            var reads = Samples.Reads(OcrLimits.RecentReads, cutout: picture);
            var ctx = Samples.Context();

            // The server sends a cut-out once: a row the page names comes without it.
            IReadOnlyList<RecentRead> Answer() => reads;
            var (vm, editor) = await ReadingAsync(ctx, Answer, decoder);
            using (vm)
            {
                ctx.Answers[OcrMethods.GetPreview] = json =>
                {
                    var known = OcrJson.Deserialize<PreviewRequest>(json).KnownCutouts ?? [];
                    return Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, [.. reads.Select(r => known.Contains(r.Id) ? r with { Cutout = null } : r)]);
                };
                Assert.Equal(OcrLimits.RecentReads, decoder.Made.Count);
                var oldest = editor.Results[^1];
                var kept = editor.Results[0].CutOut;
                Assert.True(oldest.HasCutOut);
                var oldestBitmap = (Bitmap)oldest.CutOut!;

                // Ten frames of the same rows decode nothing.
                for (var i = 0; i < 10; i++)
                {
                    await editor.RefreshPreviewAsync(CancellationToken.None);
                }

                Assert.Equal(OcrLimits.RecentReads, decoder.Made.Count);
                Assert.Equal(OcrLimits.RecentReads, OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.GetPreview)).KnownCutouts!.Count);

                // A new read: one more decode, the row that left gives its bitmap back, the others keep theirs.
                reads = Samples.Reads(OcrLimits.RecentReads, newest: 1001, cutout: picture);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(OcrLimits.RecentReads + 1, decoder.Made.Count);
                Assert.Same(kept, editor.Results[1].CutOut);
                Assert.Null(oldest.CutOut);
                Assert.True(IsGone(oldestBitmap));
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_warning_of_small_text_follows_the_reading_in_the_note()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var (vm, editor) = await ReadingAsync(ctx, () => Samples.Reads(3));
            using (vm)
            {
                Assert.Null(editor.ReadingNote);
                Assert.All(editor.Results, r => Assert.False(r.TooSmall));

                // With the reading's own note, once the area had no value for the wait.
                ((FakeTimeProvider)editor.PreviewTime).Advance(OcrLimits.SmallTextWait);
                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Samples.GateExtras, null, Samples.Reads(3));
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(ReaderRules.TooManyTexts + " Lane: " + ReaderRules.TooSmall, editor.ReadingNote);
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_list_keeps_its_one_line_and_a_reading_without_rows_empties_the_table()
    {
        await Headless.Session.Dispatch(async () =>
        {
            IReadOnlyList<RecentRead> reads = [];
            var ctx = Samples.Context();
            var (vm, editor) = await ReadingAsync(ctx, () => reads);
            using (vm)
            {
                Assert.False(editor.HasResults);
                Assert.Equal(OcrTexts.WaitingForPicture, editor.ResultsEmptyText);
                return true;
            }
        }, CancellationToken.None);
    }

    /// <summary>The fake server's pause: the frame's reading, no rows while paused unless a test says otherwise.</summary>
    private static Func<bool> PausingServer(FakePageContext ctx, Reading? held = null)
    {
        var paused = false;
        ctx.Answers[OcrMethods.PausePreview] = _ =>
        {
            paused = true;
            return OcrJson.Serialize(new PauseReply { Paused = true, Picture = Samples.CutOutPicture(), Reading = held ?? Samples.GateReading(), Details = Samples.GateExtras });
        };
        ctx.Answers[OcrMethods.StartPreview] = json => OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId, Paused = paused });
        ctx.Answers[OcrMethods.ResumePreview] = json =>
        {
            paused = false;
            return OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId });
        };
        return () => paused;
    }

    [Fact]
    public async Task While_paused_the_table_stays_the_last_reads_and_Resume_brings_it_up_to_date()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            PausingServer(ctx);
            var reads = Samples.Reads(OcrLimits.RecentReads, cutout: Samples.CutOutPicture());
            var (vm, editor) = await ReadingAsync(ctx, () => reads);
            using (vm)
            {
                var dialogs = new Opened();
                editor.Dialogs = dialogs;
                var shown = editor.Results.ToList();
                var resets = 0;
                NotifyCollectionChangedEventHandler count = (_, _) => resets++;
                editor.Results.CollectionChanged += count;

                await editor.TogglePauseCommand.ExecuteAsync(null);
                Assert.Equal(shown, editor.Results);

                // The reader goes on: a reply with newer rows, and the server's own paused reply without rows, change nothing.
                reads = Samples.Reads(OcrLimits.RecentReads, newest: 1001, cutout: Samples.CutOutPicture());
                await editor.RefreshPreviewAsync(CancellationToken.None);
                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, []);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(0, resets);
                Assert.Equal(shown, editor.Results);

                // A re-read of the frame after an edit neither: the table is not the frame's.
                editor.Areas[2].FormatText = "@@";
                await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) >= 2, "the draft sent");
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(0, resets);

                // A click on a crop still opens it.
                await editor.OpenCutOutCommand.ExecuteAsync(editor.Results[3]);
                Assert.Equal(editor.Results[3].Text, dialogs.CutOut?.Text);

                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);
                await editor.TogglePauseCommand.ExecuteAsync(null);

                Assert.False(editor.IsPaused);
                Assert.Equal(1, resets);
                Assert.Equal(1001, editor.Results[0].ReadId);
                Assert.Same(shown[0], editor.Results[1]); // a row that stays keeps its picture
                Assert.True(editor.Results[1].HasCutOut);
                Assert.Null(shown[^1].CutOut); // the one that left gave its bitmap back
                editor.Results.CollectionChanged -= count;
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Answers_around_a_Pause_and_a_Resume_never_empty_the_table()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var isPaused = PausingServer(ctx);
            var reads = Samples.Reads(OcrLimits.RecentReads);
            var (vm, editor) = await ReadingAsync(ctx, () => reads);
            using (vm)
            {
                var shown = editor.Results.ToList();

                // The server has paused and answers a fetch before the page has the Pause's own answer: no rows in that reply.
                var pause = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pauseAnswer = ctx.Answers[OcrMethods.PausePreview];
                ctx.AsyncAnswers[OcrMethods.PausePreview] = _ => pause.Task;
                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, []);
                var pausing = editor.TogglePauseCommand.ExecuteAsync(null);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal(shown, editor.Results);
                ctx.AsyncAnswers.Remove(OcrMethods.PausePreview);
                pause.SetResult(pauseAnswer(null));
                await pausing;
                Assert.True(editor.IsPaused);
                Assert.Equal(3, editor.HeldAreas.Count); // the Pause's own reading, newer than the fetch that came first

                // A paused answer on its way when Resume ends: it no longer counts, the fetch after it brings the rows.
                var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                ctx.AsyncAnswers[OcrMethods.GetPreview] = _ => late.Task;
                var fetching = editor.RefreshPreviewAsync(CancellationToken.None);
                await editor.TogglePauseCommand.ExecuteAsync(null);
                Assert.False(isPaused());
                Assert.Equal(shown, editor.Results);
                ctx.AsyncAnswers.Remove(OcrMethods.GetPreview);
                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);
                late.SetResult(Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, []));
                await fetching;
                Assert.Equal(shown, editor.Results);
                Assert.True(editor.TogglePauseCommand.CanExecute(null)); // the fetch after it was a live reading
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_table_while_paused_waits_for_nothing()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            PausingServer(ctx);
            var (vm, editor) = await ReadingAsync(ctx, () => []);
            using (vm)
            {
                Assert.Equal(OcrTexts.WaitingForPicture, editor.ResultsEmptyText);
                await editor.TogglePauseCommand.ExecuteAsync(null);
                Assert.Equal(OcrTexts.NoReads, editor.ResultsEmptyText);
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Use_read_text_as_format_takes_the_areas_text_not_a_row_of_the_table()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var (vm, editor) = await ReadingAsync(ctx, () => Samples.Reads(20));
            using (vm)
            {
                editor.UseReadTextAsFormatCommand.Execute(editor.Areas[1]); // the second area's text, 22G1
                Assert.Equal("##@#", editor.Areas[1].FormatText);
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Use_read_text_as_format_takes_the_current_frame_live_and_paused_never_an_older_row()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            PausingServer(ctx, Samples.GateReading("AB-1011-22"));

            // Older rows of another shape in the table, the current frame reads something else. Live.
            var older = new[] { Samples.Reads(1)[0] with { Area = "Container number", Text = "OLD ROW 99" } };
            var (vm, editor) = await ReadingAsync(ctx, () => older);
            using (vm)
            {
                ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading("TCLU 000000 1"), Samples.GateExtras, null, older);
                await editor.RefreshPreviewAsync(CancellationToken.None);
                Assert.Equal("OLD ROW 99", Assert.Single(editor.Results).Text);
                editor.UseReadTextAsFormatCommand.Execute(editor.Areas[0]);
                Assert.Equal("@@@@ ###### #", editor.Areas[0].FormatText);

                // Paused: the held frame's text, the table frozen on the older row.
                await editor.TogglePauseCommand.ExecuteAsync(null);
                Assert.Equal("OLD ROW 99", Assert.Single(editor.Results).Text);
                editor.UseReadTextAsFormatCommand.Execute(editor.Areas[0]);
                Assert.Equal("@@-####-##", editor.Areas[0].FormatText);

                // The area keeps its own text when one before it goes.
                editor.RemoveAreaCommand.Execute(editor.Areas[0]);
                editor.UseReadTextAsFormatCommand.Execute(editor.Areas[0]); // Type, 22G1
                Assert.Equal("##@#", editor.Areas[0].FormatText);
                return true;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_click_on_a_thumbnail_opens_the_picture_with_its_text_area_and_time()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            var reads = Samples.Reads(4, cutout: Samples.CutOutPicture());
            ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);
            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            var dialogs = new Opened();
            vm.Dialogs = dialogs;
            Headless.Pump();

            var editor = vm.Editor!;
            var thumbnail = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CutOutButton" && b.IsEffectivelyVisible);
            var row = (ResultRow)thumbnail.DataContext!;
            var centre = thumbnail.TranslatePoint(new Point(thumbnail.Bounds.Width / 2, thumbnail.Bounds.Height / 2), window)!.Value;
            window.MouseMove(centre);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            await Eventually.TrueAsync(() => dialogs.CutOut is not null, "the picture opened");

            var form = dialogs.CutOut!;
            Assert.Equal(row.Text, form.Text);
            Assert.Equal(row.Area, form.Area);
            Assert.Equal(row.Time, form.Time);
            Assert.NotSame(row.CutOut, form.Picture); // its own copy: the row may leave while the dialog is open
            Assert.Contains(row, editor.Results);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_small_cut_out_is_enlarged_by_a_whole_number_and_never_smoothed()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var row = new ResultRow(Samples.Reads(1)[0], null);
            using var bytes = new MemoryStream(Samples.CutOutPicture());
            using var picture = new Bitmap(bytes); // 220 x 40
            var form = new CutOutViewModel(row, picture);
            Assert.Equal(2, form.Scale);
            Assert.Equal(440, form.ShownWidth);
            Assert.Equal(80, form.ShownHeight);

            var window = new CutOutWindow { DataContext = form };
            window.Show();
            Headless.Pump();
            var image = window.FindControl<Image>("Picture")!;
            Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(image));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == row.Text);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Close"));
            Headless.Capture(window, $"ocr-cutout-{Headless.Appearance}.png");

            var closed = false;
            window.Closed += (_, _) => closed = true;
            form.CloseRequested += (_, _) => window.Close();
            form.CloseCommand.Execute(null);
            Assert.True(closed);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Without_Live_view_a_row_has_no_thumbnail_and_nothing_opens()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Denied.Add(HostPermissionKeys.DevicesLiveView);
            var (vm, editor) = await ReadingAsync(ctx, () => Samples.Reads(3));
            using (vm)
            {
                var dialogs = new Opened();
                editor.Dialogs = dialogs;
                Assert.All(editor.Results, r => Assert.False(r.HasCutOut));

                await editor.OpenCutOutCommand.ExecuteAsync(editor.Results[0]);

                Assert.Null(dialogs.CutOut);
                return true;
            }
        }, CancellationToken.None);
    }

    /// <summary>A read without a format holds the area's rows (docs/spec/ocr.md "Several lines"): the cell shows them as lines.</summary>
    [Fact]
    public async Task A_read_without_a_format_shows_its_rows_as_lines()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            const string Rows = "1x Water 1.90\nTOTAL EUR 10.60\nORDER 23F7H";
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            var receipt = new RecentRead { Id = 2000, Time = Samples.Reads(1)[0].Time.AddSeconds(2), Area = "Receipt", Text = Rows, Confidence = 93, Cutout = ReceiptPicture(Rows) };
            IReadOnlyList<RecentRead> reads = [receipt, .. Samples.WithCutOuts(Samples.Reads(4))];
            ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);
            ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);

            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 2000, 870);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            var editor = vm.Editor!;
            editor.PreviewTime = new FakeTimeProvider();
            editor.StartPreview();
            await Eventually.TrueAsync(() => editor.PreviewId is not null, "the preview started");
            await editor.RefreshPreviewAsync(CancellationToken.None);
            Headless.Pump();

            Assert.Equal(Rows, editor.Results[0].Text);
            Assert.False(editor.Results[0].HasFormat);
            var grid = view.FindControl<DataGrid>("ResultsGrid")!;
            var cell = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Text == Rows);
            Assert.Equal(3, cell.TextLayout.TextLines.Count); // one line per row of the text
            var single = grid.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == editor.Results[1].Text);
            Assert.Single(single.TextLayout.TextLines);
            Headless.Capture(window, $"ocr-recent-rows-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);

        // A cut-out of the three rows, like the level box around an area's lines.
        static byte[] ReceiptPicture(string rows)
        {
            using var target = new RenderTargetBitmap(new PixelSize(220, 96));
            using (var dc = target.CreateDrawingContext())
            {
                dc.FillRectangle(new SolidColorBrush(Color.Parse("#f8f7f0")), new Rect(0, 0, 220, 96));
                dc.DrawText(new FormattedText(rows, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 22, Brushes.Black), new Point(8, 6));
            }

            using var stream = new MemoryStream();
            target.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
    }

    /// <summary>The table with 20 rows and paused, in the window size the maintainer works in.</summary>
    [Fact]
    public async Task Screenshots_of_the_last_20_reads()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Video!.Still = Samples.ContainerPicture();
            var reads = Samples.WithCutOuts(Samples.Reads(OcrLimits.RecentReads));
            ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, reads);
            var paused = false;
            ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading(), Samples.GateExtras, null, paused ? [] : reads);
            ctx.Answers[OcrMethods.PausePreview] = _ =>
            {
                paused = true;
                return OcrJson.Serialize(new PauseReply { Paused = true, Picture = Samples.ContainerPicture(), Reading = Samples.GateReading(), Details = Samples.WithCutout(Samples.GateExtras, Samples.CutOutPicture()) });
            };
            ctx.Answers[OcrMethods.StartPreview] = json => OcrJson.Serialize(new PreviewReply { PreviewId = OcrJson.Deserialize<PreviewRequest>(json).PreviewId, Paused = paused });

            using var vm = new OcrViewModel(ctx) { DecodePicture = Samples.Decode };
            var view = new OcrView();
            var window = Headless.Host(view, 2000, 870);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync(CancellationToken.None);
            var editor = vm.Editor!;
            var clock = new FakeTimeProvider();
            editor.PreviewTime = clock;
            editor.StartPreview();
            await Eventually.TrueAsync(() => editor.PreviewId is not null, "the preview started");
            await editor.RefreshPreviewAsync(CancellationToken.None);
            clock.Advance(OcrLimits.SmallTextWait);
            await editor.RefreshPreviewAsync(CancellationToken.None);
            Headless.Pump();

            var grid = view.FindControl<DataGrid>("ResultsGrid")!;
            Assert.Equal(OcrLimits.RecentReads, editor.Results.Count);
            Assert.True(grid.IsEffectivelyVisible);
            Assert.True(editor.HasReadingNote);
            Headless.Capture(window, $"ocr-recent-20-{suffix}.png");

            await editor.TogglePauseCommand.ExecuteAsync(null);
            Headless.Pump();
            Assert.Equal(OcrLimits.RecentReads, editor.Results.Count);
            Assert.True(grid.IsEffectivelyVisible);
            Assert.Equal(3, editor.HeldAreas.Count);
            Headless.Capture(window, $"ocr-recent-paused-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
