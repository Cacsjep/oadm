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
/// One device's install preview (<see cref="AcapCompatibility.Preview"/>): the chosen package (<see cref="PackageIndex"/>, -1
/// when none fits), the report, the short verdict, the full text and the details shown per device.
/// </summary>
public sealed record AcapInstallPreview(
    int PackageIndex,
    AcapCompatibilityReport? Report,
    bool IsOk,
    bool IsWarning,
    bool IsError,
    string Verdict,
    string Result,
    string Details,
    string? PackageArchitecture)
{
    /// <summary>How the build was found.</summary>
    public AcapBuildChoice? Choice { get; init; }

    /// <summary>Several builds fit equally: a person or an agent chooses one.</summary>
    public bool IsUnclear => Choice?.IsUnclear == true;

    /// <summary>Install, upgrade, reinstall or downgrade on a compatible device; null otherwise.</summary>
    public InstallKind? Kind => IsOk ? Report?.Kind : null;

    /// <summary>The device has a newer version than the package (installs only with the downgrade option).</summary>
    public bool IsDowngrade => Report?.Kind == InstallKind.Downgrade;
}

/// <summary>
/// Pure compatibility rules for installing an .eap on a device (see the plugin README decision table).
/// Used by the dialog for the preview and by the task again, with fresh device data, before uploading.
/// </summary>
public static class AcapCompatibility
{
    /// <summary>
    /// Manifest schema version -> minimum AXIS OS (developer.axis.com, manifest schemas), for packages that state no AXIS OS range.
    /// 1.7.5 is not in Axis's table: ACAP Native SDK 1.15.4 (the AXIS OS 11.11 LTS track) writes it, and AXIS OS 11.11 installs
    /// and runs it (measured on ARTPEC-7 and ARTPEC-8, AXIS OS 11.11.238). Without the entry it took 1.7.4's 12.4.
    /// </summary>
    public static readonly IReadOnlyList<(string Schema, string MinOs)> SchemaMinimumOs =
    [
        ("1.0", "10.7"), ("1.1", "10.7"), ("1.2", "10.7"), ("1.3", "10.9"), ("1.3.1", "11.0"),
        ("1.4.0", "11.7"), ("1.5.0", "11.8"), ("1.6.0", "11.9"), ("1.7.0", "11.10"), ("1.7.1", "12.0"),
        ("1.7.2", "12.1"), ("1.7.3", "12.2"), ("1.7.4", "12.4"), ("1.7.5", "11.11"), ("1.8.0", "12.6"), ("1.9.0", "12.8"),
        ("1.10.0", "12.10"), ("1.11.0", "12.11"), ("2.0.0", "12.10"), ("2.1.0", "12.11"), ("2.2.0", "12.11"),
    ];

    /// <summary>Checks a package against a device. <paramref name="installed"/> is the same-named application on the device, if any.</summary>
    /// <param name="all">Every installed application: one whose name differs only in upper and lower case blocks the install
    /// (the device refuses it with error 14).</param>
    public static AcapCompatibilityReport Check(EapManifest package, AcapDeviceFacts device, InstalledApplication? installed, bool allowDowngrade, IReadOnlyList<InstalledApplication>? all = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(device);
        var problems = new List<string>();
        var warnings = new List<string>();
        if (installed is null && all?.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.OrdinalIgnoreCase)) is { } clash)
        {
            problems.Add($"Another application named {clash.Name} ({clash.DisplayName}) is installed. Remove it first.");
        }

        CheckArchitecture(package, device, problems);
        CheckFirmware(package, device, problems, warnings);
        CheckRoot(package, device, problems, warnings);
        CheckManifest(package, device, problems);
        if (CheckSdk(package, device, warnings) is { } sdk)
        {
            problems.Add(sdk.Sentence);
        }

        // AXIS OS 13 removed the AllowUnsigned setting (null then): it installs signed applications only.
        var fwPrefix = NumericPrefix(device.FirmwareVersion);
        if (device.AllowUnsigned == false || (fwPrefix.Count > 0 && CompareTruncated(fwPrefix, [13]) >= 0))
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

    /// <summary>
    /// The install preview of one device from its own read (the Applications page's row): the build of the device's group
    /// (<see cref="AcapBuildChooser.Choose"/>), then <see cref="Check"/> against the installed applications. Without names the
    /// builds are called "aarch64 build".
    /// </summary>
    public static AcapInstallPreview Preview(IReadOnlyList<EapManifest> packages, AcapDeviceFacts device, IReadOnlyList<InstalledApplication> applications, bool allowDowngrade,
        IReadOnlyList<string>? names = null, IReadOnlyList<AcapPackageRule>? rules = null, IReadOnlyList<AcapPackageMapping>? mapping = null,
        AcapUnmatched unmatched = AcapUnmatched.Automatic)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(device);
        var builds = Builds(packages, names);
        return Preview(builds, AcapBuildChooser.Choose(builds, AcapDeviceGroup.Of(device), rules, mapping, unmatched, device), device, applications, allowDowngrade);
    }

    /// <summary>The builds of packages before an upload: key and name are the file name, or "aarch64 build" without names.</summary>
    public static IReadOnlyList<AcapBuild> Builds(IReadOnlyList<EapManifest> packages, IReadOnlyList<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return [.. packages.Select((p, i) =>
        {
            var name = names is not null && i < names.Count ? names[i]
                : (IsArchitectureIndependent(p.Architecture) ? "any architecture" : p.Architecture) + " build";
            return new AcapBuild(name, name, p);
        })];
    }

    /// <summary>The install preview of one device for the build its group gets (<see cref="AcapBuildChooser.Choose"/>).</summary>
    public static AcapInstallPreview Preview(IReadOnlyList<AcapBuild> builds, AcapBuildChoice choice, AcapDeviceFacts device, IReadOnlyList<InstalledApplication> applications, bool allowDowngrade)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(applications);
        if (choice.IsUnclear)
        {
            var reason = choice.Reason ?? AcapBuildChooser.SeveralFit(choice.Candidates.Select(i => builds[i].Name));
            return new AcapInstallPreview(-1, null, IsOk: false, IsWarning: true, IsError: false, ChoosePackageVerdict, reason, reason, null) { Choice = choice };
        }

        if (!choice.IsChosen)
        {
            var none = choice.Reason ?? NoPackageFor(device.Architecture);
            return new AcapInstallPreview(-1, null, IsOk: false, IsWarning: true, IsError: false, "No package", none, none, null) { Choice = choice };
        }

        var build = builds[choice.Index];
        var package = build.Package;
        var name = builds.Count > 1 ? build.Name : null;
        var installed = applications.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.Ordinal));
        var report = Check(package, device, installed, allowDowngrade, applications);
        var details = string.Join(" ", report.IsCompatible ? report.Warnings : report.Problems);
        return new AcapInstallPreview(
            choice.Index,
            report,
            report.IsCompatible,
            report.IsCompatible && (report.Warnings.Count > 0 || report.Kind == InstallKind.Downgrade),
            !report.IsCompatible,
            report.IsCompatible ? report.KindText : "Not compatible",
            report.Summary,
            name is { } n ? (details.Length == 0 ? n : $"{n}. {details}") : details,
            builds.Count > 1 ? package.Architecture : null) { Choice = choice };
    }

    /// <summary>The verdict of a device whose package a person chooses in the table.</summary>
    public const string ChoosePackageVerdict = "Choose package";

    /// <summary>True when the package runs on any architecture (no native code).</summary>
    public static bool IsArchitectureIndependent(string? architecture) =>
        string.IsNullOrWhiteSpace(architecture)
        || architecture.Equals("all", StringComparison.OrdinalIgnoreCase)
        || architecture.Equals("noarch", StringComparison.OrdinalIgnoreCase);

    /// <summary>"No package for armv7hf." (several packages, none for the device's architecture: nothing is changed).</summary>
    public static string NoPackageFor(string? deviceArchitecture) =>
        string.IsNullOrWhiteSpace(deviceArchitecture) ? "No package for this device: its architecture is unknown." : $"No package for {deviceArchitecture.Trim()}.";

    /// <summary>The refusal of more files than one install takes.</summary>
    public static string TooManyFiles { get; } = string.Create(CultureInfo.InvariantCulture, $"Choose at most {AcapPayload.MaxFiles} files.");

    /// <summary>
    /// Why several packages cannot be installed together, null when they can: every package names the same application and no
    /// two files have the same name (a mapping names them). Several builds per architecture, also of other versions, and a
    /// package for any architecture are fine: each device group gets one (<see cref="AcapBuildChooser"/>). Checked before any upload.
    /// </summary>
    public static string? CheckPackageSet(IReadOnlyList<(string FileName, EapManifest Package)> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count <= 1)
        {
            return null;
        }

        if (packages.Count > AcapPayload.MaxFiles)
        {
            return TooManyFiles;
        }

        var first = packages[0].Package;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, package) in packages)
        {
            if (!string.Equals(package.AppName, first.AppName, StringComparison.Ordinal))
            {
                return $"{file} is another application ({package.DisplayName}). Choose the files of one application.";
            }

            if (!names.Add(file))
            {
                return $"Two files are named {file}. Rename one of them.";
            }
        }

        return null;
    }

    /// <summary>"1.2.0" for one version, "1.2.0 and 1.1.4" for builds of several (newest first).</summary>
    public static string VersionsText(IEnumerable<EapManifest> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var versions = packages.Select(p => p.Version).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).ToList();
        return versions.Count <= 1 ? versions.FirstOrDefault() ?? string.Empty : string.Join(", ", versions[..^1]) + " and " + versions[^1];
    }

    /// <summary>The version starts with a number ("12.11.77", "10.12").</summary>
    public static bool HasNumericVersion(string? version) => NumericPrefix(version).Count > 0;

    /// <summary>The leading numeric components of a version.</summary>
    internal static List<int> NumericParts(string? version) => NumericPrefix(version);

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

    /// <summary>
    /// No version lies in the range: its min is above its max at the precision both bounds have ("13" to "12", "12.10" to
    /// "12.9"; "12.5" to "12" is fine, max "12" is any 12.x).
    /// </summary>
    public static bool IsInverted(OsVersionRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.Min is not { } min || range.Max is not { } max)
        {
            return false;
        }

        var low = NumericPrefix(min);
        var high = NumericPrefix(max);
        for (var i = 0; i < Math.Min(low.Count, high.Count); i++)
        {
            if (low[i] != high[i])
            {
                return low[i] > high[i];
            }
        }

        return false;
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
                problems.Add("The AXIS OS version of the device is unknown.");
            }

            return;
        }

        var fw = device.FirmwareVersion;
        if (package.CompatibleOsVersions.Count > 0 && !package.CompatibleOsVersions.Any(r => IsInRange(fw, r)))
        {
            problems.Add($"AXIS OS {fw} is not supported by the package (needs {string.Join(" or ", package.CompatibleOsVersions)}).");
        }

        // The package's own range wins: the schema table only says which release a schema came with, and packages with
        // schema 2.1.0 that state 11.11 install and run there. The device still has the last word on the upload.
        if (package.CompatibleOsVersions.Count == 0 && minFromSchema is not null && CompareTruncated(NumericPrefix(fw), NumericPrefix(minFromSchema)) < 0)
        {
            problems.Add($"The package needs AXIS OS {minFromSchema} or later (manifest schema {package.SchemaVersion}), the device has {fw}.");
        }

        if (newerSchema)
        {
            warnings.Add($"Manifest schema {package.SchemaVersion} is newer than this plugin knows.");
        }
    }

    /// <summary>The kinds of applications built with the ACAP 3 SDK or the ACAP Native SDK (both write a manifest.json).</summary>
    public static readonly IReadOnlyList<string> ManifestSdks = ["acap3", "acap4-native"];

    /// <summary>The refusal of a package with a manifest.json on a device that lists neither ACAP 3 nor ACAP 4 native.</summary>
    public const string ManifestSdkMissing = "This device does not support ACAP 3 or ACAP 4 applications.";

    /// <summary>
    /// The SDK rules (plugin README "SDK"), null when the package fits or the data to decide is missing. 1) The required SDK
    /// version (manifest <c>embeddedSdkVersion</c>, else package.conf <c>REQEMBDEVVERSION</c>) below 3 is compared with
    /// <c>Properties.EmbeddedDevelopment.Version</c> by its numbers: lower refuses, not reported warns. 3 and later is not on
    /// that scale (AXIS OS 12.11 reports 2.18 and runs packages that say 3.0): compared only when the device reports 3 or later
    /// too, and then only a warning. 2) A package with a manifest.json (ACAP 3 SDK or ACAP Native SDK; the package does not
    /// say which) needs "acap3" or "acap4-native" in the device's info.cgi list; no list, no check. Nothing in a package says
    /// it needs "acap4-cv", so that is never checked. Used by <see cref="Check"/> and by <see cref="AcapBuildChooser"/> per build.
    /// </summary>
    /// <returns>The refusal as a sentence and as a fragment for "x.eap does not fit: ...".</returns>
    public static (string Sentence, string Fragment)? CheckSdk(EapManifest package, AcapDeviceFacts device, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(device);
        var required = (package.Source == "manifest.json" ? package.EmbeddedSdkVersion : package.RequiredEmbeddedDevelopmentVersion)?.Trim();
        var needs = NumericPrefix(required);
        if (needs.Count > 0)
        {
            var reported = device.EmbeddedDevelopmentVersion?.Trim();
            var has = NumericPrefix(reported);
            if (needs[0] < 3)
            {
                if (has.Count == 0)
                {
                    warnings?.Add($"The application needs ACAP version {required}; the device does not report its version.");
                }
                else if (CompareVersions(reported, required) < 0)
                {
                    return ($"The application needs a newer ACAP version ({required}) than this device has ({reported}). Upgrade AXIS OS first.",
                        $"it needs a newer ACAP version ({required}) than the device has ({reported})");
                }
            }
            else if (has.Count > 0 && has[0] >= 3 && CompareVersions(reported, required) < 0)
            {
                warnings?.Add($"The application needs ACAP version {required} and the device reports {reported}. The device decides on install.");
            }
        }

        if (package.Source == "manifest.json" && device.SupportedSdks is { Count: > 0 } sdks
            && !sdks.Any(s => ManifestSdks.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            return (ManifestSdkMissing, "the device does not support ACAP 3 or ACAP 4 applications");
        }

        return null;
    }

    /// <summary>The refusal of a package without a manifest.json on AXIS OS 13 and later.</summary>
    public const string ManifestMissing = "The package has no manifest.json, which AXIS OS 13 and later need.";

    /// <summary>AXIS OS 13 no longer installs a package that has only a package.conf (AXIS OS 13.0 release notes).</summary>
    private static void CheckManifest(EapManifest package, AcapDeviceFacts device, List<string> problems)
    {
        var fw = NumericPrefix(device.FirmwareVersion);
        if (package.Source == "package.conf" && fw.Count > 0 && CompareTruncated(fw, [13]) >= 0)
        {
            problems.Add(ManifestMissing);
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
