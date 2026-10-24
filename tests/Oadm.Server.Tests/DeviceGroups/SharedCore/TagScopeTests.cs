using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
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
    public async Task AProtectedTagIsNeverRenamedOrDeletedAlsoByAnAdministrator()
    {
        await using var host = await TestServerHost.StartAsync();
        await GroupCallers.DeviceAsync(host, 1, "Vlan 20", "Vlan 30");
        var registry = host.Get<ProtectedTagRegistry>();
        registry.For("oadm.radius").Protect(["Vlan 20"], name => name, name => $"The tag {name} sets a VLAN. Change the VLAN rule first. Nothing was changed.");
        registry.For("other.plugin").Protect(["Vlan 30"], name => name); // no rename-or-delete text: the host's own

        var rename = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "vlan 20", NewName = "Vlan 21" }));
        Assert.Equal(StatusCode.FailedPrecondition, rename.StatusCode);
        Assert.Equal("The tag Vlan 20 sets a VLAN. Change the VLAN rule first. Nothing was changed.", rename.Status.Detail);
        var delete = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.DeleteAsync(new Proto.TagName { Name = "Vlan 30" }));
        Assert.Equal(StatusCode.FailedPrecondition, delete.StatusCode);
        Assert.Equal(ProtectedTagRegistry.DefaultChangeRefusal("Vlan 30"), delete.Status.Detail);
        var tags = (await host.Tags.ListAsync(new Proto.Empty())).Tags;
        Assert.Contains(tags, t => t.Name == "Vlan 20");
        Assert.Contains(tags, t => t.Name == "Vlan 30");

        // A recolor keeps the name; a stopped plugin still refuses, a removed one no longer.
        await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Vlan 20", Color = Proto.TagColor.Blue });
        registry.Detach("oadm.radius");
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.DeleteAsync(new Proto.TagName { Name = "Vlan 20" }))).StatusCode);
        registry.RemoveOwner("oadm.radius");
        await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Vlan 20", NewName = "Vlan 21" });
        await host.Tags.DeleteAsync(new Proto.TagName { Name = "Vlan 21" });
    }

    [Fact]
    public async Task AServerRestartedWithThePluginOffStillRefusesAGroupUserAndForgetsARemovedPlugin()
    {
        var host = await TestServerHost.StartAsync(configureServices: s => InstalledVlanPlugin.Install(s));
        host.KeepDataDirectory = true;
        var dataDirectory = host.DataDirectory;
        Guid deviceId;
        try
        {
            deviceId = (await GroupCallers.DeviceAsync(host, 1, "Lobby")).Id;
            var registry = host.Get<ProtectedTagRegistry>();
            registry.For(InstalledVlanPlugin.PluginId).Protect(["Vlan 20"], name => $"The tag {name} sets a VLAN.");
            registry.Detach(InstalledVlanPlugin.PluginId); // the package was turned off
        }
        finally
        {
            await host.DisposeAsync(); // the server stop waits for the stored set
        }

        // Started again with the plugin installed but off: nobody calls Protect, the stored set refuses.
        var restarted = await TestServerHost.StartAsync(configureServices: s => InstalledVlanPlugin.Install(s), dataDirectory: dataDirectory);
        restarted.KeepDataDirectory = true;
        try
        {
            Assert.False(restarted.Get<PluginRegistry>().IsEnabled("test.vlan.pkg"));
            var user = await GroupCallers.ForTagAsync(restarted, "lobby", "Lobby");
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await new Proto.TagService.TagServiceClient(user.Invoker).SetDeviceTagsAsync(Request([deviceId], ["vlan 20"])));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
            Assert.Equal("The tag Vlan 20 sets a VLAN.", refused.Status.Detail);
        }
        finally
        {
            await restarted.DisposeAsync();
        }

        // Started without the plugin (removed): its set is forgotten.
        var removed = await TestServerHost.StartAsync(configureServices: s => InstalledVlanPlugin.Install(s, installed: false), dataDirectory: dataDirectory);
        try
        {
            Assert.Empty(removed.Get<ProtectedTagRegistry>().Names);
            var user = await GroupCallers.UserAsync(removed, "lobby2", (await removed.Get<Core.DeviceGroups.DeviceGroupStore>().ListAsync(CancellationToken.None)).Single().Id);
            Assert.Equal(1, (await new Proto.TagService.TagServiceClient(user.Invoker).SetDeviceTagsAsync(Request([deviceId], ["Vlan 20"]))).DevicesChanged);
        }
        finally
        {
            removed.KeepDataDirectory = false;
            await removed.DisposeAsync();
        }
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

    /// <summary>A core plugin of a package that is off by default: installed, never started.</summary>
    internal sealed class InstalledVlanPlugin : ICorePlugin
    {
        public const string PluginId = "test.vlan";

        public string Id => PluginId;

        public string DisplayName => "VLAN test";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public UserRole RequiredRole(string method) => UserRole.Admin;

        /// <summary>The server's plugins: this plugin installed and off (or none: the plugin was removed).</summary>
        public static void Install(IServiceCollection services, bool installed = true)
        {
            services.RemoveAll<PluginRegistry>();
            services.AddSingleton(_ =>
            {
                var registry = new PluginRegistry();
                if (installed)
                {
                    registry.RegisterCorePlugin(new InstalledVlanPlugin(), new PluginOrigin("test.vlan.pkg", "1.0.0", null) { EnabledByDefault = false });
                }

                return registry;
            });
        }
    }
}
