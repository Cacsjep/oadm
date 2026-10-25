using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Discovery;
using Oadm.Server.Discovery;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// Commit of the add page with 30,000 selected devices (docs/spec/add-devices.md): the session's devices are looked up in
/// one step and "managed already" comes from one read of the managed serials, so the call is linear.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class AddCommitScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ACommitOfThirtyThousandManagedDevicesLooksThemUpOnce()
    {
        const int count = 30_000;
        var backend = new ManyDevices(count);
        await using var host = await TestServerHost.StartAsync(configureServices: s => s.AddSingleton<IDiscoveryBackend>(backend));
        await ServerScale.SeedDevicesAsync(host, count); // the same serials: every selected device is managed already
        var session = (await host.Discovery.StartZeroConfAsync(new Proto.Empty())).SessionId;
        var request = new Proto.CommitRequest { SessionId = session };
        request.DiscoveredIds.AddRange(backend.Ids);

        var watch = Stopwatch.StartNew();
        var reply = await host.AddDevices.CommitAsync(request);
        watch.Stop();

        output.WriteLine($"Commit of {count:N0} managed devices: {watch.ElapsedMilliseconds} ms");
        Assert.Empty(reply.DeviceIds);
        Assert.Equal(1, backend.Lookups);
        Assert.Equal(0, backend.SingleLookups);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"too slow: {watch.ElapsedMilliseconds} ms (measured about 220 ms)");
    }

    /// <summary>One zero-conf session that found <c>count</c> devices with the serials of <see cref="ServerScale.SeedDevicesAsync"/>.</summary>
    private sealed class ManyDevices : IDiscoveryBackend
    {
        private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        private int _lookups;
        private int _singleLookups;

        public ManyDevices(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var serial = $"ACCC8E{i:X6}";
                var address = new IPAddress([10, (byte)(i / 65536), (byte)(i / 256 % 256), (byte)(i % 256)]);
                _devices[serial] = new DiscoveredDevice(serial, serial, address, null, "P3265-V", "12.11.77",
                    DiscoveredDeviceStatus.CredentialsRequired, "http", DiscoverySources.Mdns, DateTimeOffset.UtcNow);
            }
        }

        public IEnumerable<string> Ids => _devices.Keys;

        public int Lookups => Volatile.Read(ref _lookups);

        public int SingleLookups => Volatile.Read(ref _singleLookups);

        public Task<DiscoverySession> StartZeroConfAsync(CancellationToken ct, DiscoveryRoutes? routes = null) =>
            Task.FromResult(new DiscoverySession("many", DiscoverySessionKind.ZeroConf));

        public void StopScan(string sessionId)
        {
        }

        public Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct, DiscoveryRoutes? routes = null) =>
            throw new NotSupportedException();

        public Task<DiscoverySession> StartAddressProbeAsync(string input, bool importLine, CancellationToken ct, Guid? siteId = null, string? expectedSerial = null, DiscoveryRoutes? routes = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(string sessionId) => Task.CompletedTask;

        public bool IsActive(string sessionId) => true;

        public DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId)
        {
            Interlocked.Increment(ref _singleLookups);
            return _devices.GetValueOrDefault(discoveredId);
        }

        public IReadOnlyDictionary<string, DiscoveredDevice> FindDiscovered(string sessionId, IReadOnlyCollection<string> discoveredIds)
        {
            Interlocked.Increment(ref _lookups);
            return discoveredIds.Where(_devices.ContainsKey).ToDictionary(id => id, id => _devices[id], StringComparer.OrdinalIgnoreCase);
        }
    }
}
