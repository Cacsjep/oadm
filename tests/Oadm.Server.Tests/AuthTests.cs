using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Logins, roles per service and per plugin method, sessions, throttling, users and the audit log.</summary>
public sealed class AuthTests
{
    private const string OperatorName = "tech1";

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

    [Fact]
    public async Task WithoutATokenOnlyStatusLoginAndFirstAdminAreOpen()
    {
        await using var host = await TestServerHost.StartAsync();
        var anonymous = host.InvokerFor(null);

        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.AuthService.AuthServiceClient(anonymous).StatusAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.DeviceService.DeviceServiceClient(anonymous).ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.SettingsService.SettingsServiceClient(anonymous).GetAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(anonymous).ListAsync(new Proto.ListTasksRequest())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.PluginService.PluginServiceClient(anonymous).ListCorePluginsAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.DiscoveryService.DiscoveryServiceClient(anonymous).StopAsync(new Proto.DiscoverySession { SessionId = "x" })));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.LiveViewService.LiveViewServiceClient(anonymous).ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = Guid.NewGuid().ToString() })));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.UserService.UserServiceClient(anonymous).ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.AuditService.AuditServiceClient(anonymous).ListAsync(new Proto.ListAuditRequest())));
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.AuthService.AuthServiceClient(anonymous).MeAsync(new Proto.Empty())));

        // A stream is refused as well.
        using var watch = new Proto.DeviceService.DeviceServiceClient(anonymous).Watch(new Proto.WatchDevicesRequest());
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await watch.ResponseStream.MoveNext()));

        // A made-up token is no login either.
        var forged = host.InvokerFor("not-a-token");
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.DeviceService.DeviceServiceClient(forged).ListAsync(new Proto.Empty())));
    }

    [Fact]
    public async Task OperatorsWorkWithDevicesAndTasksButNotWithServerConfiguration()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = await host.InvokerForUserAsync(OperatorName, UserRole.Operator);
        var settings = new Proto.SettingsService.SettingsServiceClient(op);

        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.DeviceService.DeviceServiceClient(op).ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await settings.GetAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await settings.ListCredentialsAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(op).ListAsync(new Proto.ListTasksRequest())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(op).ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.PluginService.PluginServiceClient(op).ListCorePluginsAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await new Proto.AuthService.AuthServiceClient(op).MeAsync(new Proto.Empty())));

        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "Pass-word-1" });
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await settings.SetAsync(new Proto.ServerSettings { PollingIntervalSeconds = 30 })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "a", Password = "b-password-1" })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.TaskService.TaskServiceClient(op).DeleteAllAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.UserService.UserServiceClient(op).ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await new Proto.AuditService.AuditServiceClient(op).ListAsync(new Proto.ListAuditRequest())));

        // The administrator may do all of it.
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Settings.SetAsync(new Proto.ServerSettings { PollingIntervalSeconds = 30 })));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id })));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Tasks.DeleteAllAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Users.ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Audit.ListAsync(new Proto.ListAuditRequest())));
    }

    [Fact]
    public void TheRoleTableNamesRealMethods()
    {
        var methods = new[]
        {
            Proto.AuthService.Descriptor, Proto.UserService.Descriptor, Proto.AuditService.Descriptor, Proto.SettingsService.Descriptor,
            Proto.TaskService.Descriptor, Proto.DeviceService.Descriptor, Proto.PluginService.Descriptor, Proto.DiscoveryService.Descriptor,
            Proto.AddDevicesService.Descriptor, Proto.FileService.Descriptor, Proto.LiveViewService.Descriptor,
        }.SelectMany(s => s.Methods.Select(m => $"/{s.FullName}/{m.Name}")).ToHashSet();

        Assert.All(AccessPolicy.ListedMethods, m => Assert.Contains(m, methods));
        Assert.Equal(
            ["/oadm.v1.AuthService/CreateFirstAdmin", "/oadm.v1.AuthService/Login", "/oadm.v1.AuthService/Status"],
            methods.Where(m => AccessPolicy.For(m) == Access.Open).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Access.Admin, AccessPolicy.For("/oadm.v1.UserService/Delete"));
        Assert.Equal(Access.Admin, AccessPolicy.For("/oadm.v1.AuditService/List"));
        Assert.Equal(Access.Operator, AccessPolicy.For("/oadm.v1.DeviceService/Remove"));
        Assert.Equal(Access.Operator, AccessPolicy.For("/oadm.v1.TaskService/Delete"));
    }

    [Fact]
    public async Task PluginMethodsNeedTheRoleThePluginDeclaresAndAdminCallsAreAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Get<PluginRegistry>().RegisterCorePlugin(new RolePlugin(), new PluginOrigin("tests", "1.0.0", null));
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync(OperatorName, UserRole.Operator));

        var read = await op.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.roles", Method = "read" });
        Assert.Equal("read", read.PayloadJson);
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.roles", Method = "save" }));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Equal("Only administrators can do this.", denied.Status.Detail);

        var saved = await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.roles", Method = "save" });
        Assert.Equal("save", saved.PayloadJson);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var entry = Assert.Single(audit, e => e.Action == AuditActions.PluginCall);
        Assert.Equal("admin", entry.UserName);
        Assert.Equal("Role test", entry.Target);
        Assert.Equal("save", entry.Detail);
    }

    [Fact]
    public async Task FirstAdministratorOnlyWhileNoUserExists()
    {
        await using var host = await TestServerHost.StartAsync(createAdmin: false);
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));
        var status = await auth.StatusAsync(new Proto.Empty());
        Assert.True(status.NeedsFirstAdmin);
        Assert.False(status.SetupCodeRequired); // the in-process client counts as local
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, AuthManager.SetupCodeFileName)));

        // No strength rule (user decision): only an empty password is refused.
        var weak = await Assert.ThrowsAsync<RpcException>(async () => await auth.CreateFirstAdminAsync(new Proto.CreateFirstAdminRequest { UserName = "boss", Password = "" }));
        Assert.Equal(StatusCode.InvalidArgument, weak.StatusCode);

        var reply = await auth.CreateFirstAdminAsync(new Proto.CreateFirstAdminRequest { UserName = "boss", Password = "Boss-pass-123" });
        Assert.Equal(Proto.UserRole.Admin, reply.User.Role);
        Assert.False(string.IsNullOrEmpty(reply.Token));
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, AuthManager.SetupCodeFileName)));
        Assert.False((await auth.StatusAsync(new Proto.Empty())).NeedsFirstAdmin);

        var again = await Assert.ThrowsAsync<RpcException>(async () => await auth.CreateFirstAdminAsync(new Proto.CreateFirstAdminRequest { UserName = "eve", Password = "Eve-pass-1234" }));
        Assert.Equal(StatusCode.FailedPrecondition, again.StatusCode);

        // The token of the new administrator works.
        var me = await new Proto.AuthService.AuthServiceClient(host.InvokerFor(reply.Token)).MeAsync(new Proto.Empty());
        Assert.Equal("boss", me.UserName);
    }

    [Fact]
    public async Task RemoteFirstAdministratorNeedsTheSetupCode()
    {
        await using var host = await TestServerHost.StartAsync(createAdmin: false);
        var manager = host.Get<AuthManager>();
        var code = (await File.ReadAllTextAsync(manager.SetupCodePath)).Trim();

        var missing = await Assert.ThrowsAsync<AuthException>(() => manager.CreateFirstAdminAsync("boss", "Boss-pass-123", null, isLoopback: false, false, "10.0.0.9", CancellationToken.None));
        Assert.Equal(AuthError.PermissionDenied, missing.Error);
        var wrong = await Assert.ThrowsAsync<AuthException>(() => manager.CreateFirstAdminAsync("boss", "Boss-pass-123", "AAAA-BBBB-CCCC", isLoopback: false, false, "10.0.0.9", CancellationToken.None));
        Assert.Equal("The setup code is wrong.", wrong.Message);

        // Case and dashes do not matter.
        var result = await manager.CreateFirstAdminAsync("boss", "Boss-pass-123", code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant(), isLoopback: false, false, "10.0.0.9", CancellationToken.None);
        Assert.Equal("boss", result.User.UserName);
    }

    [Fact]
    public async Task LoginLogoutAndTheTaskOwnerFollowTheAuthenticatedUser()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "Anna", Password = "Anna-pass-123", Role = Proto.UserRole.Operator });
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));

        var wrong = await Assert.ThrowsAsync<RpcException>(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = "anna", Password = "nope-nope-nope" }));
        Assert.Equal(StatusCode.Unauthenticated, wrong.StatusCode);
        Assert.Equal(AuthManager.WrongLoginMessage, wrong.Status.Detail);
        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = "nobody", Password = "nope-nope-nope" }));
        Assert.Equal(wrong.Status.Detail, unknown.Status.Detail); // no hint which names exist

        var login = await auth.LoginAsync(new Proto.LoginRequest { UserName = "ANNA", Password = "Anna-pass-123" });
        Assert.Equal("Anna", login.User.UserName);
        var anna = host.InvokerFor(login.Token);
        Assert.Equal("Anna", (await new Proto.AuthService.AuthServiceClient(anna).MeAsync(new Proto.Empty())).UserName);

        await new Proto.AuthService.AuthServiceClient(anna).LogoutAsync(new Proto.Empty());
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await new Proto.AuthService.AuthServiceClient(anna).MeAsync(new Proto.Empty())));

        var actions = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Select(e => e.Action).ToList();
        Assert.Contains(AuditActions.LoginFailed, actions);
        Assert.Contains(AuditActions.LoginOk, actions);
        Assert.Contains(AuditActions.Logout, actions);
        Assert.Contains(AuditActions.UserAdded, actions);
    }

    [Fact]
    public async Task FiveFailedLoginsLockTheNameForFiveMinutes()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "tech", Password = "Tech-pass-123", Role = Proto.UserRole.Operator });
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));
        for (int i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = "tech", Password = "wrong-pass-" + i })));
        }

        var locked = await Assert.ThrowsAsync<RpcException>(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = "tech", Password = "Tech-pass-123" }));
        Assert.Equal(StatusCode.ResourceExhausted, locked.StatusCode);
        Assert.StartsWith("Too many failed logins for tech.", locked.Status.Detail, StringComparison.Ordinal);
        Assert.Contains((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.LoginLocked && e.Target == "tech");

        // Other names are not affected.
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = "other", Password = "x" })));
    }

    [Fact]
    public void ThrottleUnlocksAfterTheLockAndForgetsOldFailures()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        for (int i = 0; i < 4; i++)
        {
            Assert.False(throttle.RecordFailure("anna"));
        }

        time.Advance(LoginThrottle.Window); // the four failures are now older than the window
        Assert.False(throttle.RecordFailure("Anna"));
        Assert.Null(throttle.LockedUntil("anna"));
        for (int i = 0; i < 3; i++)
        {
            Assert.False(throttle.RecordFailure("anna"));
        }

        Assert.True(throttle.RecordFailure("anna"));
        Assert.NotNull(throttle.LockedUntil("ANNA"));
        time.Advance(LoginThrottle.LockDuration);
        Assert.Null(throttle.LockedUntil("anna"));
    }

    [Fact]
    public async Task SessionsExpireAfterEightHoursWithoutUseOrThirtyDaysWithRememberMe()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new ManualTime();
        var tokens = new AuthTokenStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Persistence.OadmDbContext>>(), time);
        var user = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;

        var session = await tokens.CreateAsync(user, remember: false, "test", CancellationToken.None);
        var remembered = await tokens.CreateAsync(user, remember: true, "test", CancellationToken.None);

        time.Advance(TimeSpan.FromHours(7));
        Assert.NotNull(await tokens.ValidateAsync(session, CancellationToken.None)); // sliding: 8 h from now
        time.Advance(TimeSpan.FromHours(7));
        Assert.NotNull(await tokens.ValidateAsync(session, CancellationToken.None));
        time.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));
        Assert.Null(await tokens.ValidateAsync(session, CancellationToken.None));

        Assert.NotNull(await tokens.ValidateAsync(remembered, CancellationToken.None));
        time.Advance(TimeSpan.FromDays(29));
        Assert.NotNull(await tokens.ValidateAsync(remembered, CancellationToken.None));

        // The sliding expiry is stored: a new process (empty cache) still knows the session.
        var restarted = new AuthTokenStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Persistence.OadmDbContext>>(), time);
        Assert.NotNull(await restarted.ValidateAsync(remembered, CancellationToken.None));
        time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        Assert.Null(await restarted.ValidateAsync(remembered, CancellationToken.None));
    }

    [Fact]
    public async Task UserRulesProtectTheLastAdministratorAndTheOwnAccount()
    {
        await using var host = await TestServerHost.StartAsync();
        var users = (await host.Users.ListAsync(new Proto.Empty())).Users;
        var self = Assert.Single(users);

        var demoteSelf = await Assert.ThrowsAsync<RpcException>(async () => await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = self.Id, Role = Proto.UserRole.Operator }));
        Assert.Equal(StatusCode.FailedPrecondition, demoteSelf.StatusCode);
        var deleteSelf = await Assert.ThrowsAsync<RpcException>(async () => await host.Users.DeleteAsync(new Proto.UserIdRequest { Id = self.Id }));
        Assert.Equal("You cannot delete your own account.", deleteSelf.Status.Detail);

        var anna = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "anna", Password = "Anna-pass-123", Role = Proto.UserRole.Admin });
        var duplicate = await Assert.ThrowsAsync<RpcException>(async () => await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "ANNA", Password = "Anna-pass-123" }));
        Assert.Equal(StatusCode.InvalidArgument, duplicate.StatusCode);

        // Anna (admin) cannot demote the only other admin once she is the last enabled one... first disable her as admin: fine.
        var annaToken = await InProcessAccess.CreateTokenAsync(host.App.Services, "anna");
        var annaUsers = new Proto.UserService.UserServiceClient(host.InvokerFor(annaToken));
        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = anna.Id, Disabled = true });
        Assert.Equal(StatusCode.Unauthenticated, await CodeOf(async () => await annaUsers.ListAsync(new Proto.Empty()))); // sessions end
        var login = await Assert.ThrowsAsync<RpcException>(async () => await host.Auth.LoginAsync(new Proto.LoginRequest { UserName = "anna", Password = "Anna-pass-123" }));
        Assert.Equal(StatusCode.Unauthenticated, login.StatusCode);

        // Only enabled administrators count: admin is the last one and anna cannot take that away.
        await host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = anna.Id, Disabled = false });
        annaToken = await InProcessAccess.CreateTokenAsync(host.App.Services, "anna");
        annaUsers = new Proto.UserService.UserServiceClient(host.InvokerFor(annaToken));
        await annaUsers.UpdateAsync(new Proto.UpdateUserRequest { Id = self.Id, Role = Proto.UserRole.Operator });
        var last = await Assert.ThrowsAsync<RpcException>(async () => await host.Users.ListAsync(new Proto.Empty()));
        Assert.Equal(StatusCode.Unauthenticated, last.StatusCode); // the role change ended admin's session
        var lastAdmin = await Assert.ThrowsAsync<AuthException>(() => host.Get<UserStore>().UpdateAsync(Guid.Parse(anna.Id), null, true, null, null, CancellationToken.None));
        Assert.Contains("last enabled administrator", lastAdmin.Message, StringComparison.Ordinal);

        var changes = (await annaUsers.ListAsync(new Proto.Empty())).Users;
        Assert.Equal(Proto.UserRole.Operator, changes.Single(u => u.UserName == "admin").Role);
        var audit = (await new Proto.AuditService.AuditServiceClient(host.InvokerFor(annaToken)).ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "admin" && e.Detail == "role Operator" && e.UserName == "anna");
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "anna" && e.Detail == "disabled");
    }

    [Fact]
    public async Task AuditLogRecordsSettingsCredentialsTasksAndDeviceRemoval()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Settings.SetAsync(new Proto.ServerSettings { PollingIntervalSeconds = 30 });
        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "Pass-word-1" });
        await host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id });
        await host.Settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id });
        await host.Tasks.DeleteAllAsync(new Proto.Empty());

        var list = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        var byAction = list.Entries.ToDictionary(e => e.Action, e => e);
        Assert.Equal("Polling interval (s) 60 -> 30", byAction[AuditActions.SettingsChanged].Detail);
        Assert.Equal("root", byAction[AuditActions.CredentialAdded].Target);
        Assert.Equal("root", byAction[AuditActions.CredentialRevealed].Target);
        Assert.Equal("root", byAction[AuditActions.CredentialRemoved].Target);
        Assert.Equal("0 tasks", byAction[AuditActions.TasksDeletedAll].Detail);
        Assert.All(list.Entries, e => Assert.Equal("admin", e.UserName));
        Assert.DoesNotContain(list.Entries, e => e.Detail.Contains("Pass-word-1", StringComparison.Ordinal));
        Assert.True(list.Entries[0].Id > list.Entries[^1].Id); // newest first
        Assert.Equal(list.Entries.Count, list.TotalCount);
    }

    [Fact]
    public async Task AuditRetentionKeepsTheNewestEntriesOfTheLastYear()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new ManualTime();
        var audit = new AuditLog(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Persistence.OadmDbContext>>(), time);
        await audit.WriteAsync("u", "c", "Old", "t", null);
        time.Advance(TimeSpan.FromDays(AuditLog.RetentionDays + 1));
        await audit.WriteAsync("u", "c", "New", "t", null);

        Assert.Equal(1, await audit.ApplyRetentionAsync(CancellationToken.None));
        var (entries, total) = await audit.ListAsync(0, 0, CancellationToken.None);
        Assert.Equal(1, total);
        Assert.Equal("New", entries[0].Action);
    }

    /// <summary>A core plugin with one Operator and one Admin method.</summary>
    private sealed class RolePlugin : ICorePlugin
    {
        public string Id => "test.roles";
        public string DisplayName => "Role test";
        public string? IconKey => null;
        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(method);

        public UserRole RequiredRole(string method) => method == "save" ? UserRole.Admin : UserRole.Operator;
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
