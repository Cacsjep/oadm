using System.Globalization;

namespace Oadm.Plugins.Acap;

/// <summary>How a package relates to what is installed on the device.</summary>
public enum InstallKind
{
    /// <summary>Not installed yet.</summary>
    Install = 0,

    /// <summary>Installed with a lower version.</summary>
    Upgrade = 1,

    /// <summary>Installed with the same version.</summary>
    Reinstall = 2,

    /// <summary>Installed with a higher version; only with an explicit option.</summary>
    Downgrade = 3,
}

/// <summary>Outcome of <see cref="AcapCompatibility.Check"/>. Problems block the install, warnings do not.</summary>
public sealed record AcapCompatibilityReport(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, InstallKind Kind, string? InstalledVersion)
{
    public bool IsCompatible => Problems.Count == 0;

    public string Summary => IsCompatible
        ? KindText + (Warnings.Count > 0 ? " (" + string.Join(" ", Warnings) + ")" : string.Empty)
        : string.Join(" ", Problems);

    public string KindText => Kind switch
    {
        InstallKind.Upgrade => $"Upgrade from {InstalledVersion}",
        InstallKind.Reinstall => $"Reinstall {InstalledVersion}",
        InstallKind.Downgrade => $"Downgrade from {InstalledVersion}",
        _ => "New install",
    };
}

/// <summary>
/// Pure compatibility rules for installing an .eap on a device (see the plugin README decision table).
/// Used by the dialog for the preview and by the task again, with fresh device data, before uploading.
/// </summary>
public static class AcapCompatibility
{
    /// <summary>Manifest schema version -> minimum AXIS OS (developer.axis.com, manifest schemas).</summary>
    public static readonly IReadOnlyList<(string Schema, string MinOs)> SchemaMinimumOs =
    [
        ("1.0", "10.7"), ("1.1", "10.7"), ("1.2", "10.7"), ("1.3", "10.9"), ("1.3.1", "11.0"),
        ("1.4.0", "11.7"), ("1.5.0", "11.8"), ("1.6.0", "11.9"), ("1.7.0", "11.10"), ("1.7.1", "12.0"),
        ("1.7.2", "12.1"), ("1.7.3", "12.2"), ("1.7.4", "12.4"), ("1.8.0", "12.6"), ("1.9.0", "12.8"),
        ("1.10.0", "12.10"), ("1.11.0", "12.11"), ("2.0.0", "12.10"), ("2.1.0", "12.11"), ("2.2.0", "12.11"),
    ];

    /// <summary>Checks a package against a device. <paramref name="installed"/> is the same-named application on the device, if any.</summary>
    public static AcapCompatibilityReport Check(EapManifest package, AcapDeviceFacts device, InstalledApplication? installed, bool allowDowngrade)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(device);
        var problems = new List<string>();
        var warnings = new List<string>();

        CheckArchitecture(package, device, problems);
        CheckFirmware(package, device, problems, warnings);
        CheckRoot(package, device, problems, warnings);

        if (device.AllowUnsigned == false)
        {
            warnings.Add("The device accepts only signed applications; an unsigned package will be rejected.");
        }

        var kind = InstallKind.Install;
        if (installed is not null)
        {
            var cmp = CompareVersions(package.Version, installed.Version);
            kind = cmp switch
            {
                > 0 => InstallKind.Upgrade,
                0 => InstallKind.Reinstall,
                _ => InstallKind.Downgrade,
            };
            if (kind == InstallKind.Downgrade && !allowDowngrade)
            {
                problems.Add($"Version {installed.Version} is installed; installing the older {package.Version} needs the downgrade option.");
            }

            if (installed.Vendor is { Length: > 0 } v && package.Vendor is { Length: > 0 } pv && !string.Equals(v, pv, StringComparison.Ordinal))
            {
                warnings.Add($"The installed application is from \"{v}\", the package from \"{pv}\".");
            }
        }

        return new AcapCompatibilityReport(problems, warnings, kind, installed?.Version);
    }

    /// <summary>True when the package runs on any architecture (no native code).</summary>
    public static bool IsArchitectureIndependent(string? architecture) =>
        string.IsNullOrWhiteSpace(architecture)
        || architecture.Equals("all", StringComparison.OrdinalIgnoreCase)
        || architecture.Equals("noarch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes architecture names (arm64 = aarch64, armhf = armv7hf).</summary>
    public static string NormalizeArchitecture(string architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        var a = architecture.Trim().ToLowerInvariant();
        return a switch
        {
            "arm64" => "aarch64",
            "armhf" or "armv7" => "armv7hf",
            _ => a,
        };
    }

    /// <summary>Compares application versions numerically per component ("1.10.0" &gt; "1.9.3"); missing components count as 0.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var pa = Split(a);
        var pb = Split(b);
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var x = i < pa.Count ? pa[i] : "0";
            var y = i < pb.Count ? pb[i] : "0";
            var bothNumeric = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var nx)
                & long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var ny);
            var c = bothNumeric ? nx.CompareTo(ny) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return 0;
    }

    /// <summary>
    /// True when <paramref name="firmware"/> lies in the range. Bounds are inclusive at their own precision:
    /// max "12" accepts every 12.x, min "12.10" accepts 12.10.0 and later.
    /// </summary>
    public static bool IsInRange(string firmware, OsVersionRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var fw = NumericPrefix(firmware);
        if (range.Min is { } min && CompareTruncated(fw, NumericPrefix(min)) < 0)
        {
            return false;
        }

        return range.Max is not { } max || CompareTruncated(fw, NumericPrefix(max)) <= 0;
    }

    /// <summary>Minimum AXIS OS for a manifest schema version, null when the schema is unknown or missing.</summary>
    public static string? MinimumOsForSchema(string? schemaVersion, out bool newerThanKnown)
    {
        newerThanKnown = false;
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return null;
        }

        var exact = SchemaMinimumOs.FirstOrDefault(e => CompareVersions(e.Schema, schemaVersion) == 0);
        if (exact.Schema is not null)
        {
            return exact.MinOs;
        }

        var lower = SchemaMinimumOs.Where(e => CompareVersions(e.Schema, schemaVersion) < 0).ToList();
        if (lower.Count == 0)
        {
            return null;
        }

        newerThanKnown = SchemaMinimumOs.All(e => CompareVersions(e.Schema, schemaVersion) < 0);
        return lower.OrderByDescending(e => e.MinOs, Comparer<string>.Create(CompareVersions)).First().MinOs;
    }

    private static void CheckArchitecture(EapManifest package, AcapDeviceFacts device, List<string> problems)
    {
        if (IsArchitectureIndependent(package.Architecture))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(device.Architecture))
        {
            problems.Add($"The package is built for {package.Architecture} but the device architecture is unknown.");
            return;
        }

        if (NormalizeArchitecture(package.Architecture!) != NormalizeArchitecture(device.Architecture))
        {
            problems.Add($"The package is built for {package.Architecture}, the device is {device.Architecture}.");
        }
    }

    private static void CheckFirmware(EapManifest package, AcapDeviceFacts device, List<string> problems, List<string> warnings)
    {
        var minFromSchema = MinimumOsForSchema(package.SchemaVersion, out var newerSchema);
        var needsFirmware = package.CompatibleOsVersions.Count > 0 || minFromSchema is not null;
        if (string.IsNullOrWhiteSpace(device.FirmwareVersion) || NumericPrefix(device.FirmwareVersion).Count == 0)
        {
            if (needsFirmware)
            {
                problems.Add("The device firmware version is unknown.");
            }

            return;
        }

        var fw = device.FirmwareVersion;
        if (package.CompatibleOsVersions.Count > 0 && !package.CompatibleOsVersions.Any(r => IsInRange(fw, r)))
        {
            problems.Add($"AXIS OS {fw} is not supported by the package (needs {string.Join(" or ", package.CompatibleOsVersions)}).");
        }

        if (minFromSchema is not null && CompareTruncated(NumericPrefix(fw), NumericPrefix(minFromSchema)) < 0)
        {
            problems.Add($"The package needs AXIS OS {minFromSchema} or later (manifest schema {package.SchemaVersion}), the device has {fw}.");
        }

        if (newerSchema)
        {
            warnings.Add($"Manifest schema {package.SchemaVersion} is newer than this plugin knows.");
        }

        if (package.Source == "package.conf" && package.RequiredEmbeddedDevelopmentVersion is { Length: > 0 } req)
        {
            if (string.IsNullOrWhiteSpace(device.EmbeddedDevelopmentVersion))
            {
                warnings.Add($"The package needs embedded development version {req}; the device does not report its version.");
            }
            else if (CompareVersions(device.EmbeddedDevelopmentVersion, req) < 0)
            {
                problems.Add($"The package needs embedded development version {req}, the device has {device.EmbeddedDevelopmentVersion}.");
            }
        }
    }

    private static void CheckRoot(EapManifest package, AcapDeviceFacts device, List<string> problems, List<string> warnings)
    {
        if (!package.RunsAsRoot)
        {
            return;
        }

        var fw = NumericPrefix(device.FirmwareVersion);
        if (fw.Count == 0 || CompareTruncated(fw, [12]) >= 0)
        {
            problems.Add("The package runs as root, which AXIS OS 12 and later do not allow.");
        }
        else if (CompareTruncated(fw, [11, 8]) >= 0)
        {
            warnings.Add("The package runs as root; the device must allow root applications (AllowRoot).");
        }
    }

    private static List<string> Split(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? []
            : version.Trim().TrimStart('v', 'V').Split(['.', '-', '_', '+'], StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Leading numeric components: "12.11.77" -> [12, 11, 77], "11.11.124.1_beta" -> [11, 11, 124, 1].</summary>
    private static List<int> NumericPrefix(string? version)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(version))
        {
            return result;
        }

        foreach (var part in version.Trim().Split('.'))
        {
            var digits = new string(part.TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                break;
            }

            result.Add(n);
            if (digits.Length != part.Length)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Compares <paramref name="value"/> cut to the precision of <paramref name="bound"/>.</summary>
    private static int CompareTruncated(List<int> value, List<int> bound)
    {
        for (var i = 0; i < bound.Count; i++)
        {
            var v = i < value.Count ? value[i] : 0;
            if (v != bound[i])
            {
                return v.CompareTo(bound[i]);
            }
        }

        return 0;
    }
}
