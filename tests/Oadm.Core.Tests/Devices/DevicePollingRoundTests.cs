using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Devices;

/// <summary>
/// The status poll round for many devices (docs/30kspec.md): the 3 s request timeout, a second try before Unreachable, no
/// second try in an outage, a full refresh on new firmware, and the refresh schedule for devices that are Ok only.
/// </summary>
public sealed class DevicePollingRoundTests : IAsyncLifetime, IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly Cameras _cameras = new();
    private TestDatabase _db = null!;
    private VapixClientFactory _factory = null!;
    private DevicePollingService _polling = null!;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), _cameras);
        _polling = new DevicePollingService(Devices, _factory, _db.Get<ServerSettingsStore>(), _time);
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
    public void TheDefaultsFitThirtyThousandDevices()
    {
        Assert.Equal(200, _polling.MaxParallelism);
        Assert.Equal(64, _polling.RefreshParallelism);
        Assert.Equal(TimeSpan.FromSeconds(3), _polling.StatusPollTimeout);
        Assert.Equal(0.10, _polling.OutageShare);
    }

    [Fact]
    public async Task TheStatusPollAsksWithTheShortTimeoutAndTheFullRefreshWithTheDefault()
    {
        var device = await AddAsync("10.0.0.1", DeviceStatus.Ok);

        await _polling.PollAllAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(_cameras.StatusTimeouts));

        await _polling.RefreshAsync(device.Id, CancellationToken.None);
        Assert.Equal(2, _cameras.StatusTimeouts.Count);
        Assert.Contains(null, _cameras.StatusTimeouts); // the full refresh keeps the client's timeout
    }

    [Fact]
    public async Task ADeviceThatMissesOnePollIsAskedAgainAndStaysOk()
    {
        var devices = await AddManyAsync(20);
        _cameras.FailOnce(devices[0].Address);

        await _polling.PollAllAsync(CancellationToken.None);

        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(devices[0].Id, CancellationToken.None))!.Status);
        Assert.Equal(2, _cameras.Requests(devices[0].Address));
        Assert.Equal(1, _cameras.Requests(devices[1].Address));
    }

    [Fact]
    public async Task ADeviceThatMissesTwiceTurnsUnreachable()
    {
        var devices = await AddManyAsync(20);
        _cameras.Fail(devices[0].Address);

        await _polling.PollAllAsync(CancellationToken.None);

        Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(devices[0].Id, CancellationToken.None))!.Status);
        Assert.Equal(2, _cameras.Requests(devices[0].Address));

        // Unreachable already: no second try in the next round.
        await _polling.PollAllAsync(CancellationToken.None);
        Assert.Equal(3, _cameras.Requests(devices[0].Address));
    }

    [Fact]
    public async Task ASmallSiteAlwaysGetsTheSecondTry()
    {
        var devices = await AddManyAsync(3);
        _cameras.FailOnce(devices[0].Address); // one of three: a third failed, but not an outage

        await _polling.PollAllAsync(CancellationToken.None);

        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(devices[0].Id, CancellationToken.None))!.Status);
        Assert.Equal(2, _cameras.Requests(devices[0].Address));
    }

    [Fact]
    public async Task InAnOutageEveryDeviceTurnsUnreachableWithoutASecondTry()
    {
        var devices = await AddManyAsync(100);
        foreach (var device in devices.Take(12)) // 12 % > 10 % and at least 10 devices
        {
            _cameras.Fail(device.Address);
        }

        await _polling.PollAllAsync(CancellationToken.None);

        foreach (var device in devices.Take(12))
        {
            Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(device.Id, CancellationToken.None))!.Status);
            Assert.Equal(1, _cameras.Requests(device.Address));
        }

        Assert.Equal(DeviceStatus.Ok, (await Devices.GetAsync(devices[12].Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ANewFirmwareQueuesAFullRefreshAtOnce()
    {
        var device = await AddAsync("10.0.0.1", DeviceStatus.Ok, firmware: "12.11.77");
        var same = await AddAsync("10.0.0.2", DeviceStatus.Ok, firmware: "12.11.77");

        _cameras.Firmware("10.0.0.1", "12.12.10");
        await _polling.PollAllAsync(CancellationToken.None);

        Assert.True(_polling.IsRefreshQueued(device.Id));
        Assert.False(_polling.IsRefreshQueued(same.Id));
        Assert.Equal("12.12.10", (await Devices.GetAsync(device.Id, CancellationToken.None))!.FirmwareVersion);
    }

    [Fact]
    public async Task TheScheduleRefreshesOnlyDevicesThatAreOk()
    {
        var ok = await AddAsync("10.0.0.1", DeviceStatus.Ok);
        await AddAsync("10.0.0.2", DeviceStatus.Unreachable);
        await AddAsync("10.0.0.3", DeviceStatus.CredentialsRequired);

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal([ok.Id], await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));
    }

    private async Task<List<Device>> AddManyAsync(int count)
    {
        var list = new List<Device>();
        for (var i = 1; i <= count; i++)
        {
            list.Add(await AddAsync($"10.0.1.{i}", DeviceStatus.Ok));
        }

        return list;
    }

    private async Task<Device> AddAsync(string address, DeviceStatus status, string firmware = "12.11.77")
    {
        var device = await Devices.AddAsync(new Device
        {
            Serial = "ACCC8E" + Convert.ToHexString(IPAddress.Parse(address).GetAddressBytes()[1..]),
            Address = address,
            Status = status,
            Scheme = DeviceScheme.Http,
            Model = "P3265-V",
            FirmwareVersion = firmware,
        }, CancellationToken.None);
        await _db.Get<CredentialStore>().SetAsync(device.Id, "root", "pass", CancellationToken.None);
        return device;
    }

    /// <summary>Cameras by host: answer, fail once, fail always, or report another firmware.</summary>
    private sealed class Cameras : IVapixConnector, IDisposable
    {
        private readonly FakeHttpMessageHandler _handler;
        private readonly ConcurrentDictionary<string, int> _requests = new();
        private readonly ConcurrentDictionary<string, int> _failures = new();
        private readonly ConcurrentDictionary<string, string> _firmware = new();
        private readonly string _info = Fixtures.Read("basicdeviceinfo-getAllProperties.json");

        public Cameras()
        {
            _handler = new FakeHttpMessageHandler((request, _) =>
            {
                var host = request.RequestUri!.Host;
                if (request.RequestUri.AbsolutePath.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
                {
                    _requests.AddOrUpdate(host, 1, (_, n) => n + 1);
                    StatusTimeouts.Add(request.Options.TryGetValue(VapixRequestOptions.Timeout, out var timeout) ? timeout : null);
                    if (_failures.TryGetValue(host, out var left) && left != 0)
                    {
                        _failures[host] = left - 1;
                        throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
                    }

                    var info = _firmware.TryGetValue(host, out var version) ? _info.Replace("12.11.77", version, StringComparison.Ordinal) : _info;
                    return Fixtures.Json(info);
                }

                return Fixtures.Text("not found", HttpStatusCode.NotFound);
            });
        }

        public ConcurrentBag<TimeSpan?> StatusTimeouts { get; } = [];

        public int Requests(string host) => _requests.GetValueOrDefault(host);

        public void FailOnce(string host) => _failures[host] = 1;

        public void Fail(string host) => _failures[host] = -1;

        public void Firmware(string host, string version) => _firmware[host] = version;

        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), _handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);

        public void Dispose() => _handler.Dispose();
    }
}
