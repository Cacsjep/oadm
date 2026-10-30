using System.Globalization;

namespace Oadm.Plugins.Firmware;

/// <summary>An AXIS OS range an application declares (<c>CompatibleOsVersions</c>), bounds inclusive at their own precision.</summary>
public sealed record AppOsRange(string? Min, string? Max)
{
    public override string ToString() => (Min, Max) switch
    {
        ({ } min, { } max) when min == max => min,
        ({ } min, { } max) => $"{min} to {max}",
        ({ } min, null) => $"{min} or later",
        (null, { } max) => $"up to {max}",
        _ => "any",
    };
}

/// <summary>An application installed on the device, as the application list reports it (only what the upgrade needs).</summary>
public sealed record InstalledApp
{
    public required string Name { get; init; }

    public string? NiceName { get; init; }

    /// <summary>Comes with AXIS OS: replaced by the upgrade, never in the way.</summary>
    public bool Bundled { get; init; }

    /// <summary>Signed, Unsigned or Unknown; null when the device does not report it.</summary>
    public string? SignatureStatus { get; init; }

    /// <summary>The declared AXIS OS ranges; empty when the app declares none (or the device does not report them).</summary>
    public IReadOnlyList<AppOsRange> CompatibleOsVersions { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? Name : NiceName;
}

/// <summary>
/// From AXIS OS 13 the device reinstalls every application during an upgrade and refuses the upgrade (fwmgr 402) when
/// one is unsigned or does not declare the new version (manifest <c>compatibleOsVersions</c>, mandatory from 13). The
/// same check before the upload, from the device's application list.
/// </summary>
public static class InstalledAppRules
{
    /// <summary>The first major version that enforces the rules.</summary>
    public const int FromMajor = 13;

    /// <summary>
    /// "Face Verifier: not signed" per application that blocks an upgrade to <paramref name="target"/>; empty when none
    /// does or the target is older than <see cref="FromMajor"/>. A rule is applied only when the device reports its field
    /// for some application (older AXIS OS reports neither); bundled applications never block.
    /// </summary>
    public static IReadOnlyList<string> Blocking(IReadOnlyList<InstalledApp> apps, AxisOsVersion target)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Major < FromMajor)
        {
            return [];
        }

        var reportsSignature = apps.Any(a => a.SignatureStatus is not null);
        var reportsVersions = apps.Any(a => a.CompatibleOsVersions.Count > 0);
        var problems = new List<string>();
        foreach (var app in apps.Where(a => !a.Bundled))
        {
            var reasons = new List<string>(2);
            if (reportsSignature && !string.Equals(app.SignatureStatus, "Signed", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("not signed");
            }

            if (reportsVersions)
            {
                var ranges = app.CompatibleOsVersions;
                if (ranges.Count == 0)
                {
                    reasons.Add("declares no AXIS OS versions");
                }
                else if (!ranges.Any(r => IsInRange(target, r)))
                {
                    reasons.Add("supports AXIS OS " + string.Join(", ", ranges));
                }
            }

            if (reasons.Count > 0)
            {
                problems.Add($"{app.DisplayName}: {string.Join(", ", reasons)}");
            }
        }

        return problems;
    }

    /// <summary>"Face Verifier: not signed. Update or remove it with Applications (ACAP) first."</summary>
    public static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return $"{string.Join("; ", problems)}. Update or remove {(problems.Count == 1 ? "it" : "them")} with Applications (ACAP) first.";
    }

    /// <summary>Bounds compare at their own precision: max "13" takes any 13.x, min "12.10" any 12.10.x.</summary>
    public static bool IsInRange(AxisOsVersion version, AppOsRange range)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(range);
        return (range.Min is not { } min || CompareAtBound(version, min) >= 0)
            && (range.Max is not { } max || CompareAtBound(version, max) <= 0);
    }

    private static int CompareAtBound(AxisOsVersion version, string bound)
    {
        var parts = version.Text.Split('.');
        var limits = bound.Split('.');
        for (var i = 0; i < limits.Length; i++)
        {
            if (!int.TryParse(limits[i], NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
            {
                return 0;
            }

            var part = i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
            if (part != limit)
            {
                return part.CompareTo(limit);
            }
        }

        return 0;
    }
}
