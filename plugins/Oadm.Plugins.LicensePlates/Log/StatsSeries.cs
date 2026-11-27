namespace Oadm.Plugins.LicensePlates.Log;

/// <summary>The sums of one hour, camera and result of the hourly count table.</summary>
internal readonly record struct HourRow(long Hour, Guid Camera, int Result, int Reads, long Confidence, long Vehicle, int Vehicles);

/// <summary>
/// The pure math of the Stats (docs/spec/license-plate-center.md "Stats"): the steps of the chosen time (hours, or days from
/// midnight in the user's zone), the reads and the average confidence per step for the Total line and each camera, the
/// averages per camera (the vehicle confidence only of cameras that sent one). From the hourly counts, never from the reads.
/// </summary>
internal static class StatsSeries
{
    private const long HourMs = 3_600_000;

    /// <summary>The user's zone: by id, else a fixed offset.</summary>
    public static TimeZoneInfo Zone(string? id, int offsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        var offset = TimeSpan.FromMinutes(Math.Clamp(offsetMinutes, -14 * 60, 14 * 60));
        return offset == TimeSpan.Zero ? TimeZoneInfo.Utc : TimeZoneInfo.CreateCustomTimeZone("UTC" + offset.ToString(), offset, null, null);
    }

    /// <summary>The start of the first step and the number of steps (at least one) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static (DateTimeOffset Start, int Steps) Frame(DateTimeOffset from, DateTimeOffset to, StatsStep step, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (to < from)
        {
            to = from;
        }

        if (step == StatsStep.Day)
        {
            var first = TimeZoneInfo.ConvertTime(from, zone).Date;
            var last = TimeZoneInfo.ConvertTime(to == from ? to : to.AddTicks(-1), zone).Date;
            var start = new DateTimeOffset(first, zone.GetUtcOffset(first));
            return (start, (last - first).Days + 1);
        }

        var startHour = from.ToUnixTimeMilliseconds() / HourMs;
        var endHour = (to.ToUnixTimeMilliseconds() + HourMs - 1) / HourMs;
        return (DateTimeOffset.FromUnixTimeMilliseconds(startHour * HourMs), (int)Math.Max(1, endHour - startHour));
    }

    /// <summary>The step an hour of the count table falls into; outside the frame: below 0 or from the step count on.</summary>
    public static int StepOf(long hour, DateTimeOffset start, StatsStep step, TimeZoneInfo zone)
    {
        if (step == StatsStep.Day)
        {
            var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(hour * HourMs), zone).Date;
            return (day - TimeZoneInfo.ConvertTime(start, zone).Date).Days;
        }

        return (int)(hour - start.ToUnixTimeMilliseconds() / HourMs);
    }

    /// <summary>The reply from the hourly rows of the chosen time and cameras.</summary>
    public static StatsReply Build(IEnumerable<HourRow> rows, DateTimeOffset from, DateTimeOffset to, StatsStep step, TimeZoneInfo zone, Func<Guid, string> name, ReadTotals totals)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(name);
        var (start, steps) = Frame(from, to, step, zone);
        var totalReads = new int[steps];
        var totalConfidence = new long[steps];
        var cameras = new Dictionary<Guid, Sums>();
        int allowed = 0, notAllowed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.Reads <= 0)
            {
                continue;
            }

            var at = StepOf(row.Hour, start, step, zone);
            if (at < 0 || at >= steps)
            {
                continue;
            }

            switch ((ReadResult)row.Result)
            {
                case ReadResult.Allowed: allowed += row.Reads; break;
                case ReadResult.NotAllowed: notAllowed += row.Reads; break;
                default: unknown += row.Reads; break;
            }

            totalReads[at] += row.Reads;
            totalConfidence[at] += row.Confidence;
            if (!cameras.TryGetValue(row.Camera, out var sums))
            {
                sums = new Sums(steps);
                cameras[row.Camera] = sums;
            }

            sums.Add(at, row);
        }

        var reads = totalReads.Sum();
        return new StatsReply
        {
            Totals = totals,
            Results = new ResultCounts(allowed, notAllowed, unknown),
            AverageConfidence = reads == 0 ? 0 : Math.Round((double)totalConfidence.Sum() / reads, 1),
            Start = start,
            Step = step,
            Steps = steps,
            TotalReads = totalReads,
            TotalConfidence = Averages(totalConfidence, totalReads),
            Cameras = [.. cameras
                .Select(c => new CameraSeries(c.Key, name(c.Key), c.Value.Reads.Sum(), Average(c.Value.Confidence.Sum(), c.Value.Reads.Sum()),
                    Average(c.Value.Vehicle, c.Value.Vehicles), c.Value.Reads, Averages(c.Value.Confidence, c.Value.Reads)))
                .OrderByDescending(c => c.Reads).ThenBy(c => c.Camera, StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    private static double? Average(long sum, long count) => count <= 0 ? null : Math.Round((double)sum / count, 1);

    private static double?[] Averages(long[] sums, int[] counts)
    {
        var values = new double?[sums.Length];
        for (var i = 0; i < sums.Length; i++)
        {
            values[i] = Average(sums[i], counts[i]);
        }

        return values;
    }

    private sealed class Sums(int steps)
    {
        public int[] Reads { get; } = new int[steps];

        public long[] Confidence { get; } = new long[steps];

        public long Vehicle { get; private set; }

        public int Vehicles { get; private set; }

        public void Add(int at, HourRow row)
        {
            Reads[at] += row.Reads;
            Confidence[at] += row.Confidence;
            Vehicle += row.Vehicle;
            Vehicles += row.Vehicles;
        }
    }
}
