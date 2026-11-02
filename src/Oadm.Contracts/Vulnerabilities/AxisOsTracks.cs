namespace Oadm.Contracts.Vulnerabilities;

/// <summary>
/// The AXIS OS long-term tracks (major.minor): the product-specific support tracks 6.50, 8.40 and 9.80, then 10.12 (LTS 2022),
/// 11.11 (LTS 2024) and 12.11 (LTS 2026). The same list as the firmware plugin's <c>AxisOsUpgradePath.LtsTracks</c> (plugins do
/// not reference Oadm.Contracts); a new LTS goes into both.
/// </summary>
public static class AxisOsTracks
{
    public static IReadOnlyList<(int Major, int Minor)> Lts { get; } = [(6, 50), (8, 40), (9, 80), (10, 12), (11, 11), (12, 11)];

    /// <summary>The track (major, minor) of a version; null for a version with one part.</summary>
    public static (int Major, int Minor)? TrackOf(FirmwareVersion version) =>
        version.Parts.Count >= 2 ? (version.Parts[0], version.Parts[1]) : null;

    /// <summary>Whether the version is on an LTS track (10.12.363: yes, 11.8.61: no).</summary>
    public static bool IsLts(FirmwareVersion version) => TrackOf(version) is { } track && Lts.Contains(track);
}
