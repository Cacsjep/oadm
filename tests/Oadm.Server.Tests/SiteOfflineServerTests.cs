using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Mapping;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// Site offline on the server (docs/sites.md, Site offline): the devices of an offline site show Site offline in every
/// list and stream, and tasks, queries, Log in, Set password, the RTSP port, live view and Web View refuse them with
/// "Site offline: &lt;site&gt;" before any request.
/// </summary>
public sealed class SiteOfflineServerTests
{
    private const string Offline = "Site offline: Building B";

    [Fact]
    public async Task ListsAndStreamsShowSiteOfflineAndASiteChangeIsPublished()
    {
        var (sites, site) = Sites();
        await using var host = await StartAsync(sites);
        var device = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 1);

        var listed = await TestHelpers.GetDeviceAsync(host, device.Id.ToString());
        Assert.Equal(Proto.DeviceStatus.Ok, listed.Status);
        Assert.Equal(site.Id.ToString(), listed.SiteId);

        using var watch = host.Devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true });
        while (await watch.ResponseStream.MoveNext(CancellationToken.None) && watch.ResponseStream.Current.Kind != Proto.DeviceChanged.Types.Kind.SnapshotEnd)
        {
        }

        sites.SetOnline(site.Id, false);
        Assert.True(await watch.ResponseStream.MoveNext(CancellationToken.None));
        var change = watch.ResponseStream.Current;
        Assert.Equal(Proto.DeviceChanged.Types.Kind.Updated, change.Kind);
        Assert.Equal(Proto.DeviceStatus.SiteOffline, change.Device.Status);
        Assert.Equal(Proto.DeviceStatus.SiteOffline, (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).Status);

        sites.SetOnline(site.Id, true);
        Assert.True(await watch.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(Proto.DeviceStatus.Ok, watch.ResponseStream.Current.Device.Status);
    }

    [Fact]
    public async Task TasksAndQueriesAreRefusedBeforeThePluginIsCalled()
    {
        var (sites, site) = Sites();
        await using var host = await StartAsync(sites);
        var plugin = new CountingPlugin();
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null)));
        var device = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 1);
        var local = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 2);
        sites.SetOnline(site.Id, false);

        var listed = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == plugin.Id);
        Assert.Equal([local.Id.ToString()], listed.RunnableDeviceIds);
        var group = Assert.Single(listed.NotRunnableGroups);
        Assert.Equal(Offline, group.Reason);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = plugin.Id, DeviceIds = { device.Id.ToString() } });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);
        Assert.Equal(Proto.TaskState.Failed, task.State);
        Assert.Equal(Offline, task.Devices[0].Message);
        Assert.Equal(0, plugin.Executions);

        var query = await Assert.ThrowsAsync<RpcException>(() => host.Tasks.QueryAsync(new Proto.TaskQueryRequest
        {
            PluginId = plugin.Id,
            DeviceId = device.Id.ToString(),
            Method = "read",
        }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, query.StatusCode);
        Assert.Equal(Offline, query.Status.Detail);
        Assert.Equal(0, plugin.Queries);
    }

    [Fact]
    public async Task LogInAndSetPasswordAreRefusedWithoutARequest()
    {
        var (sites, site) = Sites();
        var network = new FakeAxisNetwork();
        var camera = network.Add("192.168.0.20", FakeSerials.Make(1), "Right-pass1");
        await using var host = await StartAsync(sites, network);
        var device = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 1, DeviceStatus.CredentialsRequired);
        sites.SetOnline(site.Id, false);

        var login = await host.Devices.LogInAsync(new Proto.DeviceLogInRequest { UserName = "root", Password = "Right-pass1", DeviceIds = { device.Id.ToString() } });
        Assert.False(login.Results[0].Ok);
        Assert.Equal(Offline, login.Results[0].Message);

        var password = await host.Devices.SetFirstPasswordAsync(new Proto.SetFirstPasswordRequest { Password = "N3w-password", DeviceIds = { device.Id.ToString() } });
        Assert.False(password.Results[0].Ok);
        Assert.Equal(Offline, password.Results[0].Message);
        Assert.Empty(camera.CredentialRequests);
        Assert.Equal(0, camera.PwdgrpCalls);
    }

    [Fact]
    public async Task LiveViewAndWebViewRefuseTheDeviceButItsRtspPortIsStored()
    {
        var (sites, site) = Sites();
        await using var host = await StartAsync(sites);
        var device = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 1);
        sites.SetOnline(site.Id, false);
        var id = device.Id.ToString();

        var web = await new Proto.DeviceWebService.DeviceWebServiceClient(host.Invoker).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = id });
        Assert.False(web.Ok);
        Assert.Equal(Offline, web.Message);

        var sources = await Assert.ThrowsAsync<RpcException>(() => host.LiveView.ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = id }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, sources.StatusCode);
        Assert.Equal(Offline, sources.Status.Detail);

        using var video = host.LiveView.Watch(new Proto.LiveViewRequest { DeviceId = id });
        var watch = await Assert.ThrowsAsync<RpcException>(() => video.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.FailedPrecondition, watch.StatusCode);
        Assert.Equal(Offline, watch.Status.Detail);

        // The RTSP port lives only in OADM: it is stored for a device of an offline site like for any other.
        var online = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(2), Address = "10.0.0.21", Scheme = DeviceScheme.Http, Status = DeviceStatus.Ok, Category = DeviceCategory.Camera },
            CancellationToken.None);
        var port = await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { Port = 8554, DeviceIds = { id, online.Id.ToString() } });
        Assert.Equal(8554, port.Port);
        var read = await host.LiveView.GetRtspPortAsync(new Proto.RtspPortRequest { DeviceIds = { id, online.Id.ToString() } });
        Assert.Equal((8554, false), (read.Port, read.Mixed));

        Assert.False((await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = id })).Supported);
        var optics = await Assert.ThrowsAsync<RpcException>(() => host.LiveView.SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = id, Autofocus = true }).ResponseAsync);
        Assert.Equal(Offline + ".", optics.Status.Detail);
    }

    [Fact]
    public void TheMapperSendsSiteOfflineAndTheSite()
    {
        var site = Guid.NewGuid();
        var proto = Mappers.ToProto(new Device { Serial = "ACCC8E000001", Address = "192.168.0.20", Status = DeviceStatus.SiteOffline, SiteId = site }, false);

        Assert.Equal(Proto.DeviceStatus.SiteOffline, proto.Status);
        Assert.Equal(site.ToString(), proto.SiteId);
        Assert.Equal(string.Empty, Mappers.ToProto(new Device { Serial = "ACCC8E000002", Address = "10.0.0.1" }, false).SiteId);
    }

    private static (FakeSiteTransport Sites, FakeSiteTransport.FakeSite Site) Sites()
    {
        var sites = new FakeSiteTransport();
        return (sites, sites.AddSite("Building B"));
    }

    private static Task<TestServerHost> StartAsync(FakeSiteTransport sites, FakeAxisNetwork? network = null) =>
        TestServerHost.StartAsync(network, configureServices: s => s.AddSingleton<ISiteStates>(sites));

    private static async Task<Device> AddAtSiteAsync(TestServerHost host, Guid siteId, string address, int serial, DeviceStatus status = DeviceStatus.Ok)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = DeviceScheme.Http, Status = status, SiteId = siteId, Category = DeviceCategory.Camera },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pw", CancellationToken.None);
        return device;
    }

    /// <summary>A task plugin that runs on every device and counts what reaches it.</summary>
    private sealed class CountingPlugin : ITaskPlugin, ITaskPluginQuery
    {
        private int _executions;
        private int _queries;

        public int Executions => Volatile.Read(ref _executions);

        public int Queries => Volatile.Read(ref _queries);

        public string Id => "test.counting";

        public string DisplayName => "Counting";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            return Task.CompletedTask;
        }

        public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromResult<string?>("{}");
        }
    }
}
