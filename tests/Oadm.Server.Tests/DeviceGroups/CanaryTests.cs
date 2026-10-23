using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Sdk.Devices;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// The canary test (docs/spec/device-scope.md, Tests): a user limited to a device group calls every method of the access
/// table with a device outside the group in every id field. The answer is NOT_FOUND "1 device is no longer available.",
/// exactly as for an unknown id, nothing changes, and no VAPIX, RTSP, task or event stream contact reaches the device
/// (recording network, video and event stream fakes). Methods not converted yet are listed with the package that converts
/// them; the list shrinks with every package and is empty at the end. Methods without device data say why; Administrator
/// and all-devices methods are covered by the access rule (a group user never reaches them).
/// </summary>
public sealed class CanaryTests
{
    private const string InsideSerial = "ACCC8E00C001";
    private const string OutsideSerial = "ACCC8E00C002";

    /// <summary>The converted methods: the call with the out-of-view id in every id field.</summary>
    private static readonly Dictionary<string, Func<Canary, Guid, Task>> Cases = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.DeviceService/GetPassphrasePolicies"] = async (c, id) =>
            await new Proto.DeviceService.DeviceServiceClient(c.Invoker).GetPassphrasePoliciesAsync(new Proto.DeviceIds { Ids = { c.Inside.ToString(), id.ToString() } }),
        ["/oadm.v1.TaskService/Run"] = async (c, id) =>
            await new Proto.TaskService.TaskServiceClient(c.Invoker).RunAsync(new Proto.RunTaskRequest
            {
                PluginId = CanaryTaskPlugin.PluginId,
                DeviceIds = { id == c.Inside ? new[] { id.ToString() } : new[] { c.Inside.ToString(), id.ToString() } },
            }),
        ["/oadm.v1.TaskService/Query"] = async (c, id) =>
            await new Proto.TaskService.TaskServiceClient(c.Invoker).QueryAsync(new Proto.TaskQueryRequest
            {
                PluginId = CanaryTaskPlugin.PluginId,
                DeviceId = id.ToString(),
                Method = "read",
            }),
    };

    /// <summary>
    /// The converted methods that take a task id (tasks package): a task of the device outside the view, a task of a removed
    /// device (history only for users with all devices) and an unknown id get the same NOT_FOUND "Task … not found.".
    /// </summary>
    private static readonly Dictionary<string, Func<Canary, Guid, Task>> TaskIdCases = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.TaskService/GetLog"] = async (c, id) =>
            await new Proto.TaskService.TaskServiceClient(c.Invoker).GetLogAsync(new Proto.TaskIdRequest { TaskId = id.ToString() }),
        ["/oadm.v1.TaskService/Cancel"] = async (c, id) =>
            await new Proto.TaskService.TaskServiceClient(c.Invoker).CancelAsync(new Proto.TaskIdRequest { TaskId = id.ToString() }),
        ["/oadm.v1.TaskService/Delete"] = async (c, id) =>
            await new Proto.TaskService.TaskServiceClient(c.Invoker).DeleteAsync(new Proto.TaskIdRequest { TaskId = id.ToString() }),
    };

    /// <summary>
    /// The converted methods that list device data without an id field (tasks package): the answer as text; it must name the
    /// inside device's task and nothing of the device outside the view or of a removed device.
    /// </summary>
    private static readonly Dictionary<string, Func<Canary, Task<string>>> ListCases = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.TaskService/List"] = async c =>
        {
            var client = new Proto.TaskService.TaskServiceClient(c.Invoker);
            var page = await client.ListAsync(new Proto.ListTasksRequest { Limit = 100 });
            var all = await client.ListAsync(new Proto.ListTasksRequest());
            return $"{page} total {page.TotalCount} | {all} total {all.TotalCount}";
        },
        ["/oadm.v1.TaskService/Watch"] = WatchTextAsync,
        ["/oadm.v1.TaskService/ListTaskPlugins"] = async c =>
        {
            var client = new Proto.TaskService.TaskServiceClient(c.Invoker);
            var full = await client.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
            var compact = await client.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true });
            return $"{full} | {compact}";
        },
    };

    /// <summary>Methods that take device ids or deliver device data and are not converted yet: method → package.</summary>
    internal static readonly Dictionary<string, string> NotConverted = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.DeviceService/List"] = "devices-core",
        ["/oadm.v1.DeviceService/Watch"] = "devices-core",
        ["/oadm.v1.DeviceService/Remove"] = "devices-core",
        ["/oadm.v1.DeviceService/Refresh"] = "devices-core",
        ["/oadm.v1.DeviceService/SetCredentials"] = "devices-core",
        ["/oadm.v1.DeviceService/LogIn"] = "devices-core",
        ["/oadm.v1.DeviceService/GetCredentialUserName"] = "devices-core",
        ["/oadm.v1.DeviceService/SetFirstPassword"] = "devices-core",
        ["/oadm.v1.DeviceService/GetWebUiUrl"] = "devices-core",
        ["/oadm.v1.DeviceWebService/Open"] = "devices-core",
        ["/oadm.v1.DeviceWebService/Exchange"] = "devices-core",
        ["/oadm.v1.LiveViewService/Watch"] = "devices-core",
        ["/oadm.v1.LiveViewService/ListSources"] = "devices-core",
        ["/oadm.v1.LiveViewService/GetOptics"] = "devices-core",
        ["/oadm.v1.LiveViewService/SetOptics"] = "devices-core",
        ["/oadm.v1.LiveViewService/GetRtspPort"] = "devices-core",
        ["/oadm.v1.LiveViewService/GetPtz"] = "devices-core",
        ["/oadm.v1.LiveViewService/MovePtz"] = "devices-core",
        ["/oadm.v1.LiveViewService/SetRtspPort"] = "devices-core",
        ["/oadm.v1.SiteService/ListSites"] = "devices-core",
        ["/oadm.v1.SiteService/WatchSites"] = "devices-core",
        ["/oadm.v1.DiscoveryService/StartZeroConf"] = "add-devices",
        ["/oadm.v1.DiscoveryService/StartRangeScan"] = "add-devices",
        ["/oadm.v1.DiscoveryService/ProbeAddress"] = "add-devices",
        ["/oadm.v1.DiscoveryService/WatchDiscovered"] = "add-devices",
        ["/oadm.v1.DiscoveryService/StopScan"] = "add-devices",
        ["/oadm.v1.DiscoveryService/Stop"] = "add-devices",
        ["/oadm.v1.AddDevicesService/Prepare"] = "add-devices",
        ["/oadm.v1.AddDevicesService/Commit"] = "add-devices",
        ["/oadm.v1.AddDevicesService/RetryAuth"] = "add-devices",
        ["/oadm.v1.WaitingDeviceService/Add"] = "add-devices",
        ["/oadm.v1.WaitingDeviceService/List"] = "add-devices",
        ["/oadm.v1.WaitingDeviceService/Watch"] = "add-devices",
        ["/oadm.v1.WaitingDeviceService/Remove"] = "add-devices",
        ["/oadm.v1.WaitingDeviceService/TryNow"] = "add-devices",
        ["/oadm.v1.TagService/List"] = "shared-core",
        ["/oadm.v1.TagService/Watch"] = "shared-core",
        ["/oadm.v1.TagService/Create"] = "shared-core",
        ["/oadm.v1.TagService/SetDeviceTags"] = "shared-core",
        ["/oadm.v1.ComplianceService/GetCatalog"] = "shared-core",
        ["/oadm.v1.ComplianceService/ListChecks"] = "shared-core",
        ["/oadm.v1.ComplianceService/WatchChecks"] = "shared-core",
        ["/oadm.v1.ComplianceService/GetCheck"] = "shared-core",
        ["/oadm.v1.ComplianceService/WatchResults"] = "shared-core",
        ["/oadm.v1.ComplianceService/CheckNow"] = "shared-core",
        ["/oadm.v1.ComplianceService/SaveCheck"] = "shared-core",
        ["/oadm.v1.ComplianceService/DeleteCheck"] = "shared-core",
        ["/oadm.v1.ComplianceService/SetCheckEnabled"] = "shared-core",
        ["/oadm.v1.ComplianceService/ListChoices"] = "shared-core",
        ["/oadm.v1.EventService/GetCatalog"] = "shared-core",
        ["/oadm.v1.EventService/ListRules"] = "shared-core",
        ["/oadm.v1.EventService/WatchRules"] = "shared-core",
        ["/oadm.v1.EventService/GetRule"] = "shared-core",
        ["/oadm.v1.EventService/ListLog"] = "shared-core",
        ["/oadm.v1.EventService/WatchLog"] = "shared-core",
        ["/oadm.v1.EventService/SaveRule"] = "shared-core",
        ["/oadm.v1.EventService/DeleteRule"] = "shared-core",
        ["/oadm.v1.EventService/SetRuleEnabled"] = "shared-core",
        ["/oadm.v1.EventService/DuplicateRule"] = "shared-core",
        ["/oadm.v1.EventService/ListActionChoices"] = "shared-core",
        ["/oadm.v1.EventService/TestAction"] = "shared-core",
        ["/oadm.v1.PluginService/Invoke"] = "plugin-host",
        ["/oadm.v1.PluginService/Watch"] = "plugin-host",
        ["/oadm.v1.PluginService/Download"] = "plugin-host",
    };

    /// <summary>Methods a group user may call that carry no device id and no device data: method → reason.</summary>
    internal static readonly Dictionary<string, string> WithoutDevices = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.AuthService/Status"] = "open: server name and version",
        ["/oadm.v1.AuthService/Login"] = "open: the session",
        ["/oadm.v1.AuthService/CreateFirstAdmin"] = "open: only while no user exists",
        ["/oadm.v1.AuthService/Logout"] = "the own session",
        ["/oadm.v1.AuthService/Me"] = "the own user (the group's name, never its devices)",
        ["/oadm.v1.AuthService/WatchSession"] = "the own user",
        ["/oadm.v1.RoleService/Mine"] = "the own role and group summary (sites, tags, counts of the own view; never excepted devices)",
        ["/oadm.v1.FileService/Upload"] = "an upload of the caller; tasks check their devices",
        ["/oadm.v1.FileService/Delete"] = "an upload by id; no device data",
        ["/oadm.v1.PluginService/ListCorePlugins"] = "the plugin pages the role may show",
        ["/oadm.v1.PluginService/ListPackages"] = "the plugin packages",
        ["/oadm.v1.SettingsService/Get"] = "server settings, no device data",
        ["/oadm.v1.LicenseService/GetLicense"] = "the license (its device count is an accepted existence leak, docs/spec/device-scope.md)",
    };

    /// <summary>The bundled plugins and the package that moves their methods to the call context (their tests name the id fields).</summary>
    internal static readonly Dictionary<string, string> PluginsNotConverted = new(StringComparer.Ordinal)
    {
        ["oadm.pki"] = "plugins-certs",
        ["oadm.est"] = "plugins-certs",
        ["oadm.device-config"] = "plugins-tasks",
        ["oadm.vapix-commander"] = "plugins-tasks",
        ["oadm.network"] = "plugins-tasks",
        ["oadm.users"] = "plugins-tasks",
        ["oadm.datetime"] = "plugins-tasks",
        ["oadm.firmware"] = "plugins-tasks",
        ["oadm.acap"] = "plugins-tasks",
        ["oadm.restart"] = "plugins-tasks",
        ["oadm.outputs"] = "plugins-tasks",
        ["oadm.notifications"] = "shared-core",
        ["oadm.device-triggers"] = "plugins-pages",
        ["oadm.dashboards"] = "plugins-pages",
        ["oadm.live-view"] = "plugins-pages",
        ["oadm.action-rules"] = "plugins-pages",
        ["oadm.edge-storage"] = "plugins-pages",
        ["oadm.image-health"] = "plugins-pages",
        ["oadm.hardening-scan"] = "plugins-pages",
        ["oadm.edge-recording-export"] = "plugins-pages",
        ["oadm.io-monitor"] = "plugins-pages",
        ["oadm.metadata-monitor"] = "plugins-pages",
        ["oadm.snapshot-report"] = "plugins-pages",
        ["oadm.system-report"] = "plugins-pages",
        ["oadm.web-view"] = "plugins-pages",
        ["oadm.ntp-server"] = "plugins-pages",
        ["oadm.dhcp-server"] = "plugins-pages",
    };

    private static readonly string[] Packages = ["devices-core", "tasks", "add-devices", "shared-core", "client", "plugin-host", "plugins-certs", "plugins-tasks", "plugins-pages"];

    /// <summary>Administrator only or all devices only: a user with a device group never reaches the method.</summary>
    private static bool ClosedByTheRule(string method) =>
        AccessPolicy.For(method) is { } rule && (rule.Access == Access.Admin || rule.AllDevices);

    [Fact]
    public void EveryMethodOfTheAccessTableIsCoveredExactlyOnce()
    {
        foreach (var method in AccessPolicy.ListedMethods)
        {
            var places = new[] { Cases.ContainsKey(method) || TaskIdCases.ContainsKey(method) || ListCases.ContainsKey(method), NotConverted.ContainsKey(method), WithoutDevices.ContainsKey(method), ClosedByTheRule(method) }.Count(x => x);
            Assert.True(places == 1, $"{method} is in {places} canary lists (a case, not converted, without devices, closed by the rule); it must be in exactly one.");
        }

        Assert.All(NotConverted.Keys.Concat(Cases.Keys).Concat(TaskIdCases.Keys).Concat(ListCases.Keys).Concat(WithoutDevices.Keys), m => Assert.Contains(m, AccessPolicy.ListedMethods));
        Assert.All(NotConverted.Values.Concat(PluginsNotConverted.Values), p => Assert.Contains(p, Packages));
        Assert.All(WithoutDevices.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void EveryBundledPluginIsConvertedOrListed()
    {
        var ids = Directory.EnumerateFiles(TestRepository.Path("plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ids.Order(StringComparer.Ordinal), PluginsNotConverted.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnOutOfViewDeviceIsAnsweredLikeAnUnknownOneAndNeverContacted()
    {
        await using var canary = await Canary.StartAsync();
        foreach (var (method, call) in Cases)
        {
            foreach (var id in new[] { canary.Outside, Guid.NewGuid() })
            {
                var before = await canary.SnapshotAsync();
                var refused = await Assert.ThrowsAsync<RpcException>(() => call(canary, id));
                Assert.True(refused.StatusCode == StatusCode.NotFound, $"{method}: {refused.StatusCode} {refused.Status.Detail}");
                Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Status.Detail);
                var after = await canary.SnapshotAsync();
                Assert.True(before == after, $"{method} contacted the device outside the view or changed something: {before} -> {after}");
            }

            // The same call inside the view works (the case calls what it should).
            await call(canary, canary.Inside);
        }
    }

    [Fact]
    public async Task AGroupUserNeverReachesTheMethodsTheRuleCloses()
    {
        await using var canary = await Canary.StartAsync();
        var closed = AccessPolicy.ListedMethods.Where(ClosedByTheRule).ToList();
        Assert.NotEmpty(closed);
        Assert.All(closed, m => Assert.False(AccessPolicy.Allows(AccessPolicy.For(m), canary.Caller)));
    }

    // ------------------------------------------------------------------ tasks package: task ids and lists

    [Fact]
    public async Task AnOutOfViewTaskIsAnsweredLikeAnUnknownOneAndNeverChanged()
    {
        await using var canary = await Canary.StartAsync();
        foreach (var (method, call) in TaskIdCases)
        {
            var outside = await AddFinishedTaskAsync(canary, canary.Outside);
            var removed = await AddFinishedTaskAsync(canary, Guid.NewGuid());
            foreach (var taskId in new[] { outside, removed, Guid.NewGuid() })
            {
                var before = await TaskSnapshotAsync(canary);
                var refused = await Assert.ThrowsAsync<RpcException>(() => call(canary, taskId));
                Assert.True(refused.StatusCode == StatusCode.NotFound, $"{method}: {refused.StatusCode} {refused.Status.Detail}");
                Assert.Equal($"Task {taskId} not found.", refused.Status.Detail);
                Assert.True(before == await TaskSnapshotAsync(canary), $"{method} changed a task outside the view");
            }

            // The same call on a task of the view works.
            await call(canary, await AddFinishedTaskAsync(canary, canary.Inside));
        }
    }

    [Fact]
    public async Task TheTaskListsCarryNothingOfDevicesOutsideTheView()
    {
        await using var canary = await Canary.StartAsync();
        var inside = await AddFinishedTaskAsync(canary, canary.Inside);
        var outside = await AddFinishedTaskAsync(canary, canary.Outside);
        var removedDevice = Guid.NewGuid();
        var removed = await AddFinishedTaskAsync(canary, removedDevice);
        foreach (var (method, call) in ListCases)
        {
            var text = await call(canary);
            Assert.True(text.Contains(method.EndsWith("ListTaskPlugins", StringComparison.Ordinal) ? canary.Inside.ToString() : inside.ToString(), StringComparison.Ordinal), $"{method} misses the view's data: {text}");
            foreach (var hidden in new[] { canary.Outside, outside, removedDevice, removed })
            {
                Assert.False(text.Contains(hidden.ToString(), StringComparison.Ordinal), $"{method} names {hidden} outside the view: {text}");
            }
        }

        // The administrator (all devices) still sees all of it.
        var admin = await canary.Host.Tasks.ListAsync(new Proto.ListTasksRequest());
        Assert.Contains(admin.Tasks, t => t.Id == outside.ToString());
        Assert.Contains(admin.Tasks, t => t.Id == removed.ToString());
    }

    /// <summary>The Watch snapshot and the live changes: an outside task started before an inside one is never delivered.</summary>
    private static async Task<string> WatchTextAsync(Canary c)
    {
        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var watch = new Proto.TaskService.TaskServiceClient(c.Invoker).Watch(new Proto.WatchTasksRequest { SnapshotEndMarker = true, SnapshotLimit = 100 }, cancellationToken: cts.Token);
        var seen = new System.Text.StringBuilder();
        while (await watch.ResponseStream.MoveNext(cts.Token) && watch.ResponseStream.Current.Kind != Proto.TaskChanged.Types.Kind.SnapshotEnd)
        {
            seen.Append(watch.ResponseStream.Current).Append(' ');
        }

        var engine = c.Host.Get<Core.Tasks.TaskEngine>();
        await engine.RunAsync(CanaryTaskPlugin.PluginId, [c.Outside], null, "system", CancellationToken.None);
        var insideRun = await engine.RunAsync(CanaryTaskPlugin.PluginId, [c.Inside], null, "system", CancellationToken.None);
        while (await watch.ResponseStream.MoveNext(cts.Token))
        {
            var change = watch.ResponseStream.Current;
            seen.Append(change).Append(' ');
            if (change.Task.Id == insideRun[0].ToString() && change.Task.State == Proto.TaskState.Done)
            {
                break;
            }
        }

        // The outside task ran first (the feed had it before the inside one): none of its changes may be in the text.
        return seen.ToString();
    }

    private static async Task<Guid> AddFinishedTaskAsync(Canary c, Guid deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new Core.Tasks.TaskRecord(Guid.NewGuid(), CanaryTaskPlugin.PluginId, "Canary", Core.Tasks.TaskState.Done, "tester@pc", now, now, now, 100, null,
            [new Core.Tasks.TaskDeviceRecord(deviceId, Core.Tasks.TaskState.Done, null, 100)]);
        await c.Host.Get<Core.Tasks.ITaskStore>().AddAsync(record, CancellationToken.None);
        await c.Host.Get<Core.Tasks.ITaskStore>().AppendLogAsync(record.Id, [new Core.Tasks.TaskLogEntry(now, deviceId, Sdk.Plugins.TaskLogLevel.Info, "Done.")], CancellationToken.None);
        return record.Id;
    }

    /// <summary>Every stored task with its state and log size: a refused call changes none of them.</summary>
    private static async Task<string> TaskSnapshotAsync(Canary c)
    {
        var store = c.Host.Get<Core.Tasks.ITaskStore>();
        var parts = new List<string> { await c.SnapshotAsync() };
        foreach (var task in await store.ListAsync(CancellationToken.None))
        {
            parts.Add($"{task.Id}:{task.State}:{(await store.GetLogAsync(task.Id, CancellationToken.None)).Count}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>A task plugin that runs and answers on every device without contacting it.</summary>
    internal sealed class CanaryTaskPlugin : Sdk.Plugins.ITaskPlugin, Sdk.Plugins.ITaskPluginQuery
    {
        public const string PluginId = "test.canary";

        public string Id => PluginId;

        public string DisplayName => "Canary";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(Sdk.Devices.IDeviceInfo device) => true;

        public Task ExecuteAsync(Sdk.Plugins.ITaskExecutionContext ctx, Sdk.Devices.IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> QueryAsync(Sdk.Plugins.ITaskQueryContext ctx, Sdk.Devices.IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
            Task.FromResult<string?>("{}");

        public static async Task RegisterAsync(TestServerHost host)
        {
            var registry = host.Get<Core.Plugins.PluginRegistry>();
            registry.RegisterTaskPlugin(new CanaryTaskPlugin(), new Core.Plugins.PluginOrigin(PluginId, "1.0.0", null));
            await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        }
    }

    /// <summary>
    /// The server with two devices on the fake network: one in the group user's view (tag "Inside"), one outside. Recording
    /// fakes count every request to a device, every opened video and event stream, and the tasks.
    /// </summary>
    internal sealed class Canary : IAsyncDisposable
    {
        private Canary(TestServerHost host, CallInvoker invoker, AuthenticatedCaller caller, Guid inside, Guid outside, FakeAxisDevice outsideDevice, RecordingStreams streams)
        {
            Host = host;
            Invoker = invoker;
            Caller = caller;
            Inside = inside;
            Outside = outside;
            OutsideDevice = outsideDevice;
            Streams = streams;
        }

        public TestServerHost Host { get; }

        /// <summary>Calls as the group user (a role with every permission).</summary>
        public CallInvoker Invoker { get; }

        public AuthenticatedCaller Caller { get; }

        public Guid Inside { get; }

        public Guid Outside { get; }

        public FakeAxisDevice OutsideDevice { get; }

        public RecordingStreams Streams { get; }

        public static async Task<Canary> StartAsync()
        {
            var network = new FakeAxisNetwork();
            network.Add("10.9.0.1", InsideSerial, "pass");
            var outsideDevice = network.Add("10.9.0.2", OutsideSerial, "pass");
            var streams = new RecordingStreams();
            var host = await TestServerHost.StartAsync(network, configureServices: s =>
            {
                s.RemoveAll<ILiveVideoSourceFactory>();
                s.AddSingleton<ILiveVideoSourceFactory>(streams);
                s.RemoveAll<Sdk.Devices.IDeviceEventStreams>();
                s.AddSingleton<Sdk.Devices.IDeviceEventStreams>(streams);
            });
            await CanaryTaskPlugin.RegisterAsync(host); // before the role takes every permission
            var repo = host.Get<DeviceRepository>();
            var inside = await repo.AddAsync(new Device { Serial = InsideSerial, Address = "10.9.0.1", AddedByAllDevices = true }, CancellationToken.None);
            var outside = await repo.AddAsync(new Device { Serial = OutsideSerial, Address = "10.9.0.2", AddedByAllDevices = true }, CancellationToken.None);
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { inside.Id.ToString() }, Add = { "Inside" } });
            var tag = (await host.Tags.ListAsync(new Proto.Empty())).Tags.Single(t => t.Name == "Inside").Id;

            var roles = new Proto.RoleService.RoleServiceClient(host.Invoker);
            var keys = (await roles.GetMatrixAsync(new Proto.Empty())).Permissions.Select(p => p.Key);
            var role = (await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Everything", Permissions = { keys } } } }))
                .Roles.Single(r => r.Name == "Everything").Id;
            var group = await new Proto.DeviceGroupService.DeviceGroupServiceClient(host.Invoker).SaveAsync(new Proto.SaveDeviceGroupRequest
            {
                Name = "Canary",
                Rules = new Proto.DeviceGroupRules { TagIds = { tag } },
            });
            var user = await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "canary", Password = "pw", RoleId = role, DeviceGroupId = group.Id });
            var invoker = await host.InvokerForRoleAsync("canary", Guid.Parse(role));
            var caller = new AuthenticatedCaller(Guid.Parse(user.Id), "canary", Guid.Parse(role), PermissionSet.All, "local", "", "x")
            {
                Devices = new CallerDevices(DeviceScope.Group(Guid.Parse(group.Id)), group.Revision),
            };
            return new Canary(host, invoker, caller, inside.Id, outside.Id, outsideDevice, streams);
        }

        /// <summary>What a call must not change for the outside device: its requests, streams, tasks and the device row.</summary>
        public async Task<string> SnapshotAsync()
        {
            var tasks = await Host.Get<Core.Tasks.ITaskStore>().ListAsync(CancellationToken.None);
            var outsideTasks = tasks.Count(t => t.Devices.Any(d => d.DeviceId == Outside));
            var row = await Host.Get<DeviceRepository>().GetAsync(Outside, CancellationToken.None);
            return $"requests {OutsideDevice.Requests}, streams {Streams.Opened(Outside)}, tasks {outsideTasks}, row {row?.Address}/{row?.Status}/{string.Join(";", row?.Tags ?? [])}";
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>Video and event streams that only count who asked; they never deliver anything.</summary>
    internal sealed class RecordingStreams : ILiveVideoSourceFactory, Sdk.Devices.IDeviceEventStreams
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _opened = new();

        public int Opened(Guid deviceId) => _opened.GetValueOrDefault(deviceId);

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct)
        {
            _opened.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
            throw new KeyNotFoundException("recording fake");
        }

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(key);
            _opened.AddOrUpdate(key.DeviceId, 1, (_, n) => n + 1);
            throw new KeyNotFoundException("recording fake");
        }

        public Task<Sdk.Devices.IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            _opened.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
            throw new KeyNotFoundException("recording fake");
        }
    }
}
