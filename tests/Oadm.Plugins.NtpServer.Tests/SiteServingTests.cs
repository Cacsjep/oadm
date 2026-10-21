using System.Net;

using Oadm.Core.Plugins;
using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Status;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>
/// "Serve at sites" (docs/sites.md, NTP server): one UDP relay of port 123 per site, answered from the server's time
/// source, the rate limits per (site, client), and "Use OADM as NTP server" for a device at a site. The relays are the
/// in-memory ones of Oadm.Sdk.Testing: no packet leaves the test, the real port 123 is never bound.
/// </summary>
public sealed class SiteServingTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly FakeSites _sites = new();
    private readonly FakePluginNetwork _network;
    private FakeSite _buildingB = null!;
    private NtpServerService _service = null!;

    public SiteServingTests()
    {
        _network = new FakePluginNetwork(_sites);
    }

    public async Task InitializeAsync()
    {
        _buildingB = _sites.Add("Building B");
        _buildingB.AllowedPorts.Add((SiteProtocol.Udp, 123));
        _service = await StartAsync(Options.Test());
    }

    public async Task DisposeAsync() => await _service.DisposeAsync();

    private async Task<NtpServerService> StartAsync(NtpServerOptions options)
    {
        var service = new NtpServerService(options, new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), network: _network, sites: _sites);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    private Task<SaveReply> SaveAsync(bool serveAtSites, bool enabled = true) =>
        _service.SaveAsync(new SaveRequest(enabled, "lo-id", null, serveAtSites), CancellationToken.None);

    private InMemoryUdpRelay OpenRelayOf(FakeSite site) =>
        Assert.Single(_sites.Relays.OfType<InMemoryUdpRelay>(), r => r.SiteId == site.Id && !r.IsDisposed);

    private static byte[] Request()
    {
        var request = new byte[NtpPacket.Length];
        new NtpPacket { Version = 4, Mode = NtpMode.Client, Poll = 6, TransmitTimestamp = NtpTimestamp.FromDateTime(DateTime.UtcNow) }.Write(request);
        return request;
    }

    private static async Task<NtpPacket> AnswerAsync(InMemoryUdpRelay relay, IPEndPoint expectedTo)
    {
        using var cts = new CancellationTokenSource(Wait);
        var sent = await relay.ReadSentAsync(cts.Token);
        Assert.Equal(expectedTo, sent.Remote);
        Assert.True(NtpPacket.TryRead(sent.Data.Span, out var answer));
        return answer;
    }

    [Fact]
    public async Task Off_by_default_no_relay_and_the_page_offers_it_while_sites_exist()
    {
        await SaveAsync(serveAtSites: false);

        Assert.Empty(_sites.Relays);
        var state = _service.GetState(full: false);
        Assert.True(state.HasSites);
        Assert.Empty(state.Sites);
    }

    [Fact]
    public async Task Each_site_relays_UDP_123_and_its_requests_are_answered_with_the_site_in_the_log()
    {
        await SaveAsync(serveAtSites: true);

        var relay = OpenRelayOf(_buildingB);
        Assert.Equal(123, relay.Port);
        Assert.Equal(NtpStatusTexts.RelayReason, relay.Reason);
        var camera = new IPEndPoint(IPAddress.Parse("192.168.0.20"), 50123);
        relay.Deliver(Request(), camera);

        var answer = await AnswerAsync(relay, camera);
        Assert.Equal(NtpMode.Server, answer.Mode);
        Assert.Equal(10, answer.Stratum); // the server's own clock
        await Eventually.TrueAsync(() => _service.Log.Snapshot().Any(e => e.SiteId == _buildingB.Id), "the request is in the log");
        var entry = _service.Log.Snapshot().First(e => e.SiteId == _buildingB.Id);
        Assert.Equal("192.168.0.20", entry.Client);
        Assert.Equal("Building B", entry.Site);
        Assert.Equal([new SiteServing(_buildingB.Id, "NTP at Building B: running", true)], _service.GetState(full: false).Sites);
    }

    [Fact]
    public async Task A_new_site_gets_a_relay_and_a_removed_one_or_turning_it_off_closes_them()
    {
        await SaveAsync(serveAtSites: true);
        var first = OpenRelayOf(_buildingB);

        var buildingC = _sites.Add("Building C"); // waits for approval in its node app
        await Eventually.TrueAsync(() => _sites.Relays.Any(r => r.SiteId == buildingC.Id), "a relay for the new site");
        Assert.Equal("NTP at Building C: waiting for approval in the OADM Site Node app", OpenRelayOf(buildingC).StateText);

        _sites.Remove(buildingC.Id);
        await Eventually.TrueAsync(() => _sites.Relays.Where(r => r.SiteId == buildingC.Id).All(r => r.IsDisposed), "the removed site's relay closes");

        await SaveAsync(serveAtSites: false);
        Assert.True(first.IsDisposed);
        Assert.True(first.IsReleased); // turned off: the node gives the port back
        Assert.Empty(_service.GetState(full: false).Sites);
    }

    [Fact]
    public async Task Only_the_chosen_sites_are_served_and_a_site_no_longer_chosen_gets_its_port_back()
    {
        var buildingC = _sites.Add("Building C");
        buildingC.AllowedPorts.Add((SiteProtocol.Udp, 123));
        await _service.SaveAsync(new SaveRequest(true, "lo-id", null, ServeAtSites: true, ServeSites: [buildingC.Id]), CancellationToken.None);

        Assert.DoesNotContain(_sites.Relays, r => r.SiteId == _buildingB.Id);
        var relayC = OpenRelayOf(buildingC);
        Assert.Equal([buildingC.Id], _service.GetState(full: false).Config.ServeSites);
        Assert.Equal(["Building B", "Building C"], _service.GetState(full: false).SiteChoices.Select(s => s.Name));
        // A device at a site that is not chosen gets the reason.
        Assert.False(_service.GetState(full: false).Config.Serves(_buildingB.Id));

        await _service.SaveAsync(new SaveRequest(true, "lo-id", null, ServeAtSites: true, ServeSites: [_buildingB.Id]), CancellationToken.None);
        Assert.True(relayC.IsReleased);
        OpenRelayOf(_buildingB);

        // Empty = every site, also one bound later.
        await _service.SaveAsync(new SaveRequest(true, "lo-id", null, ServeAtSites: true, ServeSites: []), CancellationToken.None);
        OpenRelayOf(buildingC);
        var buildingD = _sites.Add("Building D");
        await Eventually.TrueAsync(() => _sites.Relays.Any(r => r.SiteId == buildingD.Id), "a relay for the new site");
    }

    [Fact]
    public async Task Disabling_the_server_or_stopping_the_plugin_closes_the_relays()
    {
        await SaveAsync(serveAtSites: true);
        var relay = OpenRelayOf(_buildingB);

        await SaveAsync(serveAtSites: true, enabled: false);
        Assert.True(relay.IsDisposed);

        await SaveAsync(serveAtSites: true);
        var again = OpenRelayOf(_buildingB);
        await _service.DisposeAsync();
        Assert.True(again.IsDisposed);
        Assert.False(again.IsReleased); // a stop keeps the port allowed at the site for the next start
        _service = await StartAsync(Options.Test());
    }

    [Fact]
    public async Task Rate_limits_are_kept_per_site_and_client()
    {
        await _service.DisposeAsync();
        _service = await StartAsync(Options.Test() with { RateLimit = new RateLimitOptions { Burst = 1, RefillSeconds = 3600 } });
        var buildingC = _sites.Add("Building C");
        buildingC.AllowedPorts.Add((SiteProtocol.Udp, 123));
        await SaveAsync(serveAtSites: true);
        var b = OpenRelayOf(_buildingB);
        var c = OpenRelayOf(buildingC);
        var camera = new IPEndPoint(IPAddress.Parse("192.168.0.20"), 123); // the same address at both sites

        b.Deliver(Request(), camera);
        Assert.Equal(10, (await AnswerAsync(b, camera)).Stratum);
        b.Deliver(Request(), camera);
        Assert.Equal(0, (await AnswerAsync(b, camera)).Stratum); // over the limit: Kiss-o'-Death "RATE"

        c.Deliver(Request(), camera);
        Assert.Equal(10, (await AnswerAsync(c, camera)).Stratum); // another site: its own bucket
    }

    [Fact]
    public async Task Use_OADM_as_NTP_server_gives_a_device_at_a_site_the_address_of_its_site_node()
    {
        var device = new FakeDevice { Address = "192.168.0.20", SiteId = _buildingB.Id, SiteName = "Building B" };
        var address = IPAddress.Parse("192.168.0.20");
        _buildingB.Network.LocalAddress = IPAddress.Parse("192.168.0.5");

        await SaveAsync(serveAtSites: false);
        Assert.Equal((null, NtpStatusTexts.NotServingAtSites), await _service.ServerAddressForAsync(device, address, _buildingB.Network, CancellationToken.None));

        await SaveAsync(serveAtSites: true);
        Assert.Equal((IPAddress.Parse("192.168.0.5"), null), await _service.ServerAddressForAsync(device, address, _buildingB.Network, CancellationToken.None));
        Assert.Contains("local 192.168.0.20", _buildingB.Network.Calls);

        _buildingB.AllowedPorts.Clear();
        _sites.Update();
        var (none, error) = await _service.ServerAddressForAsync(device, address, _buildingB.Network, CancellationToken.None);
        Assert.Null(none);
        Assert.Equal("NTP at Building B: waiting for approval in the OADM Site Node app. Nothing was changed.", error);
    }
}
