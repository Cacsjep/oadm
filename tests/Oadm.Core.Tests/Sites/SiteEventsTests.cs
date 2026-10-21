using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Events;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// The site events of the event rules (docs/spec/sites.md, Event rules): "Site offline: &lt;site&gt;" per site and "Any site
/// offline", server scope, types that follow the sites; devices of an offline site never fire "Device unreachable".
/// </summary>
public sealed class SiteEventsTests
{
    [Fact]
    public async Task EachSiteHasAnOfflineEventAndAnyOfflineCountsThem()
    {
        var sites = new FakeSiteTransport();
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        var building = sites.AddSite("Building B");
        var warehouse = sites.AddSite("Warehouse");
        await AddSiteRowAsync(rig, building.Id, "Building B", "10.1.0.5");
        await AddSiteRowAsync(rig, warehouse.Id, "Warehouse", "10.2.0.5");
        sites.SetOnline(warehouse.Id, true); // a change: the types are read again
        var offlineType = SiteEventSource.OfflineTypeId(warehouse.Id);
        await Eventually.TrueAsync(() => rig.Catalog.FindEventType(offlineType) is not null, "the site types");

        var type = rig.Catalog.FindEventType(offlineType)!;
        Assert.Equal("Site offline: Warehouse", type.Info.DisplayName);
        Assert.False(type.DeviceScope);
        Assert.True(type.Stateful);
        Assert.Equal(SiteEventSource.EventSourceName, type.SourceName);
        Assert.Equal("Any site offline", rig.Catalog.FindEventType(SiteEventSource.AnyOfflineTypeId)!.Info.DisplayName);

        await rig.SaveAsync(EventRulesRig.Rule(offlineType, rig.WithoutDevice(new JsonObject { ["text"] = "{event} {site} {address} {state}" })));
        await rig.SaveAsync(EventRulesRig.Rule(SiteEventSource.AnyOfflineTypeId, rig.WithoutDevice(new JsonObject { ["text"] = "{count}: {site} {state}" })));

        var any = new List<PublishedEvent>();
        using var subscription = rig.Engine.SubscriptionsFor("test").Subscribe(e =>
        {
            if (e.TypeId == SiteEventSource.AnyOfflineTypeId)
            {
                lock (any)
                {
                    any.Add(e);
                }
            }
        });

        sites.SetOnline(warehouse.Id, false);
        await WaitRunsAsync(rig, 2);
        sites.SetOnline(building.Id, false);
        await WaitAnyAsync(any, 2);
        sites.SetOnline(warehouse.Id, true);
        await WaitAnyAsync(any, 3);
        sites.SetOnline(building.Id, true);
        await WaitRunsAsync(rig, 4);
        await WaitAnyAsync(any, 4);

        var texts = rig.NoDevice.Runs.Select(r => r.Text).ToList();
        Assert.Contains("Site offline: Warehouse Warehouse 10.2.0.5 on|", texts);
        Assert.Contains(texts, t => t.StartsWith("Site offline: Warehouse ", StringComparison.Ordinal) && t.EndsWith(" off|", StringComparison.Ordinal));
        Assert.Contains("1: Warehouse on|", texts);
        Assert.Equal(4, texts.Count); // Any site offline stays on while the second site goes offline
        lock (any)
        {
            Assert.Equal(
                [(PublishedEventKind.On, "1", "Warehouse"), (PublishedEventKind.On, "2", "Building B, Warehouse"), (PublishedEventKind.On, "1", "Building B"), (PublishedEventKind.Off, "", "")],
                any.Select(e => (e.Kind, e.Data.GetValueOrDefault(SiteEventSource.CountField) ?? "", e.Data.GetValueOrDefault(SiteEventSource.SiteField) ?? "")));
            Assert.All(any, e => Assert.Null(e.DeviceId));
        }
    }

    private static Task WaitAnyAsync(List<PublishedEvent> any, int count) =>
        Eventually.TrueAsync(() =>
        {
            lock (any)
            {
                return any.Count >= count;
            }
        }, $"{count} Any site offline events");

    [Fact]
    public async Task ASiteGoingOfflineNeverFiresOrEndsDeviceUnreachable()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await using var publisher = new SiteStatusPublisher(sites, rig.Devices, rig.Devices.Changes);
        publisher.Start();
        var ok = await rig.Devices.AddAsync(new Device { Serial = "ACCC8E200001", Address = "192.168.0.20", SiteId = site.Id, Status = DeviceStatus.Ok, Model = "P3265-V" }, CancellationToken.None);
        var down = await rig.Devices.AddAsync(new Device { Serial = "ACCC8E200002", Address = "192.168.0.21", SiteId = site.Id, Status = DeviceStatus.Unreachable, Model = "P3265-V" }, CancellationToken.None);
        await rig.SaveAsync(EventRulesRig.Rule(BuiltInEvents.Unreachable, rig.WithoutDevice(new JsonObject { ["text"] = "{address} {state}" })));
        await WaitRunsAsync(rig, 1); // the device that was unreachable before
        Assert.Equal("192.168.0.21 on|", Assert.Single(rig.NoDevice.Runs).Text);

        var handled = publisher.Handled;
        sites.SetOnline(site.Id, false);
        await Eventually.TrueAsync(() => publisher.Handled > handled, "the site went offline");
        await rig.SettleAsync();
        handled = publisher.Handled;
        sites.SetOnline(site.Id, true);
        await Eventually.TrueAsync(() => publisher.Handled > handled, "the site is back");
        await rig.SettleAsync();

        Assert.Single(rig.NoDevice.Runs); // neither a new "on" for the device that is fine nor an "off" for the one that is not
        Assert.Equal(DeviceStatus.Ok, (await rig.Devices.GetAsync(ok.Id, CancellationToken.None))!.Status);
        Assert.Equal(DeviceStatus.Unreachable, (await rig.Devices.GetAsync(down.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ARemovedSiteTurnsTheStatusEventsOfItsKeptDevicesOff()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await using var publisher = new SiteStatusPublisher(sites, rig.Devices, rig.Devices.Changes);
        publisher.Start();
        var down = await rig.Devices.AddAsync(new Device { Serial = "ACCC8E200003", Address = "192.168.0.22", SiteId = site.Id, Status = DeviceStatus.Unreachable, Model = "P3265-V" }, CancellationToken.None);
        await rig.SaveAsync(EventRulesRig.Rule(BuiltInEvents.Unreachable, rig.WithoutDevice(new JsonObject { ["text"] = "{address} {state}" })));
        await WaitRunsAsync(rig, 1);

        // Removed with Keep devices: the device stays (Site offline, "(removed)"), but it never comes back through that site.
        sites.MarkRemoved(site.Id);
        await WaitRunsAsync(rig, 2);

        Assert.Equal(["192.168.0.22 on|", "192.168.0.22 off|"], rig.NoDevice.Runs.Select(r => r.Text));
        var kept = (await rig.Devices.GetAsync(down.Id, CancellationToken.None))!;
        Assert.Equal(DeviceStatus.SiteOffline, kept.Status);
        Assert.Equal("Building B (removed)", kept.SiteName);
        Assert.True(kept.SiteRemoved);
    }

    private static async Task WaitRunsAsync(EventRulesRig rig, int count)
    {
        await Eventually.TrueAsync(async () =>
        {
            await rig.SettleAsync();
            return rig.NoDevice.Runs.Count >= count;
        }, $"{count} action runs");
    }

    private static async Task AddSiteRowAsync(EventRulesRig rig, Guid id, string name, string address)
    {
        await using var db = await rig.DbFactory.CreateDbContextAsync();
        db.Sites.Add(new Site
        {
            Id = id,
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Address = address,
            NodeKeyFingerprint = id.ToString("N"),
            NodeId = "node-" + id.ToString("N"),
            BoundUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
