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
    public async Task FullRefreshIsDueEveryConfiguredInterval()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        Assert.Equal(10, _polling.FullRefreshMinutes);

        Assert.Empty(await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([device.Id], await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));

        await _polling.RefreshAsync(device.Id, CancellationToken.None);
        Assert.Equal(_time.GetUtcNow(), _polling.LastFullRefresh(device.Id));
        Assert.Empty(await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal([device.Id], await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));

        // Setting changes apply live.
        await _polling.RefreshAsync(device.Id, CancellationToken.None);
        await _db.Get<ServerSettingsStore>().SetAsync(SettingKeys.PollingFullRefreshMinutes, 30, CancellationToken.None);
        Assert.Equal(30, _polling.FullRefreshMinutes);
        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Empty(await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await _polling.QueueDueFullRefreshesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FullRefreshIntervalIsValidated()
    {
        var store = _db.Get<ServerSettingsStore>();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(SettingKeys.PollingFullRefreshMinutes, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(SettingKeys.PollingFullRefreshMinutes, 1441, CancellationToken.None));
        await store.SetAsync(SettingKeys.PollingFullRefreshMinutes, 1440, CancellationToken.None);
        Assert.Equal(1440, (await store.GetServerSettingsAsync(CancellationToken.None)).FullRefreshMinutes);
        await store.ResetAsync(SettingKeys.PollingFullRefreshMinutes, CancellationToken.None);
        Assert.Equal(ServerSettings.DefaultFullRefreshMinutes, (await store.GetServerSettingsAsync(CancellationToken.None)).FullRefreshMinutes);
    }

    [Fact]
    public async Task RunningServiceDoesAFullRefreshAfterTenMinutes()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        var start = _time.GetUtcNow();
        using var cts = new CancellationTokenSource();
        var run = _polling.RunAsync(cts.Token);

        // Step the clock one check period at a time, each step only once the schedule loop waits for it again, so no
        // step is lost on a slow machine. Stop as soon as a refresh is queued.
        while (_time.GetUtcNow() - start < TimeSpan.FromMinutes(30))
        {
            await Eventually.TrueAsync(() => _timers.Pending(_polling.FullRefreshCheckPeriod) > 0, "the schedule loop waiting");
            if (_polling.IsRefreshQueued(device.Id) || _polling.LastFullRefresh(device.Id) is not null)
            {
                break;
            }

            _time.Advance(_polling.FullRefreshCheckPeriod);
        }

        await WaitUntilAsync(async () => (await Devices.GetAsync(device.Id, CancellationToken.None))!.DhcpEnabled is not null);
        // First refresh exactly at 10 min (the clock stands still once it is queued), no duplicate inside the period.
        var times = _camera.FullRefreshTimes.ToArray().Order().ToArray();
        Assert.Equal(TimeSpan.FromMinutes(10), times[0] - start);
        Assert.All(times.Skip(1), t => Assert.True(t - times[0] >= TimeSpan.FromMinutes(10), $"duplicate full refresh at {t - times[0]} after the first"));
        Assert.True(_camera.StatusPolls > 0, "the light poll keeps running alongside");

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

        // Ok -> Ok and Ok -> Unreachable do not trigger another full refresh.
        await _polling.PollAllAsync(CancellationToken.None);
        _camera.Online = false;
        await _polling.PollAllAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Unreachable, (await Devices.GetAsync(device.Id, CancellationToken.None))!.Status);
        await Task.Delay(100);
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
