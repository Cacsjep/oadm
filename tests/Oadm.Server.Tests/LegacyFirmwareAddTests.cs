using Oadm.Core.Devices;
using Oadm.Core.Vapix;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// Firmware without basicdeviceinfo (AXIS OS before 8.x, the A1001 door controller line): the Axis check takes the serial
/// from param.cgi's login realm, the login and every later read take the device information from param.cgi. Modeled on
/// an AXIS Q2901-E with 6.50.5.18 and an AXIS A1001 with 1.65.6.
/// </summary>
public sealed class LegacyFirmwareAddTests
{
    private const string Password = "s3cret-Pass";

    private static FakeAxisDevice Legacy(FakeAxisNetwork network, string address, int serial, string model = "A1001", string type = "Network Door Controller")
    {
        var device = network.Add(address, FakeSerials.Make(serial), Password, model);
        device.NoBasicDeviceInfo = true;
        device.Firmware = "1.65.6";
        device.ProductType = type;
        return device;
    }

    [Fact]
    public async Task AScanFindsItLogsInAfterTheAxisCheckAndAddsItWithItsModel()
    {
        var network = new FakeAxisNetwork();
        var controller = Legacy(network, "10.9.3.1", 1);
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.3.1", "10.9.3.1");

        var found = devices["10.9.3.1"];
        Assert.Equal(FakeSerials.Make(1), found.Serial);
        Assert.Equal(Proto.AuthState.Authenticated, found.AuthState);
        // The login only after the anonymous check; HTTP only (the device has no HTTPS).
        Assert.NotEmpty(controller.CredentialRequests);
        Assert.All(controller.CredentialRequests, r => Assert.Equal(("http", "root"), r));
        var connections = network.Connections.Where(c => c.Address == "10.9.3.1").ToList();
        Assert.True(connections.FindIndex(c => c.Credentials is not null) > connections.FindIndex(c => c.Credentials is null && c.Scheme == "http"));

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { found.DiscoveredId } });
        var id = Assert.Single(reply.DeviceIds);

        // The add's verified login already stored the category: no live video offered while the refresh is queued.
        var stored = (await host.Get<DeviceRepository>().GetAsync(Guid.Parse(id), CancellationToken.None))!;
        Assert.Equal(Oadm.Sdk.Devices.DeviceCategory.DoorController, stored.Category);
        Assert.Equal("A1001", stored.Model);
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, id)) is { Status: Proto.DeviceStatus.Ok, FirmwareVersion: "1.65.6" },
            "the added device is polled");
        var device = await TestHelpers.GetDeviceAsync(host, id);
        Assert.Equal("A1001", device.Model);
        Assert.Equal("Network Door Controller", device.ProductType);
        Assert.Equal(Proto.DeviceCategory.DoorController, device.Category);
    }

    [Fact]
    public async Task AddManuallyFindsItAndAWrongPasswordIsRejected()
    {
        var network = new FakeAxisNetwork();
        Legacy(network, "10.9.3.2", 2, "Q2901-E", "Temperature Alarm Camera");
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "wrong-Pass" });

        var session = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.3.2:80" });
        var found = Assert.Single((await TestHelpers.WatchToEndAsync(host, session.SessionId)).Values);
        Assert.Equal(FakeSerials.Make(2), found.Serial);
        Assert.Equal(Proto.AuthState.LoginFailed, found.AuthState);

        var retry = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = session.SessionId, DiscoveredId = found.DiscoveredId, UserName = "root", Password = Password,
        });
        Assert.Equal(Proto.AuthState.Authenticated, retry.AuthState);
    }

    [Fact]
    public async Task AnAddedDeviceIsPolledThroughTheParameters()
    {
        var network = new FakeAxisNetwork();
        var camera = Legacy(network, "10.9.3.3", 3, "Q2901-E", "Temperature Alarm Camera");
        await using var host = await TestServerHost.StartAsync(network);
        var added = await DeviceServiceTests.AddDeviceAsync(host, "10.9.3.3", 3, Password);

        await host.Devices.RefreshAsync(new Proto.DeviceIds { Ids = { added.Id.ToString() } });
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, added.Id.ToString())) is { Status: Proto.DeviceStatus.Ok, Model: "Q2901-E" },
            "the refresh reads the parameters");
        Assert.Equal(Proto.DeviceCategory.Camera, (await TestHelpers.GetDeviceAsync(host, added.Id.ToString())).Category);
        Assert.True((await host.Get<VapixClientFactory>().GetClientAsync(added.Id, CancellationToken.None)).UsesDeviceInfoParameters);

        // A wrong stored password: Credentials required, not an unknown error.
        camera.Password = "changed-Pass";
        await host.Devices.RefreshAsync(new Proto.DeviceIds { Ids = { added.Id.ToString() } });
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, added.Id.ToString())).Status == Proto.DeviceStatus.CredentialsRequired,
            "the refused login shows");
        Assert.NotNull(await host.Get<DeviceRepository>().GetAsync(added.Id, CancellationToken.None));
    }
}
