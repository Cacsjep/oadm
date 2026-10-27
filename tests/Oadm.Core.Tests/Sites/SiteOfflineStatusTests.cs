using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// Site offline is computed, never stored (docs/spec/data-model.md, device statuses): reads and the change feed show it while the
/// device's site is not online, the stored status stays the last real one, and a site going offline or online publishes
/// its devices in one batch without writes.
/// </summary>
public sealed class SiteOfflineStatusTests : IAsyncLifetime
{
    private readonly FakeSiteTransport _sites = new();
    private FakeSiteTransport.FakeSite _site = null!;
    private TestDatabase _db = null!;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _site = _sites.AddSite("Building B");
        _db = await TestDatabase.CreateAsync(configure: s => s.AddSingleton<ISiteStates>(_sites));
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ReadsShowSiteOfflineWhileTheSiteIsOfflineAndTheStoredStatusOtherwise()
    {
        var local = await AddAsync("ACCC8E000001", null, DeviceStatus.Ok);
        var atSite = await AddAsync("ACCC8E000002", _site.Id, DeviceStatus.Unreachable);
        Assert.Equal(DeviceStatus.Unreachable, atSite.Status);
        Assert.Equal("Building B", atSite.SiteName);

        _sites.SetOnline(_site.Id, false);

        var read = (await Devices.GetAsync(atSite.Id, CancellationToken.None))!;
        Assert.Equal(DeviceStatus.SiteOffline, read.Status);
        Assert.Equal("Building B", read.SiteName);
        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(local.Id, CancellationToken.None))!.Status);
        Assert.Equal(DeviceStatus.SiteOffline, (await Devices.FindBySerialAsync(atSite.Serial, CancellationToken.None))!.Status);
        Assert.Equal(DeviceStatus.SiteOffline, (await Devices.ListDevicesAsync(CancellationToken.None)).Single(d => d.Id == atSite.Id).Status);
        Assert.Equal(DeviceStatus.SiteOffline, (await Devices.ListDevicesPageAsync(null, 10, CancellationToken.None)).Single(d => d.Id == atSite.Id).Status);
        var statuses = await Devices.ListIdsWithStatusAsync(CancellationToken.None);
        Assert.Contains((atSite.Id, DeviceStatus.SiteOffline), statuses);
        Assert.Contains((local.Id, DeviceStatus.Ok), statuses);

        _sites.SetOnline(_site.Id, true);
        Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(atSite.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task SiteOfflineIsNeverStored()
    {
        var device = await AddAsync("ACCC8E000003", _site.Id, DeviceStatus.Ok);
        _sites.SetOnline(_site.Id, false);

        // A copy read while the site is offline, written back with another change, keeps the real status.
        var copy = (await Devices.GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(DeviceStatus.SiteOffline, copy.Status);
        copy.Model = "P3265-LV";
        var written = await Devices.UpdateAsync(copy, CancellationToken.None);
        Assert.Equal(DeviceStatus.SiteOffline, written.Status);
        await Devices.UpdateAsync(device.Id, d => d.Status = DeviceStatus.SiteOffline, CancellationToken.None);

        var added = await Devices.AddAsync(new Device { Serial = "ACCC8E000004", Address = "192.168.0.21", SiteId = _site.Id, Status = DeviceStatus.SiteOffline }, CancellationToken.None);

        _sites.SetOnline(_site.Id, true);
        var back = (await Devices.GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(DeviceStatus.Ok, back.Status);
        Assert.Equal("P3265-LV", back.Model);
        Assert.Equal(DeviceStatus.Unknown, (await Devices.GetAsync(added.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task WithoutSitesADeviceOfAnUnknownSiteIsSiteOfflineAndLocalDevicesAreNotAffected()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repo = db.Get<DeviceRepository>();
        Assert.Same(NoSiteStates.Instance, db.Get<ISiteStates>());
        var local = await repo.AddAsync(new Device { Serial = "ACCC8E000010", Address = "10.0.0.10", Status = DeviceStatus.Ok }, CancellationToken.None);
        var remote = await repo.AddAsync(new Device { Serial = "ACCC8E000011", Address = "192.168.0.11", SiteId = Guid.NewGuid(), Status = DeviceStatus.Ok }, CancellationToken.None);

        Assert.Equal(DeviceStatus.Ok, (await repo.GetAsync(local.Id, CancellationToken.None))!.Status);
        var read = (await repo.GetAsync(remote.Id, CancellationToken.None))!;
        Assert.Equal(DeviceStatus.SiteOffline, read.Status);
        Assert.Null(read.SiteName);
    }

    [Fact]
    public async Task TheChangeFeedPublishesTheComputedStatus()
    {
        var local = await AddAsync("ACCC8E000005", null, DeviceStatus.Ok);
        var atSite = await AddAsync("ACCC8E000006", _site.Id, DeviceStatus.Ok);
        _sites.SetOnline(_site.Id, false);
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();

        await Devices.UpdateAsync(atSite.Id, d => d.Model = "Q6135-LE", CancellationToken.None);
        await Devices.UpdateAsync(local.Id, d => d.Model = "Q6135-LE", CancellationToken.None);

        Assert.True(subscription.Reader.TryRead(out var siteChange));
        Assert.Equal(DeviceStatus.SiteOffline, siteChange!.Device!.Status);
        Assert.Equal("Building B", siteChange.Device.SiteName);
        Assert.True(subscription.Reader.TryRead(out var localChange));
        Assert.Equal(DeviceStatus.Ok, localChange!.Device!.Status);
    }

    [Fact]
    public async Task ASiteGoingOfflineAndOnlinePublishesItsDevicesInOneBatchWithoutWrites()
    {
        var other = _sites.AddSite("Warehouse");
        await AddAsync("ACCC8E000020", null, DeviceStatus.Ok);
        await AddAsync("ACCC8E000021", other.Id, DeviceStatus.Ok);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await AddAsync("ACCC8E00003" + i, _site.Id, i == 0 ? DeviceStatus.CredentialsRequired : DeviceStatus.Ok)).Id);
        }

        var feed = _db.Get<DeviceChangeFeed>();
        await using var publisher = new SiteStatusPublisher(_sites, Devices, feed);
        publisher.Start();
        using var subscription = feed.Subscribe();
        var version = feed.Version;

        _sites.SetOnline(_site.Id, false);
        await Eventually.TrueAsync(() => publisher.Batches == 1, "the batch of the site going offline");

        Assert.Equal(version + 1, feed.Version); // one batch
        var changes = Drain(subscription);
        Assert.Equal(ids.Order(), changes.Select(c => c.DeviceId).Order());
        Assert.All(changes, c => Assert.Equal(DeviceChangeKind.Updated, c.Kind));
        Assert.All(changes, c => Assert.Equal(DeviceStatus.SiteOffline, c.Device!.Status));

        _sites.SetOnline(_site.Id, true);
        await Eventually.TrueAsync(() => publisher.Batches == 2, "the batch of the site coming back");
        changes = Drain(subscription);
        Assert.Equal(3, changes.Count);
        Assert.Equal(DeviceStatus.CredentialsRequired, changes.Single(c => c.DeviceId == ids[0]).Device!.Status);
        Assert.All(changes.Where(c => c.DeviceId != ids[0]), c => Assert.Equal(DeviceStatus.Ok, c.Device!.Status));
    }

    [Fact]
    public async Task ASiteBackAfterMoreThanFiveMinutesGetsAFullRefreshPerDevice()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await AddAsync("ACCC8E00004" + i, _site.Id, DeviceStatus.Ok)).Id);
        }

        var refreshed = new ConcurrentQueue<IReadOnlyList<Guid>>();
        await using var publisher = new SiteStatusPublisher(_sites, Devices, _db.Get<IDeviceChangeFeed>(), refreshed.Enqueue, time);
        publisher.Start();

        // A short dropout: no refresh.
        await SwitchAsync(publisher, online: false);
        time.Advance(TimeSpan.FromMinutes(4));
        await SwitchAsync(publisher, online: true);
        Assert.Empty(refreshed);

        await SwitchAsync(publisher, online: false);
        time.Advance(TimeSpan.FromMinutes(6));
        await SwitchAsync(publisher, online: true);
        Assert.Equal(ids.Order(), Assert.Single(refreshed).Order());
    }

    [Fact]
    public void ASiteErrorIsNeverADeviceError()
    {
        var site = SiteException.For(_site.Id, "Building B", SiteProblem.Offline);
        var wrapped = new HttpRequestException(HttpRequestError.ConnectionError, "connect failed", site);

        Assert.Same(site, SiteErrors.Find(wrapped));
        Assert.Same(site, SiteErrors.Find(new AggregateException(wrapped)));
        Assert.Null(SiteErrors.Find(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.Equal(DeviceStatus.SiteOffline, DeviceStatusClassifier.FromException(wrapped));
        Assert.Equal(DeviceStatus.Unreachable, DeviceStatusClassifier.FromException(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
    }

    [Fact]
    public void TheSharedTextsNameTheSite()
    {
        var device = new Device { Serial = "ACCC8E000050", Address = "192.168.0.50", Status = DeviceStatus.SiteOffline, SiteId = _site.Id, SiteName = "Building B" };

        Assert.Equal("Site offline: Building B", DeviceMessages.Refusal(device));
        Assert.Equal("Site offline", DeviceMessages.Refusal(DeviceStatus.SiteOffline));
        Assert.Equal("Site offline: Building B", DeviceMessages.StatusText(device));
        Assert.Equal("Site offline", DeviceMessages.StatusText(DeviceStatus.SiteOffline));
        Assert.Equal("Site offline: Building B", TaskSupportReasons.ForStatus(device));
        Assert.Equal(SiteTexts.SiteOffline(null), TaskSupportReasons.ForStatus(DeviceStatus.SiteOffline));
        Assert.Contains(Sdk.Actions.ActionPlaceholders.Fixed, p => p.Name == "site");

        device.Status = DeviceStatus.CredentialsRequired;
        Assert.Equal(DeviceMessages.CredentialsRequired, DeviceMessages.Refusal(device));
    }

    private async Task SwitchAsync(SiteStatusPublisher publisher, bool online)
    {
        var before = publisher.Handled;
        _sites.SetOnline(_site.Id, online);
        await Eventually.TrueAsync(() => publisher.Handled > before, "the site change was handled");
    }

    private static List<DeviceChange> Drain(DeviceChangeSubscription subscription)
    {
        var list = new List<DeviceChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            list.Add(change);
        }

        return list;
    }

    private async Task<Device> AddAsync(string serial, Guid? siteId, DeviceStatus status) =>
        await Devices.AddAsync(new Device
        {
            Serial = serial,
            Address = "192.168.0." + serial[^2..],
            SiteId = siteId,
            Status = status,
            Model = "P3265-V",
        }, CancellationToken.None);
}
