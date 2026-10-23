using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Core.Tests.Tasks;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// The task history of a device group at full size (docs/spec/device-scope.md, Tests): 30,000 devices, half of them at the
/// group's site, and 50,000 tasks. The history page and the Watch snapshot are filtered in the query; the limit counts
/// after the filter.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class TaskViewScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    [Fact]
    public async Task TheHistoryAndTheWatchSnapshotOfAGroupAreFilteredInTheQuery()
    {
        var site = Sites.Add("Building B");
        var deviceIds = new Guid[PerfScale.Devices];
        await using (var ctx = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var devices = Enumerable.Range(0, PerfScale.Devices).Select(i => new Device
            {
                Serial = $"B8A44F{i:X6}",
                Address = $"10.1.{i / 250}.{i % 250}",
                Model = "P3265-V",
                SiteId = i % 2 == 0 ? site : null,
                AddedByAllDevices = true,
            }).ToList();
            ctx.Devices.AddRange(devices);
            await ctx.SaveChangesAsync();
            for (var i = 0; i < devices.Count; i++)
            {
                deviceIds[i] = devices[i].Id;
            }
        }

        var history = PerfScale.History();
        for (var i = 0; i < history.Count; i++)
        {
            var task = history[i];
            history[i] = task with { Devices = [task.Devices[0] with { DeviceId = deviceIds[i % deviceIds.Length] }] };
        }

        var store = Db.Get<EfTaskStore>();
        await store.AddRangeAsync(history, Ct);
        var atSite = deviceIds.Where((_, i) => i % 2 == 0).ToHashSet();
        var expected = history.Where(t => atSite.Contains(t.DeviceId)).OrderByDescending(t => t.CreatedUtc).ToList();

        var group = (await SaveGroupAsync("B", sites: [site])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        await using var engine = new TaskEngine(store, new PluginRegistry(), Devices, new FakeVapixClientFactory()) { Views = Views };

        IReadOnlySet<Guid> ids = new HashSet<Guid>();
        await PerfScale.MeasureAsync(output, "the view's 15,000 device ids (first read)", TimeSpan.FromSeconds(10), async () =>
            ids = await view.IdsAsync(Ct));
        Assert.Equal(PerfScale.Devices / 2, ids.Count);

        TaskPage first = null!;
        await PerfScale.MeasureAsync(output, "group history, first page of 100 (json_each over 15,000 ids)", TimeSpan.FromSeconds(5), async () =>
            first = await engine.ListPageAsync(view, 0, 100, Ct));
        Assert.Equal(expected.Count, first.TotalCount);
        Assert.Equal(expected.Take(100).Select(t => t.Id), first.Tasks.Select(t => t.Id));

        TaskPage last = null!;
        await PerfScale.MeasureAsync(output, "group history, last page", TimeSpan.FromSeconds(5), async () =>
            last = await engine.ListPageAsync(view, expected.Count - 100, 100, Ct));
        Assert.Equal(expected.Skip(expected.Count - 100).Select(t => t.Id), last.Tasks.Select(t => t.Id));

        IReadOnlyList<TaskRecord> snapshot = [];
        await PerfScale.MeasureAsync(output, "group Watch snapshot, newest 10,000 of the group's 25,000", TimeSpan.FromSeconds(20), async () =>
        {
            var (tasks, subscription) = await engine.SubscribeWithSnapshotAsync(view, 10_000, Ct);
            subscription.Dispose();
            snapshot = tasks;
        });
        Assert.Equal(10_000, snapshot.Count);
        Assert.Equal(expected.Take(10_000).Select(t => t.Id), snapshot.Select(t => t.Id));

        // Users with all devices: the unfiltered paths, for comparison.
        var all = await Views.ForScopeAsync(DeviceScope.All, "boss", Ct);
        await PerfScale.MeasureAsync(output, "all devices, Watch snapshot, newest 10,000 of 50,000", TimeSpan.FromSeconds(20), async () =>
            Assert.Equal(10_000, (await engine.SnapshotAsync(all, 10_000, Ct)).Count));
    }
}
