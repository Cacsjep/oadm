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

/// <summary>Status polling and the full refresh schedule with 5,000 devices.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
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

        // Seeded in one transaction (5,000 AddAsync calls would take a while and are not what is measured).
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
    public async Task APollRoundSpreads5000DevicesOverTheIntervalWithBoundedParallelism()
    {
        using var factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), new Connector(_handler));
        using var polling = new DevicePollingService(Devices, factory, _db.Get<ServerSettingsStore>()) { MaxParallelism = 16 };
        // 500 starts per second: well below what the fake devices and SQLite sustain (about 800/s), so the
        // measured distribution shows the pacing, not the throughput limit.
        var spread = TimeSpan.FromSeconds(10);

        var start = DateTimeOffset.UtcNow;
        await PerfScale.MeasureAsync(output, "PollRoundAsync 5,000 devices spread over 10 s", TimeSpan.FromSeconds(40), () =>
            polling.PollRoundAsync(spread, CancellationToken.None));
        Assert.Equal(PerfScale.Devices, _firstPoll.Count);

        // Ideal: 10 % of the devices per tenth of the window. The former burst started all 5,000 at once.
        var bins = new int[10];
        foreach (var time in _firstPoll.Values)
        {
            var bin = (int)Math.Clamp((time - start) / spread * 10, 0, 9);
            bins[bin]++;
        }

        output.WriteLine("Starts per second: " + string.Join(", ", bins));
        // Robust under a loaded CI machine (a busy machine falls behind and catches up later, never earlier):
        // no burst at the start, and a large share of the starts in the second half of the window.
        Assert.InRange(bins[0], 1, 1000);
        Assert.InRange(bins.Skip(5).Sum(), 2000, 5000);
        Assert.True(DateTimeOffset.UtcNow - start >= spread * 0.85, "the round took the spread time");
    }

    [Fact]
    public async Task ASecondPollThatOnlyMovesLastSeenPublishesNothing()
    {
        using var factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), new Connector(_handler));
        using var polling = new DevicePollingService(Devices, factory, _db.Get<ServerSettingsStore>()) { MaxParallelism = 32 };
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();

        await PerfScale.MeasureAsync(output, "PollAllAsync 5,000 devices, first round (model and firmware learned)", TimeSpan.FromSeconds(60), () =>
            polling.PollAllAsync(CancellationToken.None));
        Assert.Equal(PerfScale.Devices, Drain(subscription));

        await PerfScale.MeasureAsync(output, "PollAllAsync 5,000 devices, second round (LastSeen only)", TimeSpan.FromSeconds(60), () =>
            polling.PollAllAsync(CancellationToken.None));
        Assert.Equal(0, Drain(subscription));
        var device = (await Devices.ListDevicesAsync(CancellationToken.None))[0];
        Assert.NotNull(device.LastSeenUtc); // stored, just not broadcast
    }

    [Fact]
    public async Task TheFirstFullRefreshAfterAStartIsStaggeredOverOneInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), new Connector(_handler));
        using var polling = new DevicePollingService(Devices, factory, _db.Get<ServerSettingsStore>(), time);

        IReadOnlyList<Guid> due = [];
        await PerfScale.MeasureAsync(output, "QueueDueFullRefreshesAsync over 5,000 device ids", TimeSpan.FromSeconds(2), async () =>
            due = await polling.QueueDueFullRefreshesAsync(CancellationToken.None));
        Assert.Empty(due);

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.InRange((await polling.QueueDueFullRefreshesAsync(CancellationToken.None)).Count, 1, 10);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.InRange((await polling.QueueDueFullRefreshesAsync(CancellationToken.None)).Count, 2000, 3000);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PerfScale.Devices, (await polling.QueueDueFullRefreshesAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Removing5000DevicesIsOneTransaction()
    {
        var ids = await Devices.ListDeviceIdsAsync(CancellationToken.None);
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();
        await PerfScale.MeasureAsync(output, "RemoveManyAsync 5,000 devices", TimeSpan.FromSeconds(10), () =>
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
