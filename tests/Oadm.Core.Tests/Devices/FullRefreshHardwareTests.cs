using System.Globalization;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Devices;

/// <summary>
/// Read-only: a full refresh of the real camera through the real VAPIX connector. Reports the
/// certificate the camera presents and its ProdType in the test output.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class FullRefreshHardwareTests(ITestOutputHelper output)
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task FullRefreshCapturesCertificateAndCategory()
    {
        var camera = Camera;
        await using var db = await TestDatabase.CreateAsync();
        var devices = db.Get<DeviceRepository>();
        var probe = await new VapixProbe(TimeSpan.FromSeconds(5)).ProbeAsync(camera.Address, CancellationToken.None);
        Assert.NotNull(probe);
        var device = await devices.AddAsync(
            new Device
            {
                Serial = probe.Serial,
                Address = camera.Address,
                Scheme = probe.Scheme == Uri.UriSchemeHttp ? DeviceScheme.Http : DeviceScheme.Https,
                CertFingerprintSha256 = probe.CertificateFingerprint,
            },
            CancellationToken.None);
        await db.Get<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
        using var factory = new VapixClientFactory(devices, db.Get<CredentialStore>());
        using var polling = new DevicePollingService(devices, factory, db.Get<ServerSettingsStore>());

        var row = await polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.NotNull(row);
        Assert.Equal(DeviceStatus.Ok, row.Status);
        output.WriteLine($"ProdType (probe, anonymous): {probe.ProductType}");
        output.WriteLine($"ProdType (full refresh): {row.ProductType} -> {row.Category}, HasVideo={row.HasVideo}");
        Assert.False(string.IsNullOrEmpty(row.ProductType));
        Assert.Equal(row.ProductType, probe.ProductType);
        Assert.NotEqual(DeviceCategory.Unknown, row.Category);
        Assert.NotEmpty(row.Apis); // apidiscovery list stored by the full refresh
        Assert.NotEmpty((await devices.GetAsync(device.Id, CancellationToken.None))!.Apis);
        output.WriteLine($"APIs: {row.Apis.Count}, e.g. {string.Join(", ", row.Apis.Take(5).Select(a => a.Id + " " + a.Version))}");

        if (row.Scheme == DeviceScheme.Https)
        {
            Assert.NotNull(row.CertNotAfterUtc);
            Assert.Contains(row.CertTrust, new[] { CertificateTrust.Trusted, CertificateTrust.SelfSigned, CertificateTrust.Untrusted, CertificateTrust.Expired });
            var daysLeft = (row.CertNotAfterUtc!.Value - DateTime.UtcNow).TotalDays;
            output.WriteLine($"Certificate trust: {row.CertTrust}");
            output.WriteLine($"Subject: {row.CertSubject}");
            output.WriteLine($"Issuer: {row.CertIssuer}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"NotAfter: {row.CertNotAfterUtc:yyyy-MM-dd HH:mm} UTC ({daysLeft:0} days left)"));
            output.WriteLine($"Name matches {camera.Address}: {row.CertNameMatches}");
        }
        else
        {
            Assert.Equal(CertificateTrust.Unknown, row.CertTrust);
        }
    }
}
