using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.LicensePlates.Log;
using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.Logging.Export;

using Xunit.Abstractions;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// The scale of docs/spec/license-plate-center.md rule 11 (<c>manage test perf</c>, never in the unit run): 100,000 plates,
/// 1,000 cameras among <see cref="DeviceLimits.MaxDevices"/> devices, a log of 10 million reads (plate search under 100 ms),
/// the export at 10,000 rows. Each writes its measured time.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PerfTests(ITestOutputHelper output)
{
    private static readonly Guid Tag = Guid.NewGuid();

    private sealed class Source(Dictionary<string, Plate> plates) : IPlateSource
    {
        public Plate? Find(string text) => plates.GetValueOrDefault(text);

        public IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags) => plates.Keys;
    }

    private static string PlateOf(int i) => "W" + i.ToString("D7", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Matching_at_100000_plates_is_a_lookup_never_a_scan(int deviation)
    {
        var plates = Enumerable.Range(0, LicensePlateLimits.MaxPlates).ToDictionary(PlateOf, i => new Plate { Text = PlateOf(i), Tags = [Tag] }, StringComparer.Ordinal);
        var matcher = new PlateMatcher(new Source(plates)) { Deviation = deviation, RebuildInline = true };
        var tags = new HashSet<Guid> { Tag };
        var now = new DateTime(2026, 11, 27, 12, 0, 0);

        var build = Stopwatch.StartNew();
        Assert.Equal(ReadResult.Allowed, matcher.Match(PlateOf(5), tags, now).Result);
        Assert.Equal(ReadResult.Unknown, matcher.Match("ZZZZZZZ", tags, now).Result); // builds the index of the tag set
        build.Stop();

        var reads = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            matcher.Match("X" + i.ToString("D7", CultureInfo.InvariantCulture)[1..] + "Q", tags, now);
        }

        reads.Stop();
        output.WriteLine($"deviation {deviation}: index {build.ElapsedMilliseconds} ms, 10,000 near reads {reads.ElapsedMilliseconds} ms");
        Assert.True(build.Elapsed < TimeSpan.FromSeconds(deviation == 2 ? 30 : 10), $"index {build.ElapsedMilliseconds} ms");
        Assert.True(reads.Elapsed < TimeSpan.FromSeconds(5), $"10,000 reads {reads.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Matching_with_1000_wildcard_plates_among_100000_plates_stays_a_lookup()
    {
        var plates = Enumerable.Range(0, LicensePlateLimits.MaxPlates).Select(i => new Plate { Text = PlateOf(i), Tags = [Tag] })
            .Concat(Enumerable.Range(0, LicensePlateLimits.MaxWildcardPlates).Select(i => new Plate
            {
                // Several shapes of several lengths, like company fleets: "FA0001**", "G*1234*", "HB12***9".
                Text = (i % 3) switch
                {
                    0 => "F" + (char)('A' + (i % 26)) + i.ToString("D4", CultureInfo.InvariantCulture) + "**",
                    1 => "G*" + i.ToString("D4", CultureInfo.InvariantCulture) + "*",
                    _ => "H" + (char)('A' + (i % 26)) + (i % 100).ToString("D2", CultureInfo.InvariantCulture) + "***" + (i % 10).ToString(CultureInfo.InvariantCulture),
                },
                Tags = [Tag],
            }))
            .DistinctBy(p => p.Text)
            .ToArray();
        var matcher = new PlateMatcher(new WildcardTests.Source(plates)) { Deviation = 1, RebuildInline = true };
        var tags = new HashSet<Guid> { Tag };
        var now = new DateTime(2026, 11, 27, 12, 0, 0);
        Assert.Equal("FB0001**", matcher.Match("FB0001XY", tags, now).Plate!.Text);

        var reads = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            matcher.Match("F" + (char)('A' + (i % 26)) + (i % 1000).ToString("D4", CultureInfo.InvariantCulture) + "QZ", tags, now);
            matcher.Match("X" + i.ToString("D7", CultureInfo.InvariantCulture), tags, now);
        }

        reads.Stop();
        output.WriteLine($"1,000 wildcard and 100,000 plates: 20,000 reads {reads.ElapsedMilliseconds} ms");
        Assert.True(reads.Elapsed < TimeSpan.FromSeconds(5), $"20,000 reads {reads.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task The_cameras_tab_with_1000_cameras_among_the_device_limit()
    {
        await using var rig = await Rig.StartAsync(cameras: 0, setup: ctx =>
        {
            for (var i = 0; i < DeviceLimits.MaxDevices; i++)
            {
                ctx.DeviceList.Add(new FakeDevice { Address = "10." + (i >> 16).ToString(CultureInfo.InvariantCulture) + "." + ((i >> 8) & 255).ToString(CultureInfo.InvariantCulture) + "." + (i & 255).ToString(CultureInfo.InvariantCulture), Status = DeviceStatus.Unreachable });
            }
        });
        var ids = rig.Ctx.DeviceList.Take(1_000).Select(d => d.Id).ToList();

        var add = Stopwatch.StartNew();
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest(ids));
        add.Stop();
        var list = Stopwatch.StartNew();
        var cameras = await rig.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras);
        list.Stop();
        var group = rig.Ctx.CallAsGroup(ids.Take(500));
        var scope = Stopwatch.StartNew();
        var groupCameras = PluginJson.Parse<CamerasReply>(await rig.Ctx.InvokeAsync(rig.Plugin, group, LicensePlatesMethods.Cameras, null), LicensePlatesJson.Options);
        scope.Stop();

        output.WriteLine($"add 1,000: {add.ElapsedMilliseconds} ms, list: {list.ElapsedMilliseconds} ms, a group's list: {scope.ElapsedMilliseconds} ms");
        Assert.Equal(1_000, cameras.Total);
        Assert.Equal(500, groupCameras.Total);
        Assert.True(list.Elapsed < TimeSpan.FromSeconds(5), $"list {list.ElapsedMilliseconds} ms");
        Assert.True(scope.Elapsed < TimeSpan.FromSeconds(5), $"group list {scope.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_log_of_10_million_reads_is_searched_by_plate_under_100_ms()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-lpc-perf", Guid.NewGuid().ToString("N"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var store = await PlateLogStore.OpenAsync(folder, time, null, CancellationToken.None);
        try
        {
            var cameras = Enumerable.Range(0, 1_000).Select(_ => Guid.NewGuid()).ToArray();
            var start = time.GetUtcNow().AddDays(-30);
            var fill = Stopwatch.StartNew();
            const int Total = 10_000_000;
            for (var i = 0; i < Total; i++)
            {
                store.Insert(new LogInsert(new PlateRead
                {
                    Id = store.NextId(),
                    Time = start.AddMilliseconds(i * 259.2),
                    CameraId = cameras[i % cameras.Length],
                    Camera = "camera",
                    Plate = PlateOf(i % 400_000),
                    Result = (ReadResult)(i % 3),
                    Name = "Name " + (i % 1000).ToString(CultureInfo.InvariantCulture),
                    Confidence = 50 + (i % 50),
                }, null));
                if (i % 100_000 == 0)
                {
                    await store.FlushAsync();
                }
            }

            await store.FlushAsync();
            fill.Stop();

            var search = Stopwatch.StartNew();
            var page = await store.PageAsync(new LogPageRequest { Query = new LogQuery { Search = PlateOf(123_456) }, Limit = 200 }, LogScope.All, CancellationToken.None);
            search.Stop();
            var prefix = Stopwatch.StartNew();
            var prefixed = await store.PageAsync(new LogPageRequest { Query = new LogQuery { Search = "W01234" }, Limit = 200 }, LogScope.All, CancellationToken.None);
            prefix.Stop();
            var stats = Stopwatch.StartNew();
            await store.StatsAsync(new StatsRequest { From = time.GetUtcNow().AddDays(-30), To = time.GetUtcNow() }, LogScope.All, _ => "camera", CancellationToken.None);
            stats.Stop();
            var export = Stopwatch.StartNew();
            var rows = await store.NewestAsync(new LogQuery(), LogScope.All, LicensePlateLimits.MaxExportRows, CancellationToken.None);
            var csv = PlateLogExport.Csv(rows, new PlateExportHead([], "server", "admin", time.GetUtcNow(), Total), LogExportClock.Of("UTC", 0));
            var pdf = PlateLogExport.Pdf(rows, new PlateExportHead([], "server", "admin", time.GetUtcNow(), Total), LogExportClock.Of("UTC", 0), _ => null, CancellationToken.None);
            export.Stop();

            output.WriteLine($"fill {Total:N0}: {fill.Elapsed.TotalSeconds:0} s, plate {search.ElapsedMilliseconds} ms, prefix {prefix.ElapsedMilliseconds} ms, stats 30 days {stats.ElapsedMilliseconds} ms, export 10,000 (CSV {csv.Length:N0} B, PDF {pdf.Length:N0} B) {export.ElapsedMilliseconds} ms, size {store.UsedBytes / 1_048_576:N0} MB");
            Assert.NotEmpty(page.Rows);
            Assert.NotEmpty(prefixed.Rows);
            Assert.True(search.Elapsed < TimeSpan.FromMilliseconds(100), $"plate search {search.ElapsedMilliseconds} ms");
            Assert.True(prefix.Elapsed < TimeSpan.FromMilliseconds(100), $"prefix search {prefix.ElapsedMilliseconds} ms");
            Assert.True(stats.Elapsed < TimeSpan.FromSeconds(5), $"stats {stats.ElapsedMilliseconds} ms");
            Assert.True(export.Elapsed < TimeSpan.FromSeconds(60), $"export {export.ElapsedMilliseconds} ms");
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(folder, recursive: true);
        }
    }
}
