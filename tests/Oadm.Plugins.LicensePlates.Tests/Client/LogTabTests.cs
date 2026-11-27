using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>The Log tab against the sample server: the rows, the filters, Pause, Add to plates, Export and the pictures; no Avalonia.</summary>
public sealed class LogTabTests
{
    private static LogRow RowOf(PlateRead read) => new(read, new PlateTagStore(), Samples.Clock());

    [Fact]
    public async Task The_log_shows_the_newest_reads_first_with_the_footer()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var log = page.Log;

        Assert.False(log.IsLoading);
        Assert.Equal(Samples.Reads().Count, log.Rows.Count);
        Assert.Equal(140, log.Rows[0].Id);
        Assert.Equal("Live · 1,204 reads today · Kept for 30 days, 3.1 of 20 GB used", log.Footer);
        Assert.Equal("Selected cameras: All", log.CameraChoiceText);
        Assert.False(log.ShowEmpty);
    }

    [Fact]
    public void A_deviated_read_shows_the_triangle_with_read_as_in_the_one_plate_column()
    {
        var row = RowOf(Samples.Reads().Single(r => r.Id == 138));
        Assert.Equal("BN417HA", row.PlateText); // the plate of the list
        Assert.True(row.IsDeviated);
        Assert.Equal("Read as BN4I7HA", row.ReadAsTip);

        var exact = RowOf(Samples.Reads().Single(r => r.Id == 140));
        Assert.False(exact.IsDeviated);
        Assert.Null(exact.ReadAsTip);

        // No plate matched: the text the camera read.
        var unknown = RowOf(Samples.Reads().Single(r => r.Id == 137));
        Assert.Equal("WB815XA", unknown.PlateText);
        Assert.True(unknown.IsUnknown);
    }

    [Fact]
    public void The_result_is_only_the_icon_and_the_tooltip_names_it_and_why()
    {
        var ended = RowOf(Samples.Reads().Single(r => r.Id == 136));
        Assert.True(ended.IsNotAllowed);
        Assert.False(ended.IsAllowed || ended.IsUnknown);
        Assert.Equal("Not allowed, ended 26 Nov 2026", ended.ResultTip);

        var noTag = RowOf(Samples.Reads().Single(r => r.Id == 128));
        Assert.Equal("Not allowed, no tag allowed here", noTag.ResultTip);
        Assert.Equal(LicensePlateTexts.Allowed, RowOf(Samples.Reads()[0]).ResultTip);
        Assert.Equal(LicensePlateTexts.Unknown, RowOf(Samples.Reads().Single(r => r.Id == 137)).ResultTip);
    }

    [Theory]
    [InlineData(100, false, false)]
    [InlineData(75, false, false)]
    [InlineData(74, true, false)]
    [InlineData(60, true, false)]
    [InlineData(59, false, true)]
    [InlineData(0, false, true)]
    public void Confidence_below_75_is_a_warning_and_below_60_an_error(int confidence, bool warning, bool error)
    {
        var row = RowOf(Samples.Read(1, 0, 1, "W12345A", ReadResult.Allowed, confidence));
        Assert.Equal(confidence.ToString(System.Globalization.CultureInfo.CurrentCulture), row.ConfidenceText);
        Assert.Equal(warning, row.IsLowConfidence);
        Assert.Equal(error, row.IsVeryLowConfidence);
        Assert.Equal(!warning && !error, row.IsPlainConfidence);
    }

    [Fact]
    public void A_gap_row_says_when_the_connection_was_lost_and_has_no_result()
    {
        var gap = RowOf(Samples.Reads().Single(r => r.Gap));
        Assert.True(gap.IsGap);
        Assert.Equal("Connection lost from 13:39 to 13:47", gap.GapText);
        Assert.False(gap.IsAllowed || gap.IsNotAllowed || gap.IsUnknown);
        Assert.False(gap.HasPictureToLoad);
        Assert.True(gap.IsPlainConfidence);
    }

    [Fact]
    public async Task The_filters_make_the_query_and_Clear_filters_resets_them_in_one_read()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var log = page.Log;
        Assert.False(log.HasFilters);
        Assert.False(log.ClearFiltersCommand.CanExecute(null));

        log.ResultChoices.Items.Single(c => Equals(c.Value, ReadResult.Unknown)).Chosen = true;
        await log.LoadTask;
        log.SelectedArea = LogViewModel.AreaChoices[2];
        log.SelectedDirection = LogViewModel.DirectionChoices[1];
        log.SelectedView = LogViewModel.ViewChoices[2];
        await log.LoadTask;

        var query = log.Query();
        Assert.Equal([ReadResult.Unknown], query.Results);
        Assert.Equal(2, query.Area);
        Assert.Equal(PlateDirection.In, query.Direction);
        Assert.Equal("rear", query.View);
        Assert.Null(query.CameraIds); // All cameras of the plugin
        Assert.True(log.HasFilters);
        Assert.Equal("Vehicle: rear", log.VehicleText);
        Assert.All(log.Rows, r => Assert.True(r.IsUnknown));
        Assert.Equal(PageTexts.NoMatchingReads, log.EmptyText);

        var before = ctx.CallsOf(LicensePlatesMethods.Log);
        log.ClearFiltersCommand.Execute(null);
        await log.LoadTask;
        Assert.Equal(before + 1, ctx.CallsOf(LicensePlatesMethods.Log));
        Assert.False(log.HasFilters);
        var cleared = log.Query();
        Assert.Null(cleared.Results);
        Assert.Null(cleared.Area);
        Assert.Null(cleared.Direction);
        Assert.Null(cleared.View);
        Assert.Equal(Samples.Reads().Count, log.Rows.Count);
    }

    [Fact]
    public async Task The_search_waits_for_the_last_key_then_reads_once()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)page.Time;
        var log = page.Log;
        var before = ctx.CallsOf(LicensePlatesMethods.Log);

        log.SearchText = "W";
        log.SearchText = "W1";
        Assert.Equal(before, ctx.CallsOf(LicensePlatesMethods.Log));
        clock.Advance(LogViewModel.TypingDelay);
        await Eventually.TrueAsync(() => ctx.CallsOf(LicensePlatesMethods.Log) == before + 1, "the log read after typing");
        await log.LoadTask;

        var query = ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).Query;
        Assert.Equal("W1", query.Search);
    }

    [Fact]
    public async Task Pause_stops_the_list_and_Resume_reads_what_came_meanwhile()
    {
        var reads = Samples.Reads().ToList();
        var ctx = Samples.Context(reads);
        var (page, _, _) = await Samples.OpenAsync(ctx);
        var log = page.Log;

        log.TogglePauseCommand.Execute(null);
        Assert.True(log.IsPaused);
        Assert.Equal(PageTexts.Resume, log.PauseText);
        Assert.StartsWith(PageTexts.Paused, log.Footer, StringComparison.Ordinal);
        reads.Insert(0, Samples.Read(141, 0, 1, "W99887Z", ReadResult.Allowed, 96));
        var before = ctx.CallsOf(LicensePlatesMethods.Log);
        page.OnEvent(LicensePlatesMethods.ReadsTopic, Samples.Serialize(new[] { Samples.CameraId(1) }));
        await log.LoadTask;
        Assert.Equal(before, ctx.CallsOf(LicensePlatesMethods.Log)); // nothing moves
        Assert.Equal(140, log.Rows[0].Id);

        log.TogglePauseCommand.Execute(null);
        await log.LoadTask;
        Assert.False(log.IsPaused);
        Assert.Equal(141, log.Rows[0].Id);
        Assert.Equal(Samples.Reads().Count + 1, log.Rows.Count);
        Assert.NotNull(ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).After);
    }

    [Fact]
    public async Task A_read_decided_later_and_a_gap_row_appear_live_at_their_time()
    {
        var reads = Samples.Reads().ToList();
        var ctx = Samples.Context(reads);
        var (page, _, _) = await Samples.OpenAsync(ctx);
        var log = page.Log;
        var first = log.Rows[0].Read.Time; // 14:04:14

        // A known plate at 14:04:20 is shown at once.
        reads.Insert(0, Samples.Read(141, 0, 1, "W99887Z", ReadResult.Allowed, 96) with { Time = first.AddSeconds(6) });
        page.OnEvent(LicensePlatesMethods.ReadsTopic, Samples.Serialize(new[] { Samples.CameraId(1) }));
        await log.LoadTask;
        Assert.Equal(141, log.Rows[0].Id);

        // Then an unknown plate read at 14:04:16 (it waited for its best read) and a gap that began at 13:58 are stored.
        reads.Insert(0, Samples.Read(142, 0, 2, "ZX123", ReadResult.Unknown, 70) with { Time = first.AddSeconds(2) });
        reads.Insert(0, new PlateRead { Id = 143, Time = first.AddMinutes(-6), CameraId = Samples.CameraId(3), Camera = "sued-entry", Gap = true, Until = first.AddMinutes(-1) });
        page.OnEvent(LicensePlatesMethods.ReadsTopic, Samples.Serialize(new[] { Samples.CameraId(2) }));
        await log.LoadTask;

        Assert.Equal(141, ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).After!.Id); // the rows stored after the newest id
        Assert.Equal([141, 142, 140], log.Rows.Take(3).Select(r => r.Id));
        var gap = log.Rows.ToList().FindIndex(r => r.Id == 143);
        Assert.True(gap > 0 && log.Rows[gap - 1].Read.Time >= log.Rows[gap].Read.Time && log.Rows[gap + 1].Read.Time <= log.Rows[gap].Read.Time);
        Assert.Equal(Samples.Reads().Count + 3, log.Rows.Count);
    }

    [Fact]
    public async Task Add_to_plates_is_for_one_selected_unknown_plate_and_opens_the_editor_with_it()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        var log = page.Log;
        dialogs.Answer = false;
        Assert.False(log.AddToPlatesCommand.CanExecute(null));

        log.SetSelection([log.Rows.Single(r => r.Id == 140)]); // allowed
        Assert.False(log.AddToPlatesCommand.CanExecute(null));
        log.SetSelection([log.Rows.Single(r => r.Id == 137)]);
        Assert.True(log.AddToPlatesCommand.CanExecute(null));
        await log.AddToPlatesCommand.ExecuteAsync(null);
        var editor = Assert.IsType<PlateEditorViewModel>(dialogs.Shown.Single());
        Assert.Equal("WB815XA", editor.Text);
        Assert.True(editor.IsNew);

        ctx.Denied.Add(PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Plates));
        ctx.RaisePermissionsChanged();
        Assert.False(log.MayAddToPlates);
        Assert.False(log.AddToPlatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_sends_the_query_and_the_columns_and_shows_no_status_after_it()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        var log = page.Log;

        await log.ExportPdfCommand.ExecuteAsync(null);
        Assert.Equal("plate-log-2026-11-27.pdf", dialogs.SaveNames.Single());
        var request = ctx.LastPayload<LogExportRequest>(LicensePlatesMethods.ExportLog);
        Assert.Equal(ExportFormat.Pdf, request.Format);
        Assert.Equal("Test/Vienna", request.TimeZone);
        Assert.Equal(60, request.UtcOffsetMinutes);
        Assert.Null(log.ErrorText);
        Assert.Null(typeof(LogViewModel).GetProperty("Notice")); // no status line after an export (decision 2026-11-27)
        Assert.Equal("Time,Camera,Plate\n", System.Text.Encoding.UTF8.GetString(dialogs.SavedBytes));

        ctx.Answers[LicensePlatesMethods.LogCount] = _ => Samples.Serialize(new CountReply(1_204));
        log.Range.Choose(DateRangePreset.Last24Hours);
        await log.LoadTask;
        await log.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(ExportFormat.Csv, ctx.LastPayload<LogExportRequest>(LicensePlatesMethods.ExportLog).Format);
        Assert.Equal("Last 24 hours", ctx.LastPayload<LogExportRequest>(LicensePlatesMethods.ExportLog).RangeName);
        Assert.Null(log.ErrorText);
        Assert.Equal(LogColumns.Defaults, ctx.LastPayload<LogExportRequest>(LicensePlatesMethods.ExportLog).Columns); // the chosen columns, in their order
    }

    [Fact]
    public async Task Export_and_the_log_tabs_follow_the_permissions()
    {
        var ctx = Samples.Context();
        ctx.Denied.Add(PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Export));
        var (page, _, _) = await Samples.OpenAsync(ctx);
        Assert.True(page.MayViewLog);
        Assert.False(page.Log.MayExport);
        Assert.False(page.Log.ExportCsvCommand.CanExecute(null));
        Assert.Contains(page.LogTab, page.Tabs);

        ctx.Denied.Add(PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Log));
        ctx.RaisePermissionsChanged();
        Assert.Equal([page.PlatesTab, page.CamerasTab], page.Tabs);
        Assert.True(page.IsPlatesTab);
    }

    [Fact]
    public async Task Pictures_are_read_only_for_the_rows_the_grid_shows_four_at_a_time()
    {
        var ctx = Samples.Context();
        var gate = new TaskCompletionSource();
        var (page, _, _) = await Samples.OpenAsync(ctx);
        var log = page.Log;
        var decoded = new List<string>();
        log.Decode = bytes =>
        {
            decoded.Add(System.Text.Encoding.UTF8.GetString(bytes));
            return null;
        };
        Assert.Equal(0, ctx.CallsOf(LicensePlatesMethods.Picture));

        ctx.MethodGates[LicensePlatesMethods.Picture] = gate.Task;
        foreach (var row in log.Rows.Take(6))
        {
            log.EnsurePicture(row);
            log.EnsurePicture(row); // a row shown again reads nothing twice
        }

        Assert.Equal(LogViewModel.PictureParallelism, ctx.CallsOf(LicensePlatesMethods.Picture));
        gate.SetResult();
        await Eventually.TrueAsync(() => log.PicturesPending == 0, "the pictures read");
        Assert.Equal(6, ctx.CallsOf(LicensePlatesMethods.Picture));
        Assert.Contains("BN4I7HA", decoded); // the crop of what the camera read
    }

    [Fact]
    public async Task Without_cameras_the_log_says_what_to_do_first()
    {
        var (page, _, _) = await Samples.OpenAsync(Samples.Context(reads: []));
        Assert.True(page.Log.ShowEmpty);
        Assert.Equal(PageTexts.NoReads, page.Log.EmptyText);
    }
}
