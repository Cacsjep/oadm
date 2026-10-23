using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Compliance;

/// <summary>
/// Compliance checks with an owner (docs/spec/device-scope.md, Shared objects): a check of a group runs only on the group's
/// devices and follows them, users of a group see and change only their group's checks, saving refuses devices and tags
/// outside the owner's view, only administrators choose the owner, a deleted group turns its checks off.
/// </summary>
public sealed class ComplianceOwnerTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static async Task<(Guid Group, DeviceView View)> GroupAsync(ComplianceRig rig, string tag)
    {
        var tagId = (await rig.Tags.ListDefinitionsAsync(Ct)).FirstOrDefault(t => t.Name == tag)?.Id
            ?? (await rig.Tags.CreateAsync(tag, TagColor.None, Ct)).Id;
        var saved = await rig.Groups.SaveAsync(null, "Group " + tag, "", new DeviceGroupRules(false, [], [tagId], [], []), 0, Ct);
        return (saved.Group.Id, await rig.Views.ForScopeAsync(DeviceScope.Group(saved.Group.Id), "operator", Ct));
    }

    private static ComplianceCheck Reachable(DeviceSelector? devices = null) => new()
    {
        Name = "Reachable here",
        TypeId = BuiltInComplianceTypes.ReachableId,
        Devices = devices ?? new DeviceSelector { Mode = DeviceSelectorMode.All },
        Severity = ComplianceSeverity.Problem,
    };

    [Fact]
    public async Task TheDefaultChecksBelongToAllDevices()
    {
        await using var rig = await ComplianceRig.StartAsync();
        Assert.All(rig.Engine.ListChecks(), c => Assert.Equal(DeviceScope.All, c.Check.Owner));
    }

    [Fact]
    public async Task ACheckOfAGroupRunsOnlyOnItsDevicesAndFollowsThem()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var lobby = await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Unreachable, "Lobby");
        var yard = await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Unreachable, "Yard");
        var (group, view) = await GroupAsync(rig, "Lobby");

        var (check, _) = await rig.Service.SaveAsync(Reachable(), view, null, mayChooseOwner: false, Ct);
        Assert.Equal(DeviceScope.Group(group), check.Owner);
        await rig.WaitForAsync(check.Id, lobby.Id, ComplianceOutcome.Failed);
        Assert.Null(rig.Result(check.Id, yard.Id));
        Assert.Equal(1, rig.Engine.ListChecks().Single(c => c.Check.Id == check.Id).Failed);

        // Tagged into the group: the check covers it; the group's rules change: it follows them.
        await rig.Tags.SetDeviceTagsAsync(await rig.AllView.GetAsync([yard.Id], Ct), ["Lobby"], [], Ct);
        await rig.WaitForAsync(check.Id, yard.Id, ComplianceOutcome.Failed);
        var none = await rig.Groups.SaveAsync(group, "Group Lobby", "", new DeviceGroupRules(false, [], [], [], []), 0, Ct);
        Assert.Equal(2, none.Group.Revision);
        await Eventually.TrueAsync(() => rig.Result(check.Id, lobby.Id) is null && rig.Result(check.Id, yard.Id) is null, "the results of devices outside the group are gone");
    }

    [Fact]
    public async Task UsersOfAGroupSeeAndChangeOnlyTheirGroupsChecks()
    {
        await using var rig = await ComplianceRig.StartAsync();
        await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Ok, "Lobby");
        await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Ok, "Yard");
        var (_, lobby) = await GroupAsync(rig, "Lobby");
        var (_, yard) = await GroupAsync(rig, "Yard");
        var (mine, _) = await rig.Service.SaveAsync(Reachable(), lobby, null, false, Ct);

        Assert.Equal([mine.Id], rig.Service.ListChecks(lobby).Select(c => c.Check.Id));
        Assert.Empty(rig.Service.ListChecks(yard));
        Assert.Equal(3, rig.Service.ListChecks(rig.AllView).Count); // the defaults and the group's check

        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Service.SaveAsync(rig.Engine.GetCheck(mine.Id)!, yard, null, false, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Service.SetEnabledAsync(mine.Id, false, yard, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Service.DeleteAsync(BuiltInComplianceTypes.ReachableCheckId, lobby, Ct));
        var lobbyDevices = await lobby.AllAsync(Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Service.CheckNowAsync([BuiltInComplianceTypes.ReachableCheckId], lobbyDevices, Ct));

        // An administrator editing it keeps the owner; only an administrator gives it another one.
        var edited = rig.Engine.GetCheck(mine.Id)!;
        edited.Name = "Lobby reachable";
        var (kept, _) = await rig.Service.SaveAsync(edited, rig.AllView, null, mayChooseOwner: true, Ct);
        Assert.Equal(lobby.GroupId, kept.Owner.GroupId);
        kept = rig.Engine.GetCheck(mine.Id)!;
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Service.SaveAsync(kept, lobby, DeviceScope.All, false, Ct));
        Assert.Equal(OwnerRules.OnlyAdministrators, refused.Message);
        Assert.Equal(lobby.ScopeKey, rig.Engine.EventTypes.Single(t => t.Id == ComplianceEngine.EventTypeId(mine.Id)).OwnerScopeKey);
        var typesChanged = 0;
        rig.Engine.EventTypesChanged += (_, _) => Interlocked.Increment(ref typesChanged);
        var (moved, _) = await rig.Service.SaveAsync(kept, rig.AllView, DeviceScope.All, true, Ct);
        Assert.Equal(DeviceScope.All, moved.Owner);
        Assert.Empty(rig.Service.ListChecks(lobby));

        // The check's event type names its owner (Dashboards shows it only to that owner's users) and follows a new one.
        Assert.Equal(1, typesChanged);
        Assert.Equal(Sdk.Plugins.ICallContext.AllDevicesScopeKey, rig.Engine.EventTypes.Single(t => t.Id == ComplianceEngine.EventTypeId(mine.Id)).OwnerScopeKey);
    }

    [Fact]
    public async Task SavingRefusesDevicesAndTagsOutsideTheOwnersDevices()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Ok, "Lobby", "Yard");
        var outside = await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Ok, "Yard");
        await rig.Devices.UpdateAsync(outside.Id, d => d.Tags = ["Yard", "Hidden"], Ct); // Hidden: a name without a definition
        await rig.Tags.CreateAsync("Yard", TagColor.None, Ct);
        var (_, view) = await GroupAsync(rig, "Lobby");

        foreach (var id in new[] { outside.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() =>
                rig.Service.SaveAsync(Reachable(new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [inside.Id, id] }), view, null, false, Ct));
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Message);
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            rig.Service.SaveAsync(Reachable(new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Hidden"] }), view, null, false, Ct));

        // Defined tags (names every user sees) are fine: the check still covers only the group's devices. The group's device
        // with the tag is checked (the round ran); the outside one with it is not.
        var (check, _) = await rig.Service.SaveAsync(Reachable(new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Yard"] }), view, null, false, Ct);
        await rig.WaitForAsync(check.Id, inside.Id, ComplianceOutcome.Passed);
        Assert.Null(rig.Result(check.Id, outside.Id));
        Assert.Empty(rig.Service.ListChecks(view).Single().Check.Devices.DeviceIds);
    }

    [Fact]
    public async Task AUserWithoutDevicesSavesNoCheck()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var (group, _) = await GroupAsync(rig, "Lobby");
        await rig.Groups.DeleteAsync(group, Ct);
        var before = rig.Engine.ListChecks().Count;

        foreach (var scope in new[] { DeviceScope.None, DeviceScope.Group(group) })
        {
            var view = await rig.Views.ForScopeAsync(scope, "operator", Ct);
            var disabled = Reachable();
            disabled.Enabled = false;
            foreach (var check in new[] { Reachable(), disabled })
            {
                var refused = await Assert.ThrowsAsync<Sdk.Plugins.DeviceAccessRefusedException>(() => rig.Service.SaveAsync(check, view, null, false, Ct));
                Assert.Equal("Your account has no devices.", refused.Message);
            }
        }

        Assert.Equal(before, rig.Engine.ListChecks().Count);
    }

    [Fact]
    public async Task CheckNowOfAGroupUserRunsOnlyOnTheView()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var inside = await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Ok, "Lobby");
        var outside = await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Ok);
        var (_, view) = await GroupAsync(rig, "Lobby");
        var (check, _) = await rig.Service.SaveAsync(new ComplianceCheck
        {
            Name = "Reads",
            TypeId = "test.read",
            Devices = new DeviceSelector { Mode = DeviceSelectorMode.All },
            IntervalMinutes = 60,
            Severity = ComplianceSeverity.Problem,
        }, view, null, false, Ct);
        await rig.WaitForAsync(check.Id, inside.Id, ComplianceOutcome.Passed);
        var before = rig.Plugin.Read.Evaluations.Count;

        await rig.Service.CheckNowAsync([], await view.GetAsync([], Ct), Ct);
        await Eventually.TrueAsync(() => rig.Plugin.Read.Evaluations.Count > before, "the device of the group read again");
        Assert.DoesNotContain(rig.Plugin.Read.Evaluations, e => e == outside.Id);
    }

    [Fact]
    public async Task DeletingTheGroupTurnsItsChecksOffWithoutAnOwner()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Unreachable, "Lobby");
        var (group, view) = await GroupAsync(rig, "Lobby");
        var (check, _) = await rig.Service.SaveAsync(Reachable(), view, null, false, Ct);
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Failed);

        await rig.Groups.DeleteAsync(group, Ct);
        await Eventually.TrueAsync(() => rig.Engine.GetCheck(check.Id) is { Enabled: false, Owner.IsNone: true }, "the check is off without an owner");
        await Eventually.TrueAsync(() => rig.Result(check.Id, device.Id) is null, "its results are gone");

        // It stays off until an administrator gives it an owner.
        var refused = await Assert.ThrowsAsync<ComplianceValidationException>(() => rig.Service.SetEnabledAsync(check.Id, true, rig.AllView, Ct));
        Assert.Equal(DeviceGroupTexts.ChooseDevices, refused.Message);
        var again = rig.Engine.GetCheck(check.Id)!;
        again.Enabled = true;
        var (owned, _) = await rig.Service.SaveAsync(again, rig.AllView, DeviceScope.All, true, Ct);
        Assert.True(owned.Enabled);
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Failed);
    }
}
