using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>The compiled rules of a group and the views of callers: what is in, what is refused, and that it fails closed.</summary>
public sealed class DeviceViewTests : DeviceGroupTestBase
{
    [Fact]
    public async Task SitesThisServerTagsAndSerialsCoverDevicesAndExceptWins()
    {
        var b = Sites.Add("Building B");
        var w = Sites.Add("Warehouse");
        var server1 = await AddDeviceAsync("ACCC8E000001");
        var atB = await AddDeviceAsync("ACCC8E000002", siteId: b);
        var atBExcepted = await AddDeviceAsync("ACCC8E000003", siteId: b);
        var atW = await AddDeviceAsync("ACCC8E000004", siteId: w);
        var tagged = await AddDeviceAsync("ACCC8E000005", siteId: w, tags: ["PTZ"]);
        var single = await AddDeviceAsync("ACCC8E000006", siteId: w);
        var ptz = await TagIdAsync("PTZ");

        var group = (await SaveGroupAsync("B", sites: [b], tags: [ptz], serials: ["ACCC8E000006"], except: ["ACCC8E000003"])).Group;
        var anna = await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var view = await Views.ForAsync(CallerOf(anna), Ct);

        Assert.False(view.HasAllDevices);
        Assert.Equal(group.Id, view.GroupId);
        Assert.Equal([atB.Id, tagged.Id, single.Id], (await view.ListAsync(Ct)).Select(d => d.Id));
        Assert.True(view.Contains(atB));
        Assert.False(view.Contains(atBExcepted));
        Assert.False(view.Contains(server1));
        Assert.False(view.Contains(atW));
        Assert.Equal(3, await view.CountAsync(Ct));

        // "This server" = exactly the devices without a site.
        var server = (await SaveGroupAsync("Server", thisServer: true)).Group;
        var serverView = await Views.ForScopeAsync(DeviceScope.Group(server.Id), "x", Ct);
        Assert.Equal([server1.Id], (await serverView.ListAsync(Ct)).Select(d => d.Id));
    }

    [Fact]
    public async Task ABatchWithAnyDeviceOutsideTheViewOrUnknownIsRefusedWholeWithTheSameText()
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);

        var ok = await view.GetAsync([inside.Id, inside.Id], Ct);
        Assert.Equal([inside.Id], ok.Ids);
        Assert.Same(view, ok.View);

        var refusedOutside = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => view.GetAsync([inside.Id, outside.Id], Ct));
        var refusedUnknown = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => view.GetAsync([inside.Id, Guid.NewGuid()], Ct));
        Assert.Equal("1 device is no longer available.", refusedOutside.Message);
        Assert.Equal(refusedOutside.Message, refusedUnknown.Message);
        Assert.Null(await view.FindAsync(outside.Id, Ct));

        // Users with all devices: unknown ids refuse the batch the same way (today Remove and Refresh skipped them).
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        var refusedForAll = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => all.GetAsync([outside.Id, Guid.NewGuid(), Guid.NewGuid()], Ct));
        Assert.Equal("2 devices are no longer available.", refusedForAll.Message);
        Assert.Equal(2, (await all.GetOrAllAsync([], Ct)).Count);

        // "Empty means all" = the caller's view.
        Assert.Equal([inside.Id], (await view.GetOrAllAsync([], Ct)).Ids);
    }

    [Fact]
    public async Task TheDevicesOfABatchComeInTheOrderOfTheRequest()
    {
        var b = Sites.Add("Building B");
        var first = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var second = await AddDeviceAsync("ACCC8E000002", siteId: b);
        var third = await AddDeviceAsync("ACCC8E000003", siteId: b);
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        var all = await Views.ForScopeAsync(DeviceScope.All, "admin", Ct);
        Guid[] request = [third.Id, first.Id, third.Id, second.Id];

        foreach (var viewed in new[] { await view.GetAsync(request, Ct), await all.GetAsync(request, Ct), await Views.System.GetAsync(request, Ct) })
        {
            Assert.Equal([third.Id, first.Id, second.Id], viewed.Ids);
            Assert.Equal(viewed.Ids, viewed.Select(d => d.Id));
        }
    }

    [Fact]
    public async Task ARenamedTagStaysAndADeletedTagOrRemovedSiteDropsOutAtOnce()
    {
        var b = Sites.Add("Building B");
        var atB = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var tagged = await AddDeviceAsync("ACCC8E000002", tags: ["PTZ"]);
        var ptz = await TagIdAsync("PTZ");
        var group = (await SaveGroupAsync("G", sites: [b], tags: [ptz])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        Assert.Equal(2, await view.CountAsync(Ct));

        await Tags.UpdateAsync("PTZ", "Domes", TagColor.None, Ct);
        var renamed = (await Devices.GetAsync(tagged.Id, Ct))!;
        Assert.Equal(["Domes"], renamed.Tags);
        Assert.True(view.Contains(renamed));
        Assert.Equal(2, await view.CountAsync(Ct));

        await Tags.DeleteAsync("Domes", Ct);
        Assert.False(view.Contains((await Devices.GetAsync(tagged.Id, Ct))!));
        Assert.Equal([atB.Id], (await view.IdsAsync(Ct)).ToList());

        // A site removed with Keep devices: its kept devices are neither in the site's groups nor "This server".
        Sites.Remove(b);
        Assert.False(view.Contains(atB));
        Assert.Empty(await view.IdsAsync(Ct));
        var server = (await SaveGroupAsync("Server", thisServer: true)).Group;
        Assert.False((await Views.ForScopeAsync(DeviceScope.Group(server.Id), null, Ct)).Contains(atB));
    }

    [Fact]
    public async Task ASingleSerialIsDormantUntilAUserAllowedToChooseItAddsTheDevice()
    {
        var group = (await SaveGroupAsync("Singles", serials: ["ACCC8E000001", "ACCC8E000002", "ACCC8E000003"])).Group;
        var byAll = await AddDeviceAsync("ACCC8E000001", addedByAllDevices: true);
        var byOther = await AddDeviceAsync("ACCC8E000002", addedByAllDevices: false, addedByGroupId: Guid.NewGuid());
        var byOwnGroup = await AddDeviceAsync("ACCC8E000003", addedByAllDevices: false, addedByGroupId: group.Id);
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);

        Assert.True(view.Contains(byAll));
        Assert.False(view.Contains(byOther)); // re-added by someone outside the group: does not join it
        Assert.True(view.Contains(byOwnGroup));
    }

    [Fact]
    public async Task TheAllowedIdsFollowTheDeviceTableAndTheGroupsRevision()
    {
        var b = Sites.Add("Building B");
        await AddDeviceAsync("ACCC8E000001", siteId: b);
        var group = (await SaveGroupAsync("G", sites: [b])).Group;
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Id), "anna", Ct);
        Assert.Null(view.CachedIds()); // nothing read yet: no answer without a read
        var first = await view.IdsAsync(Ct);
        Assert.Single(first);
        Assert.Same(first, await view.IdsAsync(Ct)); // cached while nothing changed
        Assert.Same(first, view.CachedIds());

        var added = await AddDeviceAsync("ACCC8E000002", siteId: b);
        Assert.Null(view.CachedIds()); // a device came in: the cached ids are no longer current
        var withAdded = await view.IdsAsync(Ct);
        Assert.Contains(added.Id, withAdded); // a new device of a chosen site is in at once

        // A status or firmware change keeps the ids; a change of what groups decide on (tags, site) reads them again.
        await Devices.UpdateAsync(added.Id, d => d.Status = DeviceStatus.Unreachable, Ct);
        await Devices.UpdateAsync(added.Id, d => d.FirmwareVersion = "12.0.0", Ct);
        Assert.Same(withAdded, await view.IdsAsync(Ct));
        await Devices.UpdateAsync(added.Id, d => d.SiteId = null, Ct);
        Assert.DoesNotContain(added.Id, await view.IdsAsync(Ct));
        await Devices.UpdateAsync(added.Id, d => d.SiteId = b, Ct);
        Assert.Contains(added.Id, await view.IdsAsync(Ct));

        await SaveGroupAsync("G", thisServer: true, id: group.Id, revision: group.Revision);
        Assert.Empty(await view.IdsAsync(Ct)); // the new rules apply to the same view
    }

    [Fact]
    public async Task AdministratorsAndUsersWithAllDevicesSeeEverythingAndNoCallerNeverMeansAll()
    {
        await AddDeviceAsync("ACCC8E000001");
        var group = (await SaveGroupAsync("Empty")).Group;
        var admin = await Users.AddAsync("boss", "pw", BuiltInRoles.AdministratorId, DeviceScope.Group(group.Id), Ct);
        var adminView = await Views.ForAsync(CallerOf(admin), Ct);
        Assert.True(adminView.HasAllDevices); // a group on an administrator is ignored
        Assert.Single(await adminView.ListAsync(Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Views.ForAsync(null, Ct));

        // A caller built without its devices reaches nothing.
        var bare = new AuthenticatedCaller(Guid.NewGuid(), "x", BuiltInRoles.OperatorId, PermissionSet.All, "", "", "h");
        Assert.Equal(CallerDevices.None, bare.Devices);
        var bareView = await Views.ForAsync(bare, Ct);
        Assert.Empty(await bareView.ListAsync(Ct));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => bareView.GetAsync([Guid.NewGuid()], Ct));

        // An unknown group: no devices.
        var unknown = await Views.ForScopeAsync(DeviceScope.Group(Guid.NewGuid()), "x", Ct);
        Assert.Empty(await unknown.ListAsync(Ct));
        Assert.True(Views.System.HasAllDevices);
        Assert.True(Views.System.IsSystem);
    }

    [Fact]
    public async Task ThePreviewCountsRulesAndTheSummaryNamesSitesAndTags()
    {
        var b = Sites.Add("Building B");
        await AddDeviceAsync("ACCC8E000001", siteId: b);
        await AddDeviceAsync("ACCC8E000002", tags: ["PTZ"]);
        await AddDeviceAsync("ACCC8E000003");
        var ptz = await TagIdAsync("PTZ");
        var rules = new DeviceGroupRules(false, [b], [ptz], ["ACCC8E000003"], ["ACCC8E000001"]);

        Assert.Equal(2, await Views.CountAsync(rules, null, Ct));
        Assert.Equal("Site Building B, tag PTZ, 1 device, except 1", DeviceGroupSummary.Text(rules, Views.Catalog));
        Assert.Equal("Site Building B, tag PTZ, 1 device", DeviceGroupSummary.Text(rules, Views.Catalog, withExcept: false));
        Assert.Equal("1 site, 1 tag, 1 device, except 1", DeviceGroupStore.Describe(rules));
        Assert.Equal(DeviceGroupTexts.NoDevices, DeviceGroupSummary.Text(DeviceGroupRules.Empty, Views.Catalog));
    }

    [Fact]
    public void TheFilterIsAFewHashLookups()
    {
        var site = Guid.NewGuid();
        var tags = new Dictionary<Guid, string> { [Guid.NewGuid()] = "unused" };
        var tagId = Guid.NewGuid();
        tags[tagId] = "PTZ";
        var filter = DeviceGroupFilter.Compile(Guid.NewGuid(), 3, new DeviceGroupRules(false, [site], [tagId], [], []), tags, _ => true, default);

        Assert.True(filter.Contains(new DeviceScopeRow(Guid.NewGuid(), "ACCC8E000001", site, [], true, null)));
        Assert.True(filter.Contains(new DeviceScopeRow(Guid.NewGuid(), "ACCC8E000002", null, ["ptz"], true, null))); // case-insensitive
        Assert.False(filter.Contains(new DeviceScopeRow(Guid.NewGuid(), "ACCC8E000003", null, ["Dome"], true, null)));
        Assert.Equal(3, filter.Revision);
        Assert.True(DeviceGroupFilter.Everything.Contains(new DeviceScopeRow(Guid.NewGuid(), "X", null, [], false, null)));
        Assert.False(DeviceGroupFilter.Nothing.Contains(new DeviceScopeRow(Guid.NewGuid(), "ACCC8E000001", site, ["PTZ"], true, null)));
    }
}
