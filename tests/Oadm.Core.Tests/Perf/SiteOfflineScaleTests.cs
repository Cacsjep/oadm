using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// Site offline at scale (CLAUDE.md "# Sites", Perf): a site with 3,000 devices going offline and online is one batch each and
/// writes nothing; the computed status of 30,000 devices costs O(1) per device on every read.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class SiteOfflineScaleTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int AtSite = 3_000;

    private readonly FakeSiteTransport _sites = new();
    private FakeSiteTransport.FakeSite _site = null!;
    private TestDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _site = _sites.AddSite("Building B");
        var other = _sites.AddSite("Warehouse");
        _db = await TestDatabase.CreateAsync(configure: s => s.AddSingleton<ISiteStates>(_sites));
        await using var ctx = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        ctx.Devices.AddRange(Enumerable.Range(0, PerfScale.Devices).Select(i => new Device
        {
            Serial = $"B8A44F{i:X6}",
            Address = $"10.1.{i / 250}.{i % 250}",
            Model = "P3265-V",
            Status = i % 7 == 0 ? DeviceStatus.Unreachable : DeviceStatus.Ok,
            SiteId = i < AtSite ? _site.Id : i < 2 * AtSite ? other.Id : null,
        }));
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ASiteWithThreeThousandDevicesGoesOfflineAndOnlineInOneBatchEachWithoutWrites()
    {
        var feed = _db.Get<DeviceChangeFeed>();
        var devices = _db.Get<DeviceRepository>();
        var stored = await StoredStatusesAsync();
        await using var publisher = new SiteStatusPublisher(_sites, devices, feed);
        publisher.Start();
        using var subscription = feed.Subscribe();

        foreach (var online in new[] { false, true })
        {
            var version = feed.Version;
            var handled = publisher.Handled;
            await PerfScale.MeasureAsync(output, online ? "Site back online, 3,000 devices published" : "Site offline, 3,000 devices published", TimeSpan.FromSeconds(10), async () =>
            {
                _sites.SetOnline(_site.Id, online);
                await Eventually.TrueAsync(() => publisher.Handled > handled, "the batch");
            });

            Assert.Equal(version + 1, feed.Version); // one batch
            var changes = 0;
            while (subscription.Reader.TryRead(out var change))
            {
                changes++;
                Assert.Equal(online, change.Device!.Status != DeviceStatus.SiteOffline);
            }

            Assert.Equal(AtSite, changes);
        }

        Assert.Equal(stored, await StoredStatusesAsync()); // nothing written
    }

    [Fact]
    public async Task TheComputedStatusOfThirtyThousandDevicesIsCheap()
    {
        var devices = _db.Get<DeviceRepository>();
        _sites.SetOnline(_site.Id, false);
        IReadOnlyList<Device> all = [];
        await PerfScale.MeasureAsync(output, "List 30,000 devices with their computed status", TimeSpan.FromSeconds(20), async () =>
            all = await devices.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(AtSite, all.Count(d => d.Status == DeviceStatus.SiteOffline));
        Assert.All(all.Where(d => d.SiteId == _site.Id), d => Assert.Equal("Building B", d.SiteName));

        IReadOnlyList<(Guid Id, DeviceStatus Status)> statuses = [];
        await PerfScale.MeasureAsync(output, "Ids and computed statuses of 30,000 devices", TimeSpan.FromSeconds(10), async () =>
            statuses = await devices.ListIdsWithStatusAsync(CancellationToken.None));
        Assert.Equal(AtSite, statuses.Count(s => s.Status == DeviceStatus.SiteOffline));

        var copies = all.Select(d => d.Clone()).ToList();
        PerfScale.Measure(output, "Computed status of 30,000 devices in memory", TimeSpan.FromSeconds(1), () =>
        {
            foreach (var device in copies)
            {
                SiteDeviceStatus.Apply(device, _sites);
            }
        });
    }

    private async Task<Dictionary<Guid, DeviceStatus>> StoredStatusesAsync()
    {
        await using var ctx = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        return await ctx.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Status);
    }
}
