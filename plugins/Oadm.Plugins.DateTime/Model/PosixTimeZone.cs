using System.Globalization;
using System.Text;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>
/// A POSIX "Mm.w.d/time" transition rule: month, week 1-5 (5 = last), weekday (0 = Sunday) and the local wall time in
/// force before the transition (standard time for the start, daylight saving time for the end).
/// </summary>
public readonly record struct PosixTransition(int Month, int Week, DayOfWeek Day, TimeSpan Time)
{
    /// <summary>
    /// The rule for a transition at local wall time <paramref name="local"/>. A day in the last seven days of the month is
    /// week 5 (last) unless <paramref name="lastWeek"/> is false (then a fourth occurrence stays week 4).
    /// </summary>
    public static PosixTransition FromLocal(System.DateTime local, bool lastWeek = true)
    {
        var week = WeekOfMonth(local);
        if (IsInLastWeek(local) && (week == 5 || lastWeek))
        {
            week = 5;
        }

        return new PosixTransition(local.Month, week, local.DayOfWeek, local.TimeOfDay);
    }

    /// <summary>1 for days 1-7, 2 for days 8-14, ... 5 for days 29-31.</summary>
    internal static int WeekOfMonth(System.DateTime local) => ((local.Day - 1) / 7) + 1;

    /// <summary>No later day of the same weekday in this month.</summary>
    internal static bool IsInLastWeek(System.DateTime local) => local.Day + 7 > System.DateTime.DaysInMonth(local.Year, local.Month);

    /// <summary>"M3.5.0/2:00:00".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"M{Month}.{Week}.{(int)Day}/{(int)Time.TotalHours}:{Time.Minutes:00}:{Time.Seconds:00}");
}

/// <summary>
/// Converts an IANA time zone into the POSIX TZ string legacy firmware takes in <c>param.cgi Time.POSIXTimeZone</c>
/// (devices without time-service). Format as AXIS devices show it, e.g. Europe/Vienna =
/// <c>&lt;UTC1&gt;-1&lt;UTC2&gt;-2,M3.5.0/2:00:00,M10.5.0/3:00:00</c> (the value 10.0.0.48 reports).
/// The rule is the one in force for <c>year</c>, derived from the instants at which the OS time zone database changes the
/// offset (<see cref="ZoneYear"/>), so the result is the same on Windows, Linux and macOS.
/// </summary>
public static class PosixTimeZone
{
    /// <summary>The POSIX string for <paramref name="ianaId"/>, or null when the OS does not know the zone.</summary>
    public static string? FromIana(string ianaId, int year)
    {
        var zone = TimeZoneCatalog.TryGetSystemZone(ianaId);
        return zone is null ? null : FromTimeZone(zone, year);
    }

    public static string FromTimeZone(TimeZoneInfo zone, int year)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var rules = ZoneYear.Of(zone, year);
        if (rules.Daylight is not { } daylight)
        {
            return Format(rules.Standard);
        }

        var startLocal = rules.DaylightStartUtc!.Value + rules.Standard;
        var endLocal = rules.DaylightEndUtc!.Value + daylight;
        var start = PosixTransition.FromLocal(startLocal, IsLastWeekRule(zone, year, startLocal, start: true));
        var end = PosixTransition.FromLocal(endLocal, IsLastWeekRule(zone, year, endLocal, start: false));
        return Format(rules.Standard, daylight, start, end);
    }

    /// <summary>
    /// The string for daylight saving time from <paramref name="startUtc"/> to <paramref name="endUtc"/>; the start is
    /// written in standard time, the end in daylight saving time (the wall time in force before each transition).
    /// </summary>
    public static string Format(TimeSpan standard, TimeSpan daylight, System.DateTime startUtc, System.DateTime endUtc) =>
        Format(standard, daylight, PosixTransition.FromLocal(startUtc + standard), PosixTransition.FromLocal(endUtc + daylight));

    /// <summary>"&lt;UTC-5&gt;5&lt;UTC-4&gt;4,M3.2.0/2:00:00,M11.1.0/2:00:00".</summary>
    public static string Format(TimeSpan standard, TimeSpan daylight, PosixTransition start, PosixTransition end) =>
        string.Create(CultureInfo.InvariantCulture, $"{Format(standard)}<{Name(daylight)}>{Offset(daylight)},{start},{end}");

    /// <summary>"&lt;UTC530&gt;-5:30" (no daylight saving time).</summary>
    public static string Format(TimeSpan standard) =>
        new StringBuilder().Append('<').Append(Name(standard)).Append('>').Append(Offset(standard)).ToString();

    /// <summary>
    /// A transition on a fourth weekday that is also the last one of its month fits both "fourth" (week 4) and "last"
    /// (week 5). The next six years decide (every weekday position of a date occurs within seven years): the rule that
    /// matches the actual transition in more of them wins, "last" on a tie. Rules such as "Friday before the last Sunday"
    /// fit neither exactly; this picks the closer one.
    /// </summary>
    private static bool IsLastWeekRule(TimeZoneInfo zone, int year, System.DateTime local, bool start)
    {
        if (PosixTransition.WeekOfMonth(local) != 4 || !PosixTransition.IsInLastWeek(local))
        {
            return true;
        }

        var fourth = 0;
        var last = 0;
        for (var y = year + 1; y <= year + 6; y++)
        {
            var other = ZoneYear.Of(zone, y);
            if (other.Daylight is not { } daylight)
            {
                break;
            }

            var otherLocal = start ? other.DaylightStartUtc!.Value + other.Standard : other.DaylightEndUtc!.Value + daylight;
            if (otherLocal.Month != local.Month || otherLocal.DayOfWeek != local.DayOfWeek)
            {
                break;
            }

            fourth += PosixTransition.WeekOfMonth(otherLocal) == 4 ? 1 : 0;
            last += PosixTransition.IsInLastWeek(otherLocal) ? 1 : 0;
        }

        return last >= fourth;
    }

    /// <summary>"UTC1", "UTC-5", "UTC530" (quoted name: letters, digits, + and - only).</summary>
    private static string Name(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : string.Empty;
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}{abs.Minutes:00}");
    }

    /// <summary>POSIX offsets are west-positive: UTC+1 = "-1", UTC-5 = "5", UTC+5:30 = "-5:30".</summary>
    private static string Offset(TimeSpan offset)
    {
        var sign = offset > TimeSpan.Zero ? "-" : string.Empty;
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours}:{abs.Minutes:00}");
    }
}
