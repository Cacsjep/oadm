using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

/// <summary>
/// The polling's clock health (docs/spec/main-window.md "Clock"): the offset from the Date header of the status poll with
/// a fake clock, the NTP state from getNTPInfo in the full refresh, and that neither ever fails a poll.
/// </summary>
public sealed class DevicePollingClockTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 9, 18, 14, 300, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private TestDatabase _db = null!;
    private Camera _camera = null!;
    private VapixClientFactory _factory = null!;
    private DevicePollingService _polling = null!;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _camera = new Camera(_time);
        _factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), _camera);
        _polling = new DevicePollingService(Devices, _factory, _db.Get<ServerSettingsStore>(), _time);
    }

    public async Task DisposeAsync()
    {
        _polling.Dispose();
        _factory.Dispose();
        _camera.Dispose();
        await _db.DisposeAsync();
    }

    public void Dispose()
    {
        _polling?.Dispose();
        _factory?.Dispose();
        _camera?.Dispose();
    }

    [Fact]
    public async Task TheStatusPollStoresTheOffsetOfTheDateHeader()
    {
        var device = await AddAsync();
        _camera.DeviceAhead = TimeSpan.FromSeconds(50);
        _camera.RoundTrip = TimeSpan.FromMilliseconds(200);

        var polled = await _polling.PollAsync(device.Id, CancellationToken.None);

        // Server middle 14.400, device clock 15:04.400 -> header "09:19:04", + 0.5 s = 50.1 s.
        Assert.Equal(50_100, polled!.ClockOffsetMs);
        Assert.Equal(50_100, (await Devices.GetAsync(device.Id, CancellationToken.None))!.ClockOffsetMs);
    }

    [Fact]
    public async Task ADeviceBehindIsNegative()
    {
        var device = await AddAsync();
        _camera.DeviceAhead = TimeSpan.FromMinutes(-2);

        var polled = await _polling.PollAsync(device.Id, CancellationToken.None);

        Assert.Equal(-119_800, polled!.ClockOffsetMs); // header 09:16:14, + 0.5 s - 14.300
    }

    [Fact]
    public async Task NoHeaderOrAnUnreadableOneKeepsTheLastValue()
    {
        var device = await AddAsync();
        _camera.DeviceAhead = TimeSpan.FromSeconds(10);
        await _polling.PollAsync(device.Id, CancellationToken.None);
        var first = (await Devices.GetAsync(device.Id, CancellationToken.None))!.ClockOffsetMs;
        Assert.NotNull(first);

        _camera.Header = HeaderKind.None;
        Assert.Equal(first, (await _polling.PollAsync(device.Id, CancellationToken.None))!.ClockOffsetMs);

        _camera.Header = HeaderKind.Garbled;
        var polled = await _polling.PollAsync(device.Id, CancellationToken.None);
        Assert.Equal(DeviceStatus.Ok, polled!.Status);
        Assert.Equal(first, polled.ClockOffsetMs);
    }

    [Fact]
    public async Task OnlyOkAnswersCount()
    {
        var device = await AddAsync();
        _camera.DeviceAhead = TimeSpan.FromSeconds(10);
        await _polling.PollAsync(device.Id, CancellationToken.None);

        _camera.DeviceAhead = TimeSpan.FromHours(1);
        _camera.Refuse = true; // 401 with a Date header
        var polled = await _polling.PollAsync(device.Id, CancellationToken.None);

        Assert.Equal(DeviceStatus.CredentialsRequired, polled!.Status);
        Assert.Equal(10_200, polled.ClockOffsetMs); // kept
    }

    [Fact]
    public async Task AChangeBelowASecondIsNotWrittenOrPublished()
    {
        var device = await AddAsync();
        await _polling.PollAsync(device.Id, CancellationToken.None);
        var published = new List<DeviceChange>();
        Devices.Changes.Changed += (_, change) => published.Add(change);

        _time.Advance(TimeSpan.FromMilliseconds(600)); // the next poll reads 0.6 s less: whole-second header jitter
        await _polling.PollAsync(device.Id, CancellationToken.None);

        Assert.Empty(published);
        Assert.Equal(200, (await Devices.GetAsync(device.Id, CancellationToken.None))!.ClockOffsetMs);
    }

    [Fact]
    public async Task TheFullRefreshReadsTheNtpState()
    {
        var device = await AddAsync();

        var refreshed = await _polling.RefreshAsync(device.Id, CancellationToken.None);
        Assert.Equal(NtpState.Synced, refreshed!.NtpState);
        Assert.Contains(_camera.NtpBodies, b => b.Contains("\"apiVersion\":\"1.5\"", StringComparison.Ordinal)); // the device's version

        _camera.Ntp = Fixtures.Read("ntp-getNTPInfo-P3265-V-12.11.json").Replace("\"synced\": true", "\"synced\": false", StringComparison.Ordinal);
        Assert.Equal(NtpState.NotSynced, (await _polling.RefreshAsync(device.Id, CancellationToken.None))!.NtpState);

        _camera.Ntp = Fixtures.Read("ntp-getNTPInfo-P3265-V-12.11.json").Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal);
        Assert.Equal(NtpState.Off, (await _polling.RefreshAsync(device.Id, CancellationToken.None))!.NtpState);
    }

    [Fact]
    public async Task AFailedNtpReadIsUnknownAndNeverFailsTheRefresh()
    {
        var device = await AddAsync();
        await _polling.RefreshAsync(device.Id, CancellationToken.None);

        _camera.Ntp = null; // HTTP 500
        var refreshed = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.Equal(DeviceStatus.Ok, refreshed!.Status);
        Assert.Equal(NtpState.Unknown, refreshed.NtpState);
    }

    [Fact]
    public async Task WithoutTheNtpApiTheStateIsUnknownAndNotAsked()
    {
        var device = await AddAsync();
        _camera.ApiList = Fixtures.Read("apidiscovery-getApiList.json").Replace("\"id\": \"ntp\"", "\"id\": \"ntp-not\"", StringComparison.Ordinal);

        var refreshed = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.Equal(NtpState.Unknown, refreshed!.NtpState);
        Assert.Empty(_camera.NtpBodies);
    }

    [Fact]
    public async Task TheStatusPollNeverAsksNtp()
    {
        var device = await AddAsync();
        await _polling.PollAsync(device.Id, CancellationToken.None);
        Assert.Empty(_camera.NtpBodies);
    }

    private async Task<Device> AddAsync()
    {
        var device = await Devices.AddAsync(new Device
        {
            Serial = "ACCC8E" + Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
            Address = "10.0.0.48",
            Status = DeviceStatus.Ok,
            Scheme = DeviceScheme.Http,
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
        }, CancellationToken.None);
        await _db.Get<CredentialStore>().SetAsync(device.Id, "root", "pass", CancellationToken.None);
        return device;
    }

    private enum HeaderKind
    {
        Valid,
        None,
        Garbled,
    }

    /// <summary>One camera whose clock runs <see cref="DeviceAhead"/> of the fake server clock; a request takes <see cref="RoundTrip"/>.</summary>
    private sealed class Camera : IVapixConnector, IDisposable
    {
        private readonly FakeHttpMessageHandler _handler;
        private readonly FakeTimeProvider _time;

        public Camera(FakeTimeProvider time)
        {
            _time = time;
            _handler = new FakeHttpMessageHandler((request, body) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
                {
                    _time.Advance(RoundTrip / 2);
                    var response = Refuse
                        ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) }
                        : Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json"));
                    AddDate(response);
                    _time.Advance(RoundTrip / 2);
                    return response;
                }

                if (path.EndsWith("apidiscovery.cgi", StringComparison.Ordinal))
                {
                    return Fixtures.Json(ApiList);
                }

                if (path.EndsWith("ntp.cgi", StringComparison.Ordinal))
                {
                    lock (NtpBodies)
                    {
                        NtpBodies.Add(body ?? string.Empty);
                    }

                    return Ntp is null ? Fixtures.Text("error", HttpStatusCode.InternalServerError) : Fixtures.Json(Ntp);
                }

                return Fixtures.Text("not found", HttpStatusCode.NotFound);
            });
        }

        public TimeSpan DeviceAhead { get; set; }

        public TimeSpan RoundTrip { get; set; }

        public HeaderKind Header { get; set; }

        public bool Refuse { get; set; }

        public string ApiList { get; set; } = Fixtures.Read("apidiscovery-getApiList.json");

        public string? Ntp { get; set; } = Fixtures.Read("ntp-getNTPInfo-P3265-V-12.11.json");

        public List<string> NtpBodies { get; } = [];

        private void AddDate(HttpResponseMessage response)
        {
            switch (Header)
            {
                case HeaderKind.Valid:
                    // As on the wire (RFC 1123): whole seconds, the fraction cut off.
                    response.Headers.TryAddWithoutValidation("Date", (_time.GetUtcNow() + DeviceAhead).ToString("r", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case HeaderKind.Garbled:
                    response.Headers.TryAddWithoutValidation("Date", "yesterday at noon");
                    break;
            }
        }

        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), _handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);

        public void Dispose() => _handler.Dispose();
    }
}
