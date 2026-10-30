using System.Globalization;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// The upgrade path Axis recommends (AXIS OS knowledge base, "Upgrade path"): never jump over an LTS track, and leave an
/// LTS track from its latest version. Since AXIS OS 12.6.85 and 11.11.169 the device refuses upgrades off this path; a
/// factory default lifts it. OADM only advises (the upgrade still runs): it knows the LTS tracks, not their latest versions
/// (no online lookup), so the advice names the track ("the latest AXIS OS 11.11"). Only an upgrade that jumps over an LTS
/// track is certainly off the path (<see cref="Advice"/>); one that leaves the device's LTS track for the next one may be on
/// it (the device may run the latest of its track), so it gets a note only (<see cref="Note"/>).
/// </summary>
public static class AxisOsUpgradePath
{
    /// <summary>The LTS tracks (major, minor), oldest first: product-specific 6.50, 8.40, 9.80, then 10.12 (2022), 11.11 (2024), 12.11 (2026).</summary>
    public static IReadOnlyList<(int Major, int Minor)> LtsTracks { get; } = [(6, 50), (8, 40), (9, 80), (10, 12), (11, 11), (12, 11)];

    /// <summary>
    /// The versions to install before <paramref name="target"/> ("the latest AXIS OS 11.11"), oldest first; empty when
    /// the upgrade jumps over no LTS track (or is not an upgrade). Otherwise every LTS track between the device's version
    /// and the target, and first the latest of the device's own track when it is an LTS track.
    /// </summary>
    public static IReadOnlyList<string> StepsBefore(AxisOsVersion current, AxisOsVersion target)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        if (target <= current)
        {
            return [];
        }

        var from = Track(current);
        var to = Track(target);
        var jumped = LtsTracks.Where(lts => Compare(lts, from) > 0 && Compare(lts, to) < 0).Select(Name).ToList();
        if (jumped.Count > 0 && IsLts(from))
        {
            jumped.Insert(0, Name(from));
        }

        return jumped;
    }

    /// <summary>
    /// The advice for the dialog and the task, or null when the upgrade is on the path:
    /// "Recommended path: the latest AXIS OS 11.11, then 12.6.85."
    /// </summary>
    public static string? Advice(AxisOsVersion current, AxisOsVersion target)
    {
        var steps = StepsBefore(current, target);
        if (steps.Count == 0)
        {
            return null;
        }

        var through = string.Join(", then ", steps.Select((s, i) => i == 0 ? $"the latest AXIS OS {s}" : $"the latest {s}"));
        return $"Recommended path: {through}, then {target}.";
    }

    /// <summary>
    /// The note for an upgrade that leaves the device's LTS track without jumping over another one (12.11.x to 13.x), or
    /// null: "Major upgrade. Make sure the device runs the latest AXIS OS 12.11 first." OADM cannot tell whether it does.
    /// </summary>
    public static string? Note(AxisOsVersion current, AxisOsVersion target)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        var from = Track(current);
        return target > current && IsLts(from) && Compare(Track(target), from) > 0 && StepsBefore(current, target).Count == 0
            ? $"Major upgrade. Make sure the device runs the latest AXIS OS {Name(from)} first."
            : null;
    }

    private static (int Major, int Minor) Track(AxisOsVersion version) => (version.Major, version.Minor);

    private static bool IsLts((int Major, int Minor) track) => LtsTracks.Contains(track);

    private static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor);

    private static string Name((int Major, int Minor) track) => string.Create(CultureInfo.InvariantCulture, $"{track.Major}.{track.Minor}");
}
