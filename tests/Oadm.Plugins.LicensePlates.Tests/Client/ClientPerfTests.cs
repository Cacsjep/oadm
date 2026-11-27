using System.Diagnostics;

using Oadm.Plugins.LicensePlates.Client;

using Xunit.Abstractions;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>
/// The page at the plugin's limits (docs/spec/license-plate-center.md rule 11): 100,000 plates in the Plates grid and 1,000
/// cameras in the Cameras grid. Search, the tag filter, a tag change and a camera reply are one pass without per-row UI work.
/// </summary>
public sealed class ClientPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task One_hundred_thousand_plates_load_search_and_filter_fast()
    {
        var plates = Enumerable.Range(0, LicensePlateLimits.MaxPlates)
            .Select(i => new Plate
            {
                Text = "W" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                Name = "Driver " + i,
                Tags = i % 3 == 0 ? [Samples.GarageNord] : i % 3 == 1 ? [Samples.GarageSued, Samples.Staff] : [],
            })
            .ToList();
        var watch = Stopwatch.StartNew();
        var (page, ctx, _) = await Samples.OpenAsync(Samples.Context(plates: plates));
        var load = watch.Elapsed;
        Assert.Equal(LicensePlateLimits.MaxPlates, page.Plates.Count);
        Assert.Equal(LicensePlateLimits.MaxPlates / LicensePlateLimits.MaxPlatePage, ctx.CallsOf(LicensePlatesMethods.Plates));

        watch.Restart();
        page.Plates.SearchText = "W0999";
        var search = watch.Elapsed;
        Assert.Equal(100, page.Plates.Rows.Count);

        watch.Restart();
        page.Plates.SearchText = string.Empty;
        page.Plates.SelectedTagFilter = page.Plates.TagFilters.Single(f => f.Id == Samples.Staff);
        var filter = watch.Elapsed;
        Assert.Equal(33_333, page.Plates.Rows.Count);

        watch.Restart();
        page.Tags.Set([.. Samples.Tags.Select(t => t with { Name = t.Name + " 2" })]);
        var retag = watch.Elapsed;

        output.WriteLine($"Load: {load.TotalMilliseconds:F0} ms, search: {search.TotalMilliseconds:F0} ms, tag filter: {filter.TotalMilliseconds:F0} ms, tags renamed: {retag.TotalMilliseconds:F0} ms");
        Assert.True(load < TimeSpan.FromSeconds(20), $"Load took {load.TotalMilliseconds:F0} ms");
        Assert.True(search < TimeSpan.FromSeconds(2), $"Search took {search.TotalMilliseconds:F0} ms");
        Assert.True(filter < TimeSpan.FromSeconds(2), $"The tag filter took {filter.TotalMilliseconds:F0} ms");
        Assert.True(retag < TimeSpan.FromSeconds(5), $"The tag change took {retag.TotalMilliseconds:F0} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task A_thousand_cameras_are_applied_and_searched_fast()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var rows = Enumerable.Range(0, 1_000).Select(i => new CameraRow
        {
            DeviceId = Guid.NewGuid(),
            Address = $"10.30.{i / 250}.{i % 250 + 1}",
            HostName = "lane-" + i,
            AlpvVersion = "3.1.7",
            Status = i % 10 == 0 ? CameraStatus.ConnectionLost : CameraStatus.Connected,
            Since = i % 10 == 0 ? Samples.Now : null,
            AllowedTags = [Samples.GarageNord, Samples.Staff],
            ImageSettings = new ImageSettingsInfo(i % 7 != 0, Samples.ImageValues(i % 7 == 0)),
        }).ToList();

        var watch = Stopwatch.StartNew();
        page.Cameras.Apply(new CamerasReply(rows, 900, 1_000));
        var apply = watch.Elapsed;
        watch.Restart();
        page.Cameras.Apply(new CamerasReply(rows, 900, 1_000)); // the same rows again: updated in place
        var again = watch.Elapsed;
        watch.Restart();
        page.Cameras.SearchText = "lane-99";
        var search = watch.Elapsed;

        output.WriteLine($"Apply: {apply.TotalMilliseconds:F0} ms, again: {again.TotalMilliseconds:F0} ms, search: {search.TotalMilliseconds:F0} ms");
        Assert.Equal(11, page.Cameras.Rows.Count); // lane-99 and lane-990 to lane-999
        Assert.Equal("900 of 1000 cameras connected", page.ConnectedText);
        Assert.True(apply < TimeSpan.FromSeconds(2), $"Apply took {apply.TotalMilliseconds:F0} ms");
        Assert.True(again < TimeSpan.FromSeconds(2), $"Apply again took {again.TotalMilliseconds:F0} ms");
        Assert.True(search < TimeSpan.FromSeconds(1), $"Search took {search.TotalMilliseconds:F0} ms");
    }
}
