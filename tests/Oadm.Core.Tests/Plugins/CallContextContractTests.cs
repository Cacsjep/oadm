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
