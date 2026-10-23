using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// A write publishes the device as stored after it, never the copy it read before (a stale copy would put a device back
/// into its old group's grids), and the feed knows every stored device from the start (the first polling round moves no
/// membership version).
/// </summary>
public sealed class StoredDevicePublishTests : DeviceGroupTestBase
{
    private IDbContextFactory<OadmDbContext> Factory => Db.Get<IDbContextFactory<OadmDbContext>>();

    private DeviceChangeFeed Feed => Db.Get<DeviceChangeFeed>();

    [Fact]
    public async Task AStatusWriteThatReadTheDeviceBeforeATagChangePublishesTheNewTags()
    {
        var device = await AddDeviceAsync("ACCC8E000001", tags: ["Building A"]);
        using var changes = Feed.Subscribe();

        // The polling read the row (old tags); a tag change lands and is published before the polling saves its status.
        var updated = await Devices.UpdateAsync(device.Id, d =>
        {
            using (var db = Factory.CreateDbContext())
            {
                var row = db.Devices.Single(x => x.Id == device.Id);
                row.Tags = ["Building B"];
                db.SaveChanges();
                Feed.Publish(new DeviceChange(DeviceChangeKind.Updated, row.Id, row.Clone()));
            }

            d.Status = DeviceStatus.Unreachable;
        }, Ct);

        var last = LastOf(changes, device.Id);
        Assert.Equal(["Building B"], last.Device!.Tags);
        Assert.Equal(DeviceStatus.Unreachable, last.Device.Status);
        Assert.Equal(["Building B"], updated!.Tags);
        Assert.Equal(["Building B"], (await Devices.GetAsync(device.Id, Ct))!.Tags);
    }

    [Fact]
    public async Task CredentialWritesPublishTheDeviceAsStored()
    {
        var one = await AddDeviceAsync("ACCC8E000002", tags: ["Lobby"]);
        var two = await AddDeviceAsync("ACCC8E000003");
        var credentials = Db.Get<CredentialStore>();
        using var changes = Feed.Subscribe();

        await credentials.SetAsync(one.Id, "root", "pass", Ct);
        Assert.Equal(["Lobby"], LastOf(changes, one.Id).Device!.Tags);

        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([two.Id], Ct), ["PTZ"], [], Ct);
        await credentials.SetManyAsync([one.Id, two.Id], "root", "other", Ct);
        Assert.Equal(["PTZ"], LastOf(changes, two.Id).Device!.Tags);

        Assert.True(await credentials.RemoveAsync(two.Id, Ct));
        Assert.Equal(["PTZ"], LastOf(changes, two.Id).Device!.Tags);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => credentials.SetAsync(Guid.NewGuid(), "root", "pass", Ct));
    }

    [Fact]
    public async Task ConcurrentWritersOfDifferentColumnsEndWithTheStoredDeviceAsTheLastPublish()
    {
        var device = await AddDeviceAsync("ACCC8E000004");
        using var changes = Feed.Subscribe();
        var view = await Views.System.GetAsync([device.Id], Ct);

        var writes = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? Task.Run(() => Devices.UpdateAsync(device.Id, d => d.FirmwareVersion = "12.0." + i, Ct))
            : Task.Run(async () => { await Tags.SetDeviceTagsAsync(view, ["T" + i], [], Ct); return (Device?)null; }));
        await Task.WhenAll(writes);

        var stored = (await Devices.GetAsync(device.Id, Ct))!;
        var last = LastOf(changes, device.Id).Device!;
        Assert.Equal(stored.FirmwareVersion, last.FirmwareVersion);
        Assert.Equal(stored.Tags, last.Tags);
    }

    [Fact]
    public async Task DevicesStoredBeforeTheStartAreKnownToTheFeedSoAStatusChangeMovesNoMembershipVersion()
    {
        var ids = new List<Guid>();
        await using (var db = await Factory.CreateDbContextAsync(Ct))
        {
            for (var i = 0; i < 20; i++)
            {
                var device = new Device { Id = Guid.NewGuid(), Serial = $"ACCC8E0001{i:D2}", Address = $"10.0.1.{i + 1}", Tags = ["PTZ"], AddedByAllDevices = true };
                db.Devices.Add(device);
                ids.Add(device.Id);
            }

            await db.SaveChangesAsync(Ct); // stored by an earlier process: the feed never published them
        }

        using var scopes = new HostDeviceScopes(Groups, Views);
        await scopes.LoadAsync(Ct);
        Assert.Equal(ids.Count, Feed.MembershipCount);

        var before = Feed.MembershipVersion;
        foreach (var id in ids)
        {
            await Devices.UpdateAsync(id, d => d.Status = DeviceStatus.Unreachable, Ct); // the first polling round
        }

        Assert.Equal(before, Feed.MembershipVersion);
        await Devices.UpdateAsync(ids[0], d => d.Tags = ["Lobby"], Ct);
        Assert.Equal(before + 1, Feed.MembershipVersion);
    }

    [Fact]
    public void TheSeedKeepsWhatWasPublishedMeanwhileAndSkipsRemovedDevices()
    {
        var feed = new DeviceChangeFeed();
        var published = new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000005", Address = "10.0.0.5", Tags = ["New"] };
        var removed = Guid.NewGuid();
        feed.Publish(new DeviceChange(DeviceChangeKind.Updated, published.Id, published.Clone()));
        feed.Publish(new DeviceChange(DeviceChangeKind.Removed, removed, null));

        // Rows read before those changes: older tags, a device removed since.
        feed.SeedMembership([new DeviceScopeRow(published.Id, published.Serial, null, ["Old"], false, null), new DeviceScopeRow(removed, "ACCC8E000006", null, [], false, null)]);
        Assert.Equal(1, feed.MembershipCount);

        var version = feed.MembershipVersion;
        feed.Publish(new DeviceChange(DeviceChangeKind.Updated, published.Id, published.Clone()));
        Assert.Equal(version, feed.MembershipVersion); // the published tags stayed known
    }

    private static DeviceChange LastOf(DeviceChangeSubscription changes, Guid id)
    {
        DeviceChange? last = null;
        while (changes.Reader.TryRead(out var change))
        {
            if (change.DeviceId == id)
            {
                last = change;
            }
        }

        return last ?? throw new InvalidOperationException("No change of the device was published.");
    }
}
