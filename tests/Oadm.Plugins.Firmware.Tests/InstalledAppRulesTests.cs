namespace Oadm.Plugins.Firmware.Tests;

/// <summary>AXIS OS 13 refuses the upgrade for unsigned or undeclared applications; the same rule from the application list.</summary>
public sealed class InstalledAppRulesTests
{
    private static readonly IReadOnlyList<InstalledApp> Recorded = InstalledAppsReader.Parse(Fixture.Read("applications-list-p3265v-12.11.xml"))!;

    private static AxisOsVersion V(string text) => AxisOsVersion.TryParse(text)!;

    [Fact]
    public void The_recorded_list_names_every_application_that_blocks_axis_os_13()
    {
        Assert.Equal(
            [
                "GenICam Camera Bridge: not signed",
                "Missing Features: not signed",
                "Face Verifier - Beta: not signed",
                "LPV Simulator: not signed, declares no AXIS OS versions",
                "Scene Telemetry Beta: not signed",
                "Example App: not signed, declares no AXIS OS versions",
            ],
            InstalledAppRules.Blocking(Recorded, V("13.0.88")));
    }

    [Fact]
    public void A_signed_application_outside_its_declared_range_blocks()
    {
        // AXIS License Plate Verifier declares 12.10 to 13: fine for 13.x, not for 14.
        Assert.Contains("AXIS License Plate Verifier: supports AXIS OS 12.10 to 13", InstalledAppRules.Blocking(Recorded, V("14.0.10")));
        Assert.DoesNotContain(InstalledAppRules.Blocking(Recorded, V("13.4.2")), p => p.StartsWith("AXIS License Plate Verifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_blocks_below_axis_os_13() =>
        Assert.Empty(InstalledAppRules.Blocking(Recorded, V("12.11.147")));

    [Fact]
    public void Bundled_applications_never_block()
    {
        // AXIS Object Analytics declares only 12 but comes with AXIS OS: the upgrade replaces it.
        Assert.DoesNotContain(InstalledAppRules.Blocking(Recorded, V("13.0.88")), p => p.StartsWith("AXIS Object Analytics", StringComparison.Ordinal));
    }

    [Fact]
    public void Fields_the_device_does_not_report_are_not_judged()
    {
        // Older AXIS OS lists neither the signature nor the versions.
        IReadOnlyList<InstalledApp> old = [new() { Name = "vmd", NiceName = "Video Motion Detection" }];

        Assert.Empty(InstalledAppRules.Blocking(old, V("13.0.88")));
    }

    [Fact]
    public void The_message_says_what_to_do()
    {
        Assert.Equal("Face Verifier: not signed. Update or remove it with Applications (ACAP) first.", InstalledAppRules.Describe(["Face Verifier: not signed"]));
        Assert.Equal("A: not signed; B: not signed. Update or remove them with Applications (ACAP) first.", InstalledAppRules.Describe(["A: not signed", "B: not signed"]));
    }

    [Theory]
    [InlineData("13.0.88", "12.10", "13", true)] // max "13" takes any 13.x
    [InlineData("13.0.88", "12", "12", false)]
    [InlineData("12.10.5", "12.10", null, true)] // min "12.10" takes 12.10.x
    [InlineData("12.9.1", "12.10", null, false)]
    [InlineData("14.1.0", null, "99", true)]
    public void Ranges_compare_at_the_precision_of_their_bounds(string version, string? min, string? max, bool inRange) =>
        Assert.Equal(inRange, InstalledAppRules.IsInRange(V(version), new AppOsRange(min, max)));

    [Fact]
    public void The_check_refuses_the_upgrade_and_names_the_applications()
    {
        var image = FirmwareImageInspector.Inspect("P3265-V_13_0_88.bin", 190 * 1024 * 1024);
        IReadOnlyList<InstalledApp> apps = [new() { Name = "faceverifier", NiceName = "Face Verifier", SignatureStatus = "Unknown", CompatibleOsVersions = [new("12.10", "99")] }];

        var check = FirmwareCompatibility.Evaluate("P3265-V", "12.11.77", image, FactoryDefaultMode.None, allowDowngrade: false, apps);

        Assert.Equal(FirmwareVerdict.AppsBlock, check.Verdict);
        Assert.False(check.WillInstall);
        Assert.Equal("12.11.77 → 13.0.88. Face Verifier: not signed. Update or remove it with Applications (ACAP) first.", check.Message);
    }
}
