using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Devices;

/// <summary>Scheduled full refresh, back-online trigger and certificate capture, driven by a fake clock.</summary>
public sealed class DevicePollingScheduleTests : IAsyncLifetime, IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private TimerWatch _timers = null!;
    private readonly FakeCamera _camera = new();
    private TestDatabase _db = null!;
    private VapixClientFactory _factory = null!;
    private DevicePollingService _polling = null!;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _camera.WithClock(_time);
        _factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), _camera);
        _timers = new TimerWatch(_time);
        _polling = new DevicePollingService(Devices, _factory, _db.Get<ServerSettingsStore>(), _timers);
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
        _camera.Dispose();
    }

    [Fact]
    public async Task AFullRefreshRoundSkipsDevicesRefreshedWithinThePause()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        Assert.Equal(TimeSpan.FromMinutes(10), _polling.FullRefreshPause);

        Assert.Equal(1, await _polling.FullRefreshRoundAsync(CancellationToken.None));
        Assert.Equal(_time.GetUtcNow(), _polling.LastFullRefresh(device.Id));
        Assert.Equal(1, _polling.LastFullRound!.Devices);

        // Refreshed a moment ago (here by the round; a task or coming back online does the same): the next round skips it.
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await _polling.FullRefreshRoundAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _polling.FullRefreshRoundAsync(CancellationToken.None));
        Assert.Equal(2, _camera.FullRefreshTimes.Count);
    }

    [Fact]
    public async Task ARoundIsFollowedBySixtySecondsOfPauseBeforeTheNext()
    {
        await AddAsync(DeviceStatus.Ok);
        using var cts = new CancellationTokenSource();
        var run = _polling.RunAsync(cts.Token);

        // No setting: the first round after the pause, the next one a pause after that round ended.
        await Eventually.TrueAsync(() => _timers.Pending(_polling.RoundPause) > 0, "the loop waits its first pause");
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, _camera.StatusPolls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => _polling.LastRound is { Devices: 1 }, "the first round");
        Assert.Equal(1, _camera.StatusPolls);

        await Eventually.TrueAsync(() => _timers.Pending(_polling.RoundPause) > 0, "the pause after the round");
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, _camera.StatusPolls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => _camera.StatusPolls == 2, "the second round");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunningServiceDoesAFullRefreshRoundTenMinutesAfterTheLast()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        var start = _time.GetUtcNow();
        using var cts = new CancellationTokenSource();
        var run = _polling.RunAsync(cts.Token);

        // Step the clock one status pause at a time, each step only once both loops wait again, so no step is lost on a
        // slow machine: the first full round after 10 minutes, the next one 10 minutes after that round.
        while (_camera.FullRefreshTimes.Count < 2 && _time.GetUtcNow() - start < TimeSpan.FromMinutes(30))
        {
            await Eventually.TrueAsync(() => _timers.Pending(_polling.RoundPause) > 0 && _timers.Pending(_polling.FullRefreshPause) > 0, "both loops waiting");
            _time.Advance(_polling.RoundPause);
        }

        var times = _camera.FullRefreshTimes.ToArray().Order().ToArray();
        Assert.Equal(TimeSpan.FromMinutes(10), times[0] - start);
        Assert.Equal(TimeSpan.FromMinutes(20), times[1] - start);
        Assert.True(_camera.StatusPolls > 0, "the status rounds keep running alongside");
        Assert.NotNull(_polling.LastFullRefresh(device.Id));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task DeviceBackOnlineGetsAnImmediateFullRefresh()
    {
        var device = await AddAsync(DeviceStatus.Unreachable);
        using var cts = new CancellationTokenSource();
        var run = _polling.RunAsync(cts.Token);

        await _polling.PollAllAsync(CancellationToken.None); // Unreachable -> Ok

        await WaitUntilAsync(() => Task.FromResult(_camera.FullRefreshTimes.Count == 1));
        await WaitUntilAsync(async () => (await Devices.GetAsync(device.Id, CancellationToken.None))!.CertTrust == CertificateTrust.SelfSigned);
        var row = await Devices.GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(DeviceStatus.Ok, row!.Status);
        Assert.True(row.DhcpEnabled);
        Assert.Equal(_time.GetUtcNow(), _polling.LastFullRefresh(device.Id));

        // Ok -> Ok and Ok -> Unreachable do not trigger another full refresh: none is queued and none started (a queued one
        // is pending until the queue takes it, and the queue stamps it before it runs).
        var lastFull = _polling.LastFullRefresh(device.Id);
        await _polling.PollAllAsync(CancellationToken.None);
        _camera.Online = false;
        await _polling.PollAllAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(device.Id, CancellationToken.None))!.Status);
        Assert.False(_polling.IsRefreshQueued(device.Id));
        Assert.Equal(lastFull, _polling.LastFullRefresh(device.Id));
        Assert.Single(_camera.FullRefreshTimes);

        // ... but coming back again does.
        _camera.Online = true;
        await _polling.PollAllAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(_camera.FullRefreshTimes.Count == 2));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task FullRefreshStoresTheCertificate()
    {
        var device = await AddAsync(DeviceStatus.Ok);

        var row = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        var cert = _camera.Certificate;
        Assert.Equal(CertificateTrust.SelfSigned, row!.CertTrust);
        Assert.Equal(cert.NotAfter.ToUniversalTime(), row.CertNotAfterUtc);
        Assert.Equal(cert.Subject, row.CertSubject);
        Assert.Equal(cert.Issuer, row.CertIssuer);
        Assert.True(row.CertNameMatches);
        Assert.Equal(CertificatePinning.ComputeFingerprint(cert), row.CertFingerprintSha256);
        Assert.Equal("Dome Camera", row.ProductType); // recorded P3265-V
        Assert.Equal("Axis Artpec-8", row.Soc);
        Assert.Equal("aarch64", row.Architecture);
        Assert.Equal(DeviceCategory.Camera, row.Category);
        Assert.True(row.HasVideo);

        // Persisted, and the light poll leaves it alone.
        await _polling.PollAllAsync(CancellationToken.None);
        var stored = await Devices.GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(CertificateTrust.SelfSigned, stored!.CertTrust);
        Assert.Equal(DateTimeKind.Utc, stored.CertNotAfterUtc!.Value.Kind);
    }

    [Fact]
    public async Task CertificatePastNotAfterIsStoredAsExpired()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        _time.Advance(_camera.Certificate.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime + TimeSpan.FromDays(3));

        var row = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.Equal(CertificateTrust.Expired, row!.CertTrust);
    }

    [Fact]
    public async Task HttpOnlyDeviceHasNoCertificateInfo()
    {
        var device = await AddAsync(DeviceStatus.Ok, DeviceScheme.Http, staleCertificate: true);

        var row = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.Equal(CertificateTrust.Unknown, row!.CertTrust);
        Assert.Null(row.CertNotAfterUtc);
        Assert.Null(row.CertSubject);
        Assert.Null(row.CertIssuer);
        Assert.Null(row.CertNameMatches);
    }

    [Fact]
    public async Task HttpDeviceGetsTheWebServerCertificateOverVapix()
    {
        _camera.ServesWebServerCertificate = true;
        var device = await AddAsync(DeviceStatus.Ok, DeviceScheme.Http);

        var row = await _polling.RefreshAsync(device.Id, CancellationToken.None);

        Assert.Equal(CertificateTrust.SelfSigned, row!.CertTrust);
        Assert.Equal(_camera.Certificate.NotAfter.ToUniversalTime(), row.CertNotAfterUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(_camera.Certificate.Subject, row.CertSubject);
        Assert.Null(row.CertFingerprintSha256); // nothing is pinned: OADM still talks HTTP to it
    }

    private async Task<Device> AddAsync(DeviceStatus status, DeviceScheme scheme = DeviceScheme.Https, bool staleCertificate = false)
    {
        var device = new Device
        {
            Serial = "B8A44F631339",
            Address = "10.0.0.48",
            Status = status,
            Scheme = scheme,
        };
        if (staleCertificate)
        {
            device.CertTrust = CertificateTrust.Trusted;
            device.CertNotAfterUtc = DateTime.UtcNow.AddDays(100);
            device.CertSubject = "CN=old";
            device.CertIssuer = "CN=old";
            device.CertNameMatches = true;
        }

        return await Devices.AddAsync(device, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        await Eventually.TrueAsync(condition, "the condition");
    }

    /// <summary>
    /// One simulated camera. Every client it creates has already "seen" <see cref="Certificate"/> in
    /// a TLS handshake (HTTPS only); param.cgi requests mark full refreshes.
    /// </summary>
    private sealed class FakeCamera : IVapixConnector, IDisposable
    {
        private readonly FakeHttpMessageHandler _handler;
        private int _statusPolls;
        private FakeTimeProvider? _clock;

        public FakeCamera()
        {
            _handler = new FakeHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (!Online)
                {
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "offline");
                }

                if (path.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _statusPolls);
                    return Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json"));
                }

                if (path.EndsWith("param.cgi", StringComparison.Ordinal))
                {
                    // Only the network parameters mark a full refresh (the API list falls back to the Properties group
                    // when apidiscovery.cgi is missing, as here).
                    if (request.RequestUri!.Query.Contains("Network", StringComparison.Ordinal))
                    {
                        FullRefreshTimes.Add(_clock?.GetUtcNow() ?? DateTimeOffset.UtcNow);
                    }

                    return Fixtures.Text(Fixtures.Read("param-list-networkinfo.txt"));
                }

                if (ServesWebServerCertificate && path.EndsWith("/vapix/services", StringComparison.Ordinal))
                {
                    return Fixtures.Text(WebServerTlsAnswer, mediaType: "application/soap+xml");
                }

                if (ServesWebServerCertificate && path.EndsWith("/config/rest/cert/v1/certificates/Default%20HTTPS", StringComparison.Ordinal))
                {
                    var answer = new System.Text.Json.Nodes.JsonObject
                    {
                        ["status"] = "success",
                        ["data"] = new System.Text.Json.Nodes.JsonObject { ["alias"] = "Default HTTPS", ["certificate"] = Certificate.ExportCertificatePem(), ["keystore"] = "SE0" },
                    };
                    return Fixtures.Json(answer.ToJsonString());
                }

                return Fixtures.Text("not found", System.Net.HttpStatusCode.NotFound);
            });
        }

        public X509Certificate2 Certificate { get; } = TestCertificates.SelfSigned(ip: "10.0.0.48");

        public volatile bool Online = true;

        /// <summary>Answers the SOAP web server settings and REST cert v1 like AXIS OS 12.11 (read over HTTP).</summary>
        public volatile bool ServesWebServerCertificate;

        private const string WebServerTlsAnswer =
            """<?xml version="1.0" encoding="UTF-8"?><SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:aweb="http://www.axis.com/vapix/ws/webserver" xmlns:acert="http://www.axis.com/vapix/ws/cert"><SOAP-ENV:Body><aweb:GetWebServerTlsConfigurationResponse><aweb:Configuration name="WebServer"><aweb:Tls>true</aweb:Tls><aweb:ConnectionPolicies><aweb:Admin>HttpAndHttps</aweb:Admin></aweb:ConnectionPolicies><aweb:CertificateSet><acert:Certificates><acert:Id>Default HTTPS</acert:Id></acert:Certificates><acert:CACertificates/><acert:TrustedCertificates/></aweb:CertificateSet></aweb:Configuration></aweb:GetWebServerTlsConfigurationResponse></SOAP-ENV:Body></SOAP-ENV:Envelope>""";

        public System.Collections.Concurrent.ConcurrentBag<DateTimeOffset> FullRefreshTimes { get; } = [];

        public int StatusPolls => Volatile.Read(ref _statusPolls);

        public FakeCamera WithClock(FakeTimeProvider clock)
        {
            _clock = clock;
            return this;
        }

        public VapixClient Connect(VapixConnectionOptions options)
        {
            var pinning = new CertificatePinning(options.PinnedCertificateFingerprint);
            if (options.Scheme == Uri.UriSchemeHttps)
            {
                pinning.Validate(Certificate, null, options.Address);
            }

            return new VapixClient(VapixClient.BuildBaseAddress(options.Scheme, options.Address), _handler, pinning, disposeHandler: false);
        }

        public void Dispose()
        {
            _handler.Dispose();
            Certificate.Dispose();
        }
    }

    /// <summary>Passes timers on to the fake clock and tells which are still waiting, so a test advances only once a loop waits.</summary>
    private sealed class TimerWatch(TimeProvider inner) : TimeProvider
    {
        private readonly List<Entry> _entries = [];

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public int Pending(TimeSpan due)
        {
            lock (_entries)
            {
                return _entries.Count(e => e.Due == due && !e.Fired && !e.Disposed);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new Entry { Due = dueTime };
            lock (_entries)
            {
                _entries.RemoveAll(e => e.Fired || e.Disposed);
                _entries.Add(entry);
            }

            entry.Timer = inner.CreateTimer(s =>
            {
                entry.Fired = true;
                callback(s);
            }, state, dueTime, period);
            return entry;
        }

        private sealed class Entry : ITimer
        {
            public TimeSpan Due { get; init; }

            public volatile bool Fired;

            public volatile bool Disposed;

            public ITimer Timer { get; set; } = null!;

            public bool Change(TimeSpan dueTime, TimeSpan period) => Timer.Change(dueTime, period);

            public void Dispose()
            {
                Disposed = true;
                Timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return Timer.DisposeAsync();
            }
        }
    }
}
