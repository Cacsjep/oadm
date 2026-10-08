namespace Oadm.Plugins.Firmware.Tests;

public sealed class FirmwareImageTests
{
    [Theory]
    [InlineData("C:\\fw\\P3265-V_12_11_77.bin", "P3265-V_12_11_77.bin")]
    [InlineData("/home/u/fw/P3265-V_12_11_77.bin", "P3265-V_12_11_77.bin")]
    [InlineData("P3265-V_12_11_77.bin", "P3265-V_12_11_77.bin")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void File_name_is_taken_from_windows_and_unix_paths_on_every_os(string? path, string expected) =>
        Assert.Equal(expected, FirmwareImageInspector.FileNameOf(path));

    [Theory]
    [InlineData("P3265-V_12_11_77.bin", "P3265-V", "12.11.77")]
    [InlineData("P3265-LV_12.0.89.bin", "P3265-LV", "12.0.89")]
    [InlineData("AXIS_Q6135-LE_10_12_236.bin", "Q6135-LE", "10.12.236")]
    [InlineData("m3085-v_11_11_160.bin", "M3085-V", "11.11.160")]
    [InlineData("C:\\Downloads\\P1468-XLE_9_80_81.bin", "P1468-XLE", "9.80.81")]
    [InlineData("/home/u/AXIS P3265-V_12_11_77_signed.bin", "P3265-V", "12.11.77")]
    [InlineData("Q1798-LE_8_40_4_3.bin", "Q1798-LE", "8.40.4.3")]
    public void Reads_product_and_version_from_axis_download_names(string name, string product, string version)
    {
        var (p, v) = FirmwareImageInspector.ParseFileName(name);

        Assert.Equal(product, p);
        Assert.Equal(version, v!.Text);
    }

    [Theory]
    [InlineData("firmware.bin")]
    [InlineData("P3265-V.bin")]
    [InlineData("latest_P3265-V.bin")]
    [InlineData("P3265-V_12_11_77.eap")]
    [InlineData("")]
    [InlineData(null)]
    public void Unrecognized_names_give_no_metadata(string? name)
    {
        var (p, v) = FirmwareImageInspector.ParseFileName(name);

        Assert.Null(p);
        Assert.Null(v);
    }

    [Fact]
    public void Plausible_image_is_accepted()
    {
        var info = FirmwareImageInspector.Inspect("P3265-V_12_11_77.bin", 87L * 1024 * 1024);

        Assert.False(info.IsRejected);
        Assert.True(info.IsIdentified);
        Assert.Equal("P3265-V", info.Product);
    }

    [Fact]
    public void Real_axis_os_10_image_is_accepted()
    {
        // M3206-LVE_10_12_338.bin (76 MB) starts with gzip bytes (1F 8B); OADM never rejects by content.
        var info = FirmwareImageInspector.Inspect("M3206-LVE_10_12_338.bin", 76L * 1024 * 1024);

        Assert.False(info.IsRejected);
        Assert.Equal("M3206-LVE", info.Product);
        Assert.Equal("10.12.338", info.Version!.Text);
    }

    [Fact]
    public void Inspection_has_no_content_input()
    {
        // The API itself makes a magic-byte rejection impossible: only name and size are inspected.
        var method = typeof(FirmwareImageInspector).GetMethod(nameof(FirmwareImageInspector.Inspect))!;

        Assert.Equal([typeof(string), typeof(long)], method.GetParameters().Select(p => p.ParameterType));
    }

    [Theory]
    [InlineData("firmware.bin")]
    [InlineData("unknown_name.bin")]
    public void Unrecognized_name_is_accepted_and_the_device_validates(string name)
    {
        var info = FirmwareImageInspector.Inspect(name, 50_000_000);

        Assert.False(info.IsRejected);
        Assert.False(info.IsIdentified);
    }

    public static TheoryData<string, long, string> Rejected => new()
    {
        { "x.bin", 1000, "smaller than 1 MB" },
        { "x.bin", 3L * 1024 * 1024 * 1024, "larger than 2 GB" },
        { "P3265-V_12_11_77.eap", 50_000_000, ".bin" },
        { "P3265-V_12_11_77.zip", 50_000_000, ".bin" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Wrong_extension_or_implausible_size_is_rejected(string name, long size, string reason)
    {
        var info = FirmwareImageInspector.Inspect(name, size);

        Assert.True(info.IsRejected);
        Assert.Contains(reason, info.RejectReason!, StringComparison.Ordinal);
    }
}

public sealed class AxisOsVersionTests
{
    [Theory]
    [InlineData("12.11.77", "12.11.77", 0)]
    [InlineData("12.11.77", "12.11.78", -1)]
    [InlineData("12.11.77", "12.9.100", 1)]
    [InlineData("11.11.160", "12.0.0", -1)]
    [InlineData("10.12", "10.12.0", 0)]
    [InlineData("8.40.4.3", "8.40.4", 1)]
    [InlineData("12_11_77", "12.11.77", 0)]
    public void Compares_numerically(string a, string b, int expected)
    {
        var va = AxisOsVersion.TryParse(a)!;
        var vb = AxisOsVersion.TryParse(b)!;

        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(expected == 0, va == vb);
        if (expected == 0)
        {
            Assert.Equal(va.GetHashCode(), vb.GetHashCode());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12")]
    public void Unparsable_versions_are_null(string? text) => Assert.Null(AxisOsVersion.TryParse(text));

    [Fact]
    public void Ignores_non_numeric_suffix_and_exposes_major()
    {
        var v = AxisOsVersion.TryParse("12.6.85_beta")!;
        Assert.Equal("12.6.85", v.Text);
        Assert.Equal(12, v.Major);
    }
}

public sealed class FirmwareCompatibilityTests
{
    private static FirmwareImageInfo Image(string name, string? reject = null)
    {
        var (p, v) = FirmwareImageInspector.ParseFileName(name);
        return new FirmwareImageInfo(name, 100_000_000, p, v, reject);
    }

    [Theory]
    [InlineData("P3265-V_12_11_77.bin", "P3265-V", "11.11.160", FactoryDefaultMode.None, false, FirmwareVerdict.Upgrade)]
    [InlineData("P3265-V_12_11_77.bin", "AXIS P3265-V", "12.11.77", FactoryDefaultMode.None, false, FirmwareVerdict.AlreadyUpToDate)]
    [InlineData("P3265-V_12_11_77.bin", "Q6135-LE", "11.11.160", FactoryDefaultMode.None, true, FirmwareVerdict.WrongProduct)]
    [InlineData("P3265-V_10_12_236.bin", "P3265-V", "11.11.160", FactoryDefaultMode.Hard, false, FirmwareVerdict.DowngradeBlocked)]
    [InlineData("P3265-V_10_12_236.bin", "P3265-V", "11.11.160", FactoryDefaultMode.None, true, FirmwareVerdict.DowngradeBlocked)]
    [InlineData("P3265-V_10_12_236.bin", "P3265-V", "11.11.160", FactoryDefaultMode.Soft, true, FirmwareVerdict.Downgrade)]
    [InlineData("P3265-V_11_11_100.bin", "P3265-V", "11.11.160", FactoryDefaultMode.None, false, FirmwareVerdict.DowngradeBlocked)]
    [InlineData("firmware.bin", "P3265-V", "11.11.160", FactoryDefaultMode.None, false, FirmwareVerdict.DeviceValidates)]
    [InlineData("firmware.bin", "P3265-V", "11.11.160", FactoryDefaultMode.Soft, false, FirmwareVerdict.UnknownVersionRefused)]
    [InlineData("firmware.bin", "P3265-V", "11.11.160", FactoryDefaultMode.Soft, true, FirmwareVerdict.DeviceValidates)]
    [InlineData("P3265-V_12_11_77.bin", null, "11.11.160", FactoryDefaultMode.None, false, FirmwareVerdict.Upgrade)]
    [InlineData("P3265-V_12_11_77.bin", "P3265-V", null, FactoryDefaultMode.None, false, FirmwareVerdict.DeviceValidates)]
    public void Decision_table(string file, string? model, string? version, FactoryDefaultMode mode, bool allowDowngrade, FirmwareVerdict expected)
    {
        var check = FirmwareCompatibility.Evaluate(model, version, Image(file), mode, allowDowngrade);

        Assert.Equal(expected, check.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void Rejected_file_wins_over_everything()
    {
        var check = FirmwareCompatibility.Evaluate("P3265-V", "11.11.160", Image("P3265-V_12_11_77.bin", "bad"), FactoryDefaultMode.None, true);

        Assert.Equal(FirmwareVerdict.InvalidFile, check.Verdict);
        Assert.False(check.WillInstall);
    }

    [Theory]
    [InlineData(FirmwareVerdict.Upgrade, true)]
    [InlineData(FirmwareVerdict.DeviceValidates, true)]
    [InlineData(FirmwareVerdict.Downgrade, true)]
    [InlineData(FirmwareVerdict.AlreadyUpToDate, false)]
    [InlineData(FirmwareVerdict.DowngradeBlocked, false)]
    [InlineData(FirmwareVerdict.WrongProduct, false)]
    [InlineData(FirmwareVerdict.InvalidFile, false)]
    [InlineData(FirmwareVerdict.UnknownVersionRefused, false)]
    public void Only_upgrade_downgrade_and_device_validated_install(FirmwareVerdict verdict, bool installs) =>
        Assert.Equal(installs, new FirmwareCheck(verdict, "m").WillInstall);
}

public sealed class FirmwarePayloadTests
{
    [Fact]
    public void Round_trips_with_camel_case_enum()
    {
        var json = new FirmwarePayload { FileId = "abc", FileName = "P3265-V_12_11_77.bin", FactoryDefaultMode = FactoryDefaultMode.Hard, AllowDowngrade = true }.ToJson();

        Assert.Contains("\"factoryDefaultMode\":\"hard\"", json, StringComparison.Ordinal);
        var back = FirmwarePayload.Parse(json);
        Assert.Equal("abc", back.FileId);
        Assert.Equal(FactoryDefaultMode.Hard, back.FactoryDefaultMode);
        Assert.True(back.AllowDowngrade);
    }

    [Theory]
    [InlineData("P3265-V_12_11_77.bin", FactoryDefaultMode.None, false, FirmwareDirection.Upgrade, "Upgrade firmware to 12.11.77")]
    [InlineData("P3265-V_12_11_77.bin", FactoryDefaultMode.None, false, FirmwareDirection.Unknown, "Upgrade firmware to 12.11.77")]
    [InlineData("P3265-V_10_12_236.bin", FactoryDefaultMode.Soft, true, FirmwareDirection.Downgrade, "Downgrade firmware to 10.12.236 (factory default)")]
    [InlineData("P3265-V_11_11_160.bin", FactoryDefaultMode.Hard, true, FirmwareDirection.Unknown, "Install firmware 11.11.160 (factory default)")]
    [InlineData("firmware.bin", FactoryDefaultMode.None, false, FirmwareDirection.Unknown, "Install firmware")]
    [InlineData("M3206-LVE_10_12_338.bin", FactoryDefaultMode.Hard, false, FirmwareDirection.Upgrade, "Upgrade firmware to 10.12.338 (factory default)")]
    public void Task_name_says_exactly_what_is_installed(string file, FactoryDefaultMode mode, bool allowDowngrade, FirmwareDirection direction, string expected)
    {
        var json = new FirmwarePayload { FileId = "f", FileName = file, FactoryDefaultMode = mode, AllowDowngrade = allowDowngrade, Direction = direction }.ToJson();

        Assert.Equal(expected, ((Oadm.Sdk.Plugins.ITaskPlugin)new FirmwareTaskPlugin()).GetTaskName(json));
        Assert.Equal(direction, FirmwarePayload.Parse(json).Direction);
    }

    [Fact]
    public void Task_name_without_a_valid_payload_is_the_display_name() =>
        Assert.Equal("Upgrade firmware", new FirmwareTaskPlugin().GetTaskName("{}"));

    [Fact]
    public void Defaults_are_safe()
    {
        var payload = FirmwarePayload.Parse("""{"fileId":"x"}""");

        Assert.Equal(FactoryDefaultMode.None, payload.FactoryDefaultMode);
        Assert.False(payload.AllowDowngrade);
    }

    [Fact]
    public void Numeric_enum_out_of_range_is_rejected() =>
        Assert.Throws<ArgumentException>(() => FirmwarePayload.Parse("""{"fileId":"x","factoryDefaultMode":7}"""));
}
