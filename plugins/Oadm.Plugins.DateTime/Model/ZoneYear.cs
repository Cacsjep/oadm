namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>
/// The offsets and daylight saving transitions of a time zone in one year, read from the offsets the OS reports
/// (<see cref="TimeZoneInfo.GetUtcOffset(System.DateTime)"/>) rather than from <see cref="TimeZoneInfo.AdjustmentRule"/>:
/// Windows describes rules as floating "second Sunday of March" transitions, Linux and macOS (tzdata) as fixed dates per
/// year, but the instants at which the offset changes are the same on every OS.
/// </summary>
/// <param name="Standard">Standard time offset (the lower one of a daylight saving zone).</param>
/// <param name="Daylight">Daylight saving offset, null when the zone has no daylight saving time in that year.</param>
/// <param name="DaylightStartUtc">Instant daylight saving time starts (UTC), null without daylight saving.</param>
/// <param name="DaylightEndUtc">Instant daylight saving time ends (UTC), null without daylight saving.</param>
public sealed record ZoneYear(TimeSpan Standard, TimeSpan? Daylight, System.DateTime? DaylightStartUtc, System.DateTime? DaylightEndUtc)
{
    /// <summary>The zone switches to daylight saving time in this year.</summary>
    public bool ObservesDaylightSaving => Daylight is not null;

    /// <summary>
    /// Scans <paramref name="year"/> day by day for offset changes and pins each one to the second. Exactly one change up
    /// and one change back down by the same amount is daylight saving time; no change, or anything irregular (a zone that
    /// changes its standard offset that year), is reported as the offset in force at the end of the year without
    /// daylight saving.
    /// </summary>
    public static ZoneYear Of(TimeZoneInfo zone, int year)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var from = new System.DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(1);
        var changes = new List<(System.DateTime Utc, TimeSpan Before, TimeSpan After)>(2);
        var previous = from;
        var previousOffset = zone.GetUtcOffset(from);
        for (var t = from.AddDays(1); t <= to; t = t.AddDays(1))
        {
            var offset = zone.GetUtcOffset(t);
            if (offset != previousOffset)
            {
                changes.Add((FindChange(zone, previous, t, previousOffset), previousOffset, offset));
            }

            previous = t;
            previousOffset = offset;
        }

        if (changes.Count == 2)
        {
            var (first, second) = (changes[0], changes[1]);
            if (first.Before == second.After && first.After == second.Before && first.Before != first.After)
            {
                var up = first.After > first.Before ? first : second;
                var down = first.After > first.Before ? second : first;
                return new ZoneYear(up.Before, up.After, up.Utc, down.Utc);
            }
        }

        return new ZoneYear(previousOffset, null, null, null);
    }

    /// <summary>The first second in (<paramref name="lo"/>, <paramref name="hi"/>] whose offset is no longer <paramref name="before"/>.</summary>
    private static System.DateTime FindChange(TimeZoneInfo zone, System.DateTime lo, System.DateTime hi, TimeSpan before)
    {
        while (hi - lo > TimeSpan.FromSeconds(1))
        {
            var mid = lo.AddSeconds(Math.Floor((hi - lo).TotalSeconds / 2));
            if (zone.GetUtcOffset(mid) == before)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return hi;
    }
}
