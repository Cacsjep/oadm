using Grpc.Core;

using Oadm.Core.Devices;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Support;

/// <summary>A user of a device group (a role with every permission) and calls in its name.</summary>
internal sealed record GroupCaller(CallInvoker Invoker, Guid GroupId, string UserName, Guid RoleId, Guid UserId);

/// <summary>Device groups and their users for the server tests of device groups.</summary>
internal static class GroupCallers
{
    /// <summary>A role "Everything" (every permission except Administrator-only areas), created once per host.</summary>
    /// <remarks>Call it after the plugins whose permissions the role needs are in the catalog.</remarks>
    public static async Task<Guid> EverythingRoleAsync(TestServerHost host)
    {
        var roles = new Proto.RoleService.RoleServiceClient(host.Invoker);
        var matrix = await roles.GetMatrixAsync(new Proto.Empty());
        if (matrix.Roles.FirstOrDefault(r => r.Name == "Everything") is { } existing)
        {
            return Guid.Parse(existing.Id);
        }

        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "Everything", Permissions = { matrix.Permissions.Select(p => p.Key) } } },
        });
        return Guid.Parse(saved.Roles.Single(r => r.Name == "Everything").Id);
    }

    /// <summary>Saves a device group with the rules (an administrator) and returns its id.</summary>
    public static async Task<Guid> GroupAsync(TestServerHost host, string name, Proto.DeviceGroupRules rules)
    {
        var group = await new Proto.DeviceGroupService.DeviceGroupServiceClient(host.Invoker).SaveAsync(new Proto.SaveDeviceGroupRequest { Name = name, Rules = rules });
        return Guid.Parse(group.Id);
    }

    /// <summary>A group of the tag <paramref name="tag"/> (created when missing) and a user of it.</summary>
    public static async Task<GroupCaller> ForTagAsync(TestServerHost host, string userName, string tag)
    {
        var tags = (await host.Tags.ListAsync(new Proto.Empty())).Tags;
        var id = tags.FirstOrDefault(t => t.Name == tag)?.Id ?? (await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = tag })).Id;
        var group = await GroupAsync(host, "Group " + tag, new Proto.DeviceGroupRules { TagIds = { id } });
        return await UserAsync(host, userName, group);
    }

    /// <summary>A new device group with <paramref name="rules"/> (named like the user) and a user of it.</summary>
    public static async Task<GroupCaller> ForRulesAsync(TestServerHost host, string userName, Proto.DeviceGroupRules rules) =>
        await UserAsync(host, userName, await GroupAsync(host, userName, rules));

    /// <summary>A user of <paramref name="groupId"/> with every permission and an invoker in that user's name.</summary>
    public static async Task<GroupCaller> UserAsync(TestServerHost host, string userName, Guid groupId)
    {
        var role = await EverythingRoleAsync(host);
        var user = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = userName, Password = "pw", RoleId = role.ToString(), DeviceGroupId = groupId.ToString() });
        var invoker = await host.InvokerForRoleAsync(userName, role);
        return new GroupCaller(invoker, groupId, userName, role, Guid.Parse(user.Id));
    }

    /// <summary>A user with all devices and every permission (not an administrator).</summary>
    public static async Task<CallInvoker> AllDevicesUserAsync(TestServerHost host, string userName)
    {
        var role = await EverythingRoleAsync(host);
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = userName, Password = "pw", RoleId = role.ToString(), AllDevices = true });
        return await host.InvokerForRoleAsync(userName, role);
    }

    /// <summary>Adds a device (serial from the number) with tags, as a user with all devices added it.</summary>
    public static async Task<Device> DeviceAsync(TestServerHost host, int serial, params string[] tags)
    {
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.1." + serial, serial);
        if (tags.Length > 0)
        {
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { tags } });
        }

        return (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
    }
}
