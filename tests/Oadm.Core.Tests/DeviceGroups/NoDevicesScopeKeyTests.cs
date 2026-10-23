using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// Users without devices never share a device scope key (fail closed): each has a key of their own, so state a plugin keeps
/// per scope (saved views, dashboards, triggers, shared scans) is never shared between two users whose groups were deleted.
/// </summary>
public sealed class NoDevicesScopeKeyTests : DeviceGroupTestBase
{
    [Fact]
    public async Task EachUserWithoutDevicesHasAKeyOfTheirOwnThatNoScopeAnswers()
    {
        var first = (await SaveGroupAsync("G1", thisServer: true)).Group;
        var second = (await SaveGroupAsync("G2", thisServer: true)).Group;
        var anna = await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(first.Id), Ct);
        var berta = await Users.AddAsync("berta", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(second.Id), Ct);
        await Groups.DeleteAsync(first.Id, Ct);
        await Groups.DeleteAsync(second.Id, Ct);
        anna = (await Users.GetAsync(anna.Id, Ct))!;
        berta = (await Users.GetAsync(berta.Id, Ct))!;
        Assert.True(anna.DeviceAccess.IsNone);

        var annaKey = (await Views.ForAsync(CallerOf(anna), Ct)).ScopeKey;
        var bertaKey = (await Views.ForAsync(CallerOf(berta), Ct)).ScopeKey;
        Assert.NotEqual(annaKey, bertaKey);
        Assert.Equal(annaKey, (await Views.ForAsync(CallerOf(anna), Ct)).ScopeKey); // the same user keeps it (own jobs)
        Assert.Equal(annaKey, (await Views.ForUserAsync(anna.Id, Ct)).ScopeKey);
        Assert.Equal(DeviceScope.NoDevicesKeyOf(anna.Id), annaKey);

        // Not the stored key of an object without devices, not "all", not a group; and no scope answers it.
        var orphan = (await Views.ForScopeAsync(DeviceScope.None, null, Ct)).ScopeKey;
        Assert.NotEqual(orphan, (await Views.ForScopeAsync(DeviceScope.None, null, Ct)).ScopeKey);
        Assert.DoesNotContain(DeviceScope.NoDevicesKey, new[] { annaKey, bertaKey, orphan });
        Assert.DoesNotContain(ICallContext.AllDevicesScopeKey, new[] { annaKey, bertaKey, orphan });
        using var scopes = new HostDeviceScopes(Groups, Views);
        await scopes.LoadAsync(Ct);
        Assert.False(scopes.Exists(annaKey));
        Assert.Empty(await scopes.Devices(annaKey).ListAsync(Ct));
    }
}
