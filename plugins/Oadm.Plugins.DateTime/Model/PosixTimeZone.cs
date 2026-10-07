using System.Globalization;
using System.Text;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>
/// Converts an IANA time zone into the POSIX TZ string legacy firmware takes in <c>param.cgi Time.POSIXTimeZone</c>
/// (devices without time-service). Format as AXIS devices show it, e.g. Europe/Vienna =
/// <c>&lt;UTC1&gt;-1&lt;UTC2&gt;-2,M3.5.0/2:00:00,M10.5.0/3:00:00</c> (the value 10.0.0.48 reports).
/// The rule is the one in force for <c>year</c> (the current daylight saving rule of the OS time zone database).
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
        var rule = RuleFor(zone, year);
        var standard = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        var sb = new StringBuilder();
        sb.Append('<').Append(Name(standard)).Append('>').Append(Offset(standard));
        if (rule is null || rule.DaylightDelta == TimeSpan.Zero)
        {
            return sb.ToString();
        }

        var daylight = standard + rule.DaylightDelta;
        sb.Append('<').Append(Name(daylight)).Append('>').Append(Offset(daylight));
        sb.Append(',').Append(Transition(rule.DaylightTransitionStart)).Append(',').Append(Transition(rule.DaylightTransitionEnd));
        return sb.ToString();
    }

    /// <summary>The adjustment rule covering 1 July of <paramref name="year"/> (else 1 January), null without daylight saving.</summary>
    private static TimeZoneInfo.AdjustmentRule? RuleFor(TimeZoneInfo zone, int year)
    {
        if (!zone.SupportsDaylightSavingTime)
        {
            return null;
        }

        var rules = zone.GetAdjustmentRules();
        foreach (var probe in new[] { new System.DateTime(year, 7, 1), new System.DateTime(year, 1, 1) })
        {
            var match = rules.LastOrDefault(r => r.DateStart <= probe && r.DateEnd >= probe && r.DaylightDelta != TimeSpan.Zero);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
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

    /// <summary>"M3.5.0/2:00:00" (month, week 1-5 with 5 = last, weekday 0 = Sunday) or "J60/2:00:00" for fixed dates.</summary>
    private static string Transition(TimeZoneInfo.TransitionTime t)
    {
        var time = t.TimeOfDay.TimeOfDay;
        var at = string.Create(CultureInfo.InvariantCulture, $"/{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}");
        if (t.IsFixedDateRule)
        {
            // Jn: day of a non-leap year (1-365), February 29 never counted.
            var day = new System.DateTime(2001, t.Month, Math.Min(t.Day, System.DateTime.DaysInMonth(2001, t.Month))).DayOfYear;
            return string.Create(CultureInfo.InvariantCulture, $"J{day}{at}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"M{t.Month}.{t.Week}.{(int)t.DayOfWeek}{at}");
    }
}
