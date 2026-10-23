using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// SDK 0.4.0 contracts: the new members are default interface members that keep plugins built on 0.3.0 working, and the
/// test fakes behave like the host for a user with a device group.
/// </summary>
public sealed class CallContextContractTests
{
    /// <summary>A plugin as built against SDK 0.3.0: only the old members.</summary>
    private sealed class OldPlugin : ICorePlugin, ICorePluginDownloads
    {
        public string Id => "test.old";

        public string DisplayName => "Old";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>("old:" + method);

        public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
            Task.FromResult(new PluginDownload(new MemoryStream([1]), method + ".bin"));
    }

    [Fact]
    public async Task TheCallOverloadsForwardToTheOldMembersOfAnOlderPlugin()
    {
        var core = new FakeCoreContext();
        var call = core.CallAsAllDevices();
        ICorePlugin plugin = new OldPlugin();

        Assert.Equal("old:read", await plugin.InvokeAsync(call, "read", null, CancellationToken.None));
        Assert.False(plugin.RequiresAllDevices("save"));
        Assert.Null(plugin.SystemAccessReason("read"));
        await using var download = await ((ICorePluginDownloads)plugin).OpenDownloadAsync(call, "file", null, CancellationToken.None);
        Assert.Equal("file.bin", download.FileName);

        // An older host's context: System is its own members, no jobs, no scopes.
        ICorePluginContext ctx = new MinimalContext(core);
        Assert.Same(core, ctx.System.Devices);
        Assert.Null(ctx.Jobs);
        Assert.Null(ctx.DeviceScopes);

        // The old Publish is what an audience falls back to on an older host.
        IPluginEvents events = new CountingEvents();
        events.Publish("t", "{}", PluginEventAudience.Device(Guid.NewGuid()));
        Assert.Equal(1, ((CountingEvents)events).Count);
        Assert.Equal(SdkInfo.Version, SdkInfo.CallContextVersion);
    }

    [Fact]
    public async Task AFakeCallWithAGroupRefusesDevicesOutsideItLikeTheHost()
    {
        var core = new FakeCoreContext();
        var inside = core.Add(new FakeDevice { Serial = "ACCC8E000001" });
        var outside = core.Add(new FakeDevice { Serial = "ACCC8E000002" });
        var call = core.CallAsGroup([inside.Id]);

        Assert.False(call.HasAllDevices);
        Assert.NotEqual(ICallContext.AllDevicesScopeKey, call.DeviceScopeKey);
        Assert.Equal([inside.Id], (await call.Devices.ListAsync(CancellationToken.None)).Select(d => d.Id));
        Assert.Null(await call.Devices.FindAsync(outside.Id, CancellationToken.None));
        Assert.True(call.Contains(inside));
        Assert.False(call.Contains(outside));
        Assert.NotNull(await call.Vapix.CreateAsync(inside.Id, CancellationToken.None));

        var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.Vapix.CreateAsync(outside.Id, CancellationToken.None));
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Message);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.Tasks.RunAsync("p", [inside.Id, outside.Id], null, "x", CancellationToken.None));
        Assert.Empty(core.TaskRuns); // the whole run was refused
        Assert.Equal(2, call.Refused);

        var all = core.CallAsAllDevices();
        Assert.True(all.HasAllDevices);
        await all.Tasks.RunAsync("p", [inside.Id, outside.Id], null, "x", CancellationToken.None);
        Assert.Single(core.TaskRuns);
    }

    [Fact]
    public async Task JobsBelongToTheUserWhoStartedThem()
    {
        var core = new FakeCoreContext();
        var anna = core.CallAsGroup([], "anna");
        var bob = core.CallAsGroup([], "bob");
        var job = core.FakeJobs.Start(anna, "report", (_, _) => Task.CompletedTask);
        await job.Completion;

        Assert.Same(job, core.FakeJobs.Find(anna, job.Id));
        Assert.Null(core.FakeJobs.Find(bob, job.Id));
        Assert.Empty(core.FakeJobs.List(bob));
        Assert.Same(anna, job.Owner);
    }

    [Fact]
    public void ADeletedGroupsObjectsCoverNothing()
    {
        var core = new FakeCoreContext();
        var device = core.Add(new FakeDevice());
        var key = core.FakeScopes.Add("Building B", [device.Id]);
        string? changed = null;
        core.FakeScopes.Changed += (_, k) => changed = k;

        Assert.True(core.FakeScopes.Exists(key));
        Assert.True(core.FakeScopes.Contains(key, device));
        Assert.True(core.FakeScopes.Exists(ICallContext.AllDevicesScopeKey));

        core.FakeScopes.Delete(key);
        Assert.Equal(key, changed);
        Assert.False(core.FakeScopes.Exists(key));
        Assert.False(core.FakeScopes.Contains(key, device));
        Assert.Null(core.FakeScopes.NameOf(key));
    }

    [Fact]
    public async Task TasksOfTheFakeStayActiveUntilEndedAndAGroupUserAsksOnlyAboutItsOwnLikeTheHost()
    {
        var core = new FakeCoreContext();
        var inside = core.Add(new FakeDevice());
        var outside = core.Add(new FakeDevice());
        var anna = core.CallAsGroup([inside.Id], "anna");
        var bob = core.CallAsGroup([inside.Id], "bob");
        var all = core.CallAsAllDevices();

        var annas = Assert.Single(await anna.Tasks.RunAsync("p", [inside.Id], null, "anna", CancellationToken.None));
        var system = Assert.Single(await all.Tasks.RunAsync("p", [outside.Id], null, "admin", CancellationToken.None));
        Assert.Equal([annas], core.TaskRuns[0].TaskIds);

        // One by one only the own tasks; together the tasks of the group's devices; a device outside makes it unknown.
        Assert.True(anna.Tasks.IsActive(annas));
        Assert.Null(bob.Tasks.IsActive(annas));
        Assert.False(bob.Tasks.Cancel(annas));
        Assert.True(await bob.Tasks.AnyActiveAsync([annas], CancellationToken.None));
        Assert.True(await anna.Tasks.AnyActiveAsync([system, annas], CancellationToken.None));
        Assert.Null(await anna.Tasks.AnyActiveAsync([system], CancellationToken.None));
        Assert.True(all.Tasks.IsActive(system));

        Assert.True(anna.Tasks.Cancel(annas));
        Assert.False(anna.Tasks.IsActive(annas));
        Assert.False(await bob.Tasks.AnyActiveAsync([annas], CancellationToken.None));
        Assert.True(core.EndTask(system));
        Assert.False(core.EndTask(system));
        Assert.Null(all.Tasks.IsActive(Guid.NewGuid()));

        // The context's own runner is refused during a group user's call, every member.
        var plugin = new TasksProbe();
        await plugin.StartAsync(core, CancellationToken.None);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => core.InvokeAsync(plugin, anna, "isActive", system.ToString()));
        Assert.Equal(["Tasks"], core.RefusedAccesses);
        Assert.Equal("False", await core.InvokeAsync(plugin, all, "isActive", system.ToString()));
    }

    [Fact]
    public async Task AFakeCallOverATestsOwnFakesRefusesDevicesOutsideItsGroup()
    {
        var inside = new FakeDevice();
        var outside = new FakeDevice();
        var devices = new ListDevices([inside, outside]);
        var vapix = new CountingVapix();
        var call = FakeCallContext.OverGroup([inside.Id], devices, vapix, userName: "anna", groupId: "b");

        Assert.Equal(("anna", "b", false), (call.UserName, call.DeviceScopeKey, call.HasAllDevices));
        Assert.Equal([inside.Id], (await call.Devices.ListAsync(CancellationToken.None)).Select(d => d.Id));
        Assert.Null(await call.Devices.FindAsync(outside.Id, CancellationToken.None));
        await call.Vapix.CreateAsync(inside.Id, CancellationToken.None);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.Vapix.CreateAsync(outside.Id, CancellationToken.None));
        Assert.Equal([inside.Id], vapix.Created);
        Assert.Equal(1, call.Refused);
        await Assert.ThrowsAsync<NotSupportedException>(() => call.Tasks.RunAsync("p", [inside.Id], null, "anna", CancellationToken.None));

        // Devices that left the test's list are gone for the call too; a call with all devices sees every device.
        devices.Items.Remove(inside);
        Assert.Null(await call.Devices.FindAsync(inside.Id, CancellationToken.None));
        var all = FakeCallContext.Over(devices);
        Assert.True(all.HasAllDevices);
        Assert.Equal([outside.Id], (await all.Devices.ListAsync(CancellationToken.None)).Select(d => d.Id));
        Assert.Null(all.EventStreams);
    }

    [Fact]
    public void FakeDeviceScopesOverATestsOwnListCoverOnlyDevicesThatExist()
    {
        var device = new FakeDevice();
        var list = new List<IDeviceInfo> { device };
        var scopes = new FakeDeviceScopes(() => list);
        var key = scopes.Add("Building B", [device.Id]);

        Assert.True(scopes.Contains(key, device.Id));
        Assert.True(scopes.Contains(ICallContext.AllDevicesScopeKey, device));
        scopes.Add("Building B", [], key); // the group's rules changed
        Assert.False(scopes.Contains(key, device.Id));
        Assert.True(scopes.Exists(key));
        list.Clear();
        Assert.False(scopes.Contains(ICallContext.AllDevicesScopeKey, device.Id));
    }

    /// <summary>A plugin method that asks the context's own task runner.</summary>
    private sealed class TasksProbe : ICorePlugin
    {
        private ICorePluginContext? _ctx;

        public string Id => "test.tasks";

        public string DisplayName => "Tasks";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            _ctx = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            await Task.Yield();
            return _ctx!.Tasks.IsActive(Guid.Parse(payloadJson!))?.ToString();
        }
    }

    private sealed class ListDevices(List<IDeviceInfo> items) : IDeviceRepository
    {
        public List<IDeviceInfo> Items { get; } = items;

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.Find(d => d.Id == id));
    }

    private sealed class CountingVapix : Sdk.Vapix.IVapixClientFactory
    {
        public List<Guid> Created { get; } = [];

        public Task<Sdk.Vapix.IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            Created.Add(deviceId);
            return Task.FromResult<Sdk.Vapix.IVapixClient>(new FakeCamera());
        }
    }

    private sealed class CountingEvents : IPluginEvents
    {
        public int Count { get; private set; }

        public void Publish(string topic, string? payloadJson) => Count++;
    }

    private sealed class MinimalContext(FakeCoreContext core) : ICorePluginContext
    {
        public IDeviceRepository Devices => core;

        public Sdk.Vapix.IVapixClientFactory Vapix => core;

        public Sdk.Tasks.ITaskRunner Tasks => core;

        public IPluginSettings Settings => core.PluginSettings;

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
