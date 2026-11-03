using Avalonia.Media.Imaging;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// The page's areas after a drag, the lines while one is adjusted, the start hint without areas, the reading's note and the
/// runtime stats (docs/spec/ocr.md "Page", "Runtime stats"), against the fake server.
/// </summary>
public sealed class PageAreasAndStatsTests
{
    private static async Task<OcrViewModel> LoadedAsync(FakePageContext ctx)
    {
        var vm = new OcrViewModel(ctx) { DecodePicture = _ => Task.FromResult<Bitmap?>(null), ReconnectDelay = TimeSpan.FromMilliseconds(10) };
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    public static OcrStats Stats(LoadState state, string text, double load = 42, params ReaderRate[] readers) => new()
    {
        State = state,
        StateText = text,
        Load = load,
        PicturesPerSecond = 12.34,
        AverageReadMs = 84.6,
        Watching = 3,
        MaxWatching = 50,
        Previews = 1,
        MaxPreviews = 8,
        Streams = 4,
        Sites = [new SiteStreams { Name = "Harbour", Streams = 2, Max = 4 }],
        ServerCpu = 23.1,
        Readers = readers,
    };

    [Fact]
    public async Task A_moved_area_stays_where_the_drag_ended_while_readings_and_keep_alives_arrive()
    {
        // The snap-back: the drag's end is the area's new place, whatever the preview sends meanwhile.
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        await editor.SendPreviewAsync(CancellationToken.None);
        var area = editor.Areas[0];
        var moved = new RelativeBox(0.30, 0.50, 0.27, 0.12);

        editor.SetAdjusting(true);
        editor.SetAreaBox(area, moved);
        editor.SetAdjusting(false);
        editor.ApplyReading(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras);
        await editor.RefreshPreviewAsync(CancellationToken.None);
        await editor.SendPreviewAsync(CancellationToken.None);
        vm.ApplyList(Samples.ListJson());

        Assert.Same(area, editor.Areas[0]);
        Assert.Equal(moved, area.Box);
        Assert.Same(vm.Editor, editor);
        var sent = OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview)).Reader!;
        Assert.Equal(moved, sent.Areas[0].Box); // the preview reads the area where it was put
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task The_lines_hide_while_an_area_is_drawn_moved_or_resized()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        Assert.Equal(3, editor.ShownLines.Count);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.SetAdjusting(true);
        Assert.Empty(editor.ShownLines);
        Assert.Contains(nameof(ReaderEditorViewModel.ShownLines), changed);
        editor.ApplyReading(Samples.GateReading("TGHU 765432 1"), Samples.GateExtras); // a reading during the drag stays hidden
        Assert.Empty(editor.ShownLines);
        editor.SetAdjusting(false);
        Assert.Equal(3, editor.ShownLines.Count);

        editor.DrawAreaCommand.Execute(null);
        Assert.True(editor.IsDrawing);
        Assert.Empty(editor.ShownLines);
        editor.StopDrawing();
        Assert.Equal(3, editor.ShownLines.Count);
    }

    [Fact]
    public async Task Without_an_area_the_video_asks_to_draw_one_and_shows_no_reading()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var gate = vm.Editor!;
        Assert.False(gate.ShowStartHint);

        // Every area removed: the lines and results go, the hint shows.
        foreach (var area in gate.Areas.ToList())
        {
            gate.RemoveAreaCommand.Execute(area);
        }

        Assert.True(gate.ShowStartHint);
        Assert.Empty(gate.ShownLines);
        Assert.False(gate.HasResults);
        Assert.Equal(string.Empty, gate.ResultsEmptyText);
        gate.ApplyReading(Samples.GateReading(), Samples.GateExtras); // a late reading of the old areas shows nothing
        Assert.Empty(gate.Lines);
        Assert.False(gate.HasResults);

        // A reader saved without areas: the hint; Draw area hides it while drawing, the first area ends it.
        await vm.OpenAsync(vm.Readers.Single(r => r.Id == "r3"), CancellationToken.None);
        var dock = vm.Editor!;
        Assert.Empty(dock.Areas);
        Assert.True(dock.ShowStartHint);
        Assert.Equal(OcrTexts.StartHint, "Start by drawing an area");
        dock.DrawAreaCommand.Execute(null);
        Assert.False(dock.ShowStartHint);
        dock.AddArea(new RelativeBox(0.1, 0.1, 0.3, 0.2));
        Assert.False(dock.ShowStartHint);
        Assert.Equal(OcrTexts.WaitingForPicture, dock.ResultsEmptyText);
    }

    [Fact]
    public async Task The_note_of_a_reading_shows_below_the_video()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        Assert.False(editor.HasReadingNote);

        editor.ApplyReading(Samples.GateReading() with { Note = ReaderRules.TooManyTexts }, Samples.GateExtras);
        Assert.True(editor.HasReadingNote);
        Assert.Equal("More than 30 texts found. Only the first 30 are read. Draw smaller areas.", editor.ReadingNote);

        editor.ApplyReading(Samples.GateReading(), Samples.GateExtras);
        Assert.False(editor.HasReadingNote);
    }

    [Fact]
    public async Task The_stats_fill_the_panel_and_a_slow_reader_turns_its_chip_orange()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        Assert.False(vm.Stats.HasStats);

        vm.ApplyStats(Stats(LoadState.Busy, ReaderRules.BusySlow(1), 42.4, new ReaderRate { ReaderId = "r1", Fps = 8, Reads = 5.1, Slow = true }));

        var stats = vm.Stats;
        Assert.True(stats.HasStats);
        Assert.True(stats.IsBusy);
        Assert.Equal("Busy: 1 reader reads slower than set", stats.StateText);
        Assert.Equal(("42 %", "12.3", "85 ms", "3 of 50", "1 of 8", "4", "23 %"),
            (stats.LoadText, stats.PicturesText, stats.ReadTimeText, stats.WatchingText, stats.PreviewsText, stats.StreamsText, stats.CpuText));
        Assert.Equal(new SiteRow("Harbour", "2 of 4"), Assert.Single(stats.Sites));
        var gate = vm.Readers.Single(r => r.Id == "r1");
        Assert.True(gate.IsSlowWatching);
        Assert.False(gate.IsKeepingUp);
        Assert.Equal("Watching · 1 fps (reads 5.1)", gate.ModeText);

        // A list update keeps the rate; the next stats without the reader clear it.
        vm.ApplyList(Samples.ListJson());
        Assert.True(gate.IsSlowWatching);
        vm.ApplyStats(Stats(LoadState.Ok, ReaderRules.KeepUp));
        Assert.True(stats.IsOk);
        Assert.False(gate.IsSlowWatching);
        Assert.True(gate.IsKeepingUp);
        Assert.Equal("Watching · 1 fps", gate.ModeText);

        // A malformed stats event changes nothing.
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(OcrMethods.StatsTopic, "{not json"));
        Assert.True(stats.IsOk);
    }

    [Fact]
    public async Task The_page_asks_for_the_stats_while_it_is_shown_and_takes_the_live_ones()
    {
        var ctx = Samples.Context();
        ctx.Answers[OcrMethods.GetStats] = _ => OcrJson.Serialize(Stats(LoadState.Ok, ReaderRules.KeepUp));
        using var vm = await LoadedAsync(ctx);
        var time = new FakeTimeProvider(Samples.Time);
        vm.StatsTime = time;

        vm.Activate();
        try
        {
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.GetStats) == 1 && vm.Stats.HasStats, "the stats on open");
            time.Advance(OcrLimits.PreviewKeepAlive);
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.GetStats) == 2, "the keep-alive asks again");

            ctx.Push(OcrMethods.StatsTopic, Stats(LoadState.Overloaded, ReaderRules.Overloaded, 99));
            await Eventually.TrueAsync(() => vm.Stats.IsOverloaded, "the live stats");
            Assert.Equal("99 %", vm.Stats.LoadText);
        }
        finally
        {
            vm.Deactivate();
        }

        // Hidden: no more calls.
        var calls = ctx.CallsOf(OcrMethods.GetStats);
        time.Advance(OcrLimits.PreviewKeepAlive * 3);
        Assert.Equal(calls, ctx.CallsOf(OcrMethods.GetStats));
    }

    [Fact]
    public async Task An_older_server_without_stats_keeps_the_panel_hidden()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var time = new FakeTimeProvider(Samples.Time);
        vm.StatsTime = time;

        vm.Activate();
        try
        {
            await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.GetStats) == 1, "the stats asked for");
        }
        finally
        {
            vm.Deactivate();
        }

        Assert.False(vm.Stats.HasStats);
    }
}
