using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// Polling and Site offline: devices of an offline site are neither polled nor refreshed; a site error during a poll
/// never changes a status and never counts as a failed poll (docs/sites.md, Site errors are not device errors).
/// </summary>
public sealed class SiteOfflinePollingTests : IAsyncLifetime, IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeSiteTransport _sites = new();
    private readonly Cameras _cameras = new();
    private FakeSiteTransport.FakeSite _site = null!;
    private TestDatabase _db = null!;
    private VapixClientFactory _factory = null!;
    private DevicePollingService _polling = null!;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _site = _sites.AddSite("Building B");
        _db = await TestDatabase.CreateAsync(configure: s => s.AddSingleton<ISiteStates>(_sites));
        _factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), _cameras);
        _polling = new DevicePollingService(Devices, _factory, _db.Get<ServerSettingsStore>(), _time, sites: _sites);
    }

    public async Task DisposeAsync()
    {
        _polling.Dispose();
        _factory.Dispose();
        await _db.DisposeAsync();
    }

    public void Dispose()
    {
        _polling?.Dispose();
        _factory?.Dispose();
        _cameras.Dispose();
    }

    [Fact]
    public async Task DevicesOfAnOfflineSiteAreNotContacted()
    {
        var local = await AddAsync("10.0.0.1", null);
        var atSite = await AddAsync("192.168.0.20", _site.Id);
        _sites.SetOnline(_site.Id, false);

        await _polling.PollAllAsync(CancellationToken.None);
        await _polling.RefreshAsync(atSite.Id, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(20));
        var due = await _polling.QueueDueFullRefreshesAsync(CancellationToken.None);

        Assert.Equal(0, _cameras.Requests("192.168.0.20"));
        Assert.Equal(1, _cameras.Requests("10.0.0.1"));
        Assert.DoesNotContain(atSite.Id, due);
        Assert.Contains(local.Id, due);
        _sites.SetOnline(_site.Id, true);
        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(atSite.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ASiteErrorDuringAPollKeepsTheStatusAndIsNoFailedPoll()
    {
        var devices = new List<Device>();
        for (var i = 1; i <= 20; i++)
        {
            var device = await AddAsync("192.168.0." + i, _site.Id);
            devices.Add(device);
            _cameras.SiteError(device.Address, SiteException.For(_site.Id, "Building B", SiteProblem.Busy));
        }

        var local = await AddAsync("10.0.0.1", null);
        _cameras.FailOnce(local.Address);

        await _polling.PollAllAsync(CancellationToken.None);

        foreach (var device in devices)
        {
            Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(device.Id, CancellationToken.None))!.Status);
            Assert.Equal(1, _cameras.Requests(device.Address)); // no second try: not a failed poll
        }

        // The site errors are no outage: the local device that missed once is asked again and stays Ok.
        Assert.Equal(2, _cameras.Requests(local.Address));
        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(local.Id, CancellationToken.None))!.Status);

        await _polling.RefreshAsync(devices[0].Id, CancellationToken.None);
        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(devices[0].Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task NoAnswerWhileTheSiteNodeDoesNotAnswerEitherIsASiteProblem()
    {
        // The link to the node dropped: the site does not count as offline yet (30 s), every poll through it fails.
        var devices = new List<Device>();
        for (var i = 1; i <= 20; i++)
        {
            var device = await AddAsync("192.168.0." + i, _site.Id);
            devices.Add(device);
            _cameras.FailAlways(device.Address);
        }

        _site.LinkAnswers = false;
        await _polling.PollAllAsync(CancellationToken.None);
        await _polling.RefreshAsync(devices[0].Id, CancellationToken.None);

        foreach (var device in devices)
        {
            // Not Unreachable, no outage of the round, no second try: no "Device unreachable" for the whole site.
            Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(device.Id, CancellationToken.None))!.Status);
        }

        Assert.Equal(1, _cameras.Requests(devices[1].Address));

        // The node answers: the device itself does not, so it is Unreachable as before.
        _site.LinkAnswers = true;
        await _polling.PollAsync(devices[1].Id, CancellationToken.None);
        Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(devices[1].Id, CancellationToken.None))!.Status);
    }

    private async Task<Device> AddAsync(string address, Guid? siteId)
    {
        var device = await Devices.AddAsync(new Device
        {
            Serial = "ACCC8E" + Convert.ToHexString(IPAddress.Parse(address).GetAddressBytes()[1..]),
            Address = address,
            SiteId = siteId,
            Status = DeviceStatus.Ok,
            Scheme = DeviceScheme.Http,
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
        }, CancellationToken.None);
        await _db.Get<CredentialStore>().SetAsync(device.Id, "root", "pass", CancellationToken.None);
        return device;
    }

    /// <summary>Cameras by host: answer, fail once, or a site error (the connect failed at the site).</summary>
    private sealed class Cameras : IVapixConnector, IDisposable
    {
        private readonly FakeHttpMessageHandler _handler;
        private readonly ConcurrentDictionary<string, int> _requests = new();
        private readonly ConcurrentDictionary<string, int> _failures = new();
        private readonly ConcurrentDictionary<string, SiteException> _siteErrors = new();
        private readonly string _info = Fixtures.Read("basicdeviceinfo-getAllProperties.json");

        public Cameras()
        {
            _handler = new FakeHttpMessageHandler((request, _) =>
            {
                var host = request.RequestUri!.Host;
                if (!request.RequestUri.AbsolutePath.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
                {
                    return Fixtures.Text("not found", HttpStatusCode.NotFound);
                }

                _requests.AddOrUpdate(host, 1, (_, n) => n + 1);
                if (_siteErrors.TryGetValue(host, out var site))
                {
                    throw new HttpRequestException(HttpRequestError.ConnectionError, site.Message, site);
                }

                if (_failures.TryGetValue(host, out var left) && left != 0)
                {
                    _failures[host] = left - 1;
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
                }

                return Fixtures.Json(_info);
            });
        }

        public int Requests(string host) => _requests.GetValueOrDefault(host);

        public void FailOnce(string host) => _failures[host] = 1;

        public void FailAlways(string host) => _failures[host] = int.MaxValue;

        public void SiteError(string host, SiteException error) => _siteErrors[host] = error;

        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), _handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);

        public void Dispose() => _handler.Dispose();
    }
}
