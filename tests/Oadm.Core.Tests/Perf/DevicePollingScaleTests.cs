using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>Status polling and the full refresh schedule with 100,000 devices.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class DevicePollingScaleTests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstPoll = new();
    private TestDatabase _db = null!;
    private FakeHttpMessageHandler _handler = null!;
    private string _basicDeviceInfo = "";

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _basicDeviceInfo = Fixtures.Read("basicdeviceinfo-getAllProperties.json");
        _handler = new FakeHttpMessageHandler((request, _) =>
        {
            _firstPoll.TryAdd(request.RequestUri!.Host, DateTimeOffset.UtcNow);
            return Fixtures.Json(_basicDeviceInfo);
        });

        // Seeded in one transaction (100,000 AddAsync calls would take a while and are not what is measured).
        await using var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        for (var i = 0; i < PerfScale.Devices; i++)
        {
            db.Devices.Add(new Device
            {
                Id = Guid.NewGuid(),
                Serial = $"ACCC8E{i:X6}",
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Scheme = DeviceScheme.Http,
                Status = DeviceStatus.Ok,
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _handler.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task APollRoundOf100000DevicesRunsAsAPoolAndWritesLastSeenInBatches()
    {
        using var factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), new Connector(_handler));
        using var polling = new DevicePollingService(Devices, factory, _db.Get<ServerSettingsStore>()) { MaxParallelism = 200 };
        await polling.PollAllAsync(CancellationToken.None); // the first round learns model and firmware (written one by one)
        _firstPoll.Clear();

        // The second round changes nothing but LastSeen: written in batches, not one transaction per device.
        await PerfScale.MeasureAsync(output, "PollRoundAsync 100,000 devices, LastSeen only, 200 at once", TimeSpan.FromSeconds(60), () =>
            polling.PollRoundAsync(CancellationToken.None));
        Assert.Equal(PerfScale.Devices, _firstPoll.Count);
        Assert.Equal(PerfScale.Devices, polling.LastRound!.Devices);
        output.WriteLine($"Last round: {polling.LastRound.Duration.TotalSeconds:0.0} s");
        var devices = await Devices.ListDevicesAsync(CancellationToken.None);
        Assert.All(devices, d => Assert.NotNull(d.LastSeenUtc));
    }

    [Fact]
    public async Task ASecondPollThatOnlyMovesLastSeenPublishesNothing()
    {
        using var factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), new Connector(_handler));
        using var polling = new DevicePollingService(Devices, factory, _db.Get<ServerSettingsStore>()) { MaxParallelism = 32 };
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();

        await PerfScale.MeasureAsync(output, "PollAllAsync 100,000 devices, first round (model and firmware learned, written one by one; measured about 65 s)", TimeSpan.FromSeconds(240), () =>
            polling.PollAllAsync(CancellationToken.None));
        Assert.Equal(PerfScale.Devices, Drain(subscription));

        await PerfScale.MeasureAsync(output, "PollAllAsync 100,000 devices, second round (LastSeen only)", TimeSpan.FromSeconds(60), () =>
            polling.PollAllAsync(CancellationToken.None));
        Assert.Equal(0, Drain(subscription));
        var device = (await Devices.ListDevicesAsync(CancellationToken.None))[0];
        Assert.NotNull(device.LastSeenUtc); // stored, just not broadcast
    }

    [Fact]
    public async Task Removing100000DevicesIsOneTransaction()
    {
        var ids = await Devices.ListDeviceIdsAsync(CancellationToken.None);
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();
        await PerfScale.MeasureAsync(output, "RemoveManyAsync 100,000 devices", TimeSpan.FromSeconds(10), () =>
            Devices.RemoveManyAsync(ids, CancellationToken.None));
        Assert.Empty(await Devices.ListDeviceIdsAsync(CancellationToken.None));
        Assert.Equal(PerfScale.Devices, Drain(subscription));
    }

    public void Dispose() => _handler?.Dispose();

    private static int Drain(DeviceChangeSubscription subscription)
    {
        var count = 0;
        while (subscription.Reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    private sealed class Connector(FakeHttpMessageHandler handler) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), handler, new CertificatePinning(options.PinnedCertificateFingerprint), disposeHandler: false);
    }
}
