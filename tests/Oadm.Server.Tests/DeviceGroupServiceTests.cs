using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>DeviceGroupService, the user's devices on the user API, Your role, the all-devices rule and streams on a group change.</summary>
public sealed class DeviceGroupServiceTests
{
    private static Proto.DeviceGroupService.DeviceGroupServiceClient Groups(TestServerHost host) => new(host.Invoker);

    private static async Task<StatusCode> CodeOf(Func<Task> call)
    {
        try
        {
            await call();
            return StatusCode.OK;
        }
        catch (RpcException ex)
        {
            return ex.StatusCode;
        }
    }

    private static async Task<Device> AddDeviceAsync(TestServerHost host, string serial, params string[] tags)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(new Device { Serial = serial, Address = "10.0.0." + serial[^2..], AddedByAllDevices = true }, CancellationToken.None);
        if (tags.Length > 0)
        {
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { tags } });
        }

        return device;
    }

    [Fact]
    public async Task AdministratorsCreateListPreviewChangeAndDeleteGroups()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddDeviceAsync(host, "ACCC8E000001", "PTZ");
        await AddDeviceAsync(host, "ACCC8E000002");
        await AddDeviceAsync(host, "ACCC8E000003");
        var ptz = (await host.Tags.ListAsync(new Proto.Empty())).Tags.Single(t => t.Name == "PTZ").Id;
        var rules = new Proto.DeviceGroupRules { TagIds = { ptz }, Serials = { "ACCC8E000002" } };

        var preview = await Groups(host).PreviewAsync(new Proto.PreviewDeviceGroupRequest { Rules = rules });
        Assert.Equal(2, preview.DeviceCount);
        Assert.Equal(3, preview.TotalDevices);

        var created = await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Name = "PTZ cameras", Description = "Domes", Rules = rules });
        Assert.Equal(1, created.Revision);
        Assert.Equal(2, created.DeviceCount);
        Assert.Equal("tag PTZ, 1 device", created.Summary);

        var taken = await Assert.ThrowsAsync<RpcException>(async () => await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Name = "ptz cameras" }));
        Assert.Equal(StatusCode.AlreadyExists, taken.StatusCode);

        var changed = await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Id = created.Id, Name = "PTZ cameras", Rules = new Proto.DeviceGroupRules { ThisServer = true }, Revision = created.Revision });
        Assert.Equal(2, changed.Revision);
        Assert.Equal(3, changed.DeviceCount);
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Id = created.Id, Name = "PTZ cameras", Revision = 1 }));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);
        Assert.Equal(DeviceGroupTexts.ChangedMeanwhile, stale.Status.Detail);

        var list = await Groups(host).ListAsync(new Proto.Empty());
        Assert.Equal(3, list.TotalDevices);
        Assert.Equal("This server", Assert.Single(list.Groups).Summary);

        var deleted = await Groups(host).DeleteAsync(new Proto.DeviceGroupIdRequest { Id = created.Id });
        Assert.Empty(deleted.UserNames);
        Assert.Empty((await Groups(host).ListAsync(new Proto.Empty())).Groups);
        Assert.Equal(StatusCode.NotFound, await CodeOf(async () => await Groups(host).GetAsync(new Proto.DeviceGroupIdRequest { Id = created.Id })));

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupCreated && e.Target == "PTZ cameras");
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupChanged && e.Detail.Contains("rules: This server", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupDeleted && e.Target == "PTZ cameras");

        // Operators never see or change groups.
        var op = new Proto.DeviceGroupService.DeviceGroupServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.PreviewAsync(new Proto.PreviewDeviceGroupRequest())));
    }

    [Fact]
    public async Task AUsersGroupShowsOnTheUserApiAndInYourRoleAndADeletedGroupLeavesNoDevices()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddDeviceAsync(host, "ACCC8E000001", "PTZ");
        await AddDeviceAsync(host, "ACCC8E000002");
        var ptz = (await host.Tags.ListAsync(new Proto.Empty())).Tags.Single(t => t.Name == "PTZ").Id;
        var group = await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest
        {
            Name = "Domes",
            Rules = new Proto.DeviceGroupRules { TagIds = { ptz }, ExceptSerials = { "ACCC8E000009" } },
        });

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "x", Password = "pw", AllDevices = false }));
        Assert.Equal(DeviceGroupTexts.ChooseDevices, refused.Status.Detail);

        var anna = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "anna", Password = "pw", RoleId = BuiltInRoles.OperatorId.ToString(), DeviceGroupId = group.Id });
        Assert.False(anna.AllDevices);
        Assert.Equal(group.Id, anna.DeviceGroupId);
        Assert.Equal("Domes", anna.DeviceGroupName);
        var old = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "old", Password = "pw" }); // an older client
        Assert.True(old.AllDevices);

        var annaInvoker = await host.InvokerForRoleAsync("anna", BuiltInRoles.OperatorId);
        var me = await new Proto.AuthService.AuthServiceClient(annaInvoker).MeAsync(new Proto.Empty());
        Assert.False(me.AllDevices);
        Assert.Equal("Domes", me.DeviceGroupName);
        var mine = (await new Proto.RoleService.RoleServiceClient(annaInvoker).MineAsync(new Proto.Empty())).Devices;
        Assert.False(mine.AllDevices);
        Assert.Equal("Domes", mine.GroupName);
        Assert.Equal("tag PTZ", mine.Summary); // never the excepted devices
        Assert.Equal(["PTZ"], mine.TagNames);
        Assert.Equal(1, mine.DeviceCount);
        Assert.True((await new Proto.RoleService.RoleServiceClient(host.Invoker).MineAsync(new Proto.Empty())).Devices.AllDevices);

        // The user's group is changed and set back to all devices through Update; the audit names it.
        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = old.Id, DeviceGroupId = group.Id });
        var back = await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = old.Id, AllDevices = true });
        Assert.True(back.AllDevices);
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "old" && e.Detail == "devices Domes");
        Assert.Contains(audit, e => e.Action == AuditActions.UserAdded && e.Target == "anna" && e.Detail == "Role Operator, devices Domes");

        var deleted = await Groups(host).DeleteAsync(new Proto.DeviceGroupIdRequest { Id = group.Id });
        Assert.Equal(["anna"], deleted.UserNames);

        // Fail closed: no flag, no group, no devices; the session stays.
        me = await new Proto.AuthService.AuthServiceClient(annaInvoker).MeAsync(new Proto.Empty());
        Assert.False(me.AllDevices);
        Assert.Equal(string.Empty, me.DeviceGroupId);
        mine = (await new Proto.RoleService.RoleServiceClient(annaInvoker).MineAsync(new Proto.Empty())).Devices;
        Assert.Equal(DeviceGroupTexts.NoDevices, mine.Summary);
        Assert.Equal(0, mine.DeviceCount);
    }

    [Fact]
    public async Task MethodsForUsersWithAllDevicesAreRefusedForAGroupWhateverTheRoleAllows()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddDeviceAsync(host, "ACCC8E000001", "PTZ");
        // A role with every permission: what is refused then is refused for the devices, not the role.
        var grouped = (await GroupCallers.ForRulesAsync(host, "grouped", new Proto.DeviceGroupRules { ThisServer = true })).Invoker;
        var everywhere = await GroupCallers.AllDevicesUserAsync(host, "everywhere");

        var denied = await Assert.ThrowsAsync<RpcException>(async () => await new Proto.TagService.TagServiceClient(grouped).DeleteAsync(new Proto.TagName { Name = "PTZ" }));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Equal(DeviceGroupTexts.AllDevicesOnly, denied.Status.Detail);
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(grouped).DeleteAllAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.EventService.EventServiceClient(grouped).ClearLogAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.ServerLogService.ServerLogServiceClient(grouped).ListAsync(new Proto.ListServerLogRequest())));

        // The same role with all devices may.
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(everywhere).DeleteAllAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.TagService.TagServiceClient(everywhere).DeleteAsync(new Proto.TagName { Name = "PTZ" })));
    }

    [Fact]
    public async Task AChangeOfTheGroupOrOfTheUsersGroupEndsTheUsersStreams()
    {
        await using var host = await TestServerHost.StartAsync();
        var group = await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Name = "Server", Rules = new Proto.DeviceGroupRules { ThisServer = true } });
        var anna = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "anna", Password = "pw", RoleId = BuiltInRoles.OperatorId.ToString(), DeviceGroupId = group.Id });
        var invoker = await host.InvokerForRoleAsync("anna", BuiltInRoles.OperatorId);

        using var session = new Proto.AuthService.AuthServiceClient(invoker).WatchSession(new Proto.Empty());
        Assert.True(await session.ResponseStream.MoveNext());
        using (var tags = new Proto.TagService.TagServiceClient(invoker).Watch(new Proto.Empty()))
        {
            Assert.True(await tags.ResponseStream.MoveNext()); // the list on subscribe
            await Groups(host).SaveAsync(new Proto.SaveDeviceGroupRequest { Id = group.Id, Name = "Server", Rules = new Proto.DeviceGroupRules(), Revision = group.Revision });
            await Assert.ThrowsAnyAsync<Exception>(async () => await tags.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        }

        using (var tags = new Proto.TagService.TagServiceClient(invoker).Watch(new Proto.Empty()))
        {
            Assert.True(await tags.ResponseStream.MoveNext());
            await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = anna.Id, AllDevices = true });
            await Assert.ThrowsAnyAsync<Exception>(async () => await tags.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        }

        // The session stream stays and reports the new devices.
        Assert.True(await session.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(async () =>
        {
            var me = await new Proto.AuthService.AuthServiceClient(invoker).MeAsync(new Proto.Empty());
            return me.AllDevices;
        }, "the user to have all devices");
    }
}
