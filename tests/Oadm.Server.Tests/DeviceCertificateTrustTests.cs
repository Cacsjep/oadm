using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// "Trust new certificate" (<c>DeviceService.GetPresentedCertificates</c>, <c>TrustNewCertificates</c>): a device with
/// Certificate changed gets the confirmed certificate pinned and keeps its record. Fake devices only; the fake "presents"
/// a real self-signed certificate to the connection's pinning.
/// </summary>
public sealed class DeviceCertificateTrustTests
{
    private const string Address = "10.0.8.40";

    private static X509Certificate2 Certificate(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>A managed HTTPS device that pinned <paramref name="old"/> and now presents <paramref name="served"/>.</summary>
    private static async Task<(Device Device, FakeAxisDevice Fake)> ChangedAsync(TestServerHost host, int serial, X509Certificate2 old, X509Certificate2 served)
    {
        var fake = host.Network.Add(Address, FakeSerials.Make(serial), "pw");
        fake.ServesHttps = true;
        fake.Certificate = served;
        var device = await DeviceServiceTests.AddDeviceAsync(host, Address, serial, status: DeviceStatus.CertificateChanged);
        await host.Get<DeviceRepository>().UpdateAsync(
            device.Id,
            d =>
            {
                d.Scheme = DeviceScheme.Https;
                d.CertFingerprintSha256 = Fingerprint(old);
                d.Tags = ["lobby"];
            },
            CancellationToken.None);
        return (device, fake);
    }

    [Fact]
    public async Task ThePresentedCertificateIsReadWithoutSendingAnythingToTheDevice()
    {
        await using var host = await TestServerHost.StartAsync();
        using var old = Certificate("old");
        using var served = Certificate("new");
        var (device, fake) = await ChangedAsync(host, 841, old, served);

        var reply = await host.Devices.GetPresentedCertificatesAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } });

        var presented = Assert.Single(reply.Certificates);
        Assert.Equal(device.Id.ToString(), presented.DeviceId);
        Assert.Equal(Fingerprint(served), presented.Fingerprint);
        Assert.Equal(Fingerprint(old), presented.PinnedFingerprint);
        Assert.Equal("CN=new", presented.Subject);
        Assert.Equal(Proto.CertificateTrust.SelfSigned, presented.Trust);
        Assert.Empty(presented.Message);
        Assert.Equal(0, fake.Requests); // the handshake failed on the old pin: no request, no login
        Assert.Empty(fake.CredentialRequests);
    }

    [Fact]
    public async Task TrustingTheShownCertificatePinsItAndKeepsTheDevice()
    {
        await using var host = await TestServerHost.StartAsync();
        using var old = Certificate("old");
        using var served = Certificate("new");
        var (device, _) = await ChangedAsync(host, 842, old, served);

        var reply = await host.Devices.TrustNewCertificatesAsync(new Proto.TrustNewCertificatesRequest
        {
            Devices = { new Proto.TrustNewCertificate { DeviceId = device.Id.ToString(), Fingerprint = Fingerprint(served) } },
        });

        var result = Assert.Single(reply.Results);
        Assert.True(result.Ok, result.Message);
        var stored = (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(Fingerprint(served), stored.CertFingerprintSha256);
        Assert.NotEqual(DeviceStatus.CertificateChanged, stored.Status);
        Assert.Equal(["lobby"], stored.Tags);
        var audit = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Contains(audit.Entries, e => e.Action == Oadm.Core.Auth.AuditActions.DeviceCertificateTrusted && e.Detail == "1 trusted");
    }

    [Fact]
    public async Task AnotherCertificateThanShownLeavesTheDeviceUnchanged()
    {
        await using var host = await TestServerHost.StartAsync();
        using var old = Certificate("old");
        using var shown = Certificate("shown");
        using var served = Certificate("swapped");
        var (device, _) = await ChangedAsync(host, 843, old, served);

        var reply = await host.Devices.TrustNewCertificatesAsync(new Proto.TrustNewCertificatesRequest
        {
            Devices = { new Proto.TrustNewCertificate { DeviceId = device.Id.ToString(), Fingerprint = Fingerprint(shown) } },
        });

        var result = Assert.Single(reply.Results);
        Assert.False(result.Ok);
        Assert.Equal(DeviceCertificateTrustService.OtherCertificateMessage, result.Message);
        var stored = (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(Fingerprint(old), stored.CertFingerprintSha256);
        Assert.Equal(DeviceStatus.CertificateChanged, stored.Status);
    }

    [Fact]
    public async Task ADeviceWithoutCertificateChangedIsRefused()
    {
        await using var host = await TestServerHost.StartAsync();
        using var old = Certificate("old");
        using var served = Certificate("new");
        var (device, _) = await ChangedAsync(host, 844, old, served);
        await host.Get<DeviceRepository>().UpdateAsync(device.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);

        var read = await host.Devices.GetPresentedCertificatesAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } });
        var trust = await host.Devices.TrustNewCertificatesAsync(new Proto.TrustNewCertificatesRequest
        {
            Devices = { new Proto.TrustNewCertificate { DeviceId = device.Id.ToString(), Fingerprint = Fingerprint(served) } },
        });

        Assert.Equal(DeviceCertificateTrustService.NotChangedMessage, Assert.Single(read.Certificates).Message);
        Assert.Equal(DeviceCertificateTrustService.NotChangedMessage, Assert.Single(trust.Results).Message);
        Assert.Equal(Fingerprint(old), (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.CertFingerprintSha256);
    }
}
