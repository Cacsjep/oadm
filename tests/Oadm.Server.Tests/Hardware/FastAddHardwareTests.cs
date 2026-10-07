using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// The fast add page against the first dev camera, read-only for the camera: the server (temp data
/// folder) gets the dev-camera credentials in its credential list, range-scans the camera's address,
/// logs in automatically (basicdeviceinfo with the credential) and adds it with the matched
/// credential (stored only in the temp database). No password is ever set and nothing on the
/// camera changes; the test aborts if the camera is factory default.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class FastAddHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task ScanLogsInWithTheCredentialListAndAddsTheCamera()
    {
        var camera = Camera;
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = camera.User, Password = camera.Password });

        var session = await host.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = camera.Address, To = camera.Address });
        var found = Assert.Single((await TestHelpers.WatchToEndAsync(host, session.SessionId, TimeSpan.FromSeconds(90))).Values);

        Assert.NotEqual(Proto.AuthState.PasswordNotSet, found.AuthState); // never set a password on a real camera
        Assert.Equal(Proto.AuthState.Authenticated, found.AuthState);
        Assert.Equal(camera.User, found.AuthUserName);
        Assert.Equal("list:" + Guid.Parse(entry.Id).ToString("N"), found.CredentialId);
        Assert.False(found.AlreadyManaged);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { found.DiscoveredId } });
        var result = Assert.Single(reply.Results);
        Assert.Equal(Proto.DeviceStatus.Ok, result.Status);
        var device = await TestHelpers.GetDeviceAsync(host, result.DeviceId);
        Assert.True(device.HasCredentials);
        Assert.Equal(found.Serial, device.Serial);
    }
}
