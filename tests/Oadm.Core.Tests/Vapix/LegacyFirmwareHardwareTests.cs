using System.Net;

using Oadm.Core.Devices;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// Read-only checks against the dev-cameras.yaml devices marked <c>legacy: true</c> (verified with an AXIS A1001 1.65.6
/// and an AXIS Q2901-E 6.50.5.18, both over HTTP): the Axis check and the device information come from param.cgi.
/// NEVER add write calls here (loan devices).
/// </summary>
[Trait("Category", "Hardware")]
public class LegacyFirmwareHardwareTests
{
    private static IReadOnlyList<DevCamera> Legacy() => DevCameras.Legacy;

    [HardwareFact]
    public async Task TheProbeFindsTheSerialInTheLoginRealm()
    {
        Assert.NotEmpty(Legacy());
        foreach (var camera in Legacy())
        {
            var result = await new VapixProbe(TimeSpan.FromSeconds(10)).ProbeAsync(camera.Address, CancellationToken.None);

            Assert.True(result is not null, $"probe of {camera} returned nothing");
            Assert.Matches("^[0-9A-F]{12}$", result.Serial);
            Assert.Equal(DeviceStatus.CredentialsRequired, result.Status);
            Assert.False(result.IsFactoryDefault);
        }
    }

    [HardwareFact]
    public async Task TheDeviceInformationComesFromTheParameters()
    {
        foreach (var camera in Legacy())
        {
            using var anonymous = VapixClient.Create(new VapixConnectionOptions { Address = camera.Address, Scheme = Uri.UriSchemeHttp, Timeout = TimeSpan.FromSeconds(10) });
            var refused = await Assert.ThrowsAsync<VapixAuthenticationException>(() => anonymous.GetUnrestrictedPropertiesAsync(CancellationToken.None));
            Assert.True(anonymous.UsesDeviceInfoParameters);

            using var client = VapixClient.Create(new VapixConnectionOptions
            {
                Address = camera.Address,
                Scheme = Uri.UriSchemeHttp,
                Credentials = new NetworkCredential(camera.User, camera.Password),
                Timeout = TimeSpan.FromSeconds(10),
            });
            var info = await client.GetBasicDeviceInfoAsync(CancellationToken.None);

            Assert.True(client.UsesDeviceInfoParameters, camera.ToString());
            Assert.Equal(refused.RealmSerial, info.SerialNumber);
            Assert.Contains(info.ProdNbr, camera.Note, StringComparison.Ordinal);
            Assert.Contains(info.Version, camera.Note, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(info.ProdType));
            Assert.NotEqual(DeviceCategory.Unknown, DeviceCategoryMapper.Map(info.ProdType));

            // The second read goes to the parameters at once (one request per poll).
            var again = await client.GetBasicDeviceInfoAsync(CancellationToken.None);
            Assert.Equal(info, again);
        }
    }
}
