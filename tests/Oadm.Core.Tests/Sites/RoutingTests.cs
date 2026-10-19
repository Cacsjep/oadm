using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Vapix;
using Oadm.Tests.Shared;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// Work stream 1 (docs/sites.md, Server: Routing): every device connection opens on its route. The server's own network
/// connects as before; a site route asks the site transport for a new stream for each connection, with TLS, pinning and
/// the login still in the server, end to end to the device.
/// </summary>
public sealed class RoutingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task OnTheServersOwnNetworkTheClientConnectsAndPinsAsBefore()
    {
        await using var device = LoopbackDevice.StartHttps();

        using (var first = VapixClient.Create(new VapixConnectionOptions { Address = $"127.0.0.1:{device.Port}" }))
        {
            Assert.Equal("ok", await GetAsync(first));
            Assert.Equal(device.Fingerprint, first.CertificateFingerprint);
        }

        using var pinnedElsewhere = VapixClient.Create(new VapixConnectionOptions
        {
            Address = $"127.0.0.1:{device.Port}",
            PinnedCertificateFingerprint = new string('A', 64),
        });
        var changed = await Assert.ThrowsAsync<CertificateChangedException>(() => GetAsync(pinnedElsewhere));
        Assert.Equal(device.Fingerprint, changed.ActualFingerprint);
    }

    [Fact]
    public async Task ThroughASiteTlsAndThePinStayEndToEnd()
    {
        await using var device = LoopbackDevice.StartHttps();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 443, device.EndPoint);
        var router = new DeviceRouter(sites, sites);

        using (var pinned = VapixClient.Create(Options(site.Id, "192.168.0.20") with { PinnedCertificateFingerprint = device.Fingerprint }, router))
        {
            Assert.Equal("ok", await GetAsync(pinned));
        }

        using var other = VapixClient.Create(Options(site.Id, "192.168.0.20") with { PinnedCertificateFingerprint = new string('A', 64) }, router);
        var changed = await Assert.ThrowsAsync<CertificateChangedException>(() => GetAsync(other));

        Assert.Equal(device.Fingerprint, changed.ActualFingerprint);
        Assert.Equal(["Building B 192.168.0.20:443 Short", "Building B 192.168.0.20:443 Short"], sites.Dials);
    }

    [Fact]
    public async Task EveryNewConnectionAsksTheSiteAgain()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        using var client = VapixClient.Create(Options(site.Id, "192.168.0.20", Uri.UriSchemeHttp), new DeviceRouter(sites, sites));

        // The device closes after each answer, so each request needs a new connection: never a stream kept from creation.
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/test.cgi");
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, CancellationToken.None);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(3, sites.Dials.Count);
        Assert.Equal(3, device.Accepted);
    }

    [Fact]
    public async Task ASiteProblemIsASiteExceptionAndADeadDeviceStaysASocketError()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        var router = new DeviceRouter(sites, sites);
        using var dead = VapixClient.Create(Options(site.Id, "192.168.0.99"), router);
        using var otherPort = VapixClient.Create(Options(site.Id, "192.168.0.20:8443"), router);

        var deviceError = await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(dead));
        var portError = await Assert.ThrowsAsync<SiteException>(() => GetAsync(otherPort));
        sites.SetOnline(site.Id, false);
        var offline = await Assert.ThrowsAsync<SiteException>(() => GetAsync(dead));

        Assert.Equal(SocketError.HostUnreachable, Assert.IsType<SocketException>(deviceError.InnerException).SocketErrorCode);
        Assert.Equal(SdkDeviceStatus.Unreachable, DeviceStatusClassifier.FromException(deviceError));
        Assert.Equal(SiteProblem.PortNotAllowed, portError.Problem);
        Assert.Equal("Port 8443 is not allowed at Building B. Allow it in the OADM Site Node app.", portError.Message);
        Assert.Equal(SiteProblem.Offline, offline.Problem);
        Assert.Equal("Site offline: Building B", offline.Message);
    }

    [Fact]
    public async Task ASiteRouteWithoutSitesIsRefusedAsOffline()
    {
        var siteId = Guid.NewGuid();
        using var client = VapixClient.Create(Options(siteId, "192.168.0.20"));

        var error = await Assert.ThrowsAsync<SiteException>(() => GetAsync(client));

        Assert.Equal(SiteProblem.Offline, error.Problem);
        Assert.Equal(siteId, error.SiteId);
    }

    [Fact]
    public async Task TimeoutsThroughASiteGetFourRoundTripsOnTop()
    {
        var transport = new RecordingTransport();
        var states = new FakeSiteTransport();
        var site = states.AddSite("Satellite");
        site.RoundTrip = TimeSpan.FromMilliseconds(600);
        var router = new DeviceRouter(transport, states);

        using var client = VapixClient.Create(Options(site.Id, "192.168.0.20"), router);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBasicDeviceInfoAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(client));

        Assert.Equal(TimeSpan.FromMilliseconds(2400), router.ExtraTimeout(DeviceRoute.ViaSite(site.Id)));
        Assert.Equal(TimeSpan.Zero, router.ExtraTimeout(DeviceRoute.Local));
        Assert.Equal(TimeSpan.FromSeconds(3), router.Timeout(DeviceRoute.Local, TimeSpan.FromSeconds(3)));
        Assert.Equal(Timeout.InfiniteTimeSpan, router.Timeout(DeviceRoute.ViaSite(site.Id), Timeout.InfiniteTimeSpan));
        // The status poll (3 s) over a 600 ms link waits 5.4 s; a normal request 15 + 2.4 s.
        Assert.Equal([TimeSpan.FromMilliseconds(5400), TimeSpan.FromMilliseconds(17400)], transport.Timeouts);
    }

    [Fact]
    public void BasicOverPlainHttpNeverCrossesASite()
    {
        var credentials = new NetworkCredential("root", "pass");
        var uri = new Uri("http://192.168.0.20/");
        using var local = VapixClient.CreateHandler(uri, credentials, new CertificatePinning(), allowBasicOverHttp: true);
        using var site = VapixClient.CreateHandler(uri, credentials, new CertificatePinning(), allowBasicOverHttp: true, route: DeviceRoute.ViaSite(Guid.NewGuid()));

        Assert.NotNull(local.Credentials!.GetCredential(uri, "Basic"));
        Assert.Null(site.Credentials!.GetCredential(uri, "Basic"));
        Assert.NotNull(site.Credentials!.GetCredential(uri, "Digest"));

        var found = Discovered(DeviceRoute.ViaSite(Guid.NewGuid()));
        Assert.False(DeviceConnections.For(found, Uri.UriSchemeHttp, Generous, credentials, allowBasicOverHttp: true).AllowBasicOverHttp);
        Assert.True(DeviceConnections.For(found with { Route = DeviceRoute.Local }, Uri.UriSchemeHttp, Generous, credentials, allowBasicOverHttp: true).AllowBasicOverHttp);
    }

    [Fact]
    public async Task WebSocketsGoThroughTheSiteAndLeaveTheConnectionPool()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        using var client = VapixClient.Create(Options(site.Id, "192.168.0.20", Uri.UriSchemeHttp), new DeviceRouter(sites, sites));

        // More WebSockets than a site allows connections per device: an upgraded connection leaves the pool.
        var sockets = new List<WebSocket>();
        for (var i = 0; i < 3; i++)
        {
            sockets.Add(await client.ConnectWebSocketAsync("/vapix/ws-data-stream?sources=events", TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        try
        {
            foreach (var socket in sockets)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[64];
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                Assert.Equal("echo hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
            }

            Assert.Equal("ok", await GetAsync(client));
            Assert.All(sites.Dials, d => Assert.StartsWith("Building B 192.168.0.20:80 ", d, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheProbeAsksTheRouteOfEachRequest()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        using var probe = new VapixDeviceProbe(Generous, router: new DeviceRouter(sites, sites));

        var found = await ((IDeviceProbe)probe).ProbeAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.ViaSite(site.Id), Generous, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(LoopbackDevice.Serial, found.Serial);
        Assert.Equal("http", found.Scheme);
        // HTTPS first (not mapped: the device does not answer there), then HTTP: every connection through the site.
        Assert.Contains("Building B 192.168.0.20:443 Short", sites.Dials);
        Assert.Contains("Building B 192.168.0.20:80 Short", sites.Dials);
    }

    [Fact]
    public async Task TheProbeHandsTheRouteToItsHandler()
    {
        var handler = new RouteRecordingHandler();
        using var probe = new VapixDeviceProbe(handler, Generous);
        var route = DeviceRoute.ViaSite(Guid.NewGuid());

        Assert.Null(await probe.ProbeAsync(IPAddress.Parse("192.168.0.20"), route, Generous, CancellationToken.None));
        Assert.Null(await probe.ProbeAsync(IPAddress.Parse("192.168.0.21"), Generous, CancellationToken.None));

        Assert.Equal([route, route, DeviceRoute.Local, DeviceRoute.Local], handler.Routes);
    }

    [Fact]
    public async Task TheProbeLetsASiteProblemThrough()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B", online: false);
        using var probe = new VapixDeviceProbe(Generous, router: new DeviceRouter(sites, sites));

        var error = await Assert.ThrowsAsync<SiteException>(() => probe.ProbeAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.ViaSite(site.Id), Generous, CancellationToken.None));

        Assert.Equal(SiteProblem.Offline, error.Problem);
    }

    [Fact]
    public async Task TheAddProbeConnectsOnTheRoute()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        var probe = new VapixProbe(Generous, router: new DeviceRouter(sites, sites));

        var found = await probe.ProbeAsync("192.168.0.20", CancellationToken.None, route: DeviceRoute.ViaSite(site.Id));

        Assert.NotNull(found);
        Assert.Equal(LoopbackDevice.Serial, found.Serial);
        Assert.All(sites.Dials, d => Assert.StartsWith("Building B 192.168.0.20:", d, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RtspConnectsThroughTheSiteOnTheDevicesRtspPort()
    {
        using var camera = new TcpListener(IPAddress.Loopback, 0);
        camera.Start();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 8554, (IPEndPoint)camera.LocalEndpoint);
        site.ConnectPorts.Add(8554);
        var router = new DeviceRouter(sites, sites);

        await using var client = await RtspClient.ConnectAsync("192.168.0.20", 8554, null, Generous, CancellationToken.None, route: DeviceRoute.ViaSite(site.Id), router: router);
        using var accepted = await camera.AcceptTcpClientAsync();

        Assert.True(accepted.Connected);
        Assert.Equal(["Building B 192.168.0.20:8554 Permanent"], sites.Dials);
    }

    [Fact]
    public async Task RtspTellsADeadCameraFromASiteProblem()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        var router = new DeviceRouter(sites, sites);
        var route = DeviceRoute.ViaSite(site.Id);

        var dead = await Assert.ThrowsAsync<LiveViewException>(() => RtspClient.ConnectAsync("192.168.0.99", 554, null, Generous, CancellationToken.None, route: route, router: router));
        var port = await Assert.ThrowsAsync<SiteException>(() => RtspClient.ConnectAsync("192.168.0.20", 8554, null, Generous, CancellationToken.None, route: route, router: router));

        Assert.Equal(LiveViewError.Unreachable, dead.Error);
        Assert.IsType<SocketException>(dead.InnerException);
        Assert.Equal(SiteProblem.PortNotAllowed, port.Problem);
    }

    [Fact]
    public async Task RtspOnTheServersOwnNetworkConnectsAsBefore()
    {
        using var camera = new TcpListener(IPAddress.Loopback, 0);
        camera.Start();

        await using var client = await RtspClient.ConnectAsync("127.0.0.1", ((IPEndPoint)camera.LocalEndpoint).Port, null, Generous, CancellationToken.None);
        using var accepted = await camera.AcceptTcpClientAsync();

        Assert.True(accepted.Connected);
    }

    [Fact]
    public async Task TheFactoryKeysItsClientsByRouteAndDropsAMovedDevice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var devices = database.Get<DeviceRepository>();
        var connector = new RecordingConnector();
        using var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), connector);
        var device = await devices.AddAsync(new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000002", Address = "192.168.0.20", Status = SdkDeviceStatus.Ok }, CancellationToken.None);
        var siteId = Guid.NewGuid();

        var local = await factory.GetClientAsync(device.Id, CancellationToken.None);
        Assert.Same(local, await factory.GetClientAsync(device.Id, CancellationToken.None));
        await devices.UpdateAsync(device.Id, d => d.SiteId = siteId, CancellationToken.None);
        Assert.Equal(0, factory.CachedCount);
        var atSite = await factory.GetClientAsync(device.Id, CancellationToken.None);
        await devices.UpdateAsync(device.Id, d => d.Address = "192.168.0.21", CancellationToken.None);
        Assert.Equal(0, factory.CachedCount);
        await factory.GetClientAsync(device.Id, CancellationToken.None);
        await devices.UpdateAsync(device.Id, d => d.Model = "P3265-V", CancellationToken.None);

        Assert.NotSame(local, atSite);
        Assert.Equal(1, factory.CachedCount);
        Assert.Equal([DeviceRoute.Local, DeviceRoute.ViaSite(siteId), DeviceRoute.ViaSite(siteId)], connector.Options.Select(o => o.Route));
        Assert.Equal(["192.168.0.20", "192.168.0.20", "192.168.0.21"], connector.Options.Select(o => o.Address));
    }

    [Fact]
    public async Task UncachedClientsKeepTheDevicesRoute()
    {
        await using var database = await TestDatabase.CreateAsync();
        var devices = database.Get<DeviceRepository>();
        var connector = new RecordingConnector();
        using var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), connector);
        var siteId = Guid.NewGuid();
        var device = await devices.AddAsync(new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000003", Address = "192.168.0.20", SiteId = siteId, Status = SdkDeviceStatus.Ok }, CancellationToken.None);

        using (await factory.CreateForAddressAsync(device, "192.168.0.30", CancellationToken.None))
        using (factory.CreateWithCredentials(device, "root", "pass"))
        {
        }

        Assert.All(connector.Options, o => Assert.Equal(DeviceRoute.ViaSite(siteId), o.Route));
        Assert.Equal(["192.168.0.30", "192.168.0.20"], connector.Options.Select(o => o.Address));
    }

    [Fact]
    public void TheOptionsOfADeviceCarryItsRoute()
    {
        var siteId = Guid.NewGuid();
        var device = new Device { Serial = "ACCC8E000004", Address = "cam.site.example:8443", SiteId = siteId, Scheme = DeviceScheme.Http, CertFingerprintSha256 = "AB" };

        var options = DeviceConnections.For(device, new NetworkCredential("root", "pass"));
        var found = DeviceConnections.For(Discovered(DeviceRoute.ViaSite(siteId)), Uri.UriSchemeHttps, Generous);

        Assert.Equal(DeviceRoute.ViaSite(siteId), options.Route);
        Assert.Equal("cam.site.example:8443", options.Address);
        Assert.Equal(Uri.UriSchemeHttp, options.Scheme);
        Assert.Equal("AB", options.PinnedCertificateFingerprint);
        Assert.Equal(DeviceConnections.DefaultTimeout, options.Timeout);
        Assert.Equal(DeviceRoute.Local, DeviceConnections.For(new Device { Serial = "ACCC8E000005", Address = "10.0.0.48" }).Route);
        Assert.Equal(DeviceRoute.ViaSite(siteId), found.Route);
        Assert.Equal("192.168.0.20", found.Address);
    }

    [Fact]
    public async Task NamesThroughASiteAreNotResolvedOnTheServer()
    {
        var states = new FakeSiteTransport();
        var site = states.AddSite("Building B");
        var dns = new RoutedDns(states);
        var lookup = new SystemDnsNameLookup(dns);

        var error = await Assert.ThrowsAsync<SiteException>(() => dns.ResolveAsync(DeviceRoute.ViaSite(site.Id), "cam.site.example", CancellationToken.None));
        Assert.Equal(SiteProblem.NotSupported, error.Problem);
        Assert.Null(await dns.ReverseLookupAsync(DeviceRoute.ViaSite(site.Id), IPAddress.Parse("192.168.0.20"), CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.ViaSite(site.Id), CancellationToken.None));
        Assert.Contains(IPAddress.Loopback, await dns.ResolveAsync(DeviceRoute.Local, "127.0.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task NamesThroughASiteResolveOnTheNode()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        site.Names["CAM.SITE.EXAMPLE"] = [IPAddress.Parse("192.168.0.20")];
        site.ReverseNames[IPAddress.Parse("192.168.0.20")] = "cam.site.example";
        var dns = new RoutedDns(sites, sites);
        var lookup = new SystemDnsNameLookup(dns);
        var route = DeviceRoute.ViaSite(site.Id);

        Assert.Equal([IPAddress.Parse("192.168.0.20")], await dns.ResolveAsync(route, "cam.site.example", CancellationToken.None));
        Assert.Equal("cam.site.example", await lookup.LookupAsync(IPAddress.Parse("192.168.0.20"), route, CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("192.168.0.21"), route, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => dns.ResolveAsync(route, "other.site.example", CancellationToken.None));
        Assert.Equal(System.Net.Sockets.SocketError.HostNotFound, unknown.SocketErrorCode);

        sites.SetOnline(site.Id, false);
        var offline = await Assert.ThrowsAsync<SiteException>(() => dns.ResolveAsync(route, "cam.site.example", CancellationToken.None));
        Assert.Equal(SiteProblem.Offline, offline.Problem);
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("192.168.0.20"), route, CancellationToken.None));
    }

    private static VapixConnectionOptions Options(Guid siteId, string address, string scheme = "https") =>
        DeviceConnections.For(Discovered(DeviceRoute.ViaSite(siteId), address), scheme, DeviceConnections.DefaultTimeout);

    private static DiscoveredDevice Discovered(DeviceRoute route, string address = "192.168.0.20")
    {
        var host = address.Split(':')[0];
        return new DiscoveredDevice(LoopbackDevice.Serial, LoopbackDevice.Serial, IPAddress.Parse(host), null, null, null,
            DiscoveredDeviceStatus.CredentialsRequired, null, DiscoverySources.None, DateTimeOffset.UnixEpoch, EnteredAddress: address == host ? null : address)
        {
            Route = route,
        };
    }

    private static async Task<string> GetAsync(VapixClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/test.cgi");
        using var response = await client.SendAsync(request, CancellationToken.None);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Records the connect timeout of every dial and answers like a device that never answers.</summary>
    private sealed class RecordingTransport : ISiteTransport
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
        {
            lock (Timeouts)
            {
                Timeouts.Add(timeout);
            }

            throw new SocketException((int)SocketError.TimedOut);
        }
    }

    private sealed class RecordingConnector : IVapixConnector
    {
        public List<VapixConnectionOptions> Options { get; } = [];

        public VapixClient Connect(VapixConnectionOptions options)
        {
            Options.Add(options);
            return VapixClient.Create(options);
        }
    }

    /// <summary>Answers nothing (404) and records the route each request carries.</summary>
    private sealed class RouteRecordingHandler : HttpMessageHandler
    {
        public List<DeviceRoute> Routes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Routes)
            {
                Routes.Add(request.Options.TryGetValue(DeviceRouter.RouteOption, out var route) ? route : default);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
