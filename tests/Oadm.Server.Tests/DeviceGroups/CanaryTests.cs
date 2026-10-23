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
        ["/oadm.v1.PluginService/Invoke"] = CanaryPlugin.InvokeAsync,
        ["/oadm.v1.PluginService/Download"] = CanaryPlugin.DownloadAsync,
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
        ["/oadm.v1.TaskService/ListTaskPlugins"] = "tasks",
        ["/oadm.v1.TaskService/List"] = "tasks",
        ["/oadm.v1.TaskService/Watch"] = "tasks",
        ["/oadm.v1.TaskService/GetLog"] = "tasks",
        ["/oadm.v1.TaskService/Run"] = "tasks",
        ["/oadm.v1.TaskService/Query"] = "tasks",
        ["/oadm.v1.TaskService/Cancel"] = "tasks",
        ["/oadm.v1.TaskService/Delete"] = "tasks",
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
        ["/oadm.v1.PluginService/Watch"] = "no device id; events reach a group user only through audiences that cover the user (PluginServiceGroupTests)",
        ["/oadm.v1.SettingsService/Get"] = "server settings, no device data",
        ["/oadm.v1.LicenseService/GetLicense"] = "the license (its device count is an accepted existence leak, docs/spec/device-scope.md)",
    };

    /// <summary>
    /// The bundled plugins not converted yet and the package that moves their methods to the call context. A converted
    /// plugin leaves the list: its plugin.json says minSdkVersion 0.4.0 or later and its test project runs a
    /// <c>Oadm.Sdk.Testing.PluginCanary</c> that names every method and its id fields (plugins/README.md, "Call context").
    /// </summary>
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
            var places = new[] { Cases.ContainsKey(method), NotConverted.ContainsKey(method), WithoutDevices.ContainsKey(method), ClosedByTheRule(method) }.Count(x => x);
            Assert.True(places == 1, $"{method} is in {places} canary lists (a case, not converted, without devices, closed by the rule); it must be in exactly one.");
        }

        Assert.All(NotConverted.Keys.Concat(Cases.Keys).Concat(WithoutDevices.Keys), m => Assert.Contains(m, AccessPolicy.ListedMethods));
        Assert.All(NotConverted.Values.Concat(PluginsNotConverted.Values), p => Assert.Contains(p, Packages));
        Assert.All(WithoutDevices.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void EveryBundledPluginIsConvertedOrListed()
    {
        var manifests = Directory.EnumerateFiles(TestRepository.Path("plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Root: System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement, Folder: Path.GetDirectoryName(f)!))
            .Select(m => (Id: m.Root.GetProperty("id").GetString()!, MinSdk: m.Root.GetProperty("minSdkVersion").GetString(), m.Folder))
            .ToList();
        var ids = manifests.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var minSdk = manifests.ToDictionary(m => m.Id, m => m.MinSdk, StringComparer.Ordinal);
        Assert.All(PluginsNotConverted.Keys, id => Assert.Contains(id, ids));
        foreach (var (id, folder) in manifests.Where(m => !PluginsNotConverted.ContainsKey(m.Id)).Select(m => (m.Id, m.Folder)))
        {
            Assert.True(Core.Plugins.PluginLoader.IsAtLeast(minSdk[id], Sdk.Plugins.SdkInfo.CallContextVersion), $"{id} is converted but its plugin.json says minSdkVersion {minSdk[id]}.");
            var tests = TestRepository.Path("tests", Path.GetFileName(folder) + ".Tests");
            Assert.True(
                Directory.Exists(tests) && Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("new PluginCanary(", StringComparison.Ordinal)),
                $"{id} is converted but {Path.GetFileName(tests)} runs no PluginCanary.");
        }
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
