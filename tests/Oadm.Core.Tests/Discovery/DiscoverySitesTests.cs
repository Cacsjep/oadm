using System.Collections.Concurrent;
using System.Net;

using Oadm.Core.Discovery;
using Oadm.Core.Sites;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Discovery;

/// <summary>
/// Discovery everywhere by itself (CLAUDE.md "# Sites", "Discovery, everywhere by itself"): every mode asks the server's own
/// network and every online site, each result carries its route, the same MAC address through several routes is one
/// device through the site, and two sites with the same 192.168.0.20 are two devices.
/// </summary>
public sealed class DiscoverySitesTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly IPEndPoint Somewhere = new(IPAddress.Loopback, 9);

    [Fact]
    public async Task ZeroConfBrowsesTheServerAndEverySiteAndKeepsTheRoute()
    {
        var (sites, a, b) = TwoSites();
        a.Announced.Add(new SiteBrowseResult("ACCC8E0000A1", "Cam A - ACCC8E0000A1", [IPAddress.Parse("192.168.0.20")], 80, "axis-accc8e0000a1.local"));
        b.Announced.Add(new SiteBrowseResult("ACCC8E0000B1", "Cam B - ACCC8E0000B1", [IPAddress.Parse("192.168.0.20")], 80, "axis-accc8e0000b1.local"));
        var probe = new RoutedProbe()
            .Add(DeviceRoute.Local, "10.0.0.48", "B8A44F631339")
            .Add(DeviceRoute.ViaSite(a.Id), "192.168.0.20", "ACCC8E0000A1", "P3265-V")
            .Add(DeviceRoute.ViaSite(b.Id), "192.168.0.20", "ACCC8E0000B1", "Q6135-LE");
        var browser = new FakeMdnsBrowser();
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe, sites: sites);

        var session = service.StartZeroConf(duration: TimeSpan.FromSeconds(30));
        browser.Announce("10.0.0.48", "B8A44F631339");
        await WaitAsync(service, session.Id, devices => devices.Count == 3 && devices.All(d => d.Model is not null));
        service.StopScan(session.Id);

        var found = service.GetDevices(session.Id).ToDictionary(d => d.Serial);
        Assert.Equal(DeviceRoute.Local, found["B8A44F631339"].Route);
        Assert.Equal(DeviceRoute.ViaSite(a.Id), found["ACCC8E0000A1"].Route);
        Assert.Equal(DeviceRoute.ViaSite(b.Id), found["ACCC8E0000B1"].Route);
        Assert.Equal("P3265-V", found["ACCC8E0000A1"].Model);
        Assert.Equal("Q6135-LE", found["ACCC8E0000B1"].Model);
        Assert.Equal("axis-accc8e0000b1", found["ACCC8E0000B1"].HostName);
        Assert.Contains((DeviceRoute.ViaSite(b.Id), "192.168.0.20"), probe.Probed);
    }

    [Fact]
    public async Task TheSameMacAddressThroughTheServerAndASiteIsOneDeviceThroughTheSite()
    {
        var (sites, a, _) = TwoSites();
        a.Announced.Add(new SiteBrowseResult("ACCC8E0000A1", "Cam - ACCC8E0000A1", [IPAddress.Parse("192.168.0.20")], 80, "axis-accc8e0000a1.local"));
        var probe = new RoutedProbe()
            .Add(DeviceRoute.Local, "192.168.0.20", "ACCC8E0000A1")
            .Add(DeviceRoute.ViaSite(a.Id), "192.168.0.20", "ACCC8E0000A1");
        var browser = new FakeMdnsBrowser();
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe, sites: sites);

        var session = service.StartZeroConf(duration: TimeSpan.FromSeconds(30));
        browser.Announce("192.168.0.20", "ACCC8E0000A1");
        await WaitAsync(service, session.Id, devices => devices.Count == 1 && devices[0].OtherRoutes.Count == 1 && devices[0].Model is not null);

        var device = Assert.Single(service.GetDevices(session.Id));
        Assert.Equal(DeviceRoute.ViaSite(a.Id), device.Route);
        Assert.Equal([DeviceRoute.Local], device.OtherRoutes.Items);
    }

    [Fact]
    public void ASiteTakesTheDeviceOverAndTheServersOwnNetworkNeverDoes()
    {
        var site = DeviceRoute.ViaSite(Guid.NewGuid());
        var other = DeviceRoute.ViaSite(Guid.NewGuid());
        var now = DateTimeOffset.UnixEpoch;
        var local = Observation("10.0.0.20", DeviceRoute.Local, "http");
        var atSite = Observation("192.168.0.20", site, "https");

        var first = DiscoveryService.Merge(null, local, now)!;
        var taken = DiscoveryService.Merge(first, atSite, now)!;
        Assert.Equal(site, taken.Route);
        Assert.Equal(IPAddress.Parse("192.168.0.20"), taken.Address);
        Assert.Equal("https", taken.Scheme);
        Assert.Equal([DeviceRoute.Local], taken.OtherRoutes.Items);

        // The server's own network or a second site later: the device stays at the first site, the route is remembered.
        Assert.Null(DiscoveryService.Merge(taken, local, now));
        var second = DiscoveryService.Merge(taken, Observation("192.168.0.20", other, "https"), now)!;
        Assert.Equal(site, second.Route);
        Assert.Equal(2, second.OtherRoutes.Count);
        Assert.True(second.OtherRoutes.Contains(other));
    }

    [Fact]
    public async Task ARangeScanRunsOnEachSiteThatReachesTheRangeAndProbesThroughTheSite()
    {
        var (sites, a, b) = TwoSites();
        a.Reaches = host => host.StartsWith("192.168.0.", StringComparison.Ordinal);
        b.Reaches = host => host.StartsWith("172.16.", StringComparison.Ordinal);
        a.Map("192.168.0.20", 443, Somewhere);
        var probe = new RoutedProbe().Add(DeviceRoute.ViaSite(a.Id), "192.168.0.20", "ACCC8E0000A1");
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe, sites: sites);

        var session = service.StartRangeScan(IPAddress.Parse("192.168.0.1"), IPAddress.Parse("192.168.0.30"));
        var events = await CollectAsync(service, session.Id);

        Assert.True(events[^1].Finished);
        Assert.Equal(100, events[^1].ProgressPercent);
        var device = Assert.Single(events.Select(e => e.Device).OfType<DiscoveredDevice>().DistinctBy(d => d.Serial));
        Assert.Equal(DeviceRoute.ViaSite(a.Id), device.Route);
        Assert.Equal(DiscoverySources.RangeScan, device.Sources);

        // Site B does not reach the range: nothing ran there; the server's own network scanned every address itself.
        Assert.DoesNotContain(probe.Probed, p => p.Route == DeviceRoute.ViaSite(b.Id));
        Assert.Equal(30, probe.Probed.Count(p => p.Route.IsLocal));
        Assert.Single(probe.Probed, p => p.Route == DeviceRoute.ViaSite(a.Id));
        var progress = events.Where(e => e.Device is null).Select(e => e.ProgressPercent).ToList();
        Assert.Equal(progress.Order(), progress);
    }

    [Fact]
    public async Task TwoSitesWithTheSameAddressAreTwoDevicesAndAnExpectedMacAddressKeepsOnlyItsOwn()
    {
        var (sites, a, b) = TwoSites();
        var probe = new RoutedProbe()
            .Add(DeviceRoute.ViaSite(a.Id), "192.168.0.20", "ACCC8E0000A1")
            .Add(DeviceRoute.ViaSite(b.Id), "192.168.0.20", "ACCC8E0000B1");
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe, sites: sites);
        RouteProbe[] Routes() =>
        [
            new(DeviceRoute.Local, ct => probe.ProbeAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.Local, TimeSpan.FromSeconds(1), ct)),
            new(DeviceRoute.ViaSite(a.Id), ct => probe.ProbeAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.ViaSite(a.Id), TimeSpan.FromSeconds(1), ct)),
            new(DeviceRoute.ViaSite(b.Id), ct => probe.ProbeAsync(IPAddress.Parse("192.168.0.20"), DeviceRoute.ViaSite(b.Id), TimeSpan.FromSeconds(1), ct)),
        ];

        var both = service.StartAddressProbe("192.168.0.20", Routes());
        await CollectAsync(service, both.Id);
        var found = service.GetDevices(both.Id).OrderBy(d => d.Serial).ToList();
        Assert.Equal(["ACCC8E0000A1", "ACCC8E0000B1"], found.Select(d => d.Serial));
        Assert.Equal([DeviceRoute.ViaSite(a.Id), DeviceRoute.ViaSite(b.Id)], found.Select(d => d.Route));
        Assert.All(found, d => Assert.Equal("192.168.0.20", d.EnteredAddress));

        var expected = service.StartAddressProbe("192.168.0.20", Routes(), "ACCC8E0000B1");
        await CollectAsync(service, expected.Id);
        Assert.Equal(DeviceRoute.ViaSite(b.Id), Assert.Single(service.GetDevices(expected.Id)).Route);

        // The expected device answers nowhere: the others are shown (they get "Another device answers" on the page).
        var missing = service.StartAddressProbe("192.168.0.20", Routes(), "ACCC8E0000C1");
        await CollectAsync(service, missing.Id);
        Assert.Equal(2, service.GetDevices(missing.Id).Count);
    }

    [Fact]
    public async Task ASiteProblemIsTheFinalEventsProblemWhenNothingAnswered()
    {
        var (sites, a, _) = TwoSites();
        var probe = new RoutedProbe();
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe, sites: sites);
        var notAllowed = SiteException.For(a.Id, a.Name, SiteProblem.PortNotAllowed, 8443);

        var session = service.StartAddressProbe("https://192.168.0.30:8443",
        [
            new RouteProbe(DeviceRoute.Local, _ => Task.FromResult<DeviceProbeResult?>(null)),
            new RouteProbe(DeviceRoute.ViaSite(a.Id), _ => throw notAllowed),
        ]);
        var events = await CollectAsync(service, session.Id);

        Assert.Equal("Port 8443 is not allowed at Building A. Allow it in the OADM Site Node app.", events[^1].Problem);
        Assert.Empty(service.GetDevices(session.Id));
    }

    [Fact]
    public async Task AnOfflineSiteIsLeftOutAndAFailingSiteNeverEndsTheScan()
    {
        var (sites, a, b) = TwoSites();
        sites.SetOnline(b.Id, false);
        a.Announced.Add(new SiteBrowseResult("ACCC8E0000A1", "Cam A - ACCC8E0000A1", [IPAddress.Parse("192.168.0.20")], 80, "axis-accc8e0000a1.local"));
        var probe = new RoutedProbe().Add(DeviceRoute.ViaSite(a.Id), "192.168.0.20", "ACCC8E0000A1");
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe, sites: sites);

        Assert.Equal([a.Id], sites.OnlineSites());
        var session = service.StartZeroConf(duration: TimeSpan.FromSeconds(30));
        await WaitAsync(service, session.Id, devices => devices.Count == 1 && devices[0].Model is not null);
        service.StopScan(session.Id);
        var events = await CollectAsync(service, session.Id);
        Assert.True(events[^1].Finished);
    }

    private static (FakeSiteTransport Sites, FakeSiteTransport.FakeSite A, FakeSiteTransport.FakeSite B) TwoSites()
    {
        var sites = new FakeSiteTransport();
        return (sites, sites.AddSite("Building A"), sites.AddSite("Building B"));
    }

    private static DiscoveryService.DeviceObservation Observation(string address, DeviceRoute route, string scheme) =>
        new("ACCC8E0000A1", IPAddress.Parse(address), null, "P3265-V", null, scheme, DiscoveredDeviceStatus.CredentialsRequired, DiscoverySources.Mdns) { Route = route };

    private static async Task<List<DiscoveryEvent>> CollectAsync(DiscoveryService service, string sessionId)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var list = new List<DiscoveryEvent>();
        await foreach (var e in service.WatchAsync(sessionId, cts.Token))
        {
            list.Add(e);
        }

        return list;
    }

    private static async Task WaitAsync(DiscoveryService service, string sessionId, Func<IReadOnlyList<DiscoveredDevice>, bool> condition) =>
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => condition(service.GetDevices(sessionId)), "the devices of the session");

    /// <summary>A probe that answers per (route, address): two sites may both have 192.168.0.20.</summary>
    private sealed class RoutedProbe : IDeviceProbe
    {
        private readonly ConcurrentDictionary<(DeviceRoute, string), DeviceProbeResult> _devices = new();

        public ConcurrentBag<(DeviceRoute Route, string Address)> Probed { get; } = [];

        public RoutedProbe Add(DeviceRoute route, string address, string serial, string model = "P3265-V")
        {
            _devices[(route, address)] = new DeviceProbeResult(IPAddress.Parse(address), serial, "https", model, null, "12.11.77",
                DiscoveredDeviceStatus.CredentialsRequired, false);
            return this;
        }

        public Task<DeviceProbeResult?> ProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken) =>
            ProbeAsync(address, DeviceRoute.Local, timeout, cancellationToken);

        public async Task<DeviceProbeResult?> ProbeAsync(IPAddress address, DeviceRoute route, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Probed.Add((route, address.ToString()));
            await Task.Yield();
            return _devices.TryGetValue((route, address.ToString()), out var result) ? result : null;
        }
    }
}
