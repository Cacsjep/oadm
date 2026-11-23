using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.MyAxis;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// <see cref="ICorePluginContext.UserCalls"/> (docs/spec/schedules.md "Host: running as the owner"): a bundled plugin gets a
/// stored user's current call (the role's permissions, the device group's view, tasks as that user, no session); a deleted or
/// disabled account gets none; a change of role or group shows on the next call; other packages get no user calls.
/// </summary>
public sealed class UserCallsTests : DeviceGroupTestBase
{
    private const string Tag = "Building B";

    private readonly PluginRegistry _registry = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly RecordingSource _signIns = new();

    private TaskEngine Engine() =>
        new(Db.Get<EfTaskStore>(), _registry, Devices, _vapix, options: new TaskEngineOptions { AxisDownloads = _signIns }) { Views = Views };

    private StoredUserCallers Callers() => new(Users, Db.Get<RoleStore>(), Views, new AccessEpochs());

    /// <summary>Starts a core plugin of a bundled (or other) package on a host with user calls and returns its context.</summary>
    private async Task<(CorePluginHost Host, ICorePluginContext Context)> StartAsync(TaskEngine engine, bool bundled = true, bool withCallers = true)
    {
        var plugin = new ContextPlugin("test.schedules");
        Assert.True(_registry.RegisterCorePlugin(plugin, new PluginOrigin("test.schedules", "1.0.0", "/plugins/test.schedules") { MinSdkVersion = SdkInfo.Version, Bundled = bundled }));
        var host = new CorePluginHost(_registry, Devices, _vapix, engine, new InMemoryPluginSettingsProvider()) { UserCallers = withCallers ? Callers() : null };
        await host.StartAllAsync(Ct);
        return (host, plugin.Context!);
    }

    private async Task<(UserRecord User, Guid GroupId)> GroupUserAsync(string name = "anna")
    {
        await Db.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(_registry), Ct);
        var group = (await SaveGroupAsync("B", tags: [await TagIdAsync(Tag)])).Group;
        return (await Users.AddAsync(name, "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct), group.Id);
    }

    [Fact]
    public async Task AnEnabledUserGetsTheRolesPermissionsAndTheGroupsViewWithoutASession()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        await AddDeviceAsync("ACCC8E000002");
        await using var engine = Engine();
        var (host, ctx) = await StartAsync(engine);
        await using var running = host;
        var (anna, _) = await GroupUserAsync();

        var call = await ctx.UserCalls!.ForUserAsync("ANNA", Ct); // names match like at the login

        Assert.NotNull(call);
        Assert.Equal("anna", call.UserName);
        Assert.Equal(string.Empty, call.SessionId);
        Assert.False(call.HasAllDevices);
        Assert.Equal((await Views.ForUserAsync(anna.Id, Ct)).ScopeKey, call.DeviceScopeKey);
        Assert.True(call.Has(HostPermissions.DevicesAddRemove)); // Operator
        Assert.False(call.Has(HostPermissions.TagsManage)); // withheld from Operators
        Assert.Equal([inside.Id], (await call.Devices.ListAsync(Ct)).Select(d => d.Id));
        Assert.Same(Oadm.Core.Uploads.NoUploadedFiles.Instance, call.Uploads); // never a session's uploads
    }

    [Fact]
    public async Task AnUnknownDeletedOrDisabledAccountHasNoCall()
    {
        await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        await using var engine = Engine();
        var (host, ctx) = await StartAsync(engine);
        await using var running = host;
        var (anna, _) = await GroupUserAsync();
        var ben = await Users.AddAsync("ben", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct);
        Assert.NotNull(await ctx.UserCalls!.ForUserAsync("anna", Ct));
        Assert.NotNull(await ctx.UserCalls.ForUserAsync("ben", Ct));

        await Users.UpdateAsync(anna.Id, null, disabled: true, null, null, null, Ct);
        await Users.DeleteAsync(ben.Id, null, Ct);

        Assert.Null(await ctx.UserCalls.ForUserAsync("anna", Ct));
        Assert.Null(await ctx.UserCalls.ForUserAsync("ben", Ct));
        Assert.Null(await ctx.UserCalls.ForUserAsync("nobody", Ct));
        Assert.Null(await ctx.UserCalls.ForUserAsync(" ", Ct));
    }

    [Fact]
    public async Task AChangedRoleOrGroupShowsOnTheNextCall()
    {
        await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var other = await AddDeviceAsync("ACCC8E000002", tags: ["Building C"]);
        await using var engine = Engine();
        var (host, ctx) = await StartAsync(engine);
        await using var running = host;
        var (anna, _) = await GroupUserAsync();
        var first = (await ctx.UserCalls!.ForUserAsync("anna", Ct))!;
        Assert.True(first.Has(HostPermissions.DevicesAddRemove));

        var groupC = (await SaveGroupAsync("C", tags: [await TagIdAsync("Building C")])).Group;
        await Users.UpdateAsync(anna.Id, BuiltInRoles.ViewerId, null, null, DeviceScope.Group(groupC.Id), null, Ct);
        var next = (await ctx.UserCalls.ForUserAsync("anna", Ct))!;

        Assert.False(next.Has(HostPermissions.DevicesAddRemove)); // Viewer now
        Assert.Equal((await Views.ForUserAsync(anna.Id, Ct)).ScopeKey, next.DeviceScopeKey);
        Assert.Equal([other.Id], (await next.Devices.ListAsync(Ct)).Select(d => d.Id));

        await Users.UpdateAsync(anna.Id, null, null, null, DeviceScope.All, null, Ct);
        Assert.True((await ctx.UserCalls.ForUserAsync("anna", Ct))!.HasAllDevices);
    }

    [Fact]
    public async Task OnlyABundledPackageOnAHostWithUsersGetsUserCalls()
    {
        await using var engine = Engine();
        var (notBundled, ctx) = await StartAsync(engine, bundled: false);
        await using (notBundled)
        {
            Assert.Null(ctx.UserCalls);
        }

        var registry = new PluginRegistry();
        var plugin = new ContextPlugin("test.other");
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin("test.other", "1.0.0", null) { MinSdkVersion = SdkInfo.Version, Bundled = true }));
        await using var withoutUsers = new CorePluginHost(registry, Devices, _vapix, engine, new InMemoryPluginSettingsProvider());
        await withoutUsers.StartAllAsync(Ct);
        Assert.Null(plugin.Context!.UserCalls);
        Assert.False(PluginOrigin.FromAssembly(typeof(UserCallsTests).Assembly).Bundled);
    }

    [Fact]
    public async Task TasksStartedThroughTheCallBelongToTheUserWithTheirSignInAndView()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var seen = new ConcurrentQueue<(string? User, string? Session, IAxisDownloads? SignIn)>();
        Assert.True(_registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.upgrade", (task, _, _) =>
            {
                seen.Enqueue((task.Caller?.UserName, task.Caller?.SessionId, task.AxisDownloads));
                return Task.CompletedTask;
            }),
            new PluginOrigin("test.t.upgrade", "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
        await using var engine = Engine();
        var (host, ctx) = await StartAsync(engine);
        await using var running = host;
        var (anna, _) = await GroupUserAsync();
        var call = (await ctx.UserCalls!.ForUserAsync("anna", Ct))!;

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.Tasks.RunAsync("t.upgrade", [outside.Id], null, "someone else", Ct));
        var ids = await call.Tasks.RunAsync("t.upgrade", [inside.Id], null, "someone else", Ct);
        await engine.WaitForCompletionAsync(Assert.Single(ids), Ct).WaitAsync(TimeSpan.FromSeconds(30));

        var task = await engine.GetAsync(ids[0], Ct);
        Assert.Equal(("anna", TaskState.Done), (task!.Owner, task.State)); // never what the plugin says
        var (user, session, signIn) = Assert.Single(seen);
        Assert.Equal(("anna", string.Empty), (user, session));
        Assert.Same(_signIns.Given, signIn);
        Assert.Equal([anna.Id], _signIns.Users);
    }

    [Fact]
    public async Task TheCheckRightBeforeAWriteReadsTheUserAgain()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        await using var engine = Engine();
        var (host, ctx) = await StartAsync(engine);
        await using var running = host;
        var (anna, _) = await GroupUserAsync();
        var call = (await ctx.UserCalls!.ForUserAsync("anna", Ct))!;

        await call.RequireCurrentDeviceAccessAsync(HostPermissions.DevicesAddRemove, inside.Id, Ct);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.RequireCurrentDeviceAccessAsync(HostPermissions.DevicesAddRemove, outside.Id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => call.RequireCurrentDeviceAccessAsync(HostPermissions.TagsManage, inside.Id, Ct));

        await Users.UpdateAsync(anna.Id, null, disabled: true, null, null, null, Ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => call.RequireCurrentDeviceAccessAsync(HostPermissions.DevicesAddRemove, inside.Id, Ct));
    }

    [Fact]
    public async Task TheTestFakeAnswersTheCallSetForAUserAndNullOnceRemoved()
    {
        var core = new FakeCoreContext();
        Assert.Null(((ICorePluginContext)core).UserCalls); // like a plugin that is not bundled
        var calls = new FakeUserCalls().Set("anna", core.CallAsAllDevices("anna"));
        core.UserCalls = calls;

        Assert.Equal("anna", (await core.UserCalls.ForUserAsync("ANNA", Ct))!.UserName);
        calls.Remove("anna");
        Assert.Null(await core.UserCalls.ForUserAsync("anna", Ct));
        Assert.Null(await core.UserCalls.ForUserAsync("ben", Ct));
        Assert.Equal(["ANNA", "anna", "ben"], calls.Asked);
    }

    private sealed class ContextPlugin(string id) : ICorePlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingSource : IAxisDownloadSource
    {
        public IAxisDownloads Given { get; } = new NoDownloads();

        public ConcurrentQueue<Guid> Users { get; } = new();

        public Task<IAxisDownloads> ForUserAsync(Guid userId, CancellationToken ct)
        {
            Users.Enqueue(userId);
            return Task.FromResult(Given);
        }
    }

    private sealed class NoDownloads : IAxisDownloads
    {
        public bool HasSession => false;

        public Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct) =>
            throw new AxisDownloadException(AxisDownloadTexts.SignIn) { NeedsSignIn = true };
    }
}
