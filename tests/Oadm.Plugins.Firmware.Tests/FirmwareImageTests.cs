namespace Oadm.Plugins.Firmware.Tests;

public sealed class FirmwareImageTests
{
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
        var image = Fixture.Image();

        var info = FirmwareImageInspector.Inspect("P3265-V_12_11_77.bin", image.Length, image.AsSpan(0, FirmwareImageInspector.HeaderLength));

        Assert.False(info.IsRejected);
        Assert.True(info.IsIdentified);
        Assert.Equal("P3265-V", info.Product);
    }

    public static TheoryData<string, byte[], long, string> Rejected => new()
    {
        { "firmware.zip.bin", Header("PK\u0003\u0004"u8), 50_000_000, "ZIP" },
        { "app.bin", Header([0x1F, 0x8B, 0x08, 0x00]), 50_000_000, "compressed archive" },
        { "x.bin", Header("\u007FELF"u8), 50_000_000, "program" },
        { "x.bin", Header("MZ\u0090\u0000"u8), 50_000_000, "program" },
        { "x.bin", Header("%PDF-1.7"u8), 50_000_000, "document" },
        { "x.bin", Header("<html>"u8), 50_000_000, "text" },
        { "x.bin", new byte[512], 50_000_000, "empty" },
        { "x.bin", Header([0xA5, 0x5A, 1, 2]), 1000, "too small" },
        { "x.bin", Header([0xA5, 0x5A, 1, 2]), 3L * 1024 * 1024 * 1024, "too large" },
        { "x.bin", [1, 2], 50_000_000, "header could not be read" },
        { "P3265-V_12_11_77.eap", Header([0xA5, 0x5A, 1, 2]), 50_000_000, ".bin" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Files_that_are_not_axis_os_images_are_rejected(string name, byte[] header, long size, string reason)
    {
        var info = FirmwareImageInspector.Inspect(name, size, header);

        Assert.True(info.IsRejected);
        Assert.Contains(reason, info.RejectReason!, StringComparison.Ordinal);
    }

    private static byte[] Header(ReadOnlySpan<byte> magic)
    {
        var header = new byte[FirmwareImageInspector.HeaderLength];
        new Random(1).NextBytes(header);
        magic.CopyTo(header);
        return header;
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
