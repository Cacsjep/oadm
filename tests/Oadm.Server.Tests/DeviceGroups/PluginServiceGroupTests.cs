using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// PluginService for a user with a device group (docs/spec/device-scope.md, Plugins): the call context reaches only the
/// view, the context's full accesses and the all-devices methods are refused before anything runs, packages before SDK
/// 0.4.0 are refused (and their pages left out) for group users only, events reach only the watchers their audience
/// covers, and another user's job id answers like an unknown one.
/// </summary>
public sealed class PluginServiceGroupTests
{
    [Fact]
    public async Task AGroupUserIsRefusedTheContextsFullAccessAndTheAllDevicesMethodsBeforeAnythingRuns()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        var plugin = await CanaryPlugin.EnsureAsync(canary);
        var group = new Proto.PluginService.PluginServiceClient(canary.Invoker);

        var full = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.Id, "ctx.count")).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, "Only users with all devices can do this."), (full.StatusCode, full.Status.Detail));

        var save = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.Id, "save")).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, "Only users with all devices can do this."), (save.StatusCode, save.Status.Detail));
        Assert.DoesNotContain("save", plugin.Ran);

        // The administrator (all devices) is unchanged.
        Assert.Equal("2", (await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.Id, "ctx.count"))).PayloadJson);
        Assert.Equal("saved", (await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.Id, "save"))).PayloadJson);
        Assert.Equal("1", (await group.InvokeAsync(Request(CanaryPlugin.Id, "call.count"))).PayloadJson);
    }

    [Fact]
    public async Task APackageBeforeSdk040IsRefusedAndHiddenForGroupUsersOnly()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        await CanaryPlugin.EnsureAsync(canary);
        await CanaryPlugin.EnsureAsync(canary, CanaryPlugin.OldId, minSdk: "0.3.0");
        var group = new Proto.PluginService.PluginServiceClient(canary.Invoker);

        var refused = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.OldId, "call.count")).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, "Only users with all devices can use this plugin."), (refused.StatusCode, refused.Status.Detail));
        var watch = group.Watch(new Proto.WatchPluginRequest { PluginId = CanaryPlugin.OldId });
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(() => watch.ResponseStream.MoveNext())).StatusCode);

        var groupPages = (await group.ListCorePluginsAsync(new Proto.Empty())).Plugins.Select(p => p.Id).ToList();
        Assert.Contains(CanaryPlugin.Id, groupPages);
        Assert.DoesNotContain(CanaryPlugin.OldId, groupPages);
        Assert.Contains(CanaryPlugin.OldId, (await canary.Host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins.Select(p => p.Id));
        Assert.Equal("2", (await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.OldId, "ctx.count"))).PayloadJson);
    }

    [Fact]
    public async Task ATaskOfAPackageBeforeSdk040IsLeftOutAndRefusedForGroupUsersOnly()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        var group = new Proto.TaskService.TaskServiceClient(canary.Invoker);
        var admin = new Proto.TaskService.TaskServiceClient(canary.Host.Invoker);
        const string old = CanaryTests.CanaryTaskPlugin.OldPluginId;

        var groupMenu = (await group.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins.Select(p => p.Id).ToList();
        Assert.Contains(CanaryTests.CanaryTaskPlugin.PluginId, groupMenu);
        Assert.DoesNotContain(old, groupMenu);
        Assert.Contains(old, (await admin.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins.Select(p => p.Id));

        var before = (await canary.Host.Get<Core.Tasks.ITaskStore>().ListAsync(CancellationToken.None)).Count;
        var refused = await Assert.ThrowsAsync<RpcException>(() => group.RunAsync(new Proto.RunTaskRequest { PluginId = old, DeviceIds = { canary.Inside.ToString() } }).ResponseAsync);
        Assert.Equal((StatusCode.PermissionDenied, DeviceAccessRefusedException.PluginNeedsAllDevices), (refused.StatusCode, refused.Status.Detail));
        Assert.Equal(before, (await canary.Host.Get<Core.Tasks.ITaskStore>().ListAsync(CancellationToken.None)).Count);

        Assert.Single((await admin.RunAsync(new Proto.RunTaskRequest { PluginId = old, DeviceIds = { canary.Inside.ToString() } })).TaskIds);
    }

    [Fact]
    public async Task AGroupUserWatchesOnlyTheEventsItsAudienceCovers()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        await CanaryPlugin.EnsureAsync(canary);
        var group = new Proto.PluginService.PluginServiceClient(canary.Invoker);
        var hub = canary.Host.Get<CorePluginHost>().Events;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var groupWatch = group.Watch(new Proto.WatchPluginRequest { PluginId = CanaryPlugin.Id }, cancellationToken: cts.Token);
        using var adminWatch = canary.Host.Plugins.Watch(new Proto.WatchPluginRequest { PluginId = CanaryPlugin.Id }, cancellationToken: cts.Token);
        await Sdk.Testing.Eventually.TrueAsync(() => hub.WatcherCount(CanaryPlugin.Id) == 2, "both watch");

        await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.Id, "publish", $"{canary.Inside},{canary.Outside}"));

        Assert.Equal(["inside", "rows:[\"" + canary.Inside + "\"]", "mine", "end"], await TopicsUntilEndAsync(groupWatch, cts.Token));
        Assert.Equal(["old", "inside", "outside", "rows:[\"" + canary.Inside + "\",\"" + canary.Outside + "\"]", "end"], await TopicsUntilEndAsync(adminWatch, cts.Token));
    }

    [Fact]
    public async Task AnotherUsersJobIdAnswersLikeAnUnknownOne()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        await CanaryPlugin.EnsureAsync(canary);
        var group = new Proto.PluginService.PluginServiceClient(canary.Invoker);

        var adminsJob = (await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.Id, "job.start"))).PayloadJson;
        Assert.Equal("found", (await canary.Host.Plugins.InvokeAsync(Request(CanaryPlugin.Id, "job.find", adminsJob))).PayloadJson);

        var foreign = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.Id, "job.find", adminsJob)).ResponseAsync);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.Id, "job.find", "0123456789abcdef0123456789abcdef")).ResponseAsync);
        Assert.Equal((StatusCode.NotFound, unknown.Status.Detail), (foreign.StatusCode, foreign.Status.Detail));
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AJobOfAGroupUserIsForgottenWhenTheGroupsRulesChange()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        await CanaryPlugin.EnsureAsync(canary);
        var group = new Proto.PluginService.PluginServiceClient(canary.Invoker);
        var job = (await group.InvokeAsync(Request(CanaryPlugin.Id, "job.start"))).PayloadJson;
        Assert.Equal("found", (await group.InvokeAsync(Request(CanaryPlugin.Id, "job.find", job))).PayloadJson);

        var groups = canary.Host.Get<Core.DeviceGroups.DeviceGroupStore>();
        var saved = groups.Loaded![canary.Caller.DeviceGroupId!.Value];

        // A new description alone keeps the job.
        saved = (await groups.SaveAsync(saved.Id, saved.Name, "changed", saved.Rules, saved.Revision, CancellationToken.None)).Group;
        Assert.Equal("found", (await group.InvokeAsync(Request(CanaryPlugin.Id, "job.find", job))).PayloadJson);

        await groups.SaveAsync(saved.Id, saved.Name, "changed", saved.Rules with { ExceptSerials = [.. saved.Rules.ExceptSerials, "ACCC8E00FFFF"] }, saved.Revision, CancellationToken.None);

        var gone = await Assert.ThrowsAsync<RpcException>(() => group.InvokeAsync(Request(CanaryPlugin.Id, "job.find", job)).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, gone.StatusCode);
    }

    private static Proto.InvokeRequest Request(string pluginId, string method, string? payload = null) =>
        new() { PluginId = pluginId, Method = method, PayloadJson = payload ?? string.Empty };

    private static async Task<List<string>> TopicsUntilEndAsync(AsyncServerStreamingCall<Proto.PluginEvent> watch, CancellationToken ct)
    {
        var topics = new List<string>();
        while (await watch.ResponseStream.MoveNext(ct))
        {
            var item = watch.ResponseStream.Current;
            topics.Add(item.Topic == "rows" ? "rows:" + item.PayloadJson : item.Topic);
            if (item.Topic == "end")
            {
                return topics;
            }
        }

        return topics;
    }
}

/// <summary>
/// A core plugin on SDK 0.4.0 for the server's group tests and the canary cases of PluginService.Invoke and Download: its
/// methods reach devices only through the call (or, on purpose, through the context's full access).
/// </summary>
internal sealed class CanaryPlugin : ICorePlugin, ICorePluginDownloads
{
    public const string Id = "test.canary-plugin";

    public const string OldId = "test.canary-old";

    private readonly string _id;

    private CanaryPlugin(string id) => _id = id;

    string IPlugin.Id => _id;

    public string DisplayName => "Canary " + _id;

    public string IconKey => "plugin";

    public bool HasPage => false;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public System.Collections.Concurrent.ConcurrentQueue<string> Ran { get; } = new();

    private ICorePluginContext? _ctx;

    /// <summary>Registers and starts the plugin on the canary server once.</summary>
    public static async Task<CanaryPlugin> EnsureAsync(CanaryTests.Canary canary, string id = Id, string minSdk = "0.4.0")
    {
        var registry = canary.Host.Get<PluginRegistry>();
        if (registry.TryGetCorePlugin(id, out var registered))
        {
            return (CanaryPlugin)registered.Plugin;
        }

        var plugin = new CanaryPlugin(id);
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(id, "1.0.0", null) { MinSdkVersion = minSdk, DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        await canary.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await canary.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return plugin;
    }

    /// <summary>The canary case of PluginService.Invoke: one device id, read through the call.</summary>
    public static async Task InvokeAsync(CanaryTests.Canary canary, Guid deviceId)
    {
        await EnsureAsync(canary);
        await new Proto.PluginService.PluginServiceClient(canary.Invoker).InvokeAsync(new Proto.InvokeRequest
        {
            PluginId = Id,
            Method = "read",
            PayloadJson = deviceId.ToString(),
        });
    }

    /// <summary>The canary case of PluginService.Download.</summary>
    public static async Task DownloadAsync(CanaryTests.Canary canary, Guid deviceId)
    {
        await EnsureAsync(canary);
        using var call = new Proto.PluginService.PluginServiceClient(canary.Invoker).Download(new Proto.InvokeRequest
        {
            PluginId = Id,
            Method = "file",
            PayloadJson = deviceId.ToString(),
        });
        while (await call.ResponseStream.MoveNext())
        {
        }
    }

    public bool RequiresAllDevices(string method) => method == "save";

    public Task StartAsync(ICorePluginContext context, CancellationToken ct)
    {
        _ctx = context;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => InvokeAsync(null!, method, payloadJson, ct);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        Ran.Enqueue(method);
        var ctx = _ctx!;
        switch (method)
        {
            case "read":
                await ReadAsync(caller, Guid.Parse(payloadJson!), ct);
                return "ok";
            case "ctx.count":
                return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "call.count":
                return (await caller.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "save":
                return "saved";
            case "publish":
                var ids = payloadJson!.Split(',').Select(Guid.Parse).ToList();
                ctx.Events!.Publish("old", null);
                ctx.Events.Publish("inside", null, PluginEventAudience.Device(ids[0]));
                ctx.Events.Publish("outside", null, PluginEventAudience.Device(ids[1]));
                ctx.Events.Publish("rows", $"[\"{ids[0]}\",\"{ids[1]}\"]", PluginEventAudience.DeviceRows(ids));
                ctx.Events.Publish("mine", null, PluginEventAudience.User("canary"));
                ctx.Events.Publish("end", null, PluginEventAudience.User("canary"));
                ctx.Events.Publish("end", null, PluginEventAudience.User(caller.UserName));
                return null;
            case "job.start":
                return ctx.Jobs!.Start(caller, "report", (_, _) => Task.CompletedTask).Id;
            case "job.find":
                return ctx.Jobs!.Find(caller, payloadJson!) is null ? throw new KeyNotFoundException("The report is no longer available.") : "found";
            default:
                throw new ArgumentException("Unknown method.");
        }
    }

    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

    public async Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        await ReadAsync(caller, Guid.Parse(payloadJson!), ct);
        return new PluginDownload(new MemoryStream([1]), "file.bin", 1);
    }

    /// <summary>Asks the device through the call (the inside device may not answer: no stored login).</summary>
    private static async Task ReadAsync(ICallContext caller, Guid deviceId, CancellationToken ct)
    {
        var client = await caller.Vapix.CreateAsync(deviceId, ct);
        try
        {
            await client.GetBasicDeviceInfoAsync(ct);
        }
#pragma warning disable CA1031 // The answer does not matter; the request does.
        catch (Exception ex) when (ex is not DevicesNotAvailableException and not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }
}
