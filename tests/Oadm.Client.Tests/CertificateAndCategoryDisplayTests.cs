using Google.Protobuf.WellKnownTypes;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

using DeviceCategory = Oadm.Contracts.V1.DeviceCategory;

namespace Oadm.Client.Tests;

public sealed class CertificateAndCategoryDisplayTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(245.5, "245 days", PillKind.Ok)]
    [InlineData(31, "31 days", PillKind.Ok)]
    [InlineData(30, "30 days", PillKind.Warning)]
    [InlineData(12.2, "12 days", PillKind.Warning)]
    [InlineData(1.5, "1 day", PillKind.Warning)]
    [InlineData(0.25, "Today", PillKind.Warning)]
    [InlineData(-0.25, "Expired today", PillKind.Error)]
    [InlineData(-1.2, "Expired 1 day ago", PillKind.Error)]
    [InlineData(-3.5, "Expired 3 days ago", PillKind.Error)]
    public void Expiry_text_and_chip(double days, string text, PillKind kind)
    {
        DateTime notAfter = Now.AddDays(days);

        Assert.Equal(text, CertificateDisplay.ExpiryText(notAfter, Now));
        Assert.Equal(kind, CertificateDisplay.ExpiryKind(notAfter, Now));
        Assert.Equal(new ChipInfo(text, kind), CertificateDisplay.Expiry(notAfter, Now));
    }

    [Fact]
    public void No_certificate_shows_empty_cells()
    {
        Assert.Equal("", CertificateDisplay.ExpiryText(null, Now));
        Assert.Same(ChipInfo.Empty, CertificateDisplay.Expiry(null, Now));
        Assert.Same(ChipInfo.Empty, CertificateDisplay.Trust(CertificateTrust.Unknown, null, Now));
        Assert.Null(CertificateDisplay.Tooltip(null, null, null));
        Assert.False(ChipInfo.Empty.HasText);
    }

    [Theory]
    [InlineData(CertificateTrust.Trusted, "Trusted", PillKind.Ok)]
    [InlineData(CertificateTrust.SelfSigned, "Self-signed", PillKind.Warning)]
    [InlineData(CertificateTrust.Untrusted, "Untrusted", PillKind.Error)]
    [InlineData(CertificateTrust.Expired, "Expired", PillKind.Error)]
    public void Trust_text_and_chip(CertificateTrust trust, string text, PillKind kind)
    {
        Assert.Equal(new ChipInfo(text, kind), CertificateDisplay.Trust(trust, Now.AddDays(100), Now));
    }

    [Fact]
    public void Passed_expiry_overrides_a_stale_trusted_result()
    {
        Assert.Equal(new ChipInfo("Expired", PillKind.Error), CertificateDisplay.Trust(CertificateTrust.Trusted, Now.AddDays(-1), Now));
    }

    [Fact]
    public void Tooltip_lists_subject_issuer_and_valid_until()
    {
        string? tip = CertificateDisplay.Tooltip("CN=cam", "CN=Lab CA", new DateTime(2027, 6, 9, 8, 30, 0, DateTimeKind.Utc));

        Assert.Equal(string.Join(Environment.NewLine, "Subject: CN=cam", "Issuer: CN=Lab CA", "Valid until: 2027-06-09 08:30 UTC"), tip);
    }

    [Fact]
    public void Row_shows_certificate_and_category_from_the_contract()
    {
        Device device = TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V");
        device.CertNotAfter = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(300).AddHours(1));
        device.CertTrust = CertificateTrust.SelfSigned;
        device.CertSubject = "CN=axis-b8a44f631339";
        device.CertIssuer = "CN=axis-b8a44f631339";
        device.ProductType = "Dome Camera";
        device.Category = DeviceCategory.Camera;
        device.HasVideo = true;

        var row = new DeviceRowViewModel(device);

        Assert.Equal(new ChipInfo("300 days", PillKind.Ok), row.CertExpires);
        Assert.Equal(new ChipInfo("Self-signed", PillKind.Warning), row.CertTrust);
        Assert.Contains("Issuer: CN=axis-b8a44f631339", row.CertTooltip, StringComparison.Ordinal);
        Assert.Equal("device.camera", row.CategoryIconKey);
        Assert.Equal("Camera (Dome Camera)", row.CategoryTooltip);
        IDeviceInfo info = row;
        Assert.Equal(Oadm.Sdk.Devices.DeviceCategory.Camera, info.Category);
        Assert.True(info.HasVideo);

        // HTTP-only update clears the certificate cells.
        device.CertNotAfter = null;
        device.CertTrust = CertificateTrust.Unknown;
        device.CertSubject = "";
        device.CertIssuer = "";
        row.Update(device);
        Assert.False(row.CertExpires.HasText);
        Assert.False(row.CertTrust.HasText);
        Assert.Null(row.CertTooltip);
    }

    [Theory]
    [InlineData(DeviceCategory.Camera, "device.camera")]
    [InlineData(DeviceCategory.Encoder, "device.encoder")]
    [InlineData(DeviceCategory.Speaker, "device.speaker")]
    [InlineData(DeviceCategory.Audio, "device.audio")]
    [InlineData(DeviceCategory.Intercom, "device.intercom")]
    [InlineData(DeviceCategory.Radar, "device.radar")]
    [InlineData(DeviceCategory.IoModule, "device.io")]
    [InlineData(DeviceCategory.DoorController, "device.door")]
    [InlineData(DeviceCategory.Other, "device.generic")]
    [InlineData(DeviceCategory.Unknown, "device.generic")]
    public void Category_icon_keys(DeviceCategory category, string key)
    {
        Assert.Equal(key, DeviceCategoryInfo.ToIconKey(category));
        Assert.Equal(category.ToString(), DeviceCategoryInfo.ToSdk(category).ToString());
    }

    [Fact]
    public async Task Fake_server_has_a_realistic_certificate_and_category_mix()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        IReadOnlyList<Device> devices = await api.ListDevicesAsync(CancellationToken.None);
        DateTime now = DateTime.UtcNow;

        var trust = devices.Select(d => CertificateDisplay.Trust(d.CertTrust, d.CertNotAfter?.ToDateTime(), now).Text).ToList();
        Assert.Contains("Trusted", trust);
        Assert.Contains("Self-signed", trust);
        Assert.Contains("Untrusted", trust);
        Assert.Contains("Expired", trust);
        Assert.Contains("", trust); // HTTP only
        var expiry = devices.Select(d => CertificateDisplay.ExpiryText(d.CertNotAfter?.ToDateTime(), now)).ToList();
        Assert.Contains("300 days", expiry);
        Assert.Contains("12 days", expiry);
        Assert.Contains("Expired 3 days ago", expiry);

        var categories = devices.Select(d => d.Category).ToList();
        Assert.Contains(DeviceCategory.Camera, categories);
        Assert.Contains(DeviceCategory.Speaker, categories);
        Assert.Contains(DeviceCategory.Radar, categories);
        Assert.Contains(DeviceCategory.IoModule, categories);
    }
}
