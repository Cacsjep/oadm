using Grpc.Core;

using Oadm.Core.Security;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

public sealed class AddDevicesServiceTests
{
    private const string Password = "s3cret-Pass";

    private static FakeAxisNetwork Network()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), Password);
        network.Add("10.9.0.2", FakeSerials.Make(2), "other-password");
        network.Add("10.9.0.3", FakeSerials.Make(3), password: null); // factory default
        return network;
    }

    [Fact]
    public async Task RangeScanFindsDevicesWithStatus()
    {
        await using var host = await TestServerHost.StartAsync(Network());

        var (_, devices) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.6");

        Assert.Equal(["10.9.0.1", "10.9.0.2", "10.9.0.3"], devices.Select(d => d.Address));
        Assert.Equal(FakeSerials.Make(1), devices[0].DiscoveredId);
        Assert.Equal(Proto.DeviceStatus.CredentialsRequired, devices[0].Status);
        Assert.Equal(Proto.DeviceStatus.PasswordNotSet, devices[2].Status);
        Assert.All(devices, d => Assert.Equal(Proto.DiscoverySource.RangeScan, d.Source));
        Assert.All(devices, d => Assert.False(d.AlreadyManaged));
        Assert.Equal("M3106-L Mk II", devices[0].Model);
    }

    [Fact]
    public async Task PrepareClassifiesFactoryDefaultAndConfiguredDevices()
    {
        await using var host = await TestServerHost.StartAsync(Network());
        var (session, devices) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.3");

        var request = new Proto.PrepareRequest { SessionId = session };
        request.DiscoveredIds.AddRange(devices.Select(d => d.DiscoveredId));
        var plan = await host.AddDevices.PrepareAsync(request);

        Assert.Equal(3, plan.Items.Count);
        Assert.True(plan.Items.Single(i => i.Address == "10.9.0.3").NeedsInitialPassword);
        Assert.All(plan.Items.Where(i => i.Address != "10.9.0.3"), i => Assert.True(i.NeedsCredentials));
        Assert.All(plan.Items.Where(i => i.Address != "10.9.0.3"), i => Assert.False(i.NeedsInitialPassword));

        var unknown = new Proto.PrepareRequest { SessionId = session, DiscoveredIds = { "FFFFFFFFFFFF" } };
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.PrepareAsync(unknown).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task CommitAddsDevicesSetsInitialPasswordAndFlagsWrongCredentials()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, devices) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.3");
        await host.Discovery.StopAsync(new Proto.DiscoverySession { SessionId = session }); // the wizard may stop discovery before Finish

        var commit = new Proto.CommitRequest
        {
            SessionId = session,
            InitialRootPassword = "initial-Pass1",
            Credentials = { new Proto.DeviceCredentials { UserName = "root", Password = Password } },
        };
        commit.DiscoveredIds.AddRange(devices.Select(d => d.DiscoveredId));
        var reply = await host.AddDevices.CommitAsync(commit);

        Assert.Equal(3, reply.DeviceIds.Count);
        Assert.Equal(string.Empty, reply.TaskId); // adding devices is not a task

        // Factory-default device got its first password (in the POST body, never in a URL).
        Assert.Equal("initial-Pass1", network["10.9.0.3"].Password);
        Assert.Equal(1, network["10.9.0.3"].PwdgrpCalls);
        Assert.Equal(0, network["10.9.0.3"].PasswordInUrl);

        // The first full refresh runs in the background, without a task in the task list.
        await TestHelpers.WaitUntilAsync(
            async () => (await host.Devices.ListAsync(new Proto.Empty())).Devices
                .Where(d => d.Address is "10.9.0.1" or "10.9.0.3").All(d => d.HasDhcpEnabled),
            "first full refresh after add");
        Assert.Empty((await host.Tasks.ListAsync(new Proto.Empty())).Tasks);

        var list = (await host.Devices.ListAsync(new Proto.Empty())).Devices.ToDictionary(d => d.Address);
        Assert.Equal(3, list.Count);

        var ok = list["10.9.0.1"];
        Assert.Equal(Proto.DeviceStatus.Ok, ok.Status);
        Assert.True(ok.HasCredentials);
        Assert.Equal(FakeSerials.Make(1), ok.Serial);
        Assert.Equal("M3106-L Mk II", ok.Model);
        Assert.Equal("12.6.85", ok.FirmwareVersion);
        Assert.True(ok.DhcpEnabled);
        Assert.True(ok.HttpsEnabled);
        Assert.False(ok.Dot1XEnabled);
        Assert.Equal($"AXIS M3106-L Mk II - {FakeSerials.Make(1)}", ok.UpnpFriendlyName);
        Assert.False(string.IsNullOrEmpty(ok.ServerName));
        Assert.Equal("http", ok.Scheme);
        Assert.NotNull(ok.LastSeen);

        Assert.Equal(Proto.DeviceStatus.CredentialsRequired, list["10.9.0.2"].Status);
        Assert.True(list["10.9.0.2"].HasCredentials);

        var initial = list["10.9.0.3"];
        Assert.Equal(Proto.DeviceStatus.Ok, initial.Status);
        var stored = await host.Get<CredentialStore>().GetAsync(Guid.Parse(initial.Id), CancellationToken.None);
        Assert.Equal("root", stored!.UserName);
        Assert.Equal("initial-Pass1", stored.Password);
        Assert.True(initial.DhcpEnabled);
    }

    [Fact]
    public async Task SkippedPasswordLeavesFactoryDefaultDeviceAndManagedSerialsAreSkipped()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.3");

        var first = new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(3), FakeSerials.Make(1) },
            Credentials =
            {
                new Proto.DeviceCredentials { UserName = "root", Password = "wrong" },
                new Proto.DeviceCredentials { DiscoveredId = FakeSerials.Make(1), UserName = "root", Password = Password },
            },
        };
        var reply = await host.AddDevices.CommitAsync(first);
        Assert.Equal(2, reply.DeviceIds.Count);
        Assert.Null(network["10.9.0.3"].Password);
        Assert.Equal(0, network["10.9.0.3"].PwdgrpCalls);
        await TestHelpers.WaitUntilAsync(
            async () =>
            {
                var all = (await host.Devices.ListAsync(new Proto.Empty())).Devices;
                return all.Any(d => d.Address == "10.9.0.3" && d.Status == Proto.DeviceStatus.PasswordNotSet)
                    && all.Any(d => d.Address == "10.9.0.1" && d.Status == Proto.DeviceStatus.Ok);
            },
            "first full refresh after add");

        var devices = (await host.Devices.ListAsync(new Proto.Empty())).Devices.ToDictionary(d => d.Address);
        Assert.Equal(Proto.DeviceStatus.PasswordNotSet, devices["10.9.0.3"].Status);
        Assert.False(devices["10.9.0.3"].HasCredentials);
        Assert.Equal(Proto.DeviceStatus.Ok, devices["10.9.0.1"].Status); // per-device override won over the default

        // Second commit with the same serials adds nothing; discovery now reports them as managed.
        var again = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { FakeSerials.Make(1) } });
        Assert.Empty(again.DeviceIds);
        Assert.Equal(string.Empty, again.TaskId);

        var (_, rescanned) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.2");
        Assert.True(rescanned.Single(d => d.Address == "10.9.0.1").AlreadyManaged);
        Assert.False(rescanned.Single(d => d.Address == "10.9.0.2").AlreadyManaged);
    }

    [Fact]
    public async Task CommitRejectsInvalidInitialPassword()
    {
        await using var host = await TestServerHost.StartAsync(Network());
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.0.3", "10.9.0.3");

        var ex = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(3) },
            InitialRootPassword = "badépassword",
        }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task CommitTakesUseHostNameFromTheServerSettingNotTheRequest()
    {
        await using var host = await TestServerHost.StartAsync(Network());
        var (session, devices) = await TestHelpers.ScanAsync(host, "10.9.0.1", "10.9.0.2");

        // The request flag is unused: setting false wins over a true request.
        await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, UseHostName = true, DiscoveredIds = { devices[0].DiscoveredId } });
        // Setting true: stored on the device; range scan results have no host name, so the IP stays the address.
        await host.Settings.SetAsync(new Proto.ServerSettings { UseHostName = true });
        await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { devices[1].DiscoveredId } });

        var list = (await host.Devices.ListAsync(new Proto.Empty())).Devices.ToDictionary(d => d.Address);
        Assert.False(list["10.9.0.1"].UseHostName);
        Assert.True(list["10.9.0.2"].UseHostName);
        Assert.True((await host.Settings.GetAsync(new Proto.Empty())).UseHostName);
    }

    [Fact]
    public async Task UseHostNameStoresTheHostNameAsAddress()
    {
        var found = new Core.Discovery.DiscoveredDevice(
            "ACCC8E000001", "ACCC8E000001", System.Net.IPAddress.Parse("10.9.0.1"), "axis-accc8e000001", null, null,
            Core.Discovery.DiscoveredDeviceStatus.CredentialsRequired, "https", Core.Discovery.DiscoverySources.Mdns, DateTimeOffset.UtcNow);

        Assert.Equal(("axis-accc8e000001.local", "axis-accc8e000001.local"), AddDevices.AddDevicesGrpcService.ChooseAddress(found, useHostName: true));
        Assert.Equal(("10.9.0.1", "axis-accc8e000001.local"), AddDevices.AddDevicesGrpcService.ChooseAddress(found, useHostName: false));
        Assert.Equal(("10.9.0.1", null), AddDevices.AddDevicesGrpcService.ChooseAddress(found with { HostName = null }, useHostName: true));
    }
}
