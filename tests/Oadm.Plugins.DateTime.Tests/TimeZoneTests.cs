using Oadm.Plugins.DateAndTime.Model;

namespace Oadm.Plugins.DateAndTime.Tests;

public sealed class TimeZoneTests
{
    [Fact]
    public void Catalog_is_the_device_list()
    {
        var device = Fixture.Read(Fixture.TimeZoneList).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(313, TimeZoneCatalog.All.Count);
        Assert.Equal(device.Order(StringComparer.Ordinal), TimeZoneCatalog.All.Select(z => z.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Catalog_is_sorted_by_offset_with_readable_labels()
    {
        var all = TimeZoneCatalog.All;
        for (var i = 1; i < all.Count; i++)
        {
            Assert.True(all[i - 1].BaseOffset <= all[i].BaseOffset);
        }

        Assert.Equal("(UTC+01:00) Vienna - Europe/Vienna", TimeZoneCatalog.Find("Europe/Vienna")!.Label);
        Assert.Equal("(UTC+05:30) Kolkata - Asia/Kolkata", TimeZoneCatalog.Find("Asia/Kolkata")!.Label);
        Assert.Equal("(UTC-05:00) New York - America/New_York", TimeZoneCatalog.Find("America/New_York")!.Label);
        Assert.Equal("(UTC) Coordinated Universal Time - UTC", TimeZoneCatalog.Find("UTC")!.Label);
        Assert.Equal("UTC+01:00", TimeZoneCatalog.Find("Europe/Vienna")!.OffsetText);
        Assert.True(TimeZoneCatalog.Find("Europe/Vienna")!.ObservesDaylightSaving);
        Assert.False(TimeZoneCatalog.Find("Asia/Kolkata")!.ObservesDaylightSaving);
    }

    [Fact]
    public void Every_zone_is_known_to_the_os_time_zone_database()
    {
        // Older ICU data (Windows 10 1809) lacks a few new zones; all but Antarctica/Troll have a same-rules alias.
        var unknown = TimeZoneCatalog.All.Where(z => TimeZoneCatalog.TryGetSystemZone(z.Id) is null).Select(z => z.Id).ToList();

        Assert.True(unknown.Count == 0 || unknown.SequenceEqual(["Antarctica/Troll"]), string.Join(", ", unknown));
        Assert.Equal("(UTC+02:00) Kyiv - Europe/Kyiv", TimeZoneCatalog.Find("Europe/Kyiv")!.Label);
    }

    [Theory]
    [InlineData("vienna", "Europe/Vienna")]
    [InlineData("Buenos Aires", "America/Argentina/Buenos_Aires")]
    [InlineData("UTC+05:30", "Asia/Kolkata")]
    [InlineData("europe/vie", "Europe/Vienna")]
    public void Search_finds_by_city_id_and_offset(string text, string id)
    {
        Assert.Contains(TimeZoneCatalog.Search(TimeZoneCatalog.All, text), z => z.Id == id);
    }

    [Fact]
    public void Empty_search_returns_everything()
    {
        Assert.Equal(313, TimeZoneCatalog.Search(TimeZoneCatalog.All, "  ").Count());
    }

    [Theory]
    [InlineData("Europe/Vienna", "<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00")] // exactly what 10.0.0.48 reports
    [InlineData("America/New_York", "<UTC-5>5<UTC-4>4,M3.2.0/2:00:00,M11.1.0/2:00:00")]
    [InlineData("Australia/Sydney", "<UTC10>-10<UTC11>-11,M10.1.0/2:00:00,M4.1.0/3:00:00")]
    [InlineData("Asia/Kolkata", "<UTC530>-5:30")]
    [InlineData("UTC", "<UTC0>0")]
    public void Posix_strings_from_iana(string id, string posix)
    {
        Assert.Equal(posix, PosixTimeZone.FromIana(id, 2026));
    }

    [Fact]
    public void Server_time_zone_resolves_to_an_axis_id()
    {
        Assert.Equal("Europe/Vienna", ServerTimeZone.Resolve(TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna")));
        Assert.Equal("UTC", ServerTimeZone.Resolve(TimeZoneInfo.Utc));
        var custom = TimeZoneInfo.CreateCustomTimeZone("Mars", TimeSpan.FromMinutes(37), "Mars", "Mars");
        Assert.Null(ServerTimeZone.Resolve(custom));
    }
}
