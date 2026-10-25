using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// <see cref="HostDeviceScopes.Changed"/> follows every change that may move devices into or out of a group (devices added,
/// removed, moved, tagged; the tag and site catalog), coalesced per group, so the sweeps of the plugins prune rows of
/// devices that left (docs/spec/device-scope.md, Plugins). An update that changes nothing a group reads raises nothing.
/// </summary>
public sealed class HostDeviceScopesTests : DeviceGroupTestBase
{
    [Fact]
    public async Task DevicesThatEnterOrLeaveAGroupRaiseChangedOnceAfterTheWindow()
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var key = group.Id.ToString();
        var time = new FakeTimeProvider();
        using var scopes = new HostDeviceScopes(Groups, Views, time);
        await scopes.LoadAsync(Ct);
        var changed = new ConcurrentQueue<string>();
        scopes.Changed += (_, k) => changed.Enqueue(k);

        // A status change and a change of a device outside every group: nothing a group reads.
        await Devices.UpdateAsync(inside.Id, d => d.Status = DeviceStatus.Unreachable, Ct);
        await Devices.UpdateAsync(outside.Id, d => d.FirmwareVersion = "12.0.0", Ct);
        await AddDeviceAsync("ACCC8E000003");
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Empty(changed);

        // A device moved into the group and one added to it: one Changed after the window.
        await Devices.UpdateAsync(outside.Id, d => d.SiteId = b, Ct);
        await AddDeviceAsync("ACCC8E000004", siteId: b);
        Assert.Empty(changed);
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal([key], changed);

        // Removed: Changed.
        changed.Clear();
        await Devices.RemoveAsync(inside.Id, Ct);
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal([key], changed);
    }

    [Fact]
    public async Task ATagRuleFollowsTaggingAndTheCatalogFollowsARemovedSite()
    {
        var b = Sites.Add("Building B");
        var device = await AddDeviceAsync("ACCC8E000001", tags: ["PTZ"]);
        var other = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("PTZ", tags: [await TagIdAsync("PTZ")])).Group;
        var key = group.Id.ToString();
        var time = new FakeTimeProvider();
        using var scopes = new HostDeviceScopes(Groups, Views, time);
        await scopes.LoadAsync(Ct);
        var changed = new ConcurrentQueue<string>();
        scopes.Changed += (_, k) => changed.Enqueue(k);

        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([other.Id], Ct), ["PTZ"], [], Ct);
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal([key], changed);

        changed.Clear();
        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([device.Id], Ct), [], ["PTZ"], Ct);
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal([key], changed);

        // A site that goes: every group may have other devices now.
        changed.Clear();
        Sites.Remove(b);
        time.Advance(HostDeviceScopes.CoalesceWindow);
        Assert.Equal([key], changed);
    }

    [Fact]
    public async Task AnOlderChangeRaisedAfterANewerOneNeverPutsADeviceBackIntoItsGroup()
    {
        var b = Sites.Add("Building B");
        var device = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var key = (await SaveGroupAsync("B", sites: [b])).Group.Id.ToString();
        using var outOfOrder = new OutOfOrderFeed(Views.Feed);
        using var scopes = new HostDeviceScopes(Groups, Views, new FakeTimeProvider());
        await scopes.LoadAsync(Ct);
        Assert.True(scopes.Contains(key, device.Id));

        // A site loop publishes its copy at site B; the device moves out meanwhile and that change reaches the handlers first.
        var moved = device.Clone();
        moved.SiteId = null;
        await outOfOrder.PublishAsync(
            new DeviceChange(DeviceChangeKind.Updated, device.Id, device.Clone()),
            new DeviceChange(DeviceChangeKind.Updated, device.Id, moved));

        Assert.False(scopes.Contains(key, device.Id));

        // Removed, then an older copy: the device stays out.
        await outOfOrder.PublishAsync(
            new DeviceChange(DeviceChangeKind.Updated, device.Id, device.Clone()),
            new DeviceChange(DeviceChangeKind.Removed, device.Id, null));
        Assert.False(scopes.Contains(key, device.Id));
    }

    [Fact]
    public async Task OverlappingLoadsReadTheDevicesOnce()
    {
        await AddDeviceAsync("ACCC8E000001");
        using var scopes = new HostDeviceScopes(Groups, Views, new FakeTimeProvider());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => scopes.LoadAsync(Ct))));
        await scopes.LoadAsync(Ct);

        // Two reads that overlap could bring back a device removed between them: every caller shares the one read.
        Assert.Equal(1, scopes.RowReads);
    }

    [Fact]
    public async Task ChangedIsRaisedWithoutTheCallerOfThePublishingCall()
    {
        var b = Sites.Add("Building B");
        _ = (await SaveGroupAsync("B", sites: [b])).Group;
        var time = new FakeTimeProvider();
        using var scopes = new HostDeviceScopes(Groups, Views, time);
        await scopes.LoadAsync(Ct);
        Auth.AuthenticatedCaller? seen = null;
        var raised = 0;
        scopes.Changed += (_, _) =>
        {
            seen = Auth.CallerContext.Current;
            raised++;
        };

        var user = await Users.AddAsync("anna", "pw", Auth.BuiltInRoles.OperatorId, Auth.DeviceScope.All, Ct);
        using (Auth.CallerContext.Use(CallerOf(user)))
        {
            await AddDeviceAsync("ACCC8E000001", siteId: b);
            time.Advance(HostDeviceScopes.CoalesceWindow);
        }

        Assert.Equal(1, raised);
        Assert.Null(seen);
    }
}
