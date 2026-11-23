using System.Globalization;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// The times of a schedule in the server's local time (docs/spec/schedules.md "A schedule", rule 6): the starts of Once, Daily,
/// Weekly and Monthly, the window end of a start and the shortest time between two starts. Pure: the zone and the instants are
/// handed in (the engine takes them from its <see cref="TimeProvider"/>). A start on a local time that does not exist (the hour
/// the clocks go forward) moves to the first time after the gap; one that exists twice (the hour they go back) is the first.
/// </summary>
public static class ScheduleTimes
{
    /// <summary>How far a search looks for the next or the last start (a monthly day 28 is found within two months).</summary>
    private const int SearchDays = 70;

    public const string ChooseDate = "Choose the date.";
    public const string DateInPast = "Choose a date and time in the future.";
    public const string ChooseDays = "Choose at least one day.";
    public const string ChooseDayOfMonth = "Choose a day from 1 to 28.";
    public const string WindowLikeStart = "Choose a window end other than the start time.";

    /// <summary>The one problem of <paramref name="when"/> for the wizard's step When; null when it is fine.</summary>
    /// <param name="when">The times.</param>
    /// <param name="now">Now (a Once in the past is refused only when <paramref name="checkPast"/>).</param>
    /// <param name="zone">The server's time zone.</param>
    /// <param name="checkPast">Refuse a Once whose time has passed (a new or changed schedule).</param>
    public static string? Problem(ScheduleWhen when, DateTimeOffset now, TimeZoneInfo zone, bool checkPast = true)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(zone);
        switch (when.Recurrence)
        {
            case Recurrence.Once:
                if (when.Date is not { } date)
                {
                    return ChooseDate;
                }

                if (checkPast && At(date, when.Time, zone) <= now)
                {
                    return DateInPast;
                }

                break;
            case Recurrence.Daily:
                break;
            case Recurrence.Weekly:
                if ((when.Days ?? []).Count == 0 || when.Days!.Any(d => !Enum.IsDefined(d)))
                {
                    return ChooseDays;
                }

                break;
            case Recurrence.Monthly:
                if (when.DayOfMonth is not (>= 1 and <= 28))
                {
                    return ChooseDayOfMonth;
                }

                break;
            default:
                return "Choose how often it runs.";
        }

        return when.WindowEnd is { } end && end == when.Time ? WindowLikeStart : null;
    }

    /// <summary>The first start strictly after <paramref name="after"/>; null when there is none (a past Once, invalid times).</summary>
    public static DateTimeOffset? NextAfter(ScheduleWhen when, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(zone);
        if (when.Recurrence == Recurrence.Once)
        {
            return when.Date is { } date && At(date, when.Time, zone) is var once && once > after ? once : null;
        }

        var day = LocalDate(after, zone).AddDays(-1);
        for (var i = 0; i < SearchDays; i++, day = day.AddDays(1))
        {
            if (Matches(when, day) && At(day, when.Time, zone) is var start && start > after)
            {
                return start;
            }
        }

        return null;
    }

    /// <summary>The next <paramref name="count"/> starts after <paramref name="after"/>.</summary>
    public static IReadOnlyList<DateTimeOffset> Next(ScheduleWhen when, DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var starts = new List<DateTimeOffset>(count);
        var from = after;
        while (starts.Count < count && NextAfter(when, from, zone) is { } next)
        {
            starts.Add(next);
            from = next;
        }

        return starts;
    }

    /// <summary>
    /// The latest start in (<paramref name="after"/>, <paramref name="notAfter"/>]: the start that is due now and has not run for
    /// its time (rule 6). Null when no start fell in between.
    /// </summary>
    public static DateTimeOffset? LatestDue(ScheduleWhen when, DateTimeOffset after, DateTimeOffset notAfter, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(zone);
        if (notAfter <= after)
        {
            return null;
        }

        if (when.Recurrence == Recurrence.Once)
        {
            return when.Date is { } date && At(date, when.Time, zone) is var once && once > after && once <= notAfter ? once : null;
        }

        var day = LocalDate(notAfter, zone).AddDays(1);
        var first = LocalDate(after, zone).AddDays(-1);
        for (var i = 0; i < SearchDays && day >= first; i++, day = day.AddDays(-1))
        {
            if (Matches(when, day) && At(day, when.Time, zone) is var start && start <= notAfter)
            {
                return start > after ? start : null;
            }
        }

        return null;
    }

    /// <summary>The window end of a run that started at <paramref name="start"/>: the next <see cref="ScheduleWhen.WindowEnd"/> after it; null without a window.</summary>
    public static DateTimeOffset? WindowEnd(ScheduleWhen when, DateTimeOffset start, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(zone);
        if (when.WindowEnd is not { } end)
        {
            return null;
        }

        var day = LocalDate(start, zone);
        var candidate = At(day, end, zone);
        return candidate > start ? candidate : At(day.AddDays(1), end, zone);
    }

    /// <summary>The shortest time between two starts (Daily 24 h, Weekly the closest two days, Monthly 28 days); null for Once.</summary>
    public static TimeSpan? ShortestGap(ScheduleWhen when)
    {
        ArgumentNullException.ThrowIfNull(when);
        switch (when.Recurrence)
        {
            case Recurrence.Daily:
                return TimeSpan.FromDays(1);
            case Recurrence.Weekly:
                var days = (when.Days ?? []).Distinct().Select(d => (int)d).Order().ToList();
                if (days.Count == 0)
                {
                    return null;
                }

                var gap = 7 - days[^1] + days[0];
                for (var i = 1; i < days.Count; i++)
                {
                    gap = Math.Min(gap, days[i] - days[i - 1]);
                }

                return TimeSpan.FromDays(gap);
            case Recurrence.Monthly:
                return TimeSpan.FromDays(28);
            default:
                return null;
        }
    }

    /// <summary>"Mondays 23:00", "Daily 02:00", "Mon 24 Nov 2026, 23:00", "Day 1 of every month, 03:00".</summary>
    public static string Describe(ScheduleWhen when)
    {
        ArgumentNullException.ThrowIfNull(when);
        var time = when.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        var text = when.Recurrence switch
        {
            Recurrence.Once when when.Date is { } date => date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) + ", " + time,
            Recurrence.Daily => "Daily " + time,
            Recurrence.Weekly => string.Join(", ", (when.Days ?? []).Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => d + "s")) + " " + time,
            Recurrence.Monthly => $"Day {when.DayOfMonth} of every month, {time}",
            _ => time,
        };
        return when.WindowEnd is { } end ? text + ", no new device after " + end.ToString("HH:mm", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>The instant of a local date and time in <paramref name="zone"/> (a gap moves forward, a repeated hour takes the first).</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (var i = 0; i < 4 * 60 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static bool Matches(ScheduleWhen when, DateOnly day) => when.Recurrence switch
    {
        Recurrence.Daily => true,
        Recurrence.Weekly => (when.Days ?? []).Contains(day.DayOfWeek),
        Recurrence.Monthly => when.DayOfMonth == day.Day,
        _ => false,
    };
}
