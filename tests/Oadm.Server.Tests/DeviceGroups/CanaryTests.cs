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

        // devices-core: the device list's actions, live view and the web interface relay.
        ["/oadm.v1.DeviceService/Remove"] = async (c, id) =>
        {
            // Inside the view a spare device is removed (the inside device stays for the other cases).
            Guid[] ids = id == c.Inside ? [await c.AddSpareAsync()] : [c.Inside, id];
            await Devices(c).RemoveAsync(new Proto.DeviceIds { Ids = { ids.Select(i => i.ToString()) } });
        },
        ["/oadm.v1.DeviceService/Refresh"] = async (c, id) =>
            await Devices(c).RefreshAsync(new Proto.DeviceIds { Ids = { c.Inside.ToString(), id.ToString() } }),
        ["/oadm.v1.DeviceService/SetCredentials"] = async (c, id) =>
            await Devices(c).SetCredentialsAsync(new Proto.SetCredentialsRequest { DeviceIds = { c.Inside.ToString(), id.ToString() }, UserName = "root", Password = "pass" }),
        ["/oadm.v1.DeviceService/LogIn"] = async (c, id) =>
            await Devices(c).LogInAsync(new Proto.DeviceLogInRequest { DeviceIds = { c.Inside.ToString(), id.ToString() }, UserName = "root", Password = "pass" }),
        ["/oadm.v1.DeviceService/GetCredentialUserName"] = async (c, id) =>
            await Devices(c).GetCredentialUserNameAsync(new Proto.DeviceIds { Ids = { c.Inside.ToString(), id.ToString() } }),
        ["/oadm.v1.DeviceService/SetFirstPassword"] = async (c, id) =>
            await Devices(c).SetFirstPasswordAsync(new Proto.SetFirstPasswordRequest { DeviceIds = { c.Inside.ToString(), id.ToString() }, Password = "Secret123" }),
        ["/oadm.v1.DeviceService/GetWebUiUrl"] = async (c, id) =>
            await Devices(c).GetWebUiUrlAsync(new Proto.DeviceId { Id = id.ToString() }),
        ["/oadm.v1.DeviceWebService/Open"] = async (c, id) =>
            await new Proto.DeviceWebService.DeviceWebServiceClient(c.Invoker).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = id.ToString() }),
        ["/oadm.v1.DeviceWebService/Exchange"] = (c, id) => Inside(c, id, async () =>
        {
            using var call = new Proto.DeviceWebService.DeviceWebServiceClient(c.Invoker).Exchange(deadline: DateTime.UtcNow.AddSeconds(30));
            await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Request = new Proto.DeviceWebRequestHead { DeviceId = id.ToString(), Method = "GET", PathAndQuery = "/" } });
            await call.RequestStream.CompleteAsync();
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
            }
        }),
        ["/oadm.v1.LiveViewService/Watch"] = (c, id) => Inside(c, id, async () =>
        {
            using var call = Live(c).Watch(new Proto.LiveViewRequest { DeviceId = id.ToString() }, deadline: DateTime.UtcNow.AddSeconds(30));
            await call.ResponseStream.MoveNext(CancellationToken.None);
        }),
        ["/oadm.v1.LiveViewService/ListSources"] = (c, id) => Inside(c, id, async () =>
            await Live(c).ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = id.ToString() })),
        ["/oadm.v1.LiveViewService/GetOptics"] = async (c, id) =>
            await Live(c).GetOpticsAsync(new Proto.OpticsRequest { DeviceId = id.ToString() }),
        ["/oadm.v1.LiveViewService/SetOptics"] = (c, id) => Inside(c, id, async () =>
            await Live(c).SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = id.ToString(), Autofocus = true })),
        ["/oadm.v1.LiveViewService/GetPtz"] = async (c, id) =>
            await Live(c).GetPtzAsync(new Proto.PtzRequest { DeviceId = id.ToString() }),
        ["/oadm.v1.LiveViewService/MovePtz"] = (c, id) => Inside(c, id, async () =>
            await Live(c).MovePtzAsync(new Proto.MovePtzRequest { DeviceId = id.ToString(), Move = Proto.PtzMove.Stop })),
        ["/oadm.v1.LiveViewService/GetRtspPort"] = async (c, id) =>
            await Live(c).GetRtspPortAsync(new Proto.RtspPortRequest { DeviceIds = { c.Inside.ToString(), id.ToString() } }),
        ["/oadm.v1.LiveViewService/SetRtspPort"] = (c, id) => Inside(c, id, async () =>
            await Live(c).SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { c.Inside.ToString(), id.ToString() }, Port = 554 })),
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

    private static Proto.DeviceService.DeviceServiceClient Devices(Canary c) => new(c.Invoker);

    private static Proto.LiveViewService.LiveViewServiceClient Live(Canary c) => new(c.Invoker);

    /// <summary>
    /// The call inside the view may fail for reasons of the fake device (no video, no PTZ, no login), never with the
    /// refusal of the view; outside ids pass every answer to the test.
    /// </summary>
    private static async Task Inside(Canary c, Guid id, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (RpcException ex) when (id == c.Inside && ex.Status.Detail != DeviceMessages.NoLongerAvailable(1))
        {
        }
    }

    /// <summary>
    /// Methods without an id field that deliver device data: the reply as text, which must never show the device outside
    /// the view (its id, serial or address). The delegates check that the inside device is there where it belongs.
    /// </summary>
    private static readonly Dictionary<string, Func<Canary, Task<string>>> Filtered = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.DeviceService/List"] = async c =>
        {
            var reply = await Devices(c).ListAsync(new Proto.Empty());
            Assert.Contains(reply.Devices, d => d.Id == c.Inside.ToString());
            return reply.ToString();
        },
        ["/oadm.v1.DeviceService/Watch"] = async c =>
        {
            using var call = Devices(c).Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, deadline: DateTime.UtcNow.AddSeconds(30));
            var text = new System.Text.StringBuilder();
            while (await call.ResponseStream.MoveNext(CancellationToken.None) && call.ResponseStream.Current.Kind != Proto.DeviceChanged.Types.Kind.SnapshotEnd)
            {
                text.Append(call.ResponseStream.Current);
            }

            Assert.Contains(c.Inside.ToString(), text.ToString(), StringComparison.Ordinal);
            return text.ToString();
        },
        ["/oadm.v1.SiteService/ListSites"] = async c => (await new Proto.SiteService.SiteServiceClient(c.Invoker).ListSitesAsync(new Proto.Empty())).ToString(),
        ["/oadm.v1.SiteService/WatchSites"] = async c =>
        {
            using var call = new Proto.SiteService.SiteServiceClient(c.Invoker).WatchSites(new Proto.Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            return call.ResponseStream.Current.ToString();
        },
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

    /// <summary>
    /// Converted methods that take no device id but objects of their own (discovery sessions, waiting entries): the group user
    /// (a group of tags only, so it cannot add devices) calls them with another user's objects that found or keep the device
    /// outside the view. Each case asserts its refusal; the test checks that nothing reached the device and nothing changed.
    /// The adds of a group with sites are in <see cref="AddDevicesScopeTests"/>.
    /// </summary>
    private static readonly Dictionary<string, Func<Canary, Task>> OwnObjectCases = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.DiscoveryService/StartZeroConf"] = async c =>
            await CannotAddAsync(() => c.Discovery.StartZeroConfAsync(new Proto.Empty()).ResponseAsync),
        ["/oadm.v1.DiscoveryService/StartRangeScan"] = async c =>
            await CannotAddAsync(() => c.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "10.9.0.2", To = "10.9.0.2" }).ResponseAsync),
        ["/oadm.v1.DiscoveryService/ProbeAddress"] = async c =>
            await CannotAddAsync(() => c.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.2" }).ResponseAsync),
        ["/oadm.v1.DiscoveryService/WatchDiscovered"] = async c =>
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                using var call = c.Discovery.WatchDiscovered(new Proto.DiscoverySession { SessionId = c.AdminSession });
                await call.ResponseStream.MoveNext(CancellationToken.None);
            });
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        },
        ["/oadm.v1.DiscoveryService/StopScan"] = async c =>
        {
            await c.Discovery.StopScanAsync(new Proto.DiscoverySession { SessionId = c.AdminSession });
            await c.AdminSessionStillWorksAsync();
        },
        ["/oadm.v1.DiscoveryService/Stop"] = async c =>
        {
            await c.Discovery.StopAsync(new Proto.DiscoverySession { SessionId = c.AdminSession });
            await c.AdminSessionStillWorksAsync();
        },
        ["/oadm.v1.AddDevicesService/Prepare"] = async c =>
            await CannotAddAsync(() => c.AddDevices.PrepareAsync(new Proto.PrepareRequest { SessionId = c.AdminSession, DiscoveredIds = { OutsideSerial } }).ResponseAsync),
        ["/oadm.v1.AddDevicesService/Commit"] = async c =>
            await CannotAddAsync(() => c.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = c.AdminSession, DiscoveredIds = { OutsideSerial } }).ResponseAsync),
        ["/oadm.v1.AddDevicesService/RetryAuth"] = async c =>
            await CannotAddAsync(() => c.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
            {
                SessionId = c.AdminSession, DiscoveredId = OutsideSerial, UserName = "root", Password = "pass",
            }).ResponseAsync),
        ["/oadm.v1.WaitingDeviceService/Add"] = async c =>
            await CannotAddAsync(() => c.Waiting.AddAsync(new Proto.AddWaitingRequest { Lines = { new Proto.WaitingLine { Address = "10.9.0.2" } } }).ResponseAsync),
        ["/oadm.v1.WaitingDeviceService/List"] = async c =>
            Assert.Empty((await c.Waiting.ListAsync(new Proto.ListWaitingRequest())).Devices),
        ["/oadm.v1.WaitingDeviceService/Watch"] = async c =>
        {
            using var call = c.Waiting.Watch(new Proto.Empty());
            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(0, call.ResponseStream.Current.Count);
        },
        ["/oadm.v1.WaitingDeviceService/Remove"] = async c =>
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await c.Waiting.RemoveAsync(new Proto.WaitingIds { Ids = { c.AdminWaitingId } }));
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
            await c.Waiting.RemoveAsync(new Proto.WaitingIds { All = true }); // all = the caller's entries: none
            Assert.Equal(1, (await new Proto.WaitingDeviceService.WaitingDeviceServiceClient(c.Host.Invoker).ListAsync(new Proto.ListWaitingRequest())).TotalCount);
        },
        ["/oadm.v1.WaitingDeviceService/TryNow"] = async c =>
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await c.Waiting.TryNowAsync(new Proto.WaitingIds { Ids = { c.AdminWaitingId } }));
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        },
    };

    private static async Task CannotAddAsync(Func<Task> call)
    {
        var refused = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal((StatusCode.PermissionDenied, Oadm.Server.AddDevices.AddDeviceTexts.CannotAdd), (refused.StatusCode, refused.Status.Detail));
    }

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
            var places = new[] { Cases.ContainsKey(method) || TaskIdCases.ContainsKey(method) || ListCases.ContainsKey(method) || OwnObjectCases.ContainsKey(method), Filtered.ContainsKey(method), NotConverted.ContainsKey(method), WithoutDevices.ContainsKey(method), ClosedByTheRule(method) }.Count(x => x);
            Assert.True(places == 1, $"{method} is in {places} canary lists (a case, filtered, not converted, without devices, closed by the rule); it must be in exactly one.");
        }

        Assert.Empty(Cases.Keys.Intersect(OwnObjectCases.Keys));
        Assert.All(NotConverted.Keys.Concat(Cases.Keys).Concat(TaskIdCases.Keys).Concat(ListCases.Keys).Concat(OwnObjectCases.Keys).Concat(Filtered.Keys).Concat(WithoutDevices.Keys), m => Assert.Contains(m, AccessPolicy.ListedMethods));
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
    public async Task AnotherUsersSessionsAndWaitingDevicesAreAnsweredLikeUnknownOnesAndNeverContacted()
    {
        await using var canary = await Canary.StartAsync();
        await canary.PrepareAdminObjectsAsync();
        foreach (var (method, call) in OwnObjectCases)
        {
            var before = await canary.SnapshotAsync();
            try
            {
                await call(canary);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{method}: {ex.Message}");
            }

            var after = await canary.SnapshotAsync();
            Assert.True(before == after, $"{method} contacted the device outside the view or changed something: {before} -> {after}");
        }
    }

    [Fact]
    public async Task AFilteredListNeverShowsTheDeviceOutsideTheView()
    {
        await using var canary = await Canary.StartAsync();
        foreach (var (method, call) in Filtered)
        {
            var before = await canary.SnapshotAsync();
            var text = await call(canary);
            Assert.False(text.Contains(canary.Outside.ToString(), StringComparison.OrdinalIgnoreCase), $"{method} showed the device outside the view.");
            Assert.False(text.Contains(OutsideSerial, StringComparison.OrdinalIgnoreCase), $"{method} showed the serial outside the view.");
            Assert.False(text.Contains("10.9.0.2", StringComparison.Ordinal), $"{method} showed the address outside the view.");
            Assert.Equal(before, await canary.SnapshotAsync());
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

        public Proto.DiscoveryService.DiscoveryServiceClient Discovery => new(Invoker);

        public Proto.AddDevicesService.AddDevicesServiceClient AddDevices => new(Invoker);

        public Proto.WaitingDeviceService.WaitingDeviceServiceClient Waiting => new(Invoker);

        /// <summary>The administrator's finished range scan that found the device outside the view.</summary>
        public string AdminSession { get; private set; } = string.Empty;

        /// <summary>An entry of the administrator's waiting list.</summary>
        public string AdminWaitingId { get; private set; } = string.Empty;

        /// <summary>The administrator's session (contact with the device) and waiting entry, before the snapshots.</summary>
        public async Task PrepareAdminObjectsAsync()
        {
            var (session, devices) = await TestHelpers.ScanWithLoginAsync(Host, "10.9.0.2", "10.9.0.2");
            Assert.Equal(OutsideSerial, Assert.Single(devices.Values).Serial);
            AdminSession = session;
            var admin = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(Host.Invoker);
            await admin.AddAsync(new Proto.AddWaitingRequest { Lines = { new Proto.WaitingLine { Address = "10.9.0.77" } } });
            AdminWaitingId = Assert.Single((await admin.ListAsync(new Proto.ListWaitingRequest())).Devices).Id;
        }

        /// <summary>The administrator's session is still there (a group user's Stop did nothing).</summary>
        public async Task AdminSessionStillWorksAsync()
        {
            var again = await TestHelpers.WatchToEndAsync(Host, AdminSession);
            Assert.Equal(Proto.AuthState.AlreadyAdded, again[OutsideSerial].AuthState);
        }

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

            // The role may open the web interface (DeviceWebService needs the Web View plugin's permission; the plugin is not loaded).
            var webView = new Core.Plugins.PluginRegistry();
            webView.RegisterCorePlugin(new WebViewPermission(), new Core.Plugins.PluginOrigin(WebViewPermission.PluginId, "1.0.0", null));
            webView.RegisterTaskPlugin(new CanaryTaskPlugin(), new Core.Plugins.PluginOrigin(CanaryTaskPlugin.PluginId, "1.0.0", null)); // one catalog: both permissions
            await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(webView), CancellationToken.None);

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

        /// <summary>Adds one more device inside the view (calls that remove the device they get).</summary>
        public async Task<Guid> AddSpareAsync()
        {
            var serial = "ACCC8E00C" + Random.Shared.Next(0x100, 0xFFF).ToString("X3", System.Globalization.CultureInfo.InvariantCulture);
            var spare = await Host.Get<DeviceRepository>().AddAsync(new Device { Serial = serial, Address = "10.9.0.9", AddedByAllDevices = true, Tags = ["Inside"] }, CancellationToken.None);
            return spare.Id;
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

    /// <summary>Only the permission of the Web View plugin, for the role of the canary user.</summary>
    private sealed class WebViewPermission : Sdk.Plugins.ICorePlugin
    {
        public const string PluginId = "oadm.web-view";

        public string Id => PluginId;

        public string DisplayName => "Web View";

        public string? IconKey => null;

        public IReadOnlyList<Sdk.Plugins.ITaskPlugin> TaskPlugins => [];

        public IReadOnlyList<Sdk.Plugins.PermissionDefinition> Permissions => [new("open", "Open devices", "Open devices.")];

        public Task StartAsync(Sdk.Plugins.ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public Sdk.Plugins.UserRole RequiredRole(string method) => Sdk.Plugins.UserRole.Operator;
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
