using System.Text.Json.Nodes;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Events;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// After a server start every site is "starting" until its node is back (or the start window passed): the devices show
/// Site offline meanwhile, but no event changes because of it (CLAUDE.md "# Sites", Site offline; stream 2 found the edges).
/// </summary>
public sealed class SiteStartupGraceTests
{
    [Fact]
    public async Task DeviceEventsStartFromTheStoredStatusWhileTheSiteStarts()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B", online: false);
        site.Starting = true;
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await rig.Devices.AddAsync(new Device { Serial = "ACCC8E300001", Address = "192.168.0.20", SiteId = site.Id, Status = DeviceStatus.Ok, Model = "P3265-V" }, CancellationToken.None);
        var down = await rig.Devices.AddAsync(new Device { Serial = "ACCC8E300002", Address = "192.168.0.21", SiteId = site.Id, Status = DeviceStatus.Unreachable, Model = "P3265-V" }, CancellationToken.None);
        Assert.Equal(DeviceStatus.SiteOffline, (await rig.Devices.GetAsync(down.Id, CancellationToken.None))!.Status);

        await rig.SaveAsync(EventRulesRig.Rule(BuiltInEvents.Unreachable, rig.WithoutDevice(new JsonObject { ["text"] = "{address} {state}" })));
        await Eventually.TrueAsync(async () =>
        {
            await rig.SettleAsync();
            return !rig.NoDevice.Runs.IsEmpty;
        }, "the run of the device that was unreachable before the start");
        Assert.Equal("192.168.0.21 on|", Assert.Single(rig.NoDevice.Runs).Text);

        // The node is back: the stored statuses are confirmed, nothing turns off or on again.
        sites.Settle(site.Id, online: true);
        await rig.Devices.UpdateAsync(down.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Single(rig.NoDevice.Runs);
    }

    [Fact]
    public async Task TheSiteEventsWaitWhileTheSiteStartsAndTurnOnOnlyWhenItStaysAway()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Warehouse", online: false);
        site.Starting = true;
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            db.Sites.Add(new Site { Id = site.Id, Name = "Warehouse", NormalizedName = "WAREHOUSE", NodeKeyFingerprint = "AB", NodeId = "n", BoundUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        sites.SetOnline(site.Id, false); // a change: the types are read again (still starting)
        var offlineType = SiteEventSource.OfflineTypeId(site.Id);
        await Eventually.TrueAsync(() => rig.Catalog.FindEventType(offlineType) is not null, "the site type");
        await rig.SaveAsync(EventRulesRig.Rule(offlineType, rig.WithoutDevice(new JsonObject { ["text"] = "{site} {state}" })));
        await rig.SettleAsync();
        Assert.Empty(rig.NoDevice.Runs); // starting: nothing known, nothing fires

        sites.Settle(site.Id, online: false); // the start window passed without the node
        await Eventually.TrueAsync(async () =>
        {
            await rig.SettleAsync();
            return !rig.NoDevice.Runs.IsEmpty;
        }, "Site offline after the start window");
        Assert.Equal("Warehouse on|", Assert.Single(rig.NoDevice.Runs).Text);
    }

    [Fact]
    public async Task ANodeBackWithinTheStartWindowFiresNothing()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Warehouse", online: false);
        site.Starting = true;
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            db.Sites.Add(new Site { Id = site.Id, Name = "Warehouse", NormalizedName = "WAREHOUSE", NodeKeyFingerprint = "AB", NodeId = "n", BoundUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        sites.SetOnline(site.Id, false);
        var offlineType = SiteEventSource.OfflineTypeId(site.Id);
        await Eventually.TrueAsync(() => rig.Catalog.FindEventType(offlineType) is not null, "the site type");
        await rig.SaveAsync(EventRulesRig.Rule(offlineType, rig.WithoutDevice(new JsonObject { ["text"] = "{site} {state}" })));
        await rig.SaveAsync(EventRulesRig.Rule(SiteEventSource.AnyOfflineTypeId, rig.WithoutDevice(new JsonObject { ["text"] = "{count} {state}" })));

        sites.Settle(site.Id, online: true);
        await rig.SettleAsync();
        Assert.Empty(rig.NoDevice.Runs);
    }
}
