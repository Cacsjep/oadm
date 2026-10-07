using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// Runs the real server in-process (temp data folder) and adds the first dev camera through
/// DiscoveryService + AddDevicesService. Read-only for the camera: no initial password is ever
/// sent (the test aborts if the camera is factory default) and no task runs.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class AddRealCameraTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task AddCameraFromRangeScanFillsTheDeviceRow()
    {
        var camera = Camera;
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);

        var (session, discovered) = await TestHelpers.ScanAsync(host, camera.Address, camera.Address);
        var found = Assert.Single(discovered);
        Assert.Equal(camera.Address, found.Address);
        Assert.Matches("^[0-9A-F]{12}$", found.Serial);
        Assert.NotEqual(Proto.DeviceStatus.PasswordNotSet, found.Status);

        var plan = await host.AddDevices.PrepareAsync(new Proto.PrepareRequest { SessionId = session, DiscoveredIds = { found.DiscoveredId } });
        var item = Assert.Single(plan.Items);
        Assert.False(item.NeedsInitialPassword, "Camera is factory default; this test never sets a password.");
        Assert.True(item.NeedsCredentials);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { found.DiscoveredId },
            Credentials = { new Proto.DeviceCredentials { UserName = camera.User, Password = camera.Password } },
        });
        var deviceId = Assert.Single(reply.DeviceIds);

        Assert.Equal(string.Empty, reply.TaskId); // adding devices is not a task
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, deviceId)).HasDhcpEnabled,
            "first full refresh after add",
            TimeSpan.FromSeconds(60));
        Assert.Empty((await host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);

        var device = await TestHelpers.GetDeviceAsync(host, deviceId);
        Assert.Equal(Proto.DeviceStatus.Ok, device.Status);
        Assert.Equal(found.Serial, device.Serial);
        Assert.False(string.IsNullOrEmpty(device.Model));
        Assert.False(string.IsNullOrEmpty(device.FirmwareVersion));
        Assert.True(device.HasDhcpEnabled, "DHCP column empty");
        Assert.True(device.HasHttpsEnabled, "HTTPS column empty");
        Assert.True(device.HasDot1XEnabled, "802.1X column empty");
        Assert.False(string.IsNullOrEmpty(device.UpnpFriendlyName));
        Assert.False(string.IsNullOrEmpty(device.ServerName));
        Assert.True(device.HasCredentials);
        Assert.NotNull(device.LastSeen);
        Assert.Equal(camera.EffectiveScheme, device.Scheme);

        var row = await host.Get<Core.Devices.DeviceRepository>().GetAsync(Guid.Parse(deviceId), CancellationToken.None);
        if (device.Scheme == "https")
        {
            Assert.Matches("^[0-9A-F]{64}$", row!.CertFingerprintSha256);
            Assert.NotNull(device.CertNotAfter);
            Assert.NotEqual(Proto.CertificateTrust.Unknown, device.CertTrust);
            Assert.False(string.IsNullOrEmpty(device.CertIssuer));
        }

        Assert.False(string.IsNullOrEmpty(device.ProductType));
        Assert.NotEqual(Proto.DeviceCategory.Unknown, device.Category);

        // The status poll keeps it Ok with the pinned certificate.
        await host.Get<Core.Devices.DevicePollingService>().PollAllAsync(CancellationToken.None);
        Assert.Equal(Proto.DeviceStatus.Ok, (await TestHelpers.GetDeviceAsync(host, deviceId)).Status);

        var url = await host.Devices.GetWebUiUrlAsync(new Proto.DeviceId { Id = deviceId });
        Assert.StartsWith(device.Scheme + "://" + camera.Address, url.Url, StringComparison.Ordinal);
    }
}
