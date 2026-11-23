using System.Globalization;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// Roll out in stages and the estimate (docs/spec/schedules.md "A run"): the stage sizes of a run's devices and about how long
/// a run takes. Pure and O(1) in the device count.
/// </summary>
public static class Rollout
{
    /// <summary>The shares of the stages after the first device, in percent of the run's devices; the rest follows.</summary>
    private static readonly int[] Percents = [2, 5, 10, 20];

    /// <summary>
    /// The stage sizes of <paramref name="devices"/> devices, rounded up: 1, 2 %, 5 %, 10 %, 20 %, then the rest. A stage not bigger
    /// than the one before is left out and its devices go to the rest (20 devices: 1, 2, 4, 13; 150: 1, 3, 8, 15, 30, 93).
    /// </summary>
    public static IReadOnlyList<int> StageSizes(int devices)
    {
        if (devices <= 0)
        {
            return [];
        }

        var sizes = new List<int> { 1 };
        var used = 1;
        foreach (var percent in Percents)
        {
            var size = (int)Math.Ceiling(devices * (long)percent / 100.0);
            if (size <= sizes[^1])
            {
                continue;
            }

            if (used + size >= devices)
            {
                break;
            }

            sizes.Add(size);
            used += size;
        }

        if (devices > used)
        {
            sizes.Add(devices - used);
        }

        return sizes;
    }

    /// <summary>
    /// About how long a run takes: per stage (one stage of all devices without stages) the waves of the parallel limit times the
    /// time per device, so every stage counts at least once for its wait. Null without devices or without a time per device.
    /// </summary>
    public static TimeSpan? Estimate(int devices, bool stages, int parallel, TimeSpan perDevice)
    {
        if (devices <= 0 || perDevice <= TimeSpan.Zero)
        {
            return null;
        }

        var limit = Math.Max(1, parallel);
        IReadOnlyList<int> parts = stages ? StageSizes(devices) : [devices];
        long waves = 0;
        foreach (var part in parts)
        {
            waves += (part + limit - 1) / limit;
        }

        return TimeSpan.FromTicks(perDevice.Ticks * waves);
    }

    /// <summary>The warning below When: a run takes longer than the time between two starts; null when it does not.</summary>
    public static string? WhenWarning(TimeSpan? estimate, TimeSpan? gap) =>
        estimate is { } run && gap is { } between && run > between
            ? $"A run takes about {Duration(run)}, longer than the {Duration(between)} between runs. A start during a run is skipped."
            : null;

    /// <summary>"40 min", "5 h", "3 h 20 min", "7 days".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = (long)Math.Ceiling(span.TotalMinutes);
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, minutes)} min");
        }

        if (minutes < 48 * 60)
        {
            var hours = minutes / 60;
            var rest = minutes % 60;
            return rest == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
                : string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalDays):0} days");
    }
}
