using System.Net;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// Read-only checks against the cameras in dev-cameras.yaml. NEVER add write calls here
/// (no restart, no password, no parameter updates) without an explicit milestone decision.
/// </summary>
[Trait("Category", "Hardware")]
public class VapixHardwareTests
{
    private static VapixClient Authenticated(DevCamera camera, string? scheme = null, string? pinnedFingerprint = null)
    {
        return VapixClient.Create(new VapixConnectionOptions
        {
            Address = camera.Address,
            Scheme = scheme ?? camera.EffectiveScheme,
            Credentials = new NetworkCredential(camera.User, camera.Password),
            PinnedCertificateFingerprint = pinnedFingerprint,
            Timeout = TimeSpan.FromSeconds(10),
        });
    }

    [HardwareFact]
    public async Task ProbeFindsSerialAnonymously()
    {
        foreach (var camera in DevCameras.All)
        {
            var result = await new VapixProbe(TimeSpan.FromSeconds(5)).ProbeAsync(camera.Address, CancellationToken.None);

            Assert.True(result is not null, $"probe of {camera} returned nothing");
            Assert.Matches("^[0-9A-F]{12}$", result.Serial);
            Assert.False(result.IsFactoryDefault);
            Assert.True(result.AuthenticationRequired);
            Assert.Equal(DeviceStatus.CredentialsRequired, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Model));
            if (result.Scheme == "https")
            {
                Assert.Matches("^[0-9A-F]{64}$", result.CertificateFingerprint ?? string.Empty);
            }

            using var client = Authenticated(camera, result.Scheme);
            var info = await client.GetBasicDeviceInfoAsync(CancellationToken.None);
            Assert.Equal(info.SerialNumber, result.Serial);
            Assert.Equal(info.ProdNbr, result.Model);
        }
    }

    [HardwareFact]
    public async Task ReadsBasicDeviceInfoNetworkInfoAndParameters()
    {
        foreach (var camera in DevCameras.All)
        {
            using var client = Authenticated(camera);

            var info = await client.GetBasicDeviceInfoAsync(CancellationToken.None);
            Assert.Matches("^[0-9A-F]{12}$", info.SerialNumber);
            Assert.False(string.IsNullOrEmpty(info.ProdNbr));
            Assert.False(string.IsNullOrEmpty(info.Version));

            var network = await client.GetNetworkInfoAsync(CancellationToken.None);
            Assert.NotNull(network.DhcpEnabled);
            Assert.NotNull(network.HttpsEnabled);
            Assert.NotNull(network.Dot1xEnabled);
            Assert.False(string.IsNullOrEmpty(network.UpnpFriendlyName));

            var p = await client.ListParametersAsync(["Network.eth0.MACAddress"], CancellationToken.None);
            Assert.Equal(info.SerialNumber, VapixParsers.NormalizeSerial(p["Network.eth0.MACAddress"]));

            var ready = await client.GetSystemReadyAsync(CancellationToken.None);
            Assert.True(ready.SystemReady);
            Assert.False(ready.NeedSetup);
        }
    }

    [HardwareFact]
    public async Task DigestAuthWorksOverPlainHttp()
    {
        foreach (var camera in DevCameras.All)
        {
            using var client = Authenticated(camera, Uri.UriSchemeHttp);
            var info = await client.GetBasicDeviceInfoAsync(CancellationToken.None);
            Assert.Matches("^[0-9A-F]{12}$", info.SerialNumber);
        }
    }

    [HardwareFact]
    public async Task CertificatePinningAcceptsSameAndRejectsDifferentFingerprint()
    {
        foreach (var camera in DevCameras.All.Where(c => c.EffectiveScheme == Uri.UriSchemeHttps))
        {
            string fingerprint;
            using (var first = Authenticated(camera))
            {
                _ = await first.GetBasicDeviceInfoAsync(CancellationToken.None);
                fingerprint = first.CertificateFingerprint!;
                Assert.NotNull(fingerprint);
            }

            using (var pinned = Authenticated(camera, pinnedFingerprint: fingerprint))
            {
                _ = await pinned.GetBasicDeviceInfoAsync(CancellationToken.None);
            }

            var wrong = new string('0', 64);
            using var mismatched = Authenticated(camera, pinnedFingerprint: wrong);
            var ex = await Assert.ThrowsAsync<CertificateChangedException>(() => mismatched.GetBasicDeviceInfoAsync(CancellationToken.None));
            Assert.Equal(wrong, ex.ExpectedFingerprint);
            Assert.Equal(fingerprint, ex.ActualFingerprint);
            Assert.Equal(DeviceStatus.CertificateChanged, DeviceStatusClassifier.FromException(ex));
        }
    }

    [HardwareFact]
    public async Task AnonymousAuthenticatedCallIsCredentialsRequired()
    {
        foreach (var camera in DevCameras.All)
        {
            using var anonymous = VapixClient.Create(new VapixConnectionOptions { Address = camera.Address, Scheme = camera.EffectiveScheme });
            var ex = await Assert.ThrowsAsync<VapixAuthenticationException>(() => anonymous.GetBasicDeviceInfoAsync(CancellationToken.None));
            Assert.Equal(DeviceStatus.CredentialsRequired, DeviceStatusClassifier.FromException(ex));
        }
    }

    [HardwareFact]
    public async Task UnusedAddressIsUnreachable()
    {
        // 192.0.2.0/24 is TEST-NET-1: never routed, so the connect times out or fails.
        using var client = VapixClient.Create(new VapixConnectionOptions { Address = "192.0.2.1", Timeout = TimeSpan.FromMilliseconds(800) });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
        Assert.Equal(DeviceStatus.Unreachable, DeviceStatusClassifier.FromException(ex));
        Assert.Null(await new VapixProbe(TimeSpan.FromMilliseconds(800)).ProbeAsync("192.0.2.1", CancellationToken.None));
    }
}
