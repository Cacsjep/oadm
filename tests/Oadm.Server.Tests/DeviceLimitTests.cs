using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The device limit (<see cref="DeviceLimits.MaxDevices"/>, lowered for the tests): an add that would go over it is
/// refused whole on the add page and one by one in the automatic add; nothing of a refused add is stored.
/// </summary>
public sealed class DeviceLimitTests
{
    private const string Password = "s3cret-Pass";

    private static FakeAxisNetwork Network(int count)
    {
        var network = new FakeAxisNetwork();
        for (var i = 1; i <= count; i++)
        {
            network.Add($"10.9.2.{i}", FakeSerials.Make(i), Password);
        }

        return network;
    }

    private static async Task<Proto.CommitReply> CommitAsync(TestServerHost host, string from, string to)
    {
        var (session, devices) = await TestHelpers.ScanAsync(host, from, to);
        var commit = new Proto.CommitRequest
        {
            SessionId = session,
            Credentials = { new Proto.DeviceCredentials { UserName = "root", Password = Password } },
        };
        commit.DiscoveredIds.AddRange(devices.Select(d => d.DiscoveredId));
        return await host.AddDevices.CommitAsync(commit);
    }

    [Fact]
    public void TheLimitIsThirtyThousandDevices()
    {
        Assert.Equal(30_000, DeviceLimits.MaxDevices);
        Assert.Equal("OADM manages at most 30,000 devices. Remove devices first, or add fewer.", DeviceLimits.TooManyDevicesText);
    }

    [Fact]
    public async Task AnAddUpToTheLimitIsStoredAndOneOverItIsRefusedWhole()
    {
        await using var host = await TestServerHost.StartAsync(Network(5));
        var devices = host.Get<DeviceRepository>();
        devices.MaxDevices = 3;

        var reply = await CommitAsync(host, "10.9.2.1", "10.9.2.3");
        Assert.Equal(3, reply.DeviceIds.Count);

        var ex = await Assert.ThrowsAsync<RpcException>(() => CommitAsync(host, "10.9.2.4", "10.9.2.5"));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Equal(DeviceLimits.TooManyDevicesText, ex.Status.Detail);
        Assert.Equal(3, await devices.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DevicesThatAreManagedAlreadyDoNotCountAsNew()
    {
        await using var host = await TestServerHost.StartAsync(Network(3));
        var devices = host.Get<DeviceRepository>();
        devices.MaxDevices = 3;
        await CommitAsync(host, "10.9.2.1", "10.9.2.2");

        // Two found again (managed) and one new: 3, at the limit.
        var reply = await CommitAsync(host, "10.9.2.1", "10.9.2.3");
        Assert.Single(reply.DeviceIds);
        Assert.Equal(3, await devices.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheRepositoryRefusesTheAddOverTheLimitForEveryCaller()
    {
        await using var host = await TestServerHost.StartAsync(Network(0));
        var devices = host.Get<DeviceRepository>();
        devices.MaxDevices = 2;
        await devices.AddAsync(new Device { Serial = FakeSerials.Make(1), Address = "10.9.2.1" }, CancellationToken.None);
        await devices.AddAsync(new Device { Serial = FakeSerials.Make(2), Address = "10.9.2.2" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeviceLimitException>(() =>
            devices.AddAsync(new Device { Serial = FakeSerials.Make(3), Address = "10.9.2.3" }, CancellationToken.None));
        Assert.Equal(DeviceLimits.TooManyDevicesText, ex.Message);
        Assert.Equal(2, await devices.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentAddsNeverPassTheLimitTogether()
    {
        await using var host = await TestServerHost.StartAsync(Network(0));
        var devices = host.Get<DeviceRepository>();
        devices.MaxDevices = 5;

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(async i =>
        {
            try
            {
                await devices.AddAsync(new Device { Serial = FakeSerials.Make(i), Address = $"10.9.3.{i}" }, CancellationToken.None);
                return true;
            }
            catch (DeviceLimitException)
            {
                return false;
            }
        }));

        Assert.Equal(5, results.Count(r => r));
        Assert.Equal(5, await devices.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheAutomaticAddStopsAtTheLimit()
    {
        var network = Network(2);
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        var devices = host.Get<DeviceRepository>();
        devices.MaxDevices = 1;
        var autoAdd = host.Get<IDeviceAutoAdd>();

        Assert.True((await autoAdd.AddAsync("10.9.2.1", FakeSerials.Make(1), "DHCP server", CancellationToken.None)).IsAdded);
        var refused = await autoAdd.AddAsync("10.9.2.2", FakeSerials.Make(2), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.LimitReached, refused.Result);
        Assert.False(refused.IsAdded);
        Assert.Equal(DeviceLimits.TooManyDevicesText, refused.Message);
        Assert.Null(await devices.FindBySerialAsync(FakeSerials.Make(2), CancellationToken.None));
    }
}
