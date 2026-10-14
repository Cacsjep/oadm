using System.Globalization;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// The upgrade path Axis recommends (AXIS OS knowledge base, "Upgrade path"): never jump over an LTS track, and leave an
/// LTS track from its latest version. Since AXIS OS 12.6.85 and 11.11.169 the device refuses upgrades off this path; a
/// factory default lifts it. OADM only advises (the upgrade still runs): it knows the LTS tracks, not their latest versions
/// (no online lookup), so the advice names the track ("the latest AXIS OS 11.11").
/// </summary>
public static class AxisOsUpgradePath
{
    /// <summary>The LTS tracks (major, minor), oldest first: product-specific 6.50, 8.40, 9.80, then 10.12 (2022), 11.11 (2024), 12.11 (2026).</summary>
    public static IReadOnlyList<(int Major, int Minor)> LtsTracks { get; } = [(6, 50), (8, 40), (9, 80), (10, 12), (11, 11), (12, 11)];

    /// <summary>
    /// The versions to install before <paramref name="target"/> ("the latest AXIS OS 11.11"), oldest first; empty when
    /// the upgrade is on the path (or not an upgrade):
    /// <list type="bullet">
    /// <item>the device runs an LTS track and the target leaves it: the latest version of that track first;</item>
    /// <item>every LTS track between the device's version and the target: its latest version.</item>
    /// </list>
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
        var steps = new List<string>();
        if (IsLts(from) && Compare(to, from) > 0)
        {
            steps.Add(Name(from));
        }

        foreach (var lts in LtsTracks)
        {
            if (Compare(lts, from) > 0 && Compare(lts, to) < 0)
            {
                steps.Add(Name(lts));
            }
        }

        return steps;
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

    private static (int Major, int Minor) Track(AxisOsVersion version) => (version.Major, version.Minor);

    private static bool IsLts((int Major, int Minor) track) => LtsTracks.Contains(track);

    private static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor);

    private static string Name((int Major, int Minor) track) => string.Create(CultureInfo.InvariantCulture, $"{track.Major}.{track.Minor}");
}
