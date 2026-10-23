using System.Collections.Concurrent;
using System.Text.Json;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

using Eventually = Oadm.Sdk.Testing.Eventually;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The core plugin host and the user's call (docs/spec/device-scope.md, Plugins): during the call of a user with a device
/// group the context's full accesses refuse (also in work the call starts), the call reaches only the user's view, methods
/// for users with all devices and packages before SDK 0.4.0 are refused before the plugin runs, and users with all devices
/// keep the old behaviour.
/// </summary>
public sealed class PluginCallContextTests : DeviceGroupTestBase
{
    private const string PluginId = "test.probe";

    [Fact]
    public async Task AGroupUsersCallRefusesTheContextsFullAccessesAndAnAllDevicesUserKeepsThem()
    {
        await using var rig = await RigAsync();
        foreach (var method in new[] { "ctx.devices", "ctx.vapix", "ctx.tasks", "ctx.streams", "ctx.system" })
        {
            var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, method, rig.Outside.Id));
            Assert.Equal("Only users with all devices can do this.", refused.Message);
        }

        // Nothing reached a device or started a task.
        Assert.Empty(rig.Vapix.Created);
        Assert.Empty(rig.Tasks.Runs);
        Assert.Empty(rig.Streams.Opened);

        // The same methods for a user with all devices work as before (plugins built on SDK 0.3.0 keep working for them).
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.devices", null));
        await rig.InvokeAsync(rig.Admin, "ctx.vapix", rig.Outside.Id);
        await rig.InvokeAsync(rig.Admin, "ctx.tasks", rig.Outside.Id);
        await rig.InvokeAsync(rig.Admin, "ctx.streams", rig.Outside.Id);
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.system", null));
        Assert.Equal([rig.Outside.Id], rig.Vapix.Created);

        // Outside any call (background work) the context is the full, system view.
        Assert.Equal(2, (await rig.Plugin.Context!.Devices.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task WorkAPluginStartsInsideAGroupUsersCallKeepsTheCallerAndIsRefused()
    {
        await using var rig = await RigAsync();
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "taskrun", null));

        // A fire-and-forget Task.Run started in the call is refused too, also after the call ended.
        Assert.Equal("started", await rig.InvokeAsync(rig.Anna, "fireandforget", null));
        var outcome = await rig.Plugin.FireAndForget.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<DeviceAccessRefusedException>(outcome);

        // For a user with all devices the same work runs.
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "taskrun", null));
    }

    [Fact]
    public async Task TheCallReachesOnlyTheUsersViewAndAnOutsideDeviceAnswersLikeAnUnknownOne()
    {
        await using var rig = await RigAsync();
        Assert.Equal("1", await rig.InvokeAsync(rig.Anna, "call.list", null));
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "call.list", null));
        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "call.find", rig.Inside.Id));
        Assert.Equal("none", await rig.InvokeAsync(rig.Anna, "call.find", rig.Outside.Id));

        foreach (var method in new[] { "call.vapix", "call.tasks", "call.streams" })
        {
            var outside = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, method, rig.Outside.Id));
            var unknown = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, method, Guid.NewGuid()));
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), outside.Message);
            Assert.Equal(outside.Message, unknown.Message);
        }

        // A batch with one outside device is refused whole: nothing starts.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, "call.tasks.both", $"{rig.Inside.Id},{rig.Outside.Id}"));
        Assert.Empty(rig.Vapix.Created);
        Assert.Empty(rig.Tasks.Runs);
        Assert.Empty(rig.Streams.Opened);

        await rig.InvokeAsync(rig.Anna, "call.vapix", rig.Inside.Id);
        await rig.InvokeAsync(rig.Anna, "call.tasks", rig.Inside.Id);
        await rig.InvokeAsync(rig.Anna, "call.streams", rig.Inside.Id);
        Assert.Equal([rig.Inside.Id], rig.Vapix.Created);
        Assert.Equal([rig.Inside.Id], Assert.Single(rig.Tasks.Runs));
        Assert.Equal([rig.Inside.Id], rig.Streams.Opened);

        // The user's tasks: cancel only those the user started through a call.
        var mine = rig.Tasks.Ids.Single();
        Assert.Equal("True", await rig.InvokeAsync(rig.Anna, "call.cancel", mine));
        Assert.Equal("False", await rig.InvokeAsync(rig.Anna, "call.cancel", Guid.NewGuid()));
        Assert.Equal("True", await rig.InvokeAsync(rig.Admin, "call.cancel", Guid.NewGuid()));

        // The call tells who calls.
        var who = JsonDocument.Parse((await rig.InvokeAsync(rig.Anna, "call.who", null))!).RootElement;
        Assert.Equal("anna", who.GetProperty("user").GetString());
        Assert.False(who.GetProperty("all").GetBoolean());
        Assert.Equal(rig.GroupId.ToString(), who.GetProperty("scope").GetString());
        Assert.Equal(SessionIds.Of(rig.Anna.Caller.TokenHash), who.GetProperty("session").GetString());
        Assert.Equal(ICallContext.AllDevicesScopeKey, JsonDocument.Parse((await rig.InvokeAsync(rig.Admin, "call.who", null))!).RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task AMethodForUsersWithAllDevicesIsRefusedForAGroupUserBeforeThePluginRuns()
    {
        await using var rig = await RigAsync();
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "save", null));
        Assert.Equal(DeviceGroupTexts.AllDevicesOnly, refused.Message);
        Assert.DoesNotContain("save", rig.Plugin.Ran);

        // A plugin that throws when asked is refused too (fail closed).
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "broken-declaration", null));
        Assert.DoesNotContain("broken-declaration", rig.Plugin.Ran);

        Assert.Equal("saved", await rig.InvokeAsync(rig.Admin, "save", null));
    }

    [Fact]
    public async Task APackageBeforeSdk040IsRefusedForGroupUsersOnly()
    {
        await using var rig = await RigAsync(minSdk: "0.3.0");
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "call.list", null));
        Assert.Equal("Only users with all devices can use this plugin.", refused.Message);
        Assert.Empty(rig.Plugin.Ran);
        Assert.False(rig.Host.IsCallable(rig.Anna, PluginId));

        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.devices", null));
        Assert.True(rig.Host.IsCallable(rig.Admin, PluginId));
    }

    [Fact]
    public async Task OnlyAMethodWithAReasonMayUseTheSystemAccessInAGroupUsersCall()
    {
        await using var rig = await RigAsync();
        Assert.Equal("2", await rig.InvokeAsync(rig.Anna, "system.count", null));

        // ctx.Devices stays refused there; ctx.System is refused in every other method.
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "system.count.ctx", null));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "ctx.system", null));
    }

    [Fact]
    public async Task AJobBelongsToItsUserRunsWithoutTheCallerAndKeepsTheOwnersDevices()
    {
        await using var rig = await RigAsync();
        var annasJob = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;
        var job = await rig.Plugin.JobDone.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(job.Caller); // the background boundary: no caller in the job
        Assert.IsType<DeviceAccessRefusedException>(job.ContextAccess); // ctx.* refuses in a group user's job too
        Assert.Equal(1, job.OwnerDevices); // the owner's view

        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", annasJob));
        Assert.Equal(1, await rig.JobCountAsync(rig.Anna));

        // Another user's id answers like an unknown id.
        var ben = await rig.CallerAsync("ben", DeviceScope.Group(rig.GroupId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(ben, "job.find", annasJob));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(ben, "job.find", "0123456789abcdef0123456789abcdef"));
        Assert.Equal(0, await rig.JobCountAsync(ben));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Admin, "job.find", annasJob));

        // An all-devices user's job uses the full context (background work).
        rig.Plugin.JobDone = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.InvokeAsync(rig.Admin, "job.start", null);
        var adminJob = await rig.Plugin.JobDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(adminJob.ContextAccess);
        Assert.Equal(2, adminJob.OwnerDevices);
    }

    [Fact]
    public async Task JobsEndWhenTheirOwnerChangesOrThePluginStops()
    {
        await using var rig = await RigAsync();
        rig.Plugin.HoldJobs = true;
        await rig.InvokeAsync(rig.Anna, "job.start", null);
        await rig.InvokeAsync(rig.Admin, "job.start", null);
        Assert.Equal(2, rig.Host.Jobs.RunningCount(PluginId));

        rig.Host.Jobs.CancelOwner(rig.Anna.Caller.UserId);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 1, "anna's job ended");

        rig.Host.Jobs.CancelSession(rig.Admin.Caller.SessionId);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "the admin session's job ended");

        await rig.InvokeAsync(rig.Admin, "job.start", null);
        Assert.Equal(1, rig.Host.Jobs.RunningCount(PluginId));
        await rig.Host.StopAllAsync(Ct);
        Assert.Equal(0, rig.Host.Jobs.RunningCount(PluginId));
    }

    [Fact]
    public async Task AJobStartedWithoutTheHostsCallIsRefused()
    {
        await using var rig = await RigAsync();
        var fake = Oadm.Sdk.Testing.FakeCallContext.AllDevices(new Oadm.Sdk.Testing.FakeCoreContext());
        Assert.Throws<ArgumentException>(() => rig.Plugin.Context!.Jobs!.Start(fake, "x", (_, _) => Task.CompletedTask));
        Assert.Null(rig.Plugin.Context!.Jobs!.Find(fake, "x"));
    }

    [Fact]
    public async Task TheBackgroundBoundaryClearsThePluginCall()
    {
        await using var rig = await RigAsync();
        Assert.Equal("2", await rig.InvokeAsync(rig.Anna, "suppressed", null));
    }

    [Fact]
    public async Task DeviceScopesFollowTheGroupsAndADeletedGroupCoversNothing()
    {
        await using var rig = await RigAsync();
        var scopes = rig.Plugin.Context!.DeviceScopes!;
        var key = rig.GroupId.ToString();
        var changed = new ConcurrentQueue<string>();
        scopes.Changed += (_, k) => changed.Enqueue(k);

        Assert.True(scopes.Exists(ICallContext.AllDevicesScopeKey));
        Assert.True(scopes.Exists(key));
        Assert.False(scopes.Exists(Guid.NewGuid().ToString()));
        Assert.False(scopes.Exists("none"));
        Assert.Equal("All devices", scopes.NameOf(ICallContext.AllDevicesScopeKey));
        Assert.Equal("B", scopes.NameOf(key));
        Assert.True(scopes.Contains(key, rig.Inside));
        Assert.False(scopes.Contains(key, rig.Outside));
        Assert.True(scopes.Contains(ICallContext.AllDevicesScopeKey, rig.Outside));
        Assert.Equal([rig.Inside.Id], (await scopes.Devices(key).ListAsync(Ct)).Select(d => d.Id));
        Assert.Null(await scopes.Devices(key).FindAsync(rig.Outside.Id, Ct));
        Assert.Equal(2, (await scopes.Devices(ICallContext.AllDevicesScopeKey).ListAsync(Ct)).Count);
        Assert.True(scopes.InAnyGroup(rig.Inside));
        Assert.False(scopes.InAnyGroup(rig.Outside));

        await Groups.DeleteAsync(rig.GroupId, Ct);

        Assert.Contains(key, changed);
        Assert.False(scopes.Exists(key));
        Assert.Null(scopes.NameOf(key));
        Assert.False(scopes.Contains(key, rig.Inside));
        Assert.Empty(await scopes.Devices(key).ListAsync(Ct));
        Assert.False(scopes.InAnyGroup(rig.Inside));
    }

    private async Task<Rig> RigAsync(string minSdk = "0.4.0")
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var registry = new PluginRegistry();
        var plugin = new ProbePlugin();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(PluginId, "1.0.0", null) { MinSdkVersion = minSdk, DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        var vapix = new RecordingVapix();
        var tasks = new RecordingTasks();
        var streams = new RecordingStreams();
        var scopes = new HostDeviceScopes(Groups, Views);
        var host = new CorePluginHost(registry, Devices, vapix, tasks, new InMemoryPluginSettingsProvider(), eventStreams: streams, deviceScopes: scopes, viewWatcher: Db.Get<DeviceViewWatcher>());
        await host.StartAllAsync(Ct);
        var rig = new Rig(this, host, scopes, plugin, vapix, tasks, streams, inside, outside, group.Id);
        rig.Anna = await rig.CallerAsync("anna", DeviceScope.Group(group.Id));
        rig.Admin = await rig.CallerAsync("root", DeviceScope.All);
        return rig;
    }

    private sealed class Rig(PluginCallContextTests test, CorePluginHost host, HostDeviceScopes scopes, ProbePlugin plugin, RecordingVapix vapix, RecordingTasks tasks, RecordingStreams streams, Device inside, Device outside, Guid groupId) : IAsyncDisposable
    {
        public CorePluginHost Host { get; } = host;

        public ProbePlugin Plugin { get; } = plugin;

        public RecordingVapix Vapix { get; } = vapix;

        public RecordingTasks Tasks { get; } = tasks;

        public RecordingStreams Streams { get; } = streams;

        public Device Inside { get; } = inside;

        public Device Outside { get; } = outside;

        public Guid GroupId { get; } = groupId;

        public PluginCaller Anna { get; set; } = null!;

        public PluginCaller Admin { get; set; } = null!;

        public async Task<PluginCaller> CallerAsync(string name, DeviceScope scope)
        {
            var user = await test.Users.AddAsync(name, "pw", BuiltInRoles.OperatorId, scope, Ct);
            var caller = test.CallerOf(user);
            return new PluginCaller(caller, await test.Views.ForAsync(caller, Ct));
        }

        /// <summary>Like a gRPC call: the interceptor's caller backstop is set too.</summary>
        public async Task<string?> InvokeAsync(PluginCaller caller, string method, object? argument)
        {
            using (CallerContext.Use(caller.Caller))
            {
                return await Host.InvokeAsync(caller, PluginId, method, argument?.ToString(), Ct);
            }
        }

        public async Task<int> JobCountAsync(PluginCaller caller) => int.Parse((await InvokeAsync(caller, "job.count", null))!, System.Globalization.CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            scopes.Dispose();
        }
    }

    internal sealed record JobOutcome(AuthenticatedCaller? Caller, Exception? ContextAccess, int OwnerDevices);

    /// <summary>A plugin whose methods each touch one access (the payload is a device id or a job id).</summary>
    private sealed class ProbePlugin : ICorePlugin
    {
        public string Id => PluginId;

        public string DisplayName => "Probe";

        public string IconKey => "plugin";

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public ConcurrentQueue<string> Ran { get; } = new();

        public TaskCompletionSource<Exception?> FireAndForget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<JobOutcome> JobDone { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldJobs { get; set; }

        public bool RequiresAllDevices(string method) => method switch
        {
            "save" => true,
            "broken-declaration" => throw new InvalidOperationException("broken"),
            _ => false,
        };

        public string? SystemAccessReason(string method) => method is "system.count" or "system.count.ctx" ? "counts every device, shows no device data" : null;

        public Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            throw new InvalidOperationException("The host calls the overload with the call.");

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            Ran.Enqueue(method);
            var ctx = Context!;
            Guid Id() => Guid.Parse(payloadJson!);
            switch (method)
            {
                case "ctx.devices":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "ctx.vapix":
                    await ctx.Vapix.CreateAsync(Id(), ct);
                    return null;
                case "ctx.tasks":
                    await ctx.Tasks.RunAsync("t", [Id()], null, "x", ct);
                    return null;
                case "ctx.streams":
                    await using (await ctx.EventStreams!.OpenAsync(Id(), ct))
                    {
                        return null;
                    }

                case "ctx.system":
                case "system.count":
                    return (await ctx.System.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "system.count.ctx":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "taskrun":
                    return await Task.Run(async () => (await ctx.Devices.ListAsync(CancellationToken.None)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
                case "fireandforget":
                    _ = Task.Run(async () =>
                    {
                        await Task.Yield();
                        try
                        {
                            await ctx.Devices.ListAsync(CancellationToken.None);
                            FireAndForget.TrySetResult(null);
                        }
                        catch (Exception ex)
                        {
                            FireAndForget.TrySetResult(ex);
                        }
                    }, CancellationToken.None);
                    return "started";
                case "suppressed":
                    using (CallerContext.Suppress())
                    {
                        return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                case "call.list":
                    return (await caller.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "call.find":
                    return await caller.Devices.FindAsync(Id(), ct) is null ? "none" : "found";
                case "call.vapix":
                    await caller.Vapix.CreateAsync(Id(), ct);
                    return null;
                case "call.tasks":
                    await caller.Tasks.RunAsync("t", [Id()], null, caller.UserName, ct);
                    return null;
                case "call.tasks.both":
                    await caller.Tasks.RunAsync("t", [.. payloadJson!.Split(',').Select(Guid.Parse)], null, caller.UserName, ct);
                    return null;
                case "call.cancel":
                    return caller.Tasks.Cancel(Id()).ToString();
                case "call.streams":
                    await using (await caller.EventStreams!.OpenAsync(Id(), ct))
                    {
                        return null;
                    }

                case "call.who":
                    return JsonSerializer.Serialize(new { user = caller.UserName, all = caller.HasAllDevices, scope = caller.DeviceScopeKey, session = caller.SessionId });
                case "save":
                case "broken-declaration":
                    return "saved";
                case "job.start":
                    return ctx.Jobs!.Start(caller, "report", async (job, token) =>
                    {
                        if (HoldJobs)
                        {
                            await Task.Delay(Timeout.Infinite, token);
                        }

                        Exception? access = null;
                        try
                        {
                            await ctx.Devices.ListAsync(token);
                        }
                        catch (DeviceAccessRefusedException ex)
                        {
                            access = ex;
                        }

                        JobDone.TrySetResult(new JobOutcome(CallerContext.Current, access, (await job.Owner.Devices.ListAsync(token)).Count));
                    }).Id;
                case "job.find":
                    return ctx.Jobs!.Find(caller, payloadJson!) is null ? throw new KeyNotFoundException("The report is no longer available.") : "found";
                case "job.count":
                    return ctx.Jobs!.List(caller).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    throw new ArgumentException("Unknown method " + method);
            }
        }
    }

    private sealed class RecordingVapix : IVapixClientFactory
    {
        public ConcurrentQueue<Guid> Created { get; } = new();

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            Created.Enqueue(deviceId);
            return Task.FromResult<IVapixClient>(new Tasks.FakeVapixClient());
        }
    }

    private sealed class RecordingTasks : ITaskRunner
    {
        public ConcurrentQueue<IReadOnlyList<Guid>> Runs { get; } = new();

        public ConcurrentQueue<Guid> Ids { get; } = new();

        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            Runs.Enqueue(deviceIds);
            IReadOnlyList<Guid> ids = [.. deviceIds.Select(_ => Guid.NewGuid())];
            foreach (var id in ids)
            {
                Ids.Enqueue(id);
            }

            return Task.FromResult(ids);
        }

        public bool Cancel(Guid taskId) => true;
    }

    private sealed class RecordingStreams : IDeviceEventStreams
    {
        public ConcurrentQueue<Guid> Opened { get; } = new();

        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            Opened.Enqueue(deviceId);
            return Task.FromResult<IDeviceEventSource>(new NoEvents());
        }
    }

    private sealed class NoEvents : IDeviceEventSource
    {
        public int LostDocuments => 0;

        public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
