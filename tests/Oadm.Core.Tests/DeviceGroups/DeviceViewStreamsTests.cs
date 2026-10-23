using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The device feed of a view (Added and Removed when a device enters or leaves it), the sessions bound to one device that
/// end when it leaves the view, the event streams of a view and the paged snapshot (docs/spec/device-scope.md, Streams).
/// </summary>
public sealed class DeviceViewStreamsTests : DeviceGroupTestBase
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheFeedOfAGroupAddsAndRemovesDevicesThatEnterOrLeaveTheView()
    {
        var b = Sites.Add("Building B");
        var atB = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var other = await AddDeviceAsync("ACCC8E000002");
        await AddDeviceAsync("ACCC8E000009", tags: ["PTZ"]);
        var ptz = await TagIdAsync("PTZ");
        var group = (await SaveGroupAsync("B", sites: [b], tags: [ptz])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);

        using var feed = view.Subscribe();
        var snapshot = await SnapshotAsync(view, feed);
        Assert.Equal(2, snapshot.Count);
        Assert.DoesNotContain(other.Id, snapshot);

        // Outside the view: nothing. A tag that brings it in: Added. Inside: Updated. The tag gone: Removed.
        await Devices.UpdateAsync(other.Id, d => d.Model = "M1", Ct);
        await Tags.SetDeviceTagsAsync([other.Id], ["PTZ"], [], Ct);
        await Devices.UpdateAsync(atB.Id, d => d.Model = "M2", Ct);
        await Tags.SetDeviceTagsAsync([other.Id], [], ["PTZ"], Ct);
        var changes = await ReadAsync(feed, 3);
        Assert.Equal(
            [(DeviceChangeKind.Added, other.Id), (DeviceChangeKind.Updated, atB.Id), (DeviceChangeKind.Removed, other.Id)],
            changes.Select(c => (c.Kind, c.DeviceId)));
        Assert.Null(changes[2].Device);

        // A removed device of the view: Removed; one outside the view: nothing.
        await Devices.RemoveAsync(other.Id, Ct);
        await Devices.RemoveAsync(atB.Id, Ct);
        var removed = Assert.Single(await ReadAsync(feed, 1));
        Assert.Equal((DeviceChangeKind.Removed, atB.Id), (removed.Kind, removed.DeviceId));
        Assert.Equal(1, feed.SentCount);
    }

    [Fact]
    public async Task ARemovedSiteOrADeletedTagRemovesItsDevicesAndANewTagAddsThemWithoutADeviceChange()
    {
        var b = Sites.Add("Building B");
        var atB = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var tagged = await AddDeviceAsync("ACCC8E000002", tags: ["PTZ"]);
        var ptz = await TagIdAsync("PTZ");
        var group = (await SaveGroupAsync("G", sites: [b], tags: [ptz])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        using var feed = view.Subscribe();
        Assert.Equal(2, (await SnapshotAsync(view, feed)).Count);

        // The site is removed with "Keep devices": no device changes, the catalog moves.
        Sites.Remove(b);
        var gone = Assert.Single(await ReadAsync(feed, 1));
        Assert.Equal((DeviceChangeKind.Removed, atB.Id), (gone.Kind, gone.DeviceId));

        await Tags.DeleteAsync("PTZ", Ct);
        var untagged = Assert.Single(await ReadAsync(feed, 1));
        Assert.Equal((DeviceChangeKind.Removed, tagged.Id), (untagged.Kind, untagged.DeviceId));
        Assert.Equal(0, feed.SentCount);
    }

    [Fact]
    public async Task TheFeedOfAllDevicesPassesEveryChangeAsItIs()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var view = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        using var feed = view.Subscribe();
        Assert.Single(await SnapshotAsync(view, feed));

        await Devices.UpdateAsync(device.Id, d => d.Model = "M1", Ct);
        await Devices.RemoveAsync(Guid.NewGuid(), Ct);
        await Devices.RemoveAsync(device.Id, Ct);
        var changes = await ReadAsync(feed, 2);
        Assert.Equal([DeviceChangeKind.Updated, DeviceChangeKind.Removed], changes.Select(c => c.Kind));
        Assert.Equal(0, feed.SentCount); // no ids kept: today's feed
    }

    [Fact]
    public async Task PagesHoldOnlyTheViewAndCountsBySiteAreTheGroups()
    {
        var b = Sites.Add("Building B");
        var w = Sites.Add("Warehouse");
        for (var i = 1; i <= 25; i++)
        {
            await AddDeviceAsync($"ACCC8E0000{i:D2}", siteId: i % 2 == 0 ? b : w);
        }

        var group = (await SaveGroupAsync("B", sites: [b], except: ["ACCC8E000002"])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        var pages = new List<IReadOnlyList<Device>>();
        await foreach (var page in view.PagesAsync(5, Ct))
        {
            pages.Add(page);
        }

        Assert.All(pages, p => Assert.True(p.Count <= 5));
        Assert.Equal(11, pages.Sum(p => p.Count));
        Assert.All(pages.SelectMany(p => p), d => Assert.Equal(b, d.SiteId));
        Assert.Equal(new Dictionary<Guid, int> { [b] = 11 }, await view.CountsBySiteAsync(Ct));
        Assert.True(view.Filter.CoversSite(b));
        Assert.False(view.Filter.CoversSite(w));

        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        var allPages = new List<IReadOnlyList<Device>>();
        await foreach (var page in all.PagesAsync(10, Ct))
        {
            allPages.Add(page);
        }

        Assert.Equal([10, 10, 5], allPages.Select(p => p.Count));
        Assert.Equal(allPages.SelectMany(p => p).Select(d => d.Serial).Order(StringComparer.Ordinal), allPages.SelectMany(p => p).Select(d => d.Serial));
        Assert.Equal(new Dictionary<Guid, int> { [b] = 12, [w] = 13 }, await all.CountsBySiteAsync(Ct));
        Assert.Empty(await (await Views.ForScopeAsync(DeviceScope.None, "x", Ct)).CountsBySiteAsync(Ct));
    }

    [Fact]
    public async Task ASessionEndsWhenItsDeviceLeavesTheViewAndNeverForAllDevices()
    {
        var b = Sites.Add("Building B");
        var tagged = await AddDeviceAsync("ACCC8E000001", tags: ["PTZ"]);
        var atB = await AddDeviceAsync("ACCC8E000002", siteId: b);
        var kept = await AddDeviceAsync("ACCC8E000003", tags: ["PTZ"]);
        var outside = await AddDeviceAsync("ACCC8E000004");
        var ptz = await TagIdAsync("PTZ");
        var group = (await SaveGroupAsync("G", sites: [b], tags: [ptz])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        var watcher = Db.Get<DeviceViewWatcher>();

        using var byTag = await watcher.WatchAsync(await view.GetAsync([tagged.Id], Ct), Ct);
        using var bySite = await watcher.WatchAsync(await view.GetAsync([atB.Id], Ct), Ct);
        using var stays = await watcher.WatchAsync(await view.GetAsync([kept.Id], Ct), Ct);
        using var forAll = await watcher.WatchAsync(await all.GetAsync([tagged.Id], Ct), Ct);
        Assert.Equal(3, watcher.Count); // all devices: nothing to follow

        await Tags.SetDeviceTagsAsync([tagged.Id], [], ["PTZ"], Ct);
        await Eventually.TrueAsync(() => byTag.HasLeft, "the session of the untagged device ended");
        Sites.Remove(b);
        await Eventually.TrueAsync(() => bySite.HasLeft, "the session of the device of the removed site ended");
        Assert.False(stays.HasLeft);
        Assert.False(forAll.HasLeft);
        Assert.False(forAll.Left.CanBeCanceled);

        // The group's rules change: a device no longer covered ends too.
        await SaveGroupAsync("G", serials: ["ACCC8E0000FF"], id: group.Id, revision: (await Groups.GetAsync(group.Id, Ct))!.Revision);
        await Eventually.TrueAsync(() => stays.HasLeft, "the session ended after the group's rules changed");
        Assert.False(forAll.HasLeft);

        // A device outside the view never gets a lease.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => view.GetAsync([outside.Id], Ct));
        stays.Dispose();
        byTag.Dispose();
        bySite.Dispose();
        Assert.Equal(0, watcher.Count);
    }

    [Fact]
    public async Task EventStreamsOfAViewOpenOnlyItsDevicesAndEndWhenTheDeviceLeaves()
    {
        var tagged = await AddDeviceAsync("ACCC8E000001", tags: ["PTZ"]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("G", tags: [await TagIdAsync("PTZ")])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        var inner = new EndlessStreams();
        var streams = new ViewedEventStreams(inner, view, Db.Get<DeviceViewWatcher>());

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => streams.OpenAsync(outside.Id, Ct));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => streams.OpenAsync(Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => streams.GetRtspPortsAsync([tagged.Id, outside.Id], Ct));
        Assert.Empty(inner.Opened); // never contacted

        await using var source = await streams.OpenAsync(tagged.Id, Ct);
        Assert.Equal([tagged.Id], inner.Opened);
        using var timeout = new CancellationTokenSource(Wait);
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in source.ReadAsync(timeout.Token))
            {
            }
        }, CancellationToken.None);

        await Tags.SetDeviceTagsAsync([tagged.Id], [], ["PTZ"], Ct);
        var ended = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => reading);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), ended.Message);
        Assert.False(timeout.IsCancellationRequested);
    }

    private static async Task<HashSet<Guid>> SnapshotAsync(DeviceView view, DeviceViewFeed feed)
    {
        var ids = new HashSet<Guid>();
        await foreach (var page in view.PagesAsync(1_000, Ct))
        {
            foreach (var device in page)
            {
                feed.Sent(device.Id);
                ids.Add(device.Id);
            }
        }

        return ids;
    }

    private static async Task<List<DeviceChange>> ReadAsync(DeviceViewFeed feed, int count)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var changes = new List<DeviceChange>();
        while (changes.Count < count && await feed.ReadAsync(100, timeout.Token) is { } batch)
        {
            changes.AddRange(batch);
        }

        return changes;
    }

    /// <summary>Event streams that never deliver a document until they are cancelled.</summary>
    private sealed class EndlessStreams : IDeviceEventStreams
    {
        public List<Guid> Opened { get; } = [];

        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            Opened.Add(deviceId);
            return Task.FromResult<IDeviceEventSource>(new Endless());
        }

        private sealed class Endless : IDeviceEventSource
        {
            public int LostDocuments => 0;

            public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                yield break;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
