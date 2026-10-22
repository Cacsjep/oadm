using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>One certificate per device and profile, and Not renewed (docs/spec/est-server.md "Renewals").</summary>
public sealed class RenewalTests : IAsyncLifetime
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Axis cameras renew halfway; Not renewed two days later (30 days: a tenth is 3 days, so 2 days count).</summary>
    private static readonly TimeSpan Due = (Lifetime / 2) + EstService.NotRenewedGrace;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeAuthority _ca = new();
    private readonly FakeSettings _settings = new();
    private readonly MemoryPluginRecords _records = new();
    private readonly ServerTests.Devices _devices = new();
    private readonly ServerTests.FakePublisher _publisher = new();
    private readonly FakeCoreContext _page = new();
    private readonly FakeDevice _camera = new() { Serial = "B8A44F631339", Address = "10.0.0.48" };
    private EstService _est = null!;

    public async Task InitializeAsync()
    {
        _devices.List.Add(_camera);
        _est = await StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _est.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public void Per_device_and_profile_the_newest_certificate_and_the_one_before_it_are_kept()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        Issue("A1", "https", now);
        Issue("B1", "mqtt", now);
        Issue("A2", "HTTPS", now.AddDays(15));
        Issue("A3", "https", now.AddDays(30));
        Issue("A0", "https", now.AddDays(-1)); // an older one arriving late is not kept

        Assert.Equal(["A3", "B1"], _est.Store.Current().Select(c => c.SerialNumber));
        Assert.NotNull(_est.Store.FindCertificate("A2")); // the camera may still renew with it
        Assert.Null(_est.Store.FindCertificate("A1"));
        Assert.Null(_est.Store.FindCertificate("A0"));
        Assert.Equal(3, _est.Store.CertificateCount);
    }

    [Theory]
    [InlineData(365, 184.5)] // halfway (182.5) and 2 days
    [InlineData(1, 0.6)] // short: halfway and a tenth of the lifetime
    public void A_certificate_counts_as_not_renewed_halfway_through_its_lifetime_and_a_little_later(int days, double dueAfterDays)
    {
        var issued = _time.GetUtcNow().UtcDateTime;
        var certificate = new IssuedCertificate { IssuedUtc = issued, NotAfterUtc = issued.AddDays(days) };

        Assert.Equal(issued.AddDays(dueAfterDays), EstStore.RenewalDue(certificate, EstService.NotRenewedGrace));
    }

    [Fact]
    public async Task A_certificate_the_camera_did_not_renew_in_time_is_flagged_once_with_an_event()
    {
        var issued = Issue("A1", "https", _time.GetUtcNow().UtcDateTime);

        _time.Advance(Due - TimeSpan.FromHours(1));
        await _est.CheckRenewalsAsync(CancellationToken.None);
        Assert.Empty(_publisher.Fired);

        _time.Advance(TimeSpan.FromHours(2));
        await _est.CheckRenewalsAsync(CancellationToken.None);
        await _est.CheckRenewalsAsync(CancellationToken.None);

        Assert.Equal((EstPluginInfo.NotRenewedEvent, (Guid?)_camera.Id), Assert.Single(_publisher.Fired));
        Assert.True(_est.Store.FindCertificate(issued.SerialNumber)!.NotRenewed);
        Assert.Equal(1, _est.Store.NotRenewed(500).Total);
        Assert.True(_page.Published.Reader.TryRead(out var published));
        Assert.Equal(EstMethods.CertificatesTopic, published.Topic);

        // A late renewal ends it.
        Issue("A2", "https", _time.GetUtcNow().UtcDateTime);
        Assert.Equal(0, _est.Store.NotRenewed(500).Total);
    }

    [Fact]
    public async Task The_hourly_check_runs_by_itself()
    {
        Issue("A1", "https", _time.GetUtcNow().UtcDateTime);

        _time.Advance(Lifetime);

        await Eventually.TrueAsync(() => _publisher.Fired.Count == 1, "the check flagged the certificate");
    }

    [Fact]
    public async Task Stopped_renewals_are_never_flagged_and_removed_devices_are_forgotten()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var stopped = Issue("A1", "https", now);
        _est.Store.StopRenewals(_camera.Id, now); // enrollment removed

        var gone = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.49" };
        var orphan = Issue("C1", "https", now, gone);

        _time.Advance(Lifetime * 2);
        await _est.CheckRenewalsAsync(CancellationToken.None);

        Assert.Empty(_publisher.Fired);
        Assert.NotNull(_est.Store.FindCertificate(stopped.SerialNumber));
        Assert.Null(_est.Store.FindCertificate(orphan.SerialNumber));
    }

    [Fact]
    public async Task Remove_certificate_enrollment_stops_the_renewal_check_of_the_camera()
    {
        var issued = Issue("A1", "https", _time.GetUtcNow().UtcDateTime);
        var camera = new FakeCamera { Respond = (request, _) => request.RequestUri!.OriginalString == "config/discover"
            ? Json("""{"apis":{"cert":{"v1":{"version":"1.0","state":"released"}},"cert-est":{"v1":{"version":"1.0.1","state":"released"}}}}""")
            : Json("""{"status":"success","data":[{"id":"oadm_https","services":["WEBSERVER"]}]}""") };

        await new EstRemoveTask(() => _est).ExecuteAsync(new FakeTaskContext(camera), _camera, "{}", CancellationToken.None);

        Assert.NotNull(_est.Store.FindCertificate(issued.SerialNumber)!.RenewalStoppedUtc);
    }

    [Fact]
    public async Task Not_renewed_survives_a_restart_and_older_records_of_earlier_versions_are_dropped()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // 4.1.0-rc.2 kept every certificate: three of the same device and profile, in any order.
        await _settings.SetAsync(EstStore.CertificatesKey, EstJson.Serialize(new[]
        {
            Certificate("A3", "https", now.AddDays(1)),
            Certificate("A1", "https", now.AddDays(-1)),
            Certificate("A2", "https", now),
        }), CancellationToken.None);
        await _est.DisposeAsync();
        _est = await StartAsync();
        Assert.Equal(["A3"], _est.Store.Current().Select(c => c.SerialNumber));
        Assert.Equal(2, _est.Store.CertificateCount);
        await _est.Store.FlushAsync(CancellationToken.None);
        Assert.Equal(["A2", "A3"], (await _records.ListAsync(EstStore.CertificatesKey, CancellationToken.None)).Keys.Order(StringComparer.Ordinal));
        Assert.Null(await _settings.GetAsync(EstStore.CertificatesKey, CancellationToken.None)); // moved to the records

        _time.Advance(Lifetime);
        await _est.CheckRenewalsAsync(CancellationToken.None);
        await _est.DisposeAsync();
        _est = await StartAsync();

        Assert.True(_est.Store.FindCertificate("A3")!.NotRenewed);
    }

    [Fact]
    public async Task Only_the_certificates_that_changed_are_written()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < 1000; i++)
        {
            Issue($"C{i}", "https", now, new FakeDevice { Serial = $"ACCC8E{i:X6}" });
        }

        await _est.Store.FlushAsync(CancellationToken.None);
        var written = _records.WrittenRecords;
        Assert.Equal(1000, written);

        Issue("A1", "https", now);
        Issue("A2", "https", now.AddDays(1));
        Issue("A3", "https", now.AddDays(2)); // A1 goes
        await _est.Store.FlushAsync(CancellationToken.None);

        Assert.Equal(3, _records.WrittenRecords - written); // A2 and A3 written, A1 deleted
        Assert.Equal(1002, (await _records.ListAsync(EstStore.CertificatesKey, CancellationToken.None)).Count);
    }

    private async Task<EstService> StartAsync()
    {
        var est = new EstService(new EstHost(_settings, _devices, new FakePluginNetwork(), () => _ca, () => true)
        {
            Time = _time,
            Records = _records,
            Publisher = _publisher,
            Events = _page,
        });
        await est.StartAsync(CancellationToken.None);
        return est;
    }

    private IssuedCertificate Issue(string serial, string profile, DateTime issuedUtc, FakeDevice? device = null)
    {
        var certificate = Certificate(serial, profile, issuedUtc, device);
        _est.Store.AddCertificate(certificate);
        return certificate;
    }

    private IssuedCertificate Certificate(string serial, string profile, DateTime issuedUtc, FakeDevice? device = null)
    {
        device ??= _camera;
        return new IssuedCertificate
        {
            SerialNumber = serial,
            DeviceId = device.Id,
            Device = device.Address,
            Profile = profile,
            IssuedUtc = issuedUtc,
            NotAfterUtc = issuedUtc + Lifetime,
        };
    }

    private static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
