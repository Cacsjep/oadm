using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Oadm.Core.Devices;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// OADM follows a re-addressed device: <see cref="DeviceAddressService"/> through a task (engine + SDK context) and
/// directly, and the periodic mDNS re-find (<see cref="DeviceRelocationHostedService"/>). Fake devices only.
/// </summary>
public sealed class DeviceAddressTests
{
    private const string Old = "10.0.7.5";
    private const string New = "10.0.7.60";

    /// <summary>The same fake device answering at another address (the old one goes offline).</summary>
    private static FakeAxisDevice Move(TestServerHost host, int serial, string to)
    {
        host.Network[Old].Offline = true;
        return host.Network.Add(to, FakeSerials.Make(serial), "pw");
    }

    [Fact]
    public async Task UpdateMovesTheRecordAndKeepsCredentialsAndPin()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(Old, FakeSerials.Make(701), "pw");
        var device = await DeviceServiceTests.AddDeviceAsync(host, Old, 701);
        await host.Get<DeviceRepository>().UpdateAsync(device.Id, d => d.CertFingerprintSha256 = "AB12", CancellationToken.None);
        Move(host, 701, New);
        var changes = new List<DeviceChange>();
        host.Get<DeviceRepository>().Changes.Changed += (_, c) => changes.Add(c);

        var result = await host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, New, CancellationToken.None);

        Assert.Equal(DeviceAddressChangeResult.Updated, result);
        var stored = await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(New, stored!.Address);
        Assert.Equal("AB12", stored.CertFingerprintSha256);
        Assert.Equal("root", (await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.UserName);
        Assert.Contains(changes, c => c.DeviceId == device.Id && c.Kind == DeviceChangeKind.Updated);
        Assert.Equal(DeviceAddressChangeResult.Unchanged, await host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, New, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateRefusesAnotherSerialAndLeavesTheRecord()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(Old, FakeSerials.Make(702), "pw");
        host.Network.Add(New, FakeSerials.Make(799), "pw"); // a different device has the new address
        var device = await DeviceServiceTests.AddDeviceAsync(host, Old, 702);

        var ex = await Assert.ThrowsAsync<DeviceIdentityException>(() =>
            host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, New, CancellationToken.None));

        Assert.Contains($"serial number {FakeSerials.Make(799)}, not {FakeSerials.Make(702)}", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Old, (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Address);

        // Nobody at the address: refused as well.
        await Assert.ThrowsAsync<DeviceIdentityException>(() =>
            host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, "10.0.7.99", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, "camera.example.com", CancellationToken.None));
    }

    [Fact]
    public async Task HostNameRecordsAreKept()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(New, FakeSerials.Make(703), "pw");
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(703), Address = "axis-cam.example.com", UseHostName = true, Scheme = DeviceScheme.Http },
            CancellationToken.None);

        var result = await host.Get<DeviceAddressService>().UpdateAddressAsync(device.Id, New, CancellationToken.None);

        Assert.Equal(DeviceAddressChangeResult.KeptHostName, result);
        Assert.Equal("axis-cam.example.com", (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Address);
    }

    [Fact]
    public async Task ATaskFollowsTheDeviceThroughTheSdkContext()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(Old, FakeSerials.Make(704), "pw");
        var device = await DeviceServiceTests.AddDeviceAsync(host, Old, 704);
        var plugin = new MovePlugin(New);
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("test.move", "1.0.0", null)));
        Move(host, 704, New);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = MovePlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);

        Assert.Equal(Proto.TaskState.Done, task.State);
        Assert.Equal(FakeSerials.Make(704), plugin.SerialAtNewAddress);
        Assert.True(plugin.Moved);
        Assert.Equal(FakeSerials.Make(704), plugin.SerialThroughContext); // ctx.Vapix now talks to the new address
        Assert.Equal(New, (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).Address);
        var log = await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = run.TaskIds[0] });
        Assert.Contains(log.Entries, e => e.Message.Contains($"OADM device address changed to {New}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelocationMovesOnlyUnreachableDevicesThatAnswerWithTheirSerial()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(Old, FakeSerials.Make(705), "pw");
        var unreachable = await DeviceServiceTests.AddDeviceAsync(host, Old, 705, status: DeviceStatus.Unreachable);
        var reachable = await DeviceServiceTests.AddDeviceAsync(host, "10.0.7.6", 706);
        Move(host, 705, New);
        host.Network.Add("10.0.7.61", FakeSerials.Make(706), "pw");
        var service = host.Get<DeviceAddressService>();

        Assert.Equal(DeviceAddressChangeResult.NotManaged, await service.TryRelocateAsync(FakeSerials.Make(799), New, CancellationToken.None));
        Assert.Equal(DeviceAddressChangeResult.StillReachable, await service.TryRelocateAsync(FakeSerials.Make(706), "10.0.7.61", CancellationToken.None));
        Assert.Equal(DeviceAddressChangeResult.NotVerified, await service.TryRelocateAsync(FakeSerials.Make(705), "10.0.7.61", CancellationToken.None));
        Assert.Equal(DeviceAddressChangeResult.Updated, await service.TryRelocateAsync(FakeSerials.Make(705), New, CancellationToken.None));

        Assert.Equal(New, (await host.Get<DeviceRepository>().GetAsync(unreachable.Id, CancellationToken.None))!.Address);
        Assert.Equal("10.0.7.6", (await host.Get<DeviceRepository>().GetAsync(reachable.Id, CancellationToken.None))!.Address);
    }

    [Fact]
    public async Task PeriodicMdnsBrowseFindsAMovedDeviceBySerial()
    {
        var network = new FakeAxisNetwork();
        var browser = new AnnouncingBrowser();
        await using var host = await TestServerHost.StartAsync(network, configureServices: services =>
        {
            services.RemoveAll<IMdnsBrowser>();
            services.AddSingleton<IMdnsBrowser>(browser);
            services.RemoveAll<DeviceRelocationOptions>();
            services.AddSingleton(new DeviceRelocationOptions { InitialDelay = TimeSpan.FromHours(1), BrowseDuration = TimeSpan.FromSeconds(10) });
        });
        network.Add(Old, FakeSerials.Make(707), "pw");
        var device = await DeviceServiceTests.AddDeviceAsync(host, Old, 707, status: DeviceStatus.Unreachable);
        Move(host, 707, New);
        browser.Announce(FakeSerials.Make(707), New);
        browser.Announce(FakeSerials.Make(799), "10.0.7.98"); // not managed: ignored
        var relocation = host.Services.GetServices<IHostedService>().OfType<DeviceRelocationHostedService>().Single();

        var moved = await relocation.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, moved);
        Assert.Equal(New, (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Address);
        Assert.Equal(0, await relocation.RunOnceAsync(CancellationToken.None)); // nothing unreachable any more: no browse
        Assert.Equal(1, browser.Browses);
    }

    /// <summary>Test plugin: reads the device at the new address, moves the record, reads again through ctx.Vapix.</summary>
    private sealed class MovePlugin(string address) : ITaskPlugin
    {
        public const string PluginId = "test.move";

        public string Id => PluginId;

        public string DisplayName => "Move";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public string? SerialAtNewAddress { get; private set; }

        public string? SerialThroughContext { get; private set; }

        public bool Moved { get; private set; }

        public bool CanRun(IDeviceInfo device) => true;

        public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            var client = await ctx.CreateClientForAsync(address, ct);
            try
            {
                SerialAtNewAddress = (await client.GetBasicDeviceInfoAsync(ct)).SerialNumber;
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }

            Moved = await ctx.UpdateDeviceAddressAsync(address, ct);
            SerialThroughContext = (await ctx.Vapix.GetBasicDeviceInfoAsync(ct)).SerialNumber;
        }
    }

    /// <summary>mDNS browser that announces the given devices once per browse, then waits.</summary>
    private sealed class AnnouncingBrowser : IMdnsBrowser
    {
        private readonly List<(string Serial, string Address)> _devices = [];

        public int Browses { get; private set; }

        public void Announce(string serial, string address) => _devices.Add((serial, address));

        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Browses++;
            foreach (var (serial, address) in _devices.ToList())
            {
                yield return new MdnsServiceInstance(
                    $"AXIS M3106-L Mk II - {serial}._axis-video._tcp.local",
                    "axis-" + serial.ToLowerInvariant(),
                    80,
                    [IPAddress.Parse(address)],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["macaddress"] = serial },
                    serial,
                    null);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
