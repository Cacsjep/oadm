using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// <see cref="HostDeviceScopes"/> at full size: 30,000 devices and 50 groups. A polling round (an update of every device
/// that changes nothing a group reads) costs one comparison per device and raises nothing; a burst of moves raises one
/// Changed per affected group.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class HostDeviceScopesScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    private const int GroupCount = 50;

    [Fact]
    public async Task APollingRoundOverEveryDeviceRaisesNothingAndABurstOfMovesOneChangedPerGroup()
    {
        var sites = Enumerable.Range(0, GroupCount).Select(i => Sites.Add("Site " + i)).ToArray();
        var devices = Enumerable.Range(0, PerfScale.Devices).Select(i => new Device
        {
            Serial = $"B8A44F{i:X6}",
            Address = $"10.1.{i / 250}.{i % 250}",
            SiteId = sites[i % GroupCount],
            Status = DeviceStatus.Ok,
            AddedByAllDevices = true,
        }).ToList();
        await using (var ctx = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            ctx.Devices.AddRange(devices);
            await ctx.SaveChangesAsync();
        }

        var keys = new List<string>();
        foreach (var site in sites)
        {
            keys.Add((await SaveGroupAsync("G " + site, sites: [site])).Group.Id.ToString());
        }

        var time = new FakeTimeProvider();
        using var scopes = new HostDeviceScopes(Groups, Views, time);
        await PerfScale.MeasureAsync(output, "load 30,000 rows", TimeSpan.FromSeconds(10), () => scopes.LoadAsync(Ct));
        var changed = new ConcurrentQueue<string>();
        scopes.Changed += (_, k) => changed.Enqueue(k);
        var feed = Views.Feed;

        var round = devices.Select(d =>
        {
            var copy = d.Clone();
            copy.Status = DeviceStatus.Unreachable;
            return new DeviceChange(DeviceChangeKind.Updated, d.Id, copy);
        }).ToList();
        PerfScale.Measure(output, "a polling round of 30,000 updates", TimeSpan.FromSeconds(5), () => feed.PublishMany(round));
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Empty(changed);

        // Every device of site 0 moves to site 1: groups 0 and 1 change, once each.
        var moves = devices.Where(d => d.SiteId == sites[0]).Select(d =>
        {
            var copy = d.Clone();
            copy.SiteId = sites[1];
            return new DeviceChange(DeviceChangeKind.Updated, d.Id, copy);
        }).ToList();
        PerfScale.Measure(output, $"{moves.Count} moves", TimeSpan.FromSeconds(5), () => feed.PublishMany(moves));
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal(new[] { keys[0], keys[1] }.Order(), changed.Order());
    }
}
