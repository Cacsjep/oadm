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
        var all = TimeZoneCatalog.All.Where(z => z.HasOffset).ToList(); // zones the OS does not know come last
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
    public void Zones_the_os_does_not_know_are_listed_last_without_an_offset()
    {
        // OS time zone data differs (Windows Server 2025 lacks Antarctica/Troll and Vostok, Windows 10 1809 the newest
        // zones): an unknown zone stays selectable, without an invented offset, after all zones with one.
        var all = TimeZoneCatalog.All;
        var unknown = all.Where(z => TimeZoneCatalog.TryGetSystemZone(z.Id) is null).ToList();
        Assert.All(unknown, z => Assert.False(z.HasOffset));
        Assert.Equal(unknown, all.Skip(all.Count - unknown.Count));
        Assert.All(all.Take(all.Count - unknown.Count), z => Assert.True(z.HasOffset));
        Assert.Equal("(UTC+02:00) Kyiv - Europe/Kyiv", TimeZoneCatalog.Find("Europe/Kyiv")!.Label);

        var troll = TimeZoneCatalog.Describe("Antarctica/Troll", zone: null, 2026);
        Assert.Equal("Troll - Antarctica/Troll", troll.Label);
        Assert.Equal("Unknown", troll.OffsetText);
        Assert.False(troll.ObservesDaylightSaving);
        var vienna = TimeZoneCatalog.Describe("Europe/Vienna", Vienna2026(), 2026);
        Assert.Equal("(UTC+01:00) Vienna - Europe/Vienna", vienna.Label);
        Assert.True(vienna.ObservesDaylightSaving);
        Assert.Equal([vienna, troll], TimeZoneCatalog.Sort([troll, vienna]));
        Assert.Null(PosixTimeZone.FromIana("Antarctica/Nowhere", 2026));
    }

    [Fact]
    public void Daylight_saving_is_read_from_the_current_rules_not_the_history()
    {
        // Asia/Kolkata had daylight saving time in the 1940s; tzdata (Linux, macOS) keeps that history.
        Assert.False(TimeZoneCatalog.Find("Asia/Kolkata")!.ObservesDaylightSaving);
        var history = TimeZoneInfo.CreateCustomTimeZone("History", TimeSpan.FromHours(5.5), "History", "History", "History",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new System.DateTime(1942, 1, 1), new System.DateTime(1945, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 0, 0, 0), 4, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 0, 0, 0), 10, 1)),
        ]);
        Assert.True(history.SupportsDaylightSavingTime);
        Assert.Equal(new ZoneYear(TimeSpan.FromHours(5.5), null, null, null), ZoneYear.Of(history, 2026));
        Assert.Equal("<UTC530>-5:30", PosixTimeZone.FromTimeZone(history, 2026));
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

    [Theory]
    [InlineData(-5, "2026-03-08T07:00:00", "2026-11-01T06:00:00", "<UTC-5>5<UTC-4>4,M3.2.0/2:00:00,M11.1.0/2:00:00")] // New York
    [InlineData(10, "2026-10-03T16:00:00", "2026-04-04T16:00:00", "<UTC10>-10<UTC11>-11,M10.1.0/2:00:00,M4.1.0/3:00:00")] // Sydney
    [InlineData(1, "2026-03-29T01:00:00", "2026-10-25T01:00:00", "<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00")] // Vienna
    [InlineData(0, "2026-03-29T01:00:00", "2026-10-25T01:00:00", "<UTC0>0<UTC1>-1,M3.5.0/1:00:00,M10.5.0/2:00:00")] // London
    public void Posix_strings_from_transition_instants(int standardHours, string startUtc, string endUtc, string posix)
    {
        var standard = TimeSpan.FromHours(standardHours);
        Assert.Equal(posix, PosixTimeZone.Format(standard, standard + TimeSpan.FromHours(1), Utc(startUtc), Utc(endUtc)));
    }

    [Fact]
    public void Transition_week_is_last_in_the_last_seven_days_unless_told_otherwise()
    {
        Assert.Equal(new PosixTransition(3, 2, DayOfWeek.Sunday, TimeSpan.FromHours(2)), PosixTransition.FromLocal(new System.DateTime(2026, 3, 8, 2, 0, 0)));
        Assert.Equal(4, PosixTransition.FromLocal(new System.DateTime(2026, 3, 22, 2, 0, 0)).Week);
        Assert.Equal(5, PosixTransition.FromLocal(new System.DateTime(2026, 3, 29, 2, 0, 0)).Week);
        Assert.Equal(5, PosixTransition.FromLocal(new System.DateTime(2026, 2, 22, 2, 0, 0)).Week); // fourth and last Sunday
        Assert.Equal(4, PosixTransition.FromLocal(new System.DateTime(2026, 2, 22, 2, 0, 0), lastWeek: false).Week);
        Assert.Equal(5, PosixTransition.FromLocal(new System.DateTime(2026, 3, 29, 2, 0, 0), lastWeek: false).Week); // a fifth Sunday is always last
        Assert.Equal("M10.5.0/3:00:00", PosixTransition.FromLocal(new System.DateTime(2026, 10, 25, 3, 0, 0)).ToString());
    }

    [Fact]
    public void Fixed_date_rules_per_year_give_the_floating_rule()
    {
        // Linux and macOS: .NET builds the tzdata transitions as one fixed-date rule per year, the end written as the wall
        // time just before the transition (1:59:59.999). The instants are what counts.
        Assert.Equal("<UTC-5>5<UTC-4>4,M3.2.0/2:00:00,M11.1.0/2:00:00", PosixTimeZone.FromTimeZone(NewYorkFixedDates(), 2026));
        Assert.Equal("<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00", PosixTimeZone.FromTimeZone(Vienna2026(), 2026));
        var zone = ZoneYear.Of(NewYorkFixedDates(), 2026);
        Assert.Equal(Utc("2026-03-08T07:00:00"), zone.DaylightStartUtc);
        Assert.Equal(Utc("2026-11-01T06:00:00"), zone.DaylightEndUtc);
        Assert.Equal(TimeSpan.FromHours(-5), zone.Standard);
        Assert.Equal(TimeSpan.FromHours(-4), zone.Daylight);
    }

    [Fact]
    public void A_fourth_weekday_rule_stays_fourth_when_another_year_has_a_fifth()
    {
        // Fourth Sunday of February: in 2026 (Feb 22) also the last one, in 2032 (Feb 22, Feb 29 follows) not.
        var fourth = TimeZoneInfo.CreateCustomTimeZone("Fourth", TimeSpan.FromHours(-3), "Fourth", "Fourth", "Fourth",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(System.DateTime.MinValue.Date, System.DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new System.DateTime(1, 1, 1, 0, 0, 0), 10, 1, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new System.DateTime(1, 1, 1, 0, 0, 0), 2, 4, DayOfWeek.Sunday)),
        ]);

        Assert.Equal("<UTC-3>3<UTC-2>2,M10.1.0/0:00:00,M2.4.0/0:00:00", PosixTimeZone.FromTimeZone(fourth, 2026));
    }

    private static System.DateTime Utc(string value) =>
        System.DateTime.SpecifyKind(System.DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>America/New_York 2025-2027 as .NET builds it from tzdata on Linux: one fixed-date rule per year.</summary>
    private static TimeZoneInfo NewYorkFixedDates()
    {
        static TimeZoneInfo.AdjustmentRule Year(int year, int startDay, int endDay) =>
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new System.DateTime(year, 1, 1), new System.DateTime(year, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 2, 0, 0), 3, startDay),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 1, 59, 59, 999), 11, endDay));
        return TimeZoneInfo.CreateCustomTimeZone("NewYorkFixed", TimeSpan.FromHours(-5), "New York", "EST", "EDT",
            [Year(2025, 9, 2), Year(2026, 8, 1), Year(2027, 14, 7)]);
    }

    /// <summary>Europe/Vienna 2026 as one fixed-date rule (Linux style).</summary>
    private static TimeZoneInfo Vienna2026() =>
        TimeZoneInfo.CreateCustomTimeZone("ViennaFixed", TimeSpan.FromHours(1), "Vienna", "CET", "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new System.DateTime(2026, 1, 1), new System.DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 2, 0, 0), 3, 29),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new System.DateTime(1, 1, 1, 2, 59, 59, 999), 10, 25)),
        ]);
}
