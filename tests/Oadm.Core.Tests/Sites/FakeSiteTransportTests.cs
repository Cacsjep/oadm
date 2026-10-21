using System.Net;
using System.Net.Sockets;

using Oadm.Core.Sites;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>The fake the other work streams test against behaves like a node: device errors vs site errors.</summary>
public sealed class FakeSiteTransportTests
{
    [Fact]
    public async Task TwoSitesReachDifferentDevicesAtTheSameAddress()
    {
        using var a = new TcpListener(IPAddress.Loopback, 0);
        using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start();
        b.Start();
        var transport = new FakeSiteTransport();
        var siteA = transport.AddSite("Building A").Map("192.168.0.20", 80, (IPEndPoint)a.LocalEndpoint);
        var siteB = transport.AddSite("Building B").Map("192.168.0.20", 80, (IPEndPoint)b.LocalEndpoint);

        await using var toA = await transport.OpenStreamAsync(siteA.Id, "192.168.0.20", 80, SiteDialKind.Short, TimeSpan.FromSeconds(30), CancellationToken.None);
        using var acceptedA = await a.AcceptTcpClientAsync();
        await using var toB = await transport.OpenStreamAsync(siteB.Id, "192.168.0.20", 80, SiteDialKind.Short, TimeSpan.FromSeconds(30), CancellationToken.None);
        using var acceptedB = await b.AcceptTcpClientAsync();

        Assert.True(acceptedA.Connected);
        Assert.True(acceptedB.Connected);
    }

    [Fact]
    public async Task SiteProblemsAreSiteExceptionsAndDeadDevicesSocketErrors()
    {
        var transport = new FakeSiteTransport();
        var site = transport.AddSite("Building B");
        site.Reaches = host => host.StartsWith("192.168.0.", StringComparison.Ordinal);

        var dead = await Assert.ThrowsAsync<SocketException>(() => Open(transport, site.Id, "192.168.0.99", 80));
        var port = await Assert.ThrowsAsync<SiteException>(() => Open(transport, site.Id, "192.168.0.20", 8443));
        var outside = await Assert.ThrowsAsync<SiteException>(() => Open(transport, site.Id, "10.0.0.5", 80));
        transport.SetOnline(site.Id, false);
        var offline = await Assert.ThrowsAsync<SiteException>(() => Open(transport, site.Id, "192.168.0.20", 80));

        Assert.Equal(SocketError.HostUnreachable, dead.SocketErrorCode);
        Assert.Equal(SiteProblem.PortNotAllowed, port.Problem);
        Assert.Equal("Port 8443 is not allowed at Building B. Allow it in the OADM Site Node app.", port.Message);
        Assert.Equal(SiteProblem.OutsideNetworks, outside.Problem);
        Assert.Equal(SiteProblem.Offline, offline.Problem);
        Assert.Equal("Site offline: Building B", offline.Message);
        Assert.False(transport.IsOnline(site.Id));
    }

    [Fact]
    public void RoutesCompareByValue()
    {
        var id = Guid.NewGuid();

        Assert.True(DeviceRoute.Local.IsLocal);
        Assert.Equal(DeviceRoute.ViaSite(id), new DeviceRoute(id));
        Assert.NotEqual(DeviceRoute.Local, DeviceRoute.ViaSite(id));
    }

    private static async Task Open(FakeSiteTransport transport, Guid site, string host, int port)
    {
        await using var stream = await transport.OpenStreamAsync(site, host, port, SiteDialKind.Short, TimeSpan.FromSeconds(30), CancellationToken.None);
    }
}
