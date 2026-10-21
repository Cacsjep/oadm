using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// <c>siteSupport</c> on the server (CLAUDE.md "# Sites", Plugins and SDK: Declaration): a task of a package that does not work
/// at sites is greyed for devices there with "Not available for devices at a site" and refused before it runs; a query too;
/// the Plugins page gets the site support and the ports; the task's network is the device's route.
/// </summary>
public sealed class SitePluginSupportServerTests
{
    [Fact]
    public async Task ATaskWithoutSiteSupportIsGreyedAndRefusedForDevicesAtASite()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var host = await StartAsync(sites);
        var plugin = new RouteRecordingPlugin("test.undeclared");
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("third.party", "1.0.0", null)));
        var atSite = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 1);
        var local = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 2);

        var listed = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == plugin.Id);
        Assert.Equal([local.Id.ToString()], listed.RunnableDeviceIds);
        var group = Assert.Single(listed.NotRunnableGroups);
        Assert.Equal(SiteTexts.NotAvailableAtSite, group.Reason);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = plugin.Id, DeviceIds = { atSite.Id.ToString() } });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);
        Assert.Equal(Proto.TaskState.Failed, task.State);
        Assert.Equal(SiteTexts.NotAvailableAtSite, task.Devices[0].Message);
        Assert.Empty(plugin.Routes);

        var query = await Assert.ThrowsAsync<RpcException>(() => host.Tasks.QueryAsync(new Proto.TaskQueryRequest
        {
            PluginId = plugin.Id,
            DeviceId = atSite.Id.ToString(),
            Method = "read",
        }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, query.StatusCode);
        Assert.Equal(SiteTexts.NotAvailableAtSite, query.Status.Detail);
    }

    [Fact]
    public async Task ATaskThatWorksAtSitesRunsWithTheNetworkOfTheDevicesRoute()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var host = await StartAsync(sites);
        var plugin = new RouteRecordingPlugin("test.full");
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("bundled", "1.0.0", null) { DeclaredSiteSupport = SiteSupport.Full }));
        var atSite = await AddAtSiteAsync(host, site.Id, "192.168.0.20", 3);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = plugin.Id, DeviceIds = { atSite.Id.ToString() } });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);

        Assert.Equal(Proto.TaskState.Done, task.State);
        Assert.Equal([site.Id], plugin.Routes);
        Assert.Equal("query on " + site.Id, await QueryAsync(host, plugin.Id, atSite.Id));
    }

    [Fact]
    public async Task ThePluginsPageGetsTheSiteSupportAndThePorts()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        Assert.True(registry.RegisterTaskPlugin(new RouteRecordingPlugin("test.ntp-like"), new PluginOrigin("acme.ntp", "1.0.0", null)
        {
            DeclaredSiteSupport = SiteSupport.Full,
            SitePorts = [new SitePort(SiteProtocol.Udp, 123, "NTP server")],
        }));
        Assert.True(registry.RegisterTaskPlugin(new RouteRecordingPlugin("test.undeclared"), new PluginOrigin("acme.old", "1.0.0", null)));

        var packages = (await new Proto.PluginService.PluginServiceClient(host.Invoker).ListPackagesAsync(new Proto.Empty())).Packages;

        var ntp = Assert.Single(packages, p => p.Id == "acme.ntp");
        Assert.Equal("full", ntp.SiteSupport);
        var port = Assert.Single(ntp.SitePorts);
        Assert.Equal(("udp", 123, "NTP server"), (port.Protocol, port.Port, port.Reason));
        var old = Assert.Single(packages, p => p.Id == "acme.old");
        Assert.Equal("none", old.SiteSupport); // a manifest without siteSupport
        Assert.Empty(old.SitePorts);
    }

    private static async Task<string> QueryAsync(TestServerHost host, string pluginId, Guid deviceId) =>
        (await host.Tasks.QueryAsync(new Proto.TaskQueryRequest { PluginId = pluginId, DeviceId = deviceId.ToString(), Method = "read" })).PayloadJson;

    private static Task<TestServerHost> StartAsync(FakeSiteTransport sites) =>
        TestServerHost.StartAsync(configureServices: s =>
        {
            s.AddSingleton<ISiteStates>(sites);
            s.AddSingleton<ISiteNetwork>(sites);
        });

    private static async Task<Device> AddAtSiteAsync(TestServerHost host, Guid siteId, string address, int serial)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = DeviceScheme.Http, Status = DeviceStatus.Ok, SiteId = siteId, Category = DeviceCategory.Camera },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pw", CancellationToken.None);
        return device;
    }

    /// <summary>A task plugin that records the route of its network (never contacts a device).</summary>
    private sealed class RouteRecordingPlugin(string id) : ITaskPlugin, ITaskPluginQuery
    {
        public List<Guid?> Routes { get; } = [];

        public string Id => id;

        public string DisplayName => "Route " + id;

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            lock (Routes)
            {
                Routes.Add(ctx.Network!.SiteId);
            }

            return Task.CompletedTask;
        }

        public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
            Task.FromResult<string?>("query on " + ctx.Network!.SiteId);
    }
}
