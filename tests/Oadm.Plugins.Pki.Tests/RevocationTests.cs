using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The list of issued certificates and the revocation list through the plugin (docs/spec/radius-server.md "Certificates and
/// revocation"): every certificate the CA service signs is listed with its use and DER, the page methods, the CA service's
/// SDK 0.4.0 members for EST and the 802.1X server, a restart, and certificates of the OADM CAs found on devices.
/// </summary>
public sealed class RevocationTests
{
    internal static byte[] Csr()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=camera", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
    }

    /// <summary>Signs as EST does; <paramref name="confirm"/>: the camera got it too (it becomes current, as after EST's answer).</summary>
    internal static async Task<IssuedDeviceCertificate> SignAsync(IDeviceCertificateAuthority authority, Guid device, string serial = "B8A44F631339", DeviceCertificateUse use = DeviceCertificateUse.Server,
        string? purpose = null, string? source = "EST", bool confirm = true)
    {
        var issued = await authority.SignAsync(
            new DeviceCertificateRequest(Csr(), "10.0.0.48", ["10.0.0.48"], ["axis-" + serial.ToLowerInvariant() + ".local"], use)
            {
                DeviceId = device,
                Source = source,
                Purpose = purpose,
                Validity = TimeSpan.FromDays(365),
            },
            CancellationToken.None);
        if (confirm && authority is PkiCertificateAuthority pki)
        {
            await pki.ConfirmAsync(issued.SerialNumber, CancellationToken.None);
        }

        return issued;
    }

    [Fact]
    public async Task Every_certificate_the_ca_service_signs_is_listed_with_its_use_and_kept_for_view_certificate()
    {
        await using var pki = await PkiHarness.StartAsync();
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var device = Guid.NewGuid();

        var https = await SignAsync(authority, device);
        var mqtt = await SignAsync(authority, device, use: DeviceCertificateUse.Client, purpose: DeviceCertificatePurposes.Mqtt);

        var page = await authority.ListIssuedAsync(new IssuedCertificateQuery(), CancellationToken.None);
        Assert.Equal(2, page.Total);
        var listed = page.Rows.Single(r => r.SerialNumber == CertificateSerials.Normalize(mqtt.SerialNumber));
        Assert.Equal((DeviceCertificatePurposes.Mqtt, IssuedCertificateSources.Est, "B8A44F631339", IssuedCertificateState.Valid), (listed.Purpose, listed.Source, listed.DeviceSerial, listed.State));
        Assert.Equal(https.Certificate, await authority.GetIssuedCertificateAsync(https.SerialNumber, CancellationToken.None));
        Assert.Null(await authority.GetIssuedCertificateAsync("ABCDEF", CancellationToken.None));

        var reply = await pki.InvokeAsync<CertificateDetailsReply>(PkiMethods.GetCertificate, new CertificateDetailsRequest(https.SerialNumber));
        Assert.Null(reply.Error);
        Assert.Contains("DNS axis-b8a44f631339.local", reply.Certificate!.Names);
        Assert.Contains("Server authentication", reply.Certificate.Uses);
        Assert.Null(reply.Revocation);
        Assert.Equal(PkiCertificateTexts.NotListed, (await pki.InvokeAsync<CertificateDetailsReply>(PkiMethods.GetCertificate, new CertificateDetailsRequest("0BAD"))).Error);
    }

    [Fact]
    public async Task Pages_of_500_with_filters_and_the_last_page_when_fewer_match_now()
    {
        await using var pki = await PkiHarness.StartAsync();
        var now = DateTime.UtcNow;
        await pki.SetIssuedAsync(Enumerable.Range(0, 1200).Select(i => new IssuedCertificate
        {
            SerialNumber = "C" + i.ToString("X5", System.Globalization.CultureInfo.InvariantCulture),
            DeviceId = Guid.NewGuid(),
            Purpose = i % 4 == 0 ? CertificatePurpose.Dot1x : CertificatePurpose.Https,
            IssuedUtc = now.AddMinutes(-i),
            NotAfterUtc = now.AddDays(300),
            Source = i % 2 == 0 ? IssuedCertificateSources.Est : null,
        }));

        // The CAs come first (the OADM CA, the Axis device ID roots and intermediates), then the device certificates.
        var cas = (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = ListedCertificateUses.Ca })).Total;
        Assert.True(cas >= 2);
        var first = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest());
        Assert.Equal((1200 + cas, 500, 0, (int?)null), (first.Total, first.Rows.Count, first.Offset, first.WarningDays)); // each device follows its Expiry warning policy
        Assert.All(first.Rows.Take(cas), r => Assert.Equal(ListedCertificateKind.Authority, r.Kind));
        Assert.Equal("C00000", first.Rows[cas].SerialNumber); // newest first
        var last = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { Offset = 1000 });
        Assert.Equal(200 + cas, last.Rows.Count);
        Assert.Equal(PkiPluginInfo.CertificatesPageSize, new CertificatesRequest().Limit);
        Assert.Equal(500, (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { Limit = 100_000 })).Rows.Count);

        // A filter with fewer rows than the asked offset: the last page of what matches.
        var dot1x = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = CertificatePurpose.Dot1x, Source = IssuedCertificateSources.Est, Offset = 1000 });
        Assert.Equal((300, 0, 300), (dot1x.Total, dot1x.Offset, dot1x.Rows.Count));
        Assert.All(dot1x.Rows, r => Assert.Equal((CertificatePurpose.Dot1x, IssuedCertificateSources.Est), (r.Purpose, r.Source)));

        // Unknown filter values count as no filter (never an error the user cannot fix).
        Assert.Equal(1200 + cas, (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = "telnet", Source = "x", State = (IssuedCertificateState)9 })).Total);
    }

    [Fact]
    public async Task Certificates_of_the_oadm_cas_found_on_a_device_are_listed_once_others_never()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = pki.Service.Ca!;
        var device = new PkiFakeDevice(Guid.NewGuid());
        using var ours = Issue(ca, "B8A44F631339", client: true);
        using var otherCamera = Issue(ca, "ACCC8E000001", client: false);
        using var foreignRoot = TestCa.Root("Foreign CA");
        using var foreign = TestCa.Leaf(foreignRoot);

        var added = await pki.Service.ImportFoundAsync(device, [("oadm_dot1x", CaCertificates.ToPem(ours)), ("OADM HTTPS x", CaCertificates.ToPem(otherCamera)), ("other", CaCertificates.ToPem(foreign)), ("broken", "not a pem")], CancellationToken.None);
        Assert.Equal(1, added);
        Assert.Equal(0, await pki.Service.ImportFoundAsync(device, [("oadm_dot1x", CaCertificates.ToPem(ours))], CancellationToken.None)); // once

        var entry = Assert.Single(pki.Service.Issued.ForDevice(device.Id));
        Assert.Equal((CertificatePurpose.Dot1x, IssuedCertificateSources.Est, "B8A44F631339", "oadm_dot1x"), (entry.Purpose, IssuedRegistry.SourceOf(entry), entry.DeviceSerial, entry.Alias));
        Assert.Equal(ours.RawData, await pki.Service.Issued.GetDerAsync(entry.SerialNumber, CancellationToken.None));
    }

    [Fact]
    public async Task A_certificate_found_on_a_device_takes_its_use_from_the_est_profile_name()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = pki.Service.Ca!;
        var device = new PkiFakeDevice(Guid.NewGuid());
        using var mqtt = Issue(ca, "B8A44F631339", client: true);
        using var rtsps = Issue(ca, "B8A44F631339", client: false);
        using var dot1x = Issue(ca, "B8A44F631339", client: true);

        var added = await pki.Service.ImportFoundAsync(device,
            [("oadm_mqtt", CaCertificates.ToPem(mqtt)), ("oadm2_rtsps", CaCertificates.ToPem(rtsps)), ("OADM 802.1X 20240101", CaCertificates.ToPem(dot1x))],
            CancellationToken.None);

        Assert.Equal(3, added);
        var purposes = pki.Service.Issued.ForDevice(device.Id).ToDictionary(e => e.Alias!, e => e.Purpose);
        Assert.Equal(CertificatePurpose.Mqtt, purposes["oadm_mqtt"]); // not IEEE 802.1X: the profile name says MQTT
        Assert.Equal(CertificatePurpose.Rtsps, purposes["oadm2_rtsps"]);
        Assert.Equal(CertificatePurpose.Dot1x, purposes["OADM 802.1X 20240101"]); // no profile name: a client certificate
    }

    [Theory]
    [InlineData("oadm_mqtt", true, CertificatePurpose.Mqtt)]
    [InlineData("oadm2_mqtt", true, CertificatePurpose.Mqtt)]
    [InlineData("oadm_MQTT_cameras", true, CertificatePurpose.Mqtt)]
    [InlineData("oadm_dot1x", true, CertificatePurpose.Dot1x)]
    [InlineData("oadm_https", false, CertificatePurpose.Https)]
    [InlineData("oadm2_rtsps", false, CertificatePurpose.Rtsps)]
    [InlineData("oadm_mqtt", false, CertificatePurpose.Https)] // the name does not fit a server certificate: today's rule
    [InlineData("oadm_https", true, CertificatePurpose.Dot1x)]
    [InlineData("oadm_cameras", true, CertificatePurpose.Dot1x)] // a name without a use
    [InlineData("oadm_mqttx", true, CertificatePurpose.Dot1x)]
    [InlineData("mqtt", true, CertificatePurpose.Dot1x)] // not an EST profile
    [InlineData("OADM HTTPS 20240101", false, CertificatePurpose.Https)]
    public void The_use_of_a_found_certificate_follows_the_est_profile_name_when_it_fits(string alias, bool client, string expected) =>
        Assert.Equal(expected, IssuedRegistry.PurposeOfFound(alias, client));

    /// <summary>A device certificate signed by the PKI's CA (like the tasks), naming the camera's axis name.</summary>
    internal static X509Certificate2 Issue(CaMaterial ca, string serial, bool client)
    {
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var names = new CertificateNames("10.0.0.48", ["10.0.0.48"], ["axis-" + serial.ToLowerInvariant() + ".local"]);
        return DeviceCertificateIssuer.Sign(ca, csr, names, client ? CertificatePurpose.Dot1x : CertificatePurpose.Https, DateTimeOffset.UtcNow, TimeSpan.FromDays(100));
    }
}

/// <summary>The one pass over the devices for certificates from before the list (OADM 4.1 and older).</summary>
public sealed class ExistingCertificateImportTests
{
    [Fact]
    public async Task The_pass_reads_every_device_that_answers_once_and_then_never_again()
    {
        await using var pki = await PkiHarness.StartAsync();
        var settings = pki.Settings;
        var answering = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        var old = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.49", "ACCC8E000001", Firmware: "6.50.5"); // older than the PKI tasks support
        var soap = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.50", "ACCC8E000003", Firmware: "10.12.236"); // the SOAP certificate service
        var camera = new FakeCamera();
        using var issued = RevocationTests.Issue(pki.Service.Ca!, "B8A44F631339", client: false);
        camera.AddCertificate("OADM HTTPS 20240101-120000", CaCertificates.ToPem(issued));
        var legacy = FakeCamera.Legacy("10.12");
        using var legacyIssued = RevocationTests.Issue(pki.Service.Ca!, "ACCC8E000003", client: false);
        legacy.AddCertificate("OADM HTTPS 20240102-120000", CaCertificates.ToPem(legacyIssued));
        var system = new FakeSystem([answering, old, soap], new Dictionary<Guid, FakeCamera> { [answering.Id] = camera, [soap.Id] = legacy });
        var import = new ExistingCertificateImport(pki.Service, system, settings, TimeProvider.System, TimeSpan.Zero, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await import.RunAsync(CancellationToken.None);

        Assert.Single(pki.Service.Issued.ForDevice(answering.Id));
        Assert.Single(pki.Service.Issued.ForDevice(soap.Id));
        Assert.Empty(pki.Service.Issued.ForDevice(old.Id));
        Assert.Equal(new[] { answering.Id, soap.Id }.Order(), system.Asked.Order()); // 4 at a time: any order
        Assert.All(camera.Requests, r => Assert.Equal("GET", r.Method)); // read only
        Assert.Empty(legacy.Writes); // read only (SOAP reads are POSTs)
        Assert.NotNull(await settings.GetAsync(ExistingCertificateImport.DoneKey, CancellationToken.None));

        await import.RunAsync(CancellationToken.None);
        Assert.Equal(2, system.Asked.Count); // done once per server
    }

    [Fact]
    public async Task A_round_that_reached_no_device_is_not_done_and_runs_again_later()
    {
        await using var pki = await PkiHarness.StartAsync();
        var settings = pki.Settings;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var device = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        var cameras = new Dictionary<Guid, FakeCamera>(); // offline at first
        var system = new FakeSystem([device], cameras);
        var import = new ExistingCertificateImport(pki.Service, system, settings, time, TimeSpan.Zero, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            RetryInterval = TimeSpan.FromHours(1),
        };

        var run = import.RunAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => system.Asked.Count == 1, "the first round");
        Assert.Null(await settings.GetAsync(ExistingCertificateImport.DoneKey, CancellationToken.None)); // not done: nothing was read

        var camera = new FakeCamera();
        using var issued = RevocationTests.Issue(pki.Service.Ca!, "B8A44F631339", client: true);
        camera.AddCertificate("oadm_dot1x", CaCertificates.ToPem(issued));
        cameras[device.Id] = camera;
        await Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromHours(1)); // the next round, an hour later
            return run.IsCompleted;
        }, "the second round");
        await run;

        Assert.Equal(2, system.Asked.Count);
        Assert.Single(pki.Service.Issued.ForDevice(device.Id));
        Assert.NotNull(await settings.GetAsync(ExistingCertificateImport.DoneKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_server_without_devices_is_done_at_once()
    {
        await using var pki = await PkiHarness.StartAsync();
        var import = new ExistingCertificateImport(pki.Service, new FakeSystem([], []), pki.Settings, TimeProvider.System, TimeSpan.Zero, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await import.RunAsync(CancellationToken.None);
        Assert.NotNull(await pki.Settings.GetAsync(ExistingCertificateImport.DoneKey, CancellationToken.None));
    }

    private sealed class FakeSystem(IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> devices, Dictionary<Guid, FakeCamera> cameras) : ISystemAccess, Oadm.Sdk.Devices.IDeviceRepository, Oadm.Sdk.Vapix.IVapixClientFactory
    {
        public List<Guid> Asked { get; } = [];

        public Oadm.Sdk.Devices.IDeviceRepository Devices => this;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => this;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();

        public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => null;

        public Task<IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult(devices);

        public Task<Oadm.Sdk.Devices.IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(devices.FirstOrDefault(d => d.Id == id));

        public Task<Oadm.Sdk.Vapix.IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            lock (Asked)
            {
                Asked.Add(deviceId);
            }

            return cameras.TryGetValue(deviceId, out var camera)
                ? Task.FromResult<Oadm.Sdk.Vapix.IVapixClient>(camera)
                : Task.FromException<Oadm.Sdk.Vapix.IVapixClient>(new HttpRequestException("unreachable", null, HttpStatusCode.ServiceUnavailable));
        }
    }
}
