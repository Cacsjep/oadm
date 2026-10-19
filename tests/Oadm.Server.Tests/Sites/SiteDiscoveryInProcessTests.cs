using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Support;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// Discovery at sites with the real OADM Site Node in this process (docs/sites.md, Tests: "Server with an in-process
/// node"): the node's network calls (<see cref="ISiteNetwork"/> over the control link) and Add manually through the node.
/// </summary>
public sealed class SiteDiscoveryInProcessTests
{
    [Fact]
    public async Task TheSiteNetworkCallsRunOnTheNode()
    {
        await using var device = await LoopbackAxisDevice.StartAsync(new FakeAxisDevice { Address = "192.168.0.30", Serial = "ACCC8E000030", Password = "pass" });
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.30", 80, device.EndPoint);
        node.Map("192.168.0.31", 80, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        node.Map("192.168.0.31", 443, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        node.Map("192.168.0.30", 443, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        var dns = (FakeDns)node.Get<INodeDns>();
        dns.Names["cam30.site.example"] = [IPAddress.Parse("192.168.0.30")];
        dns.Reverse[IPAddress.Parse("192.168.0.30")] = "cam30.site.example";
        var id = await server.BindAsync(node);
        var sites = server.Host.Get<ISiteNetwork>();
        await Eventually.TrueAsync(() => sites.Reaches(id, IPAddress.Parse("192.168.0.30")), "the node's networks from Watch");
        var ct = CancellationToken.None;

        Assert.Equal([id], sites.OnlineSites());
        Assert.Equal(id, sites.FindSite("building b"));
        Assert.Null(sites.FindSite("Warehouse"));
        Assert.False(sites.Reaches(id, IPAddress.Parse("10.0.0.20"))); // the office card is not reached
        Assert.Equal([IPAddress.Parse("192.168.0.30")], await sites.ResolveAsync(id, "cam30.site.example", ct));
        Assert.Empty(await sites.ResolveAsync(id, "nothing.site.example", ct));
        Assert.Equal("cam30.site.example", await sites.ReverseLookupAsync(id, IPAddress.Parse("192.168.0.30"), ct));
        Assert.Equal(IPAddress.Parse("192.168.0.5"), await sites.LocalAddressForAsync(id, "192.168.0.30", ct));

        // Ping: the node has no OS ping in the test, so it checks TCP 80 / 443.
        Assert.True(await sites.PingAsync(id, "192.168.0.30", TimeSpan.FromSeconds(5), ct));
        // A host that refuses (sends a reset) is there; one where nothing is does not answer.
        Assert.True(await sites.PingAsync(id, "192.168.0.31", TimeSpan.FromSeconds(5), ct));
        node.Connector.Dead.Add(IPAddress.Parse("192.168.0.32"));
        Assert.False(await sites.PingAsync(id, "192.168.0.32", TimeSpan.FromSeconds(5), ct));
        Assert.True(await sites.ProbeTcpAsync(id, "192.168.0.30", 80, TimeSpan.FromSeconds(5), ct));
        var port = await Assert.ThrowsAsync<SiteException>(() => sites.ProbeTcpAsync(id, "192.168.0.30", 8443, TimeSpan.FromSeconds(5), ct));
        Assert.Equal(SiteProblem.PortNotAllowed, port.Problem);
        var outside = await Assert.ThrowsAsync<SiteException>(() => sites.PingAsync(id, "10.0.0.20", TimeSpan.FromSeconds(5), ct));
        Assert.Equal(SiteProblem.OutsideNetworks, outside.Problem);

        // The first contact of a range scan on the node: .30 answers on 80, .31 is closed, .29 is outside the map (refused).
        node.Map("192.168.0.29", 80, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        node.Map("192.168.0.29", 443, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        var scan = new List<SiteRangeScanResult>();
        await foreach (var result in sites.RangeScanAsync(id, IPAddress.Parse("192.168.0.29"), IPAddress.Parse("192.168.0.31"), 4, TimeSpan.FromSeconds(5), ct))
        {
            scan.Add(result);
        }

        Assert.True(scan[^1].Finished);
        Assert.Equal(3, scan[^1].Total);
        var open = Assert.Single(scan, r => r.Address is not null);
        Assert.Equal(IPAddress.Parse("192.168.0.30"), open.Address);
        Assert.Equal([80], open.OpenPorts);

        // mDNS on the node's reached cards: nothing announces in the test, the browse ends after its time.
        var browsed = new List<SiteBrowseResult>();
        await foreach (var found in sites.BrowseAsync(id, TimeSpan.FromSeconds(1), ct))
        {
            browsed.Add(found);
        }

        Assert.DoesNotContain(browsed, b => b.Serial == "ACCC8E000030");
    }

    [Fact]
    public async Task AddManuallyFindsADeviceThroughTheNodeAndAddsItAtTheSite()
    {
        // Factory default: the add needs no login, so the plain loopback device is enough.
        await using var device = await LoopbackAxisDevice.StartAsync(new FakeAxisDevice { Address = "192.168.0.20", Serial = "ACCC8E000020", Model = "P3265-V" });
        await using var server = await SiteServer.StartAsync(configure: RoutedProbe);
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.20", 80, device.EndPoint);
        node.Map("192.168.0.20", 443, new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp()));
        var id = await server.BindAsync(node, "Building B");
        await Eventually.TrueAsync(() => server.Host.Get<ISiteNetwork>().Reaches(id, IPAddress.Parse("192.168.0.20")), "the node's networks from Watch");
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(server.Host.Invoker);
        var add = new Proto.AddDevicesService.AddDevicesServiceClient(server.Host.Invoker);

        var session = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var found = await SiteDiscoveryTests.WatchAsync(discovery, session.SessionId);

        var row = Assert.Single(found.Devices);
        Assert.Equal(id.ToString(), row.SiteId);
        Assert.Equal("Building B", row.SiteName);
        Assert.Equal(Proto.AuthState.PasswordNotSet, row.AuthState);
        Assert.Equal("http", row.Scheme);

        var commit = await add.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { row.DiscoveredId } });
        Assert.Equal(Proto.DeviceStatus.PasswordNotSet, Assert.Single(commit.Results).Status);
        var stored = await server.Host.Get<DeviceRepository>().GetAsync(Guid.Parse(Assert.Single(commit.DeviceIds)), CancellationToken.None);
        Assert.Equal(id, stored!.SiteId);
        Assert.Equal("192.168.0.20", stored.Address);
    }

    /// <summary>The add page's probe on its real route through the node; the server's own network stays the fake one (no real network in tests).</summary>
    private static void RoutedProbe(IServiceCollection services)
    {
        services.RemoveAll<VapixProbe>();
        services.AddSingleton(sp =>
        {
            var router = sp.GetRequiredService<DeviceRouter>();
            var local = new FakeAxisNetwork();
            return new VapixProbe(TimeSpan.FromSeconds(10), (uri, pinning, route) => route.IsLocal
                ? local.CreateHandler()
                : VapixClient.CreateHandler(uri, null, pinning, route: route, router: router, timeout: TimeSpan.FromSeconds(10)));
        });
    }
}
