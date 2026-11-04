using System.Globalization;

namespace Oadm.Contracts.Updates;

/// <summary>
/// Release versions of self update (docs/spec/self-update.md): "v4.9.0", "4.9.0" and "4.9.0+abc" are 4.9.0; a version with
/// a pre-release part ("4.9.0-rc.1") is never a stable release and is older than its own stable release.
/// </summary>
public static class UpdateVersions
{
    /// <summary>Major.minor.patch of "v4.6.0", "4.6.0-rc.1" or "4.6.0+abc"; null when it is none.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string core = text.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        string[] parts = core.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>A plain release version ("4.9.0" or "v4.9.0"), no pre-release part.</summary>
    public static Version? ParseStable(string? text)
    {
        if (Parse(text) is not { } version)
        {
            return null;
        }

        string core = text!.Trim().TrimStart('v', 'V').Split('+')[0];
        return core.Contains('-', StringComparison.Ordinal) ? null : version;
    }

    /// <summary>"4.9.0" (the form of the installer names).</summary>
    public static string Plain(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");
    }

    /// <summary>"V4.9.0" (the form of the UI).</summary>
    public static string Display(Version version) => "V" + Plain(version);

    /// <summary>"V4.9.0" of a version text; the text itself when it is none.</summary>
    public static string Display(string? text) => Parse(text) is { } v ? Display(v) : text ?? string.Empty;

    /// <summary>A development build (no version, 0.x "-dev" or 1.0.0) is never updated and never offered an update.</summary>
    public static bool IsDevelopmentBuild(string? text) =>
        Parse(text) is not { } v || v == new Version(1, 0, 0) || (text ?? string.Empty).Contains("-dev", StringComparison.OrdinalIgnoreCase);

    /// <summary>The version has a pre-release part ("4.9.0-rc.1").</summary>
    public static bool IsPreRelease(string? text) => Parse(text) is not null && ParseStable(text) is null;

    /// <summary>
    /// True when <paramref name="version"/> is a release older than <paramref name="than"/> (both readable); "4.9.0-rc.2" is
    /// older than "4.9.0", two pre-releases of one version count as equal.
    /// </summary>
    public static bool IsOlder(string? version, string? than) =>
        Parse(version) is { } a && Parse(than) is { } b && (a < b || (a == b && IsPreRelease(version) && !IsPreRelease(than)));

    /// <summary>True when <paramref name="running"/> is the stable release <paramref name="target"/>, not a pre-release of it.</summary>
    public static bool IsRelease(string? running, string? target) =>
        Parse(running) is { } a && a == Parse(target) && !IsPreRelease(running);
}
