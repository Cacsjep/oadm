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
        var entered = Assert.Single(entries, e => e.Action == AuditActions.DevicesEnteredGroup);
        Assert.Equal("Group Lobby", entered.Target);
        Assert.Equal($"1 device ({b.Serial}): +Lobby", entered.Detail);
        var left = Assert.Single(entries, e => e.Action == AuditActions.DevicesLeftGroup);
        Assert.StartsWith("2 devices (", left.Detail, StringComparison.Ordinal);
        Assert.EndsWith("): tag Lobby deleted", left.Detail, StringComparison.Ordinal);
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
