namespace Oadm.Plugins.Acap.Tests;

public sealed class CompatibilityTests
{
    private static readonly AcapDeviceFacts P3265 = new() { Architecture = "aarch64", FirmwareVersion = "12.11.77", EmbeddedDevelopmentVersion = "2.18", AllowUnsigned = true };

    private static EapManifest Package(string version = "1.0.0", string? arch = "aarch64", string? schema = "1.7.1", OsVersionRange[]? os = null, bool root = false, string source = "manifest.json", string? reqEmbDev = null) => new()
    {
        AppName = "hello",
        Version = version,
        Architecture = arch,
        SchemaVersion = schema,
        CompatibleOsVersions = os ?? [],
        RunsAsRoot = root,
        Source = source,
        RequiredEmbeddedDevelopmentVersion = reqEmbDev,
        Vendor = "Acme",
    };

    [Fact]
    public void Matching_package_is_a_new_install()
    {
        var r = AcapCompatibility.Check(Package(), P3265, null, allowDowngrade: false);
        Assert.True(r.IsCompatible);
        Assert.Equal(InstallKind.Install, r.Kind);
        Assert.Equal("New install", r.Summary);
    }

    [Theory]
    [InlineData("armv7hf", "aarch64", false)]
    [InlineData("aarch64", "armv7hf", false)]
    [InlineData("mips", "aarch64", false)]
    [InlineData("arm64", "aarch64", true)]
    [InlineData("all", "armv7hf", true)]
    [InlineData(null, "armv7hf", true)]
    public void Architecture_must_match(string? package, string device, bool ok)
    {
        var r = AcapCompatibility.Check(Package(arch: package), P3265 with { Architecture = device }, null, false);
        Assert.Equal(ok, r.IsCompatible);
        if (!ok)
        {
            Assert.Contains($"built for {package}", r.Problems[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown_device_architecture_blocks_native_packages()
    {
        var r = AcapCompatibility.Check(Package(arch: "aarch64"), P3265 with { Architecture = null }, null, false);
        Assert.False(r.IsCompatible);
    }

    [Theory]
    [InlineData("12.11.77", "12.10", "99", true)]
    [InlineData("12.9.10", "12.10", "99", false)]
    [InlineData("12.11.77", "12", "12", true)]
    [InlineData("13.0.1", "12", "12", false)]
    [InlineData("13.2.0", "12.10", "13", true)]
    [InlineData("11.11.124", "11.11", "99", true)]
    [InlineData("11.10.5", "11.11", null, false)]
    public void Firmware_must_be_in_compatible_range(string firmware, string? min, string? max, bool ok)
    {
        var r = AcapCompatibility.Check(Package(schema: null, os: [new OsVersionRange(min, max)]), P3265 with { FirmwareVersion = firmware }, null, false);
        Assert.Equal(ok, r.IsCompatible);
    }

    [Theory]
    [InlineData("1.7.1", "11.11.124", false)]
    [InlineData("1.7.1", "12.0.5", true)]
    [InlineData("1.11.0", "12.10.40", false)]
    [InlineData("1.11.0", "12.11.77", true)]
    [InlineData("1.3", "10.9.0", true)]
    [InlineData("2.0.0", "12.10.1", true)]
    public void Schema_version_implies_minimum_firmware(string schema, string firmware, bool ok)
    {
        var r = AcapCompatibility.Check(Package(schema: schema), P3265 with { FirmwareVersion = firmware }, null, false);
        Assert.Equal(ok, r.IsCompatible);
        if (!ok)
        {
            Assert.Contains("needs AXIS OS", r.Problems[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Newer_unknown_schema_warns_but_uses_highest_known_minimum()
    {
        var r = AcapCompatibility.Check(Package(schema: "2.9.0"), P3265, null, false);
        Assert.True(r.IsCompatible);
        Assert.Contains(r.Warnings, w => w.Contains("newer than this plugin knows", StringComparison.Ordinal));
        Assert.False(AcapCompatibility.Check(Package(schema: "2.9.0"), P3265 with { FirmwareVersion = "12.10.5" }, null, false).IsCompatible);
    }

    [Fact]
    public void Unknown_firmware_blocks_when_package_has_requirements()
    {
        Assert.False(AcapCompatibility.Check(Package(), P3265 with { FirmwareVersion = null }, null, false).IsCompatible);
        Assert.True(AcapCompatibility.Check(Package(schema: null), P3265 with { FirmwareVersion = null }, null, false).IsCompatible);
    }

    [Fact]
    public void Legacy_package_checks_embedded_development_version()
    {
        var pkg = Package(schema: null, source: "package.conf", reqEmbDev: "2.20");
        Assert.False(AcapCompatibility.Check(pkg, P3265, null, false).IsCompatible);
        Assert.True(AcapCompatibility.Check(pkg with { RequiredEmbeddedDevelopmentVersion = "2.0" }, P3265, null, false).IsCompatible);
    }

    [Theory]
    [InlineData("12.11.77", false, false)]
    [InlineData("12.0.0", false, false)]
    [InlineData("11.11.124", true, true)]
    [InlineData("11.7.0", true, false)]
    public void Root_applications_are_refused_from_AXIS_OS_12(string firmware, bool ok, bool warns)
    {
        var r = AcapCompatibility.Check(Package(schema: null, root: true), P3265 with { FirmwareVersion = firmware }, null, false);
        Assert.Equal(ok, r.IsCompatible);
        Assert.Equal(warns, r.Warnings.Any(w => w.Contains("AllowRoot", StringComparison.Ordinal)));
    }

    [Fact]
    public void Signed_only_device_gives_a_warning()
    {
        var r = AcapCompatibility.Check(Package(), P3265 with { AllowUnsigned = false }, null, false);
        Assert.True(r.IsCompatible);
        Assert.Contains(r.Warnings, w => w.Contains("only signed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0.0", "1.2.0", InstallKind.Upgrade, true)]
    [InlineData("1.2.0", "1.2.0", InstallKind.Reinstall, true)]
    [InlineData("1.10.0", "1.9.0", InstallKind.Downgrade, false)]
    public void Detects_upgrade_reinstall_and_downgrade(string installed, string package, InstallKind kind, bool ok)
    {
        var app = new InstalledApplication { Name = "hello", Version = installed, Vendor = "Acme" };
        var r = AcapCompatibility.Check(Package(version: package), P3265, app, allowDowngrade: false);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(ok, r.IsCompatible);
        Assert.Equal(installed, r.InstalledVersion);
    }

    [Fact]
    public void Downgrade_needs_the_explicit_option()
    {
        var app = new InstalledApplication { Name = "hello", Version = "2.0.0" };
        var blocked = AcapCompatibility.Check(Package(version: "1.0.0"), P3265, app, allowDowngrade: false);
        var allowed = AcapCompatibility.Check(Package(version: "1.0.0"), P3265, app, allowDowngrade: true);
        Assert.Contains("downgrade option", blocked.Problems.Single(), StringComparison.Ordinal);
        Assert.True(allowed.IsCompatible);
        Assert.Equal("Downgrade from 2.0.0", allowed.Summary);
    }

    [Fact]
    public void Vendor_change_warns()
    {
        var app = new InstalledApplication { Name = "hello", Version = "1.0.0", Vendor = "Other" };
        var r = AcapCompatibility.Check(Package(version: "1.1.0"), P3265, app, false);
        Assert.Contains(r.Warnings, w => w.Contains("\"Other\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("3.0.13", "3.0.2", 1)]
    [InlineData("1.26.205", "1.26.205", 0)]
    [InlineData("0.9.0", "1.0.0", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    public void Compares_versions_numerically(string a, string b, int expected)
    {
        Assert.Equal(expected, AcapCompatibility.CompareVersions(a, b));
    }
}
