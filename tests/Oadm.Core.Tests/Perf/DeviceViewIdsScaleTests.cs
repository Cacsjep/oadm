using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// The allowed ids of a group's view at full size (30,000 devices): status changes of every device never read the device
/// table again (the cache follows the feed's membership version); a move out of the group does, once.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class DeviceViewIdsScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    [Fact]
    public async Task StatusChangesOfEveryDeviceKeepTheAllowedIdsAndAMoveReadsThemOnce()
    {
        var b = Sites.Add("Building B");
        var w = Sites.Add("Warehouse");
        var devices = Enumerable.Range(0, PerfScale.Devices).Select(i => new Device
        {
            Serial = $"B8A44F{i:X6}",
            Address = $"10.1.{i / 250}.{i % 250}",
            SiteId = i % 2 == 0 ? b : w,
            Status = DeviceStatus.Ok,
            AddedByAllDevices = true,
        }).ToList();
        await using (var ctx = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            ctx.Devices.AddRange(devices);
            await ctx.SaveChangesAsync();
        }

        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        var feed = (DeviceChangeFeed)Views.Feed;

        IReadOnlyList<DeviceChange> Round(DeviceStatus status) => [.. devices.Select(d =>
        {
            var copy = d.Clone();
            copy.Status = status;
            return new DeviceChange(DeviceChangeKind.Updated, d.Id, copy);
        })];

        feed.PublishMany(Round(DeviceStatus.Ok)); // the feed learns every device once
        IReadOnlySet<Guid> ids = null!;
        await PerfScale.MeasureAsync(output, "allowed ids of 15,000 of 30,000", TimeSpan.FromSeconds(10), async () => ids = await view.IdsAsync(Ct));
        Assert.Equal(PerfScale.Devices / 2, ids.Count);

        // A polling round, then 1,000 single status changes each followed by a read of the ids (one per plugin event).
        var round = Round(DeviceStatus.Unreachable);
        PerfScale.Measure(output, "a polling round of 30,000 status changes", TimeSpan.FromSeconds(5), () => feed.PublishMany(round));
        await PerfScale.MeasureAsync(output, "1,000 status changes, each with a read of the ids", TimeSpan.FromSeconds(2), async () =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                feed.Publish(round[i]);
                Assert.Same(ids, await view.IdsAsync(Ct));
            }
        });

        // A device moves to the warehouse (the repository writes it and publishes the change): the ids are read again, without it.
        var moved = devices[0];
        await Devices.UpdateAsync(moved.Id, d => d.SiteId = w, Ct);
        var after = await view.IdsAsync(Ct);
        Assert.NotSame(ids, after);
        Assert.DoesNotContain(moved.Id, after);
        Assert.Equal(ids.Count - 1, after.Count);
    }
}
