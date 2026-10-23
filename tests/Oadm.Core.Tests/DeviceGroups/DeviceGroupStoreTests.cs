using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Events;
using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>The device groups table: create, change with revisions, delete (fail closed), and what uses tags and sites.</summary>
public sealed class DeviceGroupStoreTests : DeviceGroupTestBase
{
    [Fact]
    public async Task AGroupIsCreatedChangedAndListedWithItsRevision()
    {
        var site = Sites.Add("Building B");
        var created = await SaveGroupAsync("Building B", sites: [site], serials: ["ac:cc:8e:00:00:01"]);
        Assert.True(created.Created);
        Assert.Equal(1, created.Group.Revision);
        Assert.Equal(["ACCC8E000001"], created.Group.Serials);

        var changed = await SaveGroupAsync("Building B", thisServer: true, sites: [site], id: created.Group.Id, revision: 1);
        Assert.False(changed.Created);
        Assert.Equal(2, changed.Group.Revision);
        Assert.Contains("rules", changed.Changes, StringComparison.Ordinal);

        // Nothing changed: no new revision, nothing to audit.
        var same = await SaveGroupAsync("Building B", thisServer: true, sites: [site], id: created.Group.Id, revision: 2);
        Assert.Equal(2, same.Group.Revision);
        Assert.Equal(string.Empty, same.Changes);

        var listed = Assert.Single(await Groups.ListAsync(Ct));
        Assert.Equal("Building B", listed.Name);
        Assert.True(listed.ThisServer);
        Assert.Equal(2, Groups.RevisionOf(listed.Id));
    }

    [Fact]
    public async Task ASaveOnAnOlderRevisionANameInUseAndABadSerialAreRefused()
    {
        var group = (await SaveGroupAsync("North")).Group;
        await SaveGroupAsync("North", thisServer: true, id: group.Id, revision: 1);

        var stale = await Assert.ThrowsAsync<DeviceGroupException>(() => SaveGroupAsync("North", id: group.Id, revision: 1));
        Assert.Equal(DeviceGroupError.Precondition, stale.Error);
        Assert.Equal(DeviceGroupTexts.ChangedMeanwhile, stale.Message);

        var taken = await Assert.ThrowsAsync<DeviceGroupException>(() => SaveGroupAsync("north"));
        Assert.Equal(DeviceGroupError.AlreadyExists, taken.Error);

        var serial = await Assert.ThrowsAsync<DeviceGroupException>(() => SaveGroupAsync("South", serials: ["not-a-mac"]));
        Assert.Equal(DeviceGroupError.InvalidArgument, serial.Error);

        var empty = await Assert.ThrowsAsync<DeviceGroupException>(() => SaveGroupAsync("  "));
        Assert.Equal(DeviceGroupTexts.EnterName, empty.Message);
    }

    [Fact]
    public async Task DeletingAGroupLeavesItsUsersWithoutDevicesAndTurnsOffWhatItOwnedInOneTransaction()
    {
        var group = (await SaveGroupAsync("Building B", thisServer: true)).Group;
        var other = (await SaveGroupAsync("Warehouse", thisServer: true)).Group;
        var anna = await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var bob = await Users.AddAsync("bob", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(other.Id), Ct);
        await using (var db = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            db.EventRules.Add(new EventRuleEntity { Id = Guid.NewGuid(), Name = "Rule", Enabled = true }); // rules have no owner
            db.ComplianceChecks.Add(new ComplianceCheckEntity { Id = Guid.NewGuid(), Name = "Owned", TypeId = "t", Enabled = true, OwnerGroupId = group.Id });
            await db.SaveChangesAsync();
        }

        var changedUsers = new List<Guid>();
        Users.UserDeviceAccessChanged += (_, id) => changedUsers.Add(id);
        DeviceGroupDeleteResult? deleted = null;
        Groups.Deleted += (_, e) => deleted = e;

        var result = await Groups.DeleteAsync(group.Id, Ct);

        Assert.Equal(["anna"], result.UserNames);
        Assert.Equal(["1 compliance check"], result.DisabledObjects);
        Assert.Same(result, deleted);
        Assert.Equal([anna.Id], changedUsers);
        Assert.Null(await Groups.GetAsync(group.Id, Ct));

        var users = (await Users.ListAsync(Ct)).ToDictionary(u => u.UserName);
        Assert.Equal(DeviceScope.None, users["anna"].DeviceAccess);
        Assert.False(users["anna"].HasAllDevices);
        Assert.Equal(DeviceScope.Group(other.Id), users["bob"].DeviceAccess);

        await using (var db = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            Assert.True((await db.EventRules.AsNoTracking().SingleAsync()).Enabled);
            var check = await db.ComplianceChecks.AsNoTracking().SingleAsync();
            Assert.False(check.Enabled);
            Assert.True(check.Owner.IsNone);
        }

        // The deleted group's user reaches nothing, also through a caller built before the delete (fail closed).
        var view = await Views.ForAsync(CallerOf(anna), Ct);
        Assert.Empty(await view.ListAsync(Ct));
        Assert.Equal(0, await view.CountAsync(Ct));
        _ = bob;
    }

    [Fact]
    public async Task AUserGetsAGroupOnlyWhenItExistsAndNeverNoDevicesByChoice()
    {
        var unknown = await Assert.ThrowsAsync<AuthException>(() => Users.AddAsync("eve", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(Guid.NewGuid()), Ct));
        Assert.Equal(DeviceGroupTexts.UnknownGroup, unknown.Message);
        var none = await Assert.ThrowsAsync<AuthException>(() => Users.AddAsync("eve", "pw", BuiltInRoles.OperatorId, DeviceScope.None, Ct));
        Assert.Equal(DeviceGroupTexts.ChooseDevices, none.Message);

        var group = (await SaveGroupAsync("North")).Group;
        var eve = await Users.AddAsync("eve", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct);
        var raised = 0;
        Users.UserDeviceAccessChanged += (_, _) => raised++;
        var (updated, changes) = await Users.UpdateAsync(eve.Id, null, null, null, DeviceScope.Group(group.Id), null, Ct);
        Assert.Equal(DeviceScope.Group(group.Id), updated.DeviceAccess);
        Assert.Equal("devices North", changes);
        Assert.Equal(1, raised);

        // The same again changes nothing.
        var (_, again) = await Users.UpdateAsync(eve.Id, null, null, null, DeviceScope.Group(group.Id), null, Ct);
        Assert.Equal(string.Empty, again);
        Assert.Equal(1, raised);

        // A group in use cannot vanish under its users (the foreign key is restricted; only DeleteAsync clears them).
        await using var db = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            db.DeviceGroups.Remove(await db.DeviceGroups.SingleAsync());
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task TheStoreSaysWhichGroupsUseATagOrASite()
    {
        var site = Sites.Add("Building B");
        await AddDeviceAsync("ACCC8E000001", tags: ["PTZ"]);
        var ptz = await TagIdAsync("PTZ");
        await SaveGroupAsync("Cameras", tags: [ptz]);
        await SaveGroupAsync("B", sites: [site], tags: [ptz]);

        Assert.Equal(["B", "Cameras"], Groups.GroupsUsingTag(ptz));
        Assert.Equal(["B"], Groups.GroupsUsingSite(site));
        Assert.Contains(ptz, Groups.TagIdsInUse());
        Assert.Equal("Used by device groups B, Cameras", DeviceGroupTexts.UsedBy(Groups.GroupsUsingTag(ptz)));
        Assert.Equal("Used by device group B", DeviceGroupTexts.UsedBy(Groups.GroupsUsingSite(site)));
    }
}
