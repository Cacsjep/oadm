using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Roles and permissions: built-in roles, the matrix, task and plugin permissions, live changes.</summary>
public sealed class RoleTests
{
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

    /// <summary>Registers the test plugins and takes their permissions into the roles, like the server start does.</summary>
    private static async Task AddPluginsAsync(TestServerHost host)
    {
        var registry = host.Get<PluginRegistry>();
        registry.RegisterTaskPlugin(new PartsTask(), new PluginOrigin("tests.parts", "1.0.0", null));
        registry.RegisterCorePlugin(new PagePlugin(), new PluginOrigin("tests.page", "1.0.0", null));
        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
    }

    private static Proto.RoleService.RoleServiceClient Roles(TestServerHost host) => new(host.Invoker);

    [Fact]
    public async Task TheBuiltInRolesHaveTheirDefaults()
    {
        await using var host = await TestServerHost.StartAsync();
        var matrix = await Roles(host).GetMatrixAsync(new Proto.Empty());

        Assert.Equal(["Administrator", "Operator", "Viewer"], matrix.Roles.Select(r => r.Name));
        var admin = matrix.Roles[0];
        Assert.Equal(Proto.RoleKind.Administrator, admin.Kind);
        Assert.Empty(admin.Permissions); // every permission, none listed
        Assert.Equal(1, admin.UserCount);

        var op = matrix.Roles[1].Permissions;
        Assert.Contains(HostPermissions.DevicesAddRemove, op);
        Assert.Contains(HostPermissions.TasksCancel, op);
        Assert.Contains(HostPermissions.EventsView, op);
        Assert.DoesNotContain(HostPermissions.TagsManage, op);
        Assert.DoesNotContain(HostPermissions.EventsManage, op);
        Assert.DoesNotContain(HostPermissions.TasksDeleteAll, op);
        Assert.Contains(HostPermissions.ComplianceView, op);
        Assert.DoesNotContain(HostPermissions.ComplianceManage, op);

        Assert.Equal([HostPermissions.ComplianceView, HostPermissions.DevicesLiveView], matrix.Roles[2].Permissions);
        Assert.Equal(HostPermissions.All.Select(p => p.Key), matrix.Permissions.Select(p => p.Key));
    }

    [Fact]
    public async Task ARunNeedsThePermissionsItsPayloadUses()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddPluginsAsync(host);
        var op = new Proto.TaskService.TaskServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        var device = Guid.NewGuid().ToString();
        Proto.RunTaskRequest Run(string part) => new() { PluginId = PartsTask.PluginId, PayloadJson = part, DeviceIds = { device } };

        Assert.Equal(StatusCode.OK, await CodeOf(async () => await op.RunAsync(Run("a"))));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.RunAsync(Run("b")));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Equal("Your role does not allow: Part B.", denied.Status.Detail);

        // No usable answer: every declared permission (Operator lacks B).
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.RunAsync(Run("?"))));

        // The administrator runs everything.
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Tasks.RunAsync(Run("b"))));
    }

    [Fact]
    public async Task TasksAndPagesARoleCannotUseAreNotListed()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddPluginsAsync(host);
        var viewer = await host.InvokerForRoleAsync("guard", BuiltInRoles.ViewerId);
        var op = await host.InvokerForUserAsync("tech1", UserRole.Operator);

        var viewerTasks = await new Proto.TaskService.TaskServiceClient(viewer).ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
        Assert.DoesNotContain(viewerTasks.Plugins, p => p.Id == PartsTask.PluginId);
        var opTasks = await new Proto.TaskService.TaskServiceClient(op).ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
        Assert.Contains(opTasks.Plugins, p => p.Id == PartsTask.PluginId);

        // The page is not a monitoring page: the Viewer neither sees nor calls it.
        var viewerPlugins = new Proto.PluginService.PluginServiceClient(viewer);
        Assert.DoesNotContain((await viewerPlugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == PagePlugin.PluginId);
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await viewerPlugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PagePlugin.PluginId, Method = "read" })));
        using (var watch = viewerPlugins.Watch(new Proto.WatchPluginRequest { PluginId = PagePlugin.PluginId }))
        {
            Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await watch.ResponseStream.MoveNext()));
        }

        var opPlugins = new Proto.PluginService.PluginServiceClient(op);
        Assert.Contains((await opPlugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == PagePlugin.PluginId);
        Assert.Equal("read", (await opPlugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PagePlugin.PluginId, Method = "read" })).PayloadJson);
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await opPlugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PagePlugin.PluginId, Method = "publish" })));
    }

    [Fact]
    public async Task NewPermissionsGoToBuiltInRolesByDefaultAndToCustomRolesNever()
    {
        await using var host = await TestServerHost.StartAsync();
        var created = await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "Guard", Permissions = { HostPermissions.DevicesLiveView } } },
        });
        Assert.Equal(4, created.Roles.Count);

        await AddPluginsAsync(host);
        var matrix = await Roles(host).GetMatrixAsync(new Proto.Empty());
        var op = matrix.Roles.Single(r => r.Kind == Proto.RoleKind.Operator).Permissions;
        Assert.Contains(PermissionKeys.Task(PartsTask.PluginId, "a"), op);
        Assert.DoesNotContain(PermissionKeys.Task(PartsTask.PluginId, "b"), op);
        Assert.Contains(PermissionKeys.Of(PagePlugin.PluginId, PermissionKeys.Show), op);
        Assert.DoesNotContain(PermissionKeys.Of(PagePlugin.PluginId, "publish"), op);
        Assert.Equal([HostPermissions.DevicesLiveView], matrix.Roles.Single(r => r.Name == "Guard").Permissions);

        // A permission is given once: taken away, it stays away at the next start.
        var operatorRole = matrix.Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = operatorRole.Id, Name = "Operator", Permissions = { operatorRole.Permissions.Where(p => !p.StartsWith("task:", StringComparison.Ordinal)) } } },
        });
        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(host.Get<PluginRegistry>()), CancellationToken.None);
        var again = (await Roles(host).GetMatrixAsync(new Proto.Empty())).Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        Assert.DoesNotContain(PermissionKeys.Task(PartsTask.PluginId, "a"), again.Permissions);
    }

    [Fact]
    public async Task ACustomRoleDecidesWhatItsUsersMayDoAndEveryChangeIsAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        var matrix = await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "Refresher", Description = "Only refreshes.", Permissions = { HostPermissions.DevicesRefresh, "made.up:key" } } },
        });
        var role = matrix.Roles.Single(r => r.Name == "Refresher");
        Assert.Equal([HostPermissions.DevicesRefresh], role.Permissions); // unknown keys are not taken

        var user = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "ref", Password = "Ref-pass-123", RoleId = role.Id });
        Assert.Equal("Refresher", user.RoleName);
        Assert.Equal(Proto.UserRole.Operator, user.Role);

        var devices = new Proto.DeviceService.DeviceServiceClient(host.InvokerFor(await InProcessAccess.CreateTokenAsync(host.Services, "ref", Guid.Parse(role.Id))));
        Assert.NotEqual(StatusCode.PermissionDenied, await CodeOf(async () => await devices.RefreshAsync(new Proto.DeviceIds { Ids = { Guid.NewGuid().ToString() } })));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await devices.RemoveAsync(new Proto.DeviceIds { Ids = { Guid.NewGuid().ToString() } }));
        Assert.Equal("Your role does not allow: Add and remove devices.", denied.Status.Detail);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var entry = Assert.Single(audit, e => e.Action == AuditActions.RoleCreated);
        Assert.Equal("Refresher", entry.Target);
        Assert.Equal("+Refresh devices", entry.Detail);
        Assert.Contains(audit, e => e.Action == AuditActions.UserAdded && e.Detail == "Role Refresher");
    }

    [Fact]
    public async Task ARoleStartsOnItsPageEvenTheAdministratorRoleAndOnlyPagesAreTaken()
    {
        await using var host = await TestServerHost.StartAsync();
        await AddPluginsAsync(host);
        var roles = Roles(host);
        var matrix = await roles.GetMatrixAsync(new Proto.Empty());
        Assert.All(matrix.Roles, r => Assert.Equal("", r.StartPage)); // Devices

        // Operator: a plugin page; Administrator: only its start page may change.
        var op = matrix.Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        var admin = matrix.Roles.Single(r => r.Kind == Proto.RoleKind.Administrator);
        matrix = await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles =
            {
                new Proto.RoleChange { Id = op.Id, Name = op.Name, Description = op.Description, Permissions = { op.Permissions }, Version = op.Version, StartPage = "plugin:" + PagePlugin.PluginId },
                new Proto.RoleChange { Id = admin.Id, Name = admin.Name, Description = admin.Description, Version = admin.Version, StartPage = "compliance" },
            },
        });
        Assert.Equal("plugin:" + PagePlugin.PluginId, matrix.Roles.Single(r => r.Id == op.Id).StartPage);
        Assert.Equal("compliance", matrix.Roles.Single(r => r.Id == admin.Id).StartPage);

        // "devices" is stored as the default; an unset start page keeps the stored one.
        var viewer = matrix.Roles.Single(r => r.Kind == Proto.RoleKind.Viewer);
        matrix = await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = viewer.Id, Name = viewer.Name, Description = viewer.Description, Permissions = { viewer.Permissions }, Version = viewer.Version, StartPage = "devices" } },
        });
        Assert.Equal("", matrix.Roles.Single(r => r.Id == viewer.Id).StartPage);
        op = matrix.Roles.Single(r => r.Id == op.Id);
        matrix = await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = op.Id, Name = op.Name, Description = "Keeps its page.", Permissions = { op.Permissions }, Version = op.Version } },
        });
        Assert.Equal("plugin:" + PagePlugin.PluginId, matrix.Roles.Single(r => r.Id == op.Id).StartPage);

        // Only pages: an unknown plugin, a plugin without a page or a made-up key is refused.
        op = matrix.Roles.Single(r => r.Id == op.Id);
        foreach (var bad in new[] { "plugin:made.up", "users", "https://example.com", new string('x', 300) })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await roles.SaveRolesAsync(new Proto.SaveRolesRequest
            {
                Roles = { new Proto.RoleChange { Id = op.Id, Name = op.Name, Description = op.Description, Permissions = { op.Permissions }, Version = op.Version, StartPage = bad } },
            }));
            Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
            Assert.Equal("Choose a start page from the list.", refused.Status.Detail);
        }

        // The caller learns its start page with the login.
        var opUser = new Proto.AuthService.AuthServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        Assert.Equal("plugin:" + PagePlugin.PluginId, (await opUser.MeAsync(new Proto.Empty())).StartPage);
        Assert.Equal("compliance", (await new Proto.AuthService.AuthServiceClient(host.Invoker).MeAsync(new Proto.Empty())).StartPage);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.RoleChanged && e.Target == "Administrator" && e.Detail == "start page Compliance");
        Assert.Contains(audit, e => e.Action == AuditActions.RoleChanged && e.Target == op.Name && e.Detail == "start page Test page");
    }

    [Fact]
    public async Task BuiltInRolesAndRolesInUseAreProtected()
    {
        await using var host = await TestServerHost.StartAsync();
        var roles = Roles(host);
        var admin = await Assert.ThrowsAsync<RpcException>(async () => await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = BuiltInRoles.AdministratorId.ToString(), Name = "Boss" } },
        }));
        Assert.Equal(StatusCode.FailedPrecondition, admin.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, await CodeOf(async () => await roles.DeleteRoleAsync(new Proto.RoleIdRequest { Id = BuiltInRoles.ViewerId.ToString() })));

        var custom = (await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Temp" } } })).Roles.Single(r => r.Name == "Temp");
        var duplicate = await Assert.ThrowsAsync<RpcException>(async () => await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "temp" } } }));
        Assert.Equal(StatusCode.InvalidArgument, duplicate.StatusCode);

        var user = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "t", Password = "T-pass-1234", RoleId = custom.Id });
        var inUse = await Assert.ThrowsAsync<RpcException>(async () => await roles.DeleteRoleAsync(new Proto.RoleIdRequest { Id = custom.Id }));
        Assert.Equal(StatusCode.FailedPrecondition, inUse.StatusCode);
        Assert.Equal("1 user has the role Temp. Give that user another role first.", inUse.Status.Detail);

        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = user.Id, RoleId = BuiltInRoles.ViewerId.ToString() });
        var after = await roles.DeleteRoleAsync(new Proto.RoleIdRequest { Id = custom.Id });
        Assert.DoesNotContain(after.Roles, r => r.Name == "Temp");

        // Operators never reach the matrix.
        var op = new Proto.RoleService.RoleServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.GetMatrixAsync(new Proto.Empty())));
        var mine = await op.MineAsync(new Proto.Empty());
        Assert.Equal("Operator", mine.RoleName);
        Assert.False(mine.AllPermissions);
        Assert.Contains(mine.Permissions, p => p.Key == HostPermissions.DevicesAddRemove && p.Name == "Add and remove devices");
    }

    [Fact]
    public async Task ARoleChangeAppliesAtOnceEndsStreamsItNoLongerAllowsAndIsReported()
    {
        await using var host = await TestServerHost.StartAsync();
        var opInvoker = await host.InvokerForUserAsync("tech1", UserRole.Operator);

        using var session = new Proto.AuthService.AuthServiceClient(opInvoker).WatchSession(new Proto.Empty());
        Assert.True(await session.ResponseStream.MoveNext());
        Assert.Contains(HostPermissions.EventsView, session.ResponseStream.Current.Permissions);
        Assert.Equal("Operator", session.ResponseStream.Current.RoleName);

        using var rules = new Proto.EventService.EventServiceClient(opInvoker).WatchRules(new Proto.Empty());
        Assert.True(await rules.ResponseStream.MoveNext()); // the rule list on subscribe

        // Take "See event rules" away from the Operator role.
        var operatorRole = (await Roles(host).GetMatrixAsync(new Proto.Empty())).Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = operatorRole.Id, Name = "Operator", Permissions = { operatorRole.Permissions.Where(p => p != HostPermissions.EventsView) } } },
        });

        await Assert.ThrowsAnyAsync<Exception>(async () => await rules.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await session.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain(HostPermissions.EventsView, session.ResponseStream.Current.Permissions);
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.EventService.EventServiceClient(opInvoker).ListRulesAsync(new Proto.Empty())));
    }

    [Fact]
    public async Task ASaveOnAnOlderVersionOfARoleIsRefused()
    {
        await using var host = await TestServerHost.StartAsync();
        var loaded = (await Roles(host).GetMatrixAsync(new Proto.Empty())).Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        Assert.NotEqual(0, loaded.Version);

        // Administrator A takes a right away; B saves on the matrix both loaded before.
        await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = loaded.Id, Name = "Operator", Permissions = { loaded.Permissions.Where(p => p != HostPermissions.EventsView) }, Version = loaded.Version } },
        });
        var stale = await Assert.ThrowsAsync<RpcException>(async () => await Roles(host).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = loaded.Id, Name = "Operator", Permissions = { loaded.Permissions }, Version = loaded.Version } },
        }));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);
        Assert.Contains("Another administrator changed the role Operator", stale.Status.Detail, StringComparison.Ordinal);
        var now = (await Roles(host).GetMatrixAsync(new Proto.Empty())).Roles.Single(r => r.Kind == Proto.RoleKind.Operator);
        Assert.DoesNotContain(HostPermissions.EventsView, now.Permissions);
    }

    [Fact]
    public async Task ANewPasswordEndsEveryOpenStreamOfTheUserAlsoTheSessionStream()
    {
        await using var host = await TestServerHost.StartAsync();
        var opInvoker = await host.InvokerForUserAsync("tech1", UserRole.Operator);
        using var session = new Proto.AuthService.AuthServiceClient(opInvoker).WatchSession(new Proto.Empty());
        Assert.True(await session.ResponseStream.MoveNext());
        using var rules = new Proto.EventService.EventServiceClient(opInvoker).WatchRules(new Proto.Empty());
        Assert.True(await rules.ResponseStream.MoveNext());

        var id = (await host.Users.ListAsync(new Proto.Empty())).Users.Single(u => u.UserName == "tech1").Id;
        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = id, NewPassword = "another secret" });

        await Assert.ThrowsAnyAsync<Exception>(async () => await rules.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAnyAsync<Exception>(async () => await session.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.AuthService.AuthServiceClient(opInvoker).MeAsync(new Proto.Empty())));
    }

    [Fact]
    public async Task AUserWithAnotherRoleKeepsTheSessionAndGetsTheNewRights()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = await host.InvokerForUserAsync("tech1", UserRole.Operator);
        var tasks = new Proto.TaskService.TaskServiceClient(op);
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await tasks.DeleteAllAsync(new Proto.Empty())));

        var id = (await host.Users.ListAsync(new Proto.Empty())).Users.Single(u => u.UserName == "tech1").Id;
        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = id, RoleId = BuiltInRoles.AdministratorId.ToString() });
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await tasks.DeleteAllAsync(new Proto.Empty())));
        var me = await new Proto.AuthService.AuthServiceClient(op).MeAsync(new Proto.Empty());
        Assert.True(me.AllPermissions);
        Assert.Equal(Proto.UserRole.Admin, me.Role);
    }

    [Fact]
    public async Task TheUpgradeNoticeNamesWhatOperatorsLost()
    {
        await using var host = await TestServerHost.StartAsync();
        var catalog = host.Get<RoleStore>().Catalog;
        var settings = host.Get<ServerSettingsStore>();
        var sync = new CatalogSyncResult(true, [HostPermissions.TagsManage, PermissionKeys.Task("oadm.users", "remove"), "oadm.web-view:open"]);

        // Unknown keys (the plugins are not loaded here) are left out; nothing to report without users.
        Assert.Null(await RolesUpgradeNotice.RecordAsync(settings, sync, hadUsers: false, catalog, TimeProvider.System, CancellationToken.None));
        Assert.Null(await RolesUpgradeNotice.RecordAsync(settings, sync, hadUsers: true, catalog, TimeProvider.System, CancellationToken.None));

        await AddPluginsAsync(host);
        var withPlugin = new CatalogSyncResult(true, [HostPermissions.TagsManage, PermissionKeys.Task(PartsTask.PluginId, "b")]);
        var notice = await RolesUpgradeNotice.RecordAsync(settings, withPlugin, hadUsers: true, host.Get<RoleStore>().Catalog, TimeProvider.System, CancellationToken.None);
        Assert.Equal(["Part B"], notice!.OperatorLost);

        var reply = await host.Settings.GetAsync(new Proto.Empty());
        Assert.Equal(["Part B"], reply.RolesNotice.OperatorLost);
        var op = new Proto.SettingsService.SettingsServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        Assert.Null((await op.GetAsync(new Proto.Empty())).RolesNotice);
    }

    /// <summary>A task with two parts: "a" (Operator default) and "b" (not); the payload names the part.</summary>
    private sealed class PartsTask : ITaskPlugin
    {
        public const string PluginId = "tests.parts";

        public string Id => PluginId;
        public string DisplayName => "Parts";
        public string? IconKey => null;
        public bool ShowInToolbar => false;
        public bool RequiresDialog => false;

        public IReadOnlyList<PermissionDefinition> Permissions { get; } =
        [
            new("a", "Part A", "The harmless part."),
            new("b", "Part B", "The risky part.") { OperatorDefault = false },
        ];

        public IReadOnlyList<string>? RequiredPermissions(string? payloadJson) => payloadJson is "a" or "b" ? [payloadJson] : null;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A page in the Extensions group with one declared permission, "publish".</summary>
    private sealed class PagePlugin : ICorePlugin
    {
        public const string PluginId = "tests.page";

        public string Id => PluginId;
        public string DisplayName => "Test page";
        public string? IconKey => null;
        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public IReadOnlyList<PermissionDefinition> Permissions { get; } =
        [
            new("publish", "Publish", "Publish something.") { OperatorDefault = false },
        ];

        public string? RequiredPermission(string method) => method == "publish" ? "publish" : PermissionKeys.Show;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(method);
    }
}
