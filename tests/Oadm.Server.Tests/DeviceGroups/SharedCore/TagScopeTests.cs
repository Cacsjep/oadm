using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups.SharedCore;

/// <summary>TagService for users of a device group (docs/spec/device-scope.md): counts of the view, ids through the view, the guard, group names, membership audit.</summary>
public sealed class TagScopeTests
{
    [Fact]
    public async Task AGroupUserSeesTheCountsOfTheViewAndNoGroupNames()
    {
        await using var host = await TestServerHost.StartAsync();
        await GroupCallers.DeviceAsync(host, 1, "Lobby", "Ptz");
        await GroupCallers.DeviceAsync(host, 2, "Ptz");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");

        var mine = (await new Proto.TagService.TagServiceClient(user.Invoker).ListAsync(new Proto.Empty())).Tags;
        Assert.Equal(1, mine.Single(t => t.Name == "Ptz").DeviceCount);
        Assert.All(mine, t => Assert.Empty(t.GroupNames));

        var admin = (await host.Tags.ListAsync(new Proto.Empty())).Tags;
        Assert.Equal(2, admin.Single(t => t.Name == "Ptz").DeviceCount);
        Assert.Equal(["Group Lobby"], admin.Single(t => t.Name == "Lobby").GroupNames);
        Assert.Empty(admin.Single(t => t.Name == "Ptz").GroupNames);
    }

    [Fact]
    public async Task AGroupUserTagsOnlyDevicesOfTheViewAndNeverATagAGroupUses()
    {
        await using var host = await TestServerHost.StartAsync();
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(host, 2, "Yard");
        await GroupCallers.GroupAsync(host, "Yard", new Proto.DeviceGroupRules { TagIds = { (await host.Tags.ListAsync(new Proto.Empty())).Tags.Single(t => t.Name == "Yard").Id } });
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var tags = new Proto.TagService.TagServiceClient(user.Invoker);

        foreach (var id in new[] { outside.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await tags.SetDeviceTagsAsync(Request([inside.Id, id], ["Outdoor"])));
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Status.Detail);
        }

        Assert.Equal(["Lobby"], await TagsOf(host, inside.Id));
        var inUse = await Assert.ThrowsAsync<RpcException>(async () => await tags.SetDeviceTagsAsync(Request([inside.Id], ["Yard"])));
        Assert.Equal(StatusCode.PermissionDenied, inUse.StatusCode);
        await Assert.ThrowsAsync<RpcException>(async () => await tags.SetDeviceTagsAsync(Request([inside.Id], [], ["Lobby"])));
        Assert.Equal(1, (await tags.SetDeviceTagsAsync(Request([inside.Id], ["Outdoor"]))).DevicesChanged);
        Assert.Equal(["Lobby", "Outdoor"], await TagsOf(host, inside.Id));
        Assert.Equal(["Yard"], await TagsOf(host, outside.Id));

        // Rename and delete stay with users with all devices.
        var rename = await Assert.ThrowsAsync<RpcException>(async () => await tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Outdoor", NewName = "Out" }));
        Assert.Equal(StatusCode.PermissionDenied, rename.StatusCode);
        Assert.Equal(Core.DeviceGroups.DeviceGroupTexts.AllDevicesOnly, rename.Status.Detail);
    }

    [Fact]
    public async Task ATagAPluginProtectsIsAddedOrRemovedOnlyByUsersWithAllDevices()
    {
        // The 802.1X server protects the tags of its VLAN rules: a group user (with every permission, or the built-in
        // Operator role) never chooses a device's VLAN by tagging; a user with all devices does.
        await using var host = await TestServerHost.StartAsync();
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby", "Vlan 30");
        const string refusal = "The tag Vlan 20 sets a VLAN. Only users with all devices can add or remove it. Nothing was changed.";
        host.Get<ProtectedTagRegistry>().For("oadm.radius").Protect(["vlan 20", "Vlan 30"], name => $"The tag {name} sets a VLAN. Only users with all devices can add or remove it. Nothing was changed.");
        var groupUser = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "olga", Password = "pw", RoleId = BuiltInRoles.OperatorId.ToString(), DeviceGroupId = groupUser.GroupId.ToString() });
        var operatorCall = await host.InvokerForRoleAsync("olga", BuiltInRoles.OperatorId);

        foreach (var invoker in new[] { groupUser.Invoker, operatorCall })
        {
            var tags = new Proto.TagService.TagServiceClient(invoker);
            var added = await Assert.ThrowsAsync<RpcException>(async () => await tags.SetDeviceTagsAsync(Request([inside.Id], ["Vlan 20", "Outdoor"])));
            Assert.Equal(StatusCode.PermissionDenied, added.StatusCode);
            Assert.Equal(refusal, added.Status.Detail);
            var removed = await Assert.ThrowsAsync<RpcException>(async () => await tags.SetDeviceTagsAsync(Request([inside.Id], [], ["vlan 30"])));
            Assert.Equal(StatusCode.PermissionDenied, removed.StatusCode);
            Assert.Equal(["Lobby", "Vlan 30"], await TagsOf(host, inside.Id)); // refused whole
        }

        var allDevices = new Proto.TagService.TagServiceClient(await GroupCallers.AllDevicesUserAsync(host, "anna"));
        Assert.Equal(1, (await allDevices.SetDeviceTagsAsync(Request([inside.Id], ["Vlan 20"], ["Vlan 30"]))).DevicesChanged);
        Assert.Equal(["Lobby", "Vlan 20"], await TagsOf(host, inside.Id));
    }

    [Fact]
    public async Task UnknownDeviceIdsRefuseTheCallForUsersWithAllDevicesToo()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await GroupCallers.DeviceAsync(host, 1);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.SetDeviceTagsAsync(Request([device.Id, Guid.NewGuid()], ["Lobby"])));
        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Empty(await TagsOf(host, device.Id));
    }

    [Fact]
    public async Task TaggingAndDeletingATagAGroupUsesAuditsWhoLeftAndEntered()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var b = await GroupCallers.DeviceAsync(host, 2);
        await GroupCallers.ForTagAsync(host, "lobby", "Lobby");

        await host.Tags.SetDeviceTagsAsync(Request([b.Id], ["Lobby"]));
        await host.Tags.DeleteAsync(new Proto.TagName { Name = "Lobby" });

        var entries = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var entered = Assert.Single(entries, e => e.Action == Core.DeviceGroups.DeviceGroupMembership.EnteredAction);
        Assert.Equal("Group Lobby", entered.Target);
        Assert.Equal($"1 device (+Lobby): {b.Serial}", entered.Detail);
        var left = Assert.Single(entries, e => e.Action == Core.DeviceGroups.DeviceGroupMembership.LeftAction);
        Assert.StartsWith("2 devices (tag Lobby deleted): ", left.Detail, StringComparison.Ordinal);
        Assert.Equal(TestServerHost.AdminUserName, left.UserName);
        Assert.Contains(a.Serial, left.Detail, StringComparison.Ordinal);
    }

    private static Proto.SetDeviceTagsRequest Request(IEnumerable<Guid> ids, string[] add, string[]? remove = null)
    {
        var request = new Proto.SetDeviceTagsRequest();
        request.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        request.Add.AddRange(add);
        request.Remove.AddRange(remove ?? []);
        return request;
    }

    private static async Task<List<string>> TagsOf(TestServerHost host, Guid id) =>
        (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!.Tags;
}
