namespace Oadm.Plugins.Firmware.Tests;

/// <summary>The upgrade path Axis recommends (AXIS OS knowledge base "Upgrade path"), its examples one by one.</summary>
public sealed class UpgradePathTests
{
    private static string? Advice(string current, string target) =>
        AxisOsUpgradePath.Advice(AxisOsVersion.TryParse(current)!, AxisOsVersion.TryParse(target)!);

    [Theory]
    // Older active track to the latest active: through the latest of the LTS in between.
    [InlineData("11.8.61", "12.6.85", "Recommended path: the latest AXIS OS 11.11, then 12.6.85.")]
    // Older LTS to two newer LTS: through the intermediate LTS (and first the latest of the current one).
    [InlineData("9.80.49", "11.11.169", "Recommended path: the latest AXIS OS 9.80, then the latest 10.12, then 11.11.169.")]
    // LTS over the next LTS: first the latest of the current LTS.
    [InlineData("11.11.109", "13.0.88", "Recommended path: the latest AXIS OS 11.11, then the latest 12.11, then 13.0.88.")]
    // The upgrade guide's example: 9.80.3.10 to the latest.
    [InlineData("9.80.3.10", "12.7.53", "Recommended path: the latest AXIS OS 9.80, then the latest 10.12, then the latest 11.11, then 12.7.53.")]
    // An old active version over two LTS tracks.
    [InlineData("10.9.4", "12.6.85", "Recommended path: the latest AXIS OS 10.12, then the latest 11.11, then 12.6.85.")]
    public void Upgrades_off_the_path_name_the_versions_to_install_first(string current, string target, string advice) =>
        Assert.Equal(advice, Advice(current, target));

    [Theory]
    [InlineData("11.11.109", "11.11.169")] // within the LTS track
    [InlineData("11.8.61", "11.11.169")] // older active to the LTS of its major line
    [InlineData("12.0.93", "12.6.85")] // within the active track
    [InlineData("12.6.85", "12.11.77")] // active to the next LTS
    [InlineData("12.11.77", "12.11.77")] // the same version
    [InlineData("12.11.77", "11.11.169")] // a downgrade (its own rules)
    public void Upgrades_on_the_path_get_no_advice(string current, string target)
    {
        Assert.Null(Advice(current, target));
        Assert.Null(AxisOsUpgradePath.Note(AxisOsVersion.TryParse(current)!, AxisOsVersion.TryParse(target)!));
    }

    [Theory]
    // Leaving an LTS track for the next major: on the path only from its latest version, which OADM does not know.
    [InlineData("12.11.147", "13.0.88", "Major upgrade. Make sure the device runs the latest AXIS OS 12.11 first.")]
    [InlineData("11.11.109", "12.6.85", "Major upgrade. Make sure the device runs the latest AXIS OS 11.11 first.")]
    [InlineData("10.12.166", "11.11.169", "Major upgrade. Make sure the device runs the latest AXIS OS 10.12 first.")]
    public void Leaving_an_lts_track_gets_a_note_not_advice(string current, string target, string note)
    {
        Assert.Null(Advice(current, target));
        Assert.Equal(note, AxisOsUpgradePath.Note(AxisOsVersion.TryParse(current)!, AxisOsVersion.TryParse(target)!));
    }

    [Fact]
    public void A_note_upgrade_is_ok_with_the_note_in_its_message()
    {
        var image = FirmwareImageInspector.Inspect("Q6088-E_13_0_88.bin", 190 * 1024 * 1024);

        var check = FirmwareCompatibility.Evaluate("AXIS Q6088-E", "12.11.147", image, FactoryDefaultMode.None, allowDowngrade: false);

        Assert.Equal(FirmwareVerdict.Upgrade, check.Verdict);
        Assert.Null(check.PathAdvice);
        Assert.Equal("Major upgrade. Make sure the device runs the latest AXIS OS 12.11 first.", check.PathNote);
        Assert.Equal("12.11.147 → 13.0.88. Major upgrade. Make sure the device runs the latest AXIS OS 12.11 first.", check.Message);
    }

    [Fact]
    public void The_check_advises_but_still_installs_and_a_factory_default_lifts_the_path()
    {
        var image = FirmwareImageInspector.Inspect("P3265-V_12_6_85.bin", 80 * 1024 * 1024);

        var keep = FirmwareCompatibility.Evaluate("AXIS P3265-V", "11.8.61", image, FactoryDefaultMode.None, allowDowngrade: false);
        Assert.Equal(FirmwareVerdict.Upgrade, keep.Verdict);
        Assert.True(keep.WillInstall);
        Assert.Equal("Recommended path: the latest AXIS OS 11.11, then 12.6.85.", keep.PathAdvice);
        Assert.Equal("11.8.61 → 12.6.85. Recommended path: the latest AXIS OS 11.11, then 12.6.85.", keep.Message);

        var reset = FirmwareCompatibility.Evaluate("AXIS P3265-V", "11.8.61", image, FactoryDefaultMode.Soft, allowDowngrade: false);
        Assert.Null(reset.PathAdvice);
        Assert.Equal("11.8.61 → 12.6.85", reset.Message);
    }
}
