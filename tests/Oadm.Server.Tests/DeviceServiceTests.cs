using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Vapix;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

public sealed class DeviceServiceTests
{
    internal static async Task<Device> AddDeviceAsync(TestServerHost host, string address, int serial, string? password = "pw", SdkDeviceStatus status = SdkDeviceStatus.Ok)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = DeviceScheme.Http, Status = status },
            CancellationToken.None);
        if (password is not null)
        {
            await host.Get<CredentialStore>().SetAsync(device.Id, "root", password, CancellationToken.None);
        }

        return device;
    }

    [Fact]
    public async Task ListReturnsDevicesWithCredentialFlag()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await AddDeviceAsync(host, "10.9.0.2", 2, password: null);

        var list = await host.Devices.ListAsync(new Proto.Empty());

        Assert.Equal(2, list.Devices.Count);
        Assert.True(list.Devices.Single(d => d.Id == a.Id.ToString()).HasCredentials);
        Assert.False(list.Devices.Single(d => d.Id == b.Id.ToString()).HasCredentials);
        Assert.Equal(Proto.DeviceStatus.Ok, list.Devices.Single(d => d.Id == a.Id.ToString()).Status);
    }

    [Fact]
    public async Task WatchStartsWithSnapshotThenStreamsChanges()
    {
        await using var host = await TestServerHost.StartAsync();
        var existing = await AddDeviceAsync(host, "10.9.0.1", 1);

        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var call = host.Devices.Watch(new Proto.Empty(), cancellationToken: cts.Token);
        var stream = call.ResponseStream;

        Assert.True(await stream.MoveNext(cts.Token));
        Assert.Equal(Proto.DeviceChanged.Types.Kind.Added, stream.Current.Kind);
        Assert.Equal(existing.Id.ToString(), stream.Current.Device.Id);
        Assert.True(stream.Current.Device.HasCredentials);

        var added = await AddDeviceAsync(host, "10.9.0.2", 2, password: null);
        Assert.True(await stream.MoveNext(cts.Token));
        Assert.Equal(Proto.DeviceChanged.Types.Kind.Added, stream.Current.Kind);
        Assert.Equal(added.Id.ToString(), stream.Current.Device.Id);

        await host.Devices.SetCredentialsAsync(new Proto.SetCredentialsRequest { DeviceIds = { added.Id.ToString() }, UserName = "root", Password = "x" });
        Assert.True(await stream.MoveNext(cts.Token));
        Assert.Equal(Proto.DeviceChanged.Types.Kind.Updated, stream.Current.Kind);
        Assert.True(stream.Current.Device.HasCredentials);

        await host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { existing.Id.ToString() } });
        while (await stream.MoveNext(cts.Token) && stream.Current.Kind != Proto.DeviceChanged.Types.Kind.Removed)
        {
            // Skip updates from the queued refresh.
        }

        Assert.Equal(existing.Id.ToString(), stream.Current.Device.Id);
        Assert.Single((await host.Devices.ListAsync(new Proto.Empty())).Devices);
    }

    [Fact]
    public async Task SetCredentialsReplacesTheCachedClientAndRefreshes()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "right");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddDeviceAsync(host, "10.9.0.1", 1, password: "wrong", status: SdkDeviceStatus.Unknown);

        var polled = await host.Get<DevicePollingService>().PollAsync(device.Id, CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.CredentialsRequired, polled!.Status);

        await host.Devices.SetCredentialsAsync(new Proto.SetCredentialsRequest { DeviceIds = { device.Id.ToString() }, UserName = "root", Password = "right" });

        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())) is { Status: Proto.DeviceStatus.Ok, DhcpEnabled: true },
            "refresh after new credentials");
    }

    [Fact]
    public async Task GetWebUiUrlAndErrors()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await AddDeviceAsync(host, "10.9.0.1", 1);

        var url = await host.Devices.GetWebUiUrlAsync(new Proto.DeviceId { Id = device.Id.ToString() });
        Assert.Equal("http://10.9.0.1/", url.Url);

        var notFound = await Assert.ThrowsAsync<RpcException>(() => host.Devices.GetWebUiUrlAsync(new Proto.DeviceId { Id = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, notFound.StatusCode);

        var invalid = await Assert.ThrowsAsync<RpcException>(() => host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { "nope" } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);

        var noUser = await Assert.ThrowsAsync<RpcException>(() => host.Devices.SetCredentialsAsync(new Proto.SetCredentialsRequest { DeviceIds = { device.Id.ToString() } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, noUser.StatusCode);
    }

    [Fact]
    public async Task RemovingADeviceDisposesItsCachedClient()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddDeviceAsync(host, "10.9.0.1", 1);
        var factory = host.Get<VapixClientFactory>();

        var first = await factory.GetClientAsync(device.Id, CancellationToken.None);
        Assert.Same(first, await factory.GetClientAsync(device.Id, CancellationToken.None));
        Assert.Equal(1, factory.CachedCount);

        await host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } });
        Assert.Equal(0, factory.CachedCount);
    }

    [Fact]
    public async Task PollingUpdatesStatusAndFollowsTheIntervalSetting()
    {
        var network = new FakeAxisNetwork();
        var fake = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddDeviceAsync(host, "10.9.0.1", 1, status: SdkDeviceStatus.Ok);
        var polling = host.Get<DevicePollingService>();

        await polling.PollAllAsync(CancellationToken.None);
        var ok = await TestHelpers.GetDeviceAsync(host, device.Id.ToString());
        Assert.Equal(Proto.DeviceStatus.Ok, ok.Status);
        Assert.Equal("12.6.85", ok.FirmwareVersion);
        Assert.NotNull(ok.LastSeen);
        Assert.False(ok.HasDhcpEnabled); // the light poll does not read network parameters

        fake.Firmware = "12.7.0";
        fake.Offline = true;
        await polling.PollAllAsync(CancellationToken.None);
        Assert.Equal(Proto.DeviceStatus.Unreachable, (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).Status);

        fake.Offline = false;
        await polling.PollAllAsync(CancellationToken.None);
        var back = await TestHelpers.GetDeviceAsync(host, device.Id.ToString());
        Assert.Equal(Proto.DeviceStatus.Ok, back.Status);
        Assert.Equal("12.7.0", back.FirmwareVersion);

        // Back online: a full refresh is queued right away and fills the network columns.
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).HasDhcpEnabled,
            "full refresh after coming back online");

        await host.Settings.SetAsync(new Proto.ServerSettings { PollingIntervalSeconds = 7 });
        Assert.Equal(7, polling.IntervalSeconds);
        await host.Get<ServerSettingsStore>().ResetAsync(SettingKeys.PollingIntervalSeconds, CancellationToken.None);
        Assert.Equal(60, polling.IntervalSeconds);
    }

    [Fact]
    public async Task RefreshQueuesAFullRefresh()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddDeviceAsync(host, "10.9.0.1", 1, status: SdkDeviceStatus.Unknown);

        await host.Devices.RefreshAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } });

        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())) is { Status: Proto.DeviceStatus.Ok, HasDhcpEnabled: true, UpnpFriendlyName.Length: > 0 },
            "full refresh");
        var refreshed = await TestHelpers.GetDeviceAsync(host, device.Id.ToString());
        Assert.Equal((await host.Settings.GetAsync(new Proto.Empty())).ServerName, refreshed.ServerName);
    }

    [Fact]
    public async Task FactoryDefaultDeviceWithoutPasswordPollsAsPasswordNotSet()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), password: null);
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddDeviceAsync(host, "10.9.0.1", 1, password: null, status: SdkDeviceStatus.Unknown);

        var polled = await host.Get<DevicePollingService>().PollAsync(device.Id, CancellationToken.None);

        Assert.Equal(SdkDeviceStatus.PasswordNotSet, polled!.Status);
    }
}
