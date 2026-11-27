using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.LicensePlates.Log;
using Oadm.Server.Logging.Export;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>docs/spec/license-plate-center.md "The log store": paging, filters, search, pictures, retention, size limit, a folder that is gone; and the export.</summary>
public sealed class LogStoreTests : IAsyncLifetime
{
    private static readonly Guid CameraA = Guid.NewGuid();
    private static readonly Guid CameraB = Guid.NewGuid();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-lpc-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
    private PlateLogStore _store = null!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _store = await PlateLogStore.OpenAsync(_folder, _time, null, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PlateRead Row(DateTimeOffset time, string plate, ReadResult result = ReadResult.Allowed, Guid? camera = null, string name = "", string[]? tags = null, PlateDirection direction = PlateDirection.In, int confidence = 90, string? country = "AUT") => new()
    {
        Id = _store.NextId(),
        Time = time,
        CameraId = camera ?? CameraA,
        Camera = camera == CameraB ? "gate-b" : "gate-a",
        Plate = plate,
        Result = result,
        Reason = result == ReadResult.Unknown ? ReadReason.NoPlate : ReadReason.Exact,
        Name = name,
        Tags = tags ?? [],
        Direction = direction,
        Area = 1,
        Country = country,
        Confidence = confidence,
        VehicleType = "car",
    };

    private async Task AddAsync(params PlateRead[] rows)
    {
        foreach (var row in rows)
        {
            _store.Insert(new LogInsert(row, null));
        }

        await _store.FlushAsync();
    }

    private DateTimeOffset T(double minutes) => _time.GetUtcNow().AddMinutes(minutes);

    [Fact]
    public async Task Pages_are_newest_first_with_a_cursor()
    {
        await AddAsync([.. Enumerable.Range(0, 25).Select(i => Row(T(-i), "P" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)))]);

        var first = await _store.PageAsync(new LogPageRequest { Limit = 10 }, LogScope.All, CancellationToken.None);
        Assert.Equal(["P000", "P001", "P002", "P003", "P004", "P005", "P006", "P007", "P008", "P009"], first.Rows.Select(r => r.Plate));
        var second = await _store.PageAsync(new LogPageRequest { Limit = 10, Before = first.Next }, LogScope.All, CancellationToken.None);
        Assert.Equal("P010", second.Rows[0].Plate);
        var last = await _store.PageAsync(new LogPageRequest { Limit = 10, Before = second.Next }, LogScope.All, CancellationToken.None);
        Assert.Equal(5, last.Rows.Count);
        Assert.Null(last.Next);

        // The live list: what is newer than the newest row shown.
        await AddAsync(Row(T(1), "NEW1"), Row(T(2), "NEW2"));
        var newer = await _store.PageAsync(new LogPageRequest { After = new LogCursor(first.Rows[0].Time.ToUnixTimeMilliseconds(), first.Rows[0].Id) }, LogScope.All, CancellationToken.None);
        Assert.Equal(["NEW2", "NEW1"], newer.Rows.Select(r => r.Plate));
    }

    [Fact]
    public async Task The_filters_of_the_Log_tab()
    {
        await AddAsync(
            Row(T(-90), "W1", tags: ["Staff"]),
            Row(T(-30), "W2", ReadResult.NotAllowed, tags: ["Visitors", "Staff"], direction: PlateDirection.Out),
            Row(T(-20), "K3", ReadResult.Unknown, camera: CameraB, country: "DEU"),
            Row(T(-10), "W4", tags: ["Visitors"]));

        async Task<IEnumerable<string>> Plates(LogQuery query) => (await _store.PageAsync(new LogPageRequest { Query = query }, LogScope.All, CancellationToken.None)).Rows.Select(r => r.Plate);

        Assert.Equal(["W4", "K3", "W2"], await Plates(new LogQuery { From = T(-60) }));
        Assert.Equal(["K3", "W2"], await Plates(new LogQuery { Results = [ReadResult.NotAllowed, ReadResult.Unknown] }));
        Assert.Equal(["W2", "W1"], await Plates(new LogQuery { Tags = ["staff"] }));
        Assert.Equal(["W2"], await Plates(new LogQuery { Direction = PlateDirection.Out }));
        Assert.Equal(["K3"], await Plates(new LogQuery { Country = "deu" }));
        Assert.Equal(["K3"], await Plates(new LogQuery { CameraIds = [CameraB] }));
        Assert.Equal(4, await _store.CountAsync(new LogQuery { VehicleType = "car" }, LogScope.All, CancellationToken.None));
    }

    [Fact]
    public async Task The_search_is_the_start_of_the_plate_or_a_name_inside_the_time()
    {
        await AddAsync(
            Row(T(-300), "W12345A", name: "Anna Gruber"),
            Row(T(-20), "W12999B", name: "Ben"),
            Row(T(-10), "K12345", name: "Lieferant 100%"));

        async Task<IEnumerable<string>> Plates(LogQuery query) => (await _store.PageAsync(new LogPageRequest { Query = query }, LogScope.All, CancellationToken.None)).Rows.Select(r => r.Plate);

        Assert.Equal(["W12999B", "W12345A"], await Plates(new LogQuery { Search = "w-12" }));
        Assert.Empty(await Plates(new LogQuery { Search = "12345" })); // from the start only
        Assert.Equal(["W12345A"], await Plates(new LogQuery { Search = "gruber" }));
        Assert.Empty(await Plates(new LogQuery { Search = "gruber", From = T(-60) }));
        Assert.Equal(["K12345"], await Plates(new LogQuery { Search = "100%" }));
    }

    [Fact]
    public async Task A_caller_of_a_device_group_sees_only_its_cameras()
    {
        await AddAsync(Row(T(-2), "A1"), Row(T(-1), "B1", camera: CameraB));

        var scope = new LogScope(new HashSet<Guid> { CameraB });
        Assert.Equal(["B1"], (await _store.PageAsync(new LogPageRequest(), scope, CancellationToken.None)).Rows.Select(r => r.Plate));
        Assert.Empty((await _store.PageAsync(new LogPageRequest { Query = new LogQuery { CameraIds = [CameraA] } }, scope, CancellationToken.None)).Rows);
        Assert.Empty((await _store.PageAsync(new LogPageRequest(), LogScope.None, CancellationToken.None)).Rows);
        Assert.Equal(0, await _store.CountAsync(new LogQuery(), LogScope.None, CancellationToken.None));
    }

    [Fact]
    public async Task Pictures_are_files_per_day_and_only_for_the_callers_cameras()
    {
        var row = Row(T(0), "W1") with { HasPicture = true };
        _store.Insert(new LogInsert(row, Fixture.Jpeg));
        _store.Insert(new LogInsert(Row(T(0), "W2"), [1, 2, 3, 4])); // not a JPEG: not kept
        await _store.FlushAsync();

        Assert.Equal(Fixture.Jpeg, await _store.PictureAsync(row.Id, LogScope.All, CancellationToken.None));
        Assert.Null(await _store.PictureAsync(row.Id, new LogScope(new HashSet<Guid> { CameraB }), CancellationToken.None));
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, PlateLogStore.PicturesFolder, "2026-11-27")));
        var page = await _store.PageAsync(new LogPageRequest(), LogScope.All, CancellationToken.None);
        Assert.Equal([false, true], page.Rows.OrderBy(r => r.Plate).Select(r => r.HasPicture).Reverse());
    }

    [Fact]
    public async Task A_later_event_changes_the_direction_and_the_picture_of_its_read()
    {
        var row = Row(T(0), "W1", direction: PlateDirection.Unknown);
        await AddAsync(row);

        _store.Update(new LogUpdate(row.Id, PlateDirection.Out, Fixture.Jpeg));
        await _store.FlushAsync();

        var read = Assert.Single((await _store.PageAsync(new LogPageRequest(), LogScope.All, CancellationToken.None)).Rows);
        Assert.Equal(PlateDirection.Out, read.Direction);
        Assert.True(read.HasPicture);
        var stats = await _store.StatsAsync(new StatsRequest { From = T(-60), To = T(60) }, LogScope.All, _ => "gate-a", CancellationToken.None);
        Assert.Equal(1, stats.Results.Total); // the new direction moves the count, never doubles it
    }

    [Fact]
    public async Task The_cleanup_removes_reads_older_than_the_days_with_their_pictures()
    {
        var old = Row(T(-31 * 24 * 60), "OLD") with { HasPicture = true };
        _store.Insert(new LogInsert(old, Fixture.Jpeg));
        await AddAsync(Row(T(-29 * 24 * 60), "KEPT"), Row(T(0), "NOW"));

        await _store.CleanupAsync(keepDays: 30, maxBytes: long.MaxValue);

        Assert.Equal(["NOW", "KEPT"], (await _store.PageAsync(new LogPageRequest(), LogScope.All, CancellationToken.None)).Rows.Select(r => r.Plate));
        Assert.Null(await _store.PictureAsync(old.Id, LogScope.All, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_folder, PlateLogStore.PicturesFolder, "2026-10-27")));
    }

    [Fact]
    public async Task Over_the_size_limit_the_oldest_days_go_first()
    {
        // Plate crops of 200,000 bytes make the size: three days are about 600 KB, a limit of 500 KB takes the oldest day.
        var crop = new byte[200_000];
        crop[0] = 0xFF;
        crop[1] = 0xD8;
        foreach (var (days, plate) in new[] { (3, "DAY1"), (2, "DAY2"), (0, "TODAY") })
        {
            _store.Insert(new LogInsert(Row(T(-days * 24 * 60), plate) with { HasPicture = true }, crop));
        }

        await _store.FlushAsync();
        Assert.True(_store.UsedBytes > 600_000);

        await _store.CleanupAsync(keepDays: 30, maxBytes: 500_000);

        Assert.Equal(["TODAY", "DAY2"], (await _store.PageAsync(new LogPageRequest(), LogScope.All, CancellationToken.None)).Rows.Select(r => r.Plate));
        Assert.True(_store.UsedBytes <= 500_000);
    }

    [Fact]
    public async Task Stats_count_by_result_confidence_hour_and_camera()
    {
        await AddAsync(
            Row(T(-5), "W1"),
            Row(T(-4), "W1", confidence: 50),
            Row(T(-3), "K9", ReadResult.Unknown, camera: CameraB, direction: PlateDirection.Out, confidence: 74),
            Row(T(-2), "K9", ReadResult.Unknown, camera: CameraB, confidence: 75),
            Row(T(-1), "W2", ReadResult.NotAllowed, confidence: 60));

        var stats = await _store.StatsAsync(new StatsRequest { From = T(-60), To = T(60) }, LogScope.All, id => id == CameraA ? "gate-a" : "gate-b", CancellationToken.None);

        Assert.Equal(new ResultCounts(2, 1, 2), stats.Results);
        Assert.Equal(5, stats.PerHour.Sum(h => h.Reads));
        var a = Assert.Single(stats.PerCamera, c => c.CameraId == CameraA);
        Assert.Equal((3, "gate-a"), (a.Reads, a.Camera));
        Assert.Equal(0.6667, a.AllowedShare, 3);
        Assert.Equal(0.3333, a.LowShare, 3);
        Assert.Equal(0.6667, a.Below75Share, 3); // 50 and 60
        var b = Assert.Single(stats.PerCamera, c => c.CameraId == CameraB);
        Assert.Equal(0.5, b.Below75Share, 3); // 74
        Assert.Equal(new ConfidenceBands(1, 2, 1, 1), stats.Confidence); // 50 | 60, 74 | 75 | 90
        Assert.Equal(69.8, stats.AverageConfidence, 1); // (90 + 50 + 74 + 75 + 60) / 5
    }

    [Fact]
    public async Task Gap_rows_show_without_value_filters()
    {
        await AddAsync(Row(T(-1), "W1"), new PlateRead { Id = _store.NextId(), Time = T(-20), Until = T(-12), Gap = true, CameraId = CameraA, Camera = "gate-a" });

        var all = (await _store.PageAsync(new LogPageRequest(), LogScope.All, CancellationToken.None)).Rows;
        Assert.Equal([false, true], all.Select(r => r.Gap));
        Assert.Equal(T(-12), all[1].Until);
        Assert.Single((await _store.PageAsync(new LogPageRequest { Query = new LogQuery { Results = [ReadResult.Allowed] } }, LogScope.All, CancellationToken.None)).Rows);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_stops_storing_with_a_problem()
    {
        await AddAsync(Row(T(0), "W1"));
        var problems = 0;
        _store.ProblemChanged += () => Interlocked.Increment(ref problems);
        var database = Path.Combine(_folder, PlateLogStore.DatabaseName);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // The database file becomes a read-only file: the next write fails.
        File.SetAttributes(database, FileAttributes.ReadOnly);
        try
        {
            await AddAsync(Row(T(1), "W2"));
            Assert.NotNull(_store.Problem);
            Assert.Equal(1, Volatile.Read(ref problems));

            // While the problem lasts nothing is queued.
            _store.Insert(new LogInsert(Row(T(2), "W3"), null));
            await _store.FlushAsync();
        }
        finally
        {
            File.SetAttributes(database, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task A_folder_that_cannot_be_created_is_a_problem_of_the_log()
    {
        var file = Path.Combine(_folder, "a-file");
        await File.WriteAllTextAsync(file, "x");
        await using var log = new PlateLog(_time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await log.ApplyAsync(new LicensePlateSettings { Folder = Path.Combine(file, "log") }, CancellationToken.None);

        Assert.Null(log.Store);
        Assert.Equal("The log folder cannot be used. Reads are not stored.", log.Problem);
    }

    [Fact]
    public async Task Without_a_folder_nothing_is_stored()
    {
        await using var log = new PlateLog(_time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await log.ApplyAsync(new LicensePlateSettings(), CancellationToken.None);

        Assert.Null(log.Store);
        Assert.Equal(LicensePlateTexts.NoFolder, log.Problem);
    }

    [Fact]
    public async Task The_CSV_has_the_grids_columns_and_more_and_says_when_it_is_limited()
    {
        await AddAsync(Row(T(-1), "W12345A", name: "Anna", tags: ["Staff", "Visitors"]) with { ReadAs = "W12345B" });
        var rows = await _store.NewestAsync(new LogQuery(), LogScope.All, LicensePlateLimits.MaxExportRows, CancellationToken.None);
        var clock = LogExportClock.Of("UTC", 0);

        var csv = System.Text.Encoding.UTF8.GetString(PlateLogExport.Csv(rows, new PlateExportHead([], "server", "admin", T(0), 48_213), clock));

        Assert.StartsWith("﻿", csv, StringComparison.Ordinal);
        var lines = csv.TrimStart('﻿').Split("\r\n");
        Assert.Equal("\"The newest 1 of 48,213 reads\"", lines[0]);
        Assert.Equal("Time,Time (ISO 8601),Camera,Plate,Read as,Name,Tags,Result,Direction,Confidence,Area,Country,Vehicle", lines[1]);
        Assert.Equal("2026-11-27 11:59:00,2026-11-27T11:59:00+00:00,gate-a,W12345A,W12345B,Anna,\"Staff, Visitors\",Allowed,In,90,1,AUT,car", lines[2]);
    }

    [Fact]
    public async Task The_PDF_renders_with_pictures_and_the_head()
    {
        var row = Row(T(-1), "W12345A") with { HasPicture = true, ReadAs = "W12345B" };
        _store.Insert(new LogInsert(row, Fixture.Jpeg));
        await _store.FlushAsync();
        var rows = await _store.NewestAsync(new LogQuery(), LogScope.All, 10, CancellationToken.None);
        var head = new PlateExportHead(["Time: Last hour"], "server", "admin", T(0), rows.Count);

        var lines = PlateLogExport.HeadLines(rows, head);
        Assert.Contains("1 read", lines);
        Assert.Contains("Allowed: 1 · Not allowed: 0 · Unknown plate: 0", lines);

        var pdf = PlateLogExport.Pdf(rows, head, LogExportClock.Of("UTC", 0), _ => null, CancellationToken.None);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task The_export_takes_at_most_10000_rows_newest_first()
    {
        _time.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < LicensePlateLimits.MaxExportRows + 5; i++)
        {
            _store.Insert(new LogInsert(Row(T(-i / 10.0), "P" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), null));
        }

        await _store.FlushAsync();

        var rows = await _store.NewestAsync(new LogQuery(), LogScope.All, LicensePlateLimits.MaxExportRows, CancellationToken.None);
        Assert.Equal(LicensePlateLimits.MaxExportRows, rows.Count);
        Assert.Equal("P0", rows[0].Plate);
        Assert.Equal(LicensePlateLimits.MaxExportRows + 5, await _store.CountAsync(new LogQuery(), LogScope.All, CancellationToken.None));
    }
}
