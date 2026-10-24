using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Est.Protocol;
using Oadm.Plugins.Est.Serving;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>The EST server end to end: TLS with the device ID over an in-memory connection, the operations, who may enroll.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private const string Serial = "B8A44F631339";

    private readonly X509Certificate2 _root = TestPki.Root("Device ID Root");
    private readonly FakeAuthority _ca = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePluginNetwork _network = new();
    private readonly Devices _devices = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeSites _sites = new();
    private readonly FakeCoreContext _events = new();
    private readonly FakeCoreContext _groups = new();
    private X509Certificate2 _intermediate = null!;
    private X509Certificate2 _deviceId = null!;
    private EstService _service = null!;
    private bool _pro = true;
    private Oadm.Plugins.Pki.Policies.PolicyStore? _policies; // PKI's policies (null: no PKI: nothing signs in)

    public async Task InitializeAsync()
    {
        // A new server's policies: the issuing ones and the EST ones (Enrollment "All devices", Sign-in CA "Axis device ID"
        // for new certificates and "OADM CA" for renewals). The store lists the CAs of the fake PKI once it has its list.
        _policies = await TestPolicies.EstStoreAsync(authorities: () => _ca.AuthorityList.Count > 0 ? [.. _ca.AuthorityList] : [TestPolicies.OadmCa],
            devices: _ => _devices.ListAsync(CancellationToken.None));
        _intermediate = TestPki.Intermediate(_root, "Device ID Intermediate");
        _deviceId = TestPki.DeviceId(_intermediate, Serial);
        _devices.List.Add(new FakeDevice { Serial = Serial, Address = "10.0.0.48", HostName = "cam-entrance" });
        _service = new EstService(new EstHost(_settings, _devices, _network, () => _ca, () => _pro)
        {
            DeviceIds = new AxisDeviceId([X509CertificateLoader.LoadCertificate(_root.RawData)]),
            LocalAddresses = () => ["10.0.0.17"],
            HostName = "oadm-server",
            Publisher = _publisher,
            Sites = _sites,
            Events = _events,
            Scopes = _groups.FakeScopes,
            Policies = () => _policies,
        });
        await _service.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _deviceId.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
        _ca.Dispose();
    }

    [Fact]
    public async Task A_written_answer_tells_pki_the_camera_received_its_certificate_and_a_refusal_tells_nothing()
    {
        _policies = await TestPolicies.EstStoreAsync();
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;

        var enrolled = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(200, enrolled.Status);
        using var issued = X509CertificateLoader.LoadCertificate(EstMessages.ReadCertsOnly(Convert.FromBase64String(enrolled.Body))[0]);

        // Written: PKI makes it current and revokes the camera's previous one (docs/spec/pki.md "Current and pending").
        await Eventually.TrueAsync(() => { lock (_ca.Deliveries) { return _ca.Deliveries.Count == 1; } }, "the delivery");
        Assert.Equal(("delivered", CertificateSerials.Normalize(issued.SerialNumber)), _ca.Deliveries[0]);

        Assert.Equal(404, (await SendAsync("POST", "/.well-known/est/nosuch/simpleenroll", _deviceId, Body(csr))).Status);
        lock (_ca.Deliveries)
        {
            Assert.Single(_ca.Deliveries); // no certificate, nothing to tell
        }
    }

    [Fact]
    public async Task Enrollment_and_renewal_issue_with_the_valid_days_of_the_profiles_policy()
    {
        _policies = await TestPolicies.EstStoreAsync();
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;

        // The ready-made profile names no policy yet: the first HTTPS certificate policy.
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);
        Assert.Equal(TimeSpan.FromDays(365), _ca.Requests[^1].Validity);

        var twoYears = await TestPolicies.AddAsync(_policies, new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "HTTPS 2 years", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.HttpsCertificate, CaId = "oadm", ValidDays = 730, Key = Oadm.Plugins.Pki.Policies.PolicyKeys.Rsa,
        });
        var config = _service.Store.Config;
        var saved = await _service.SaveAsync(new SaveRequest(config with
        {
            Profiles = [.. config.Profiles.Select(p => p with { PolicyId = p.Use == ProfileUse.Https ? twoYears.Id : _policies.FirstOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Dot1xCertificate)!.Id })],
        }), CancellationToken.None);
        Assert.Null(saved.Error);
        var enrolled = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(200, enrolled.Status);
        Assert.Equal(TimeSpan.FromDays(730), _ca.Requests[^1].Validity);

        // The renewal takes the policy's values of now.
        Assert.Null((await _policies.SaveAsync(twoYears with { ValidDays = 200 }, "admin", Oadm.Plugins.Pki.PkiService.CipherSuites, CancellationToken.None)).Error);
        using var issued = X509CertificateLoader.LoadCertificate(EstMessages.ReadCertsOnly(Convert.FromBase64String(enrolled.Body))[0]);
        using var renewalIdentity = TestPki.Usable(issued.CopyWithPrivateKey(key));
        var (csr2, key2) = TestPki.Request();
        using var __ = key2;
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simplereenroll", renewalIdentity, Body(csr2))).Status);
        Assert.Equal(TimeSpan.FromDays(200), _ca.Requests[^1].Validity);
        Assert.Equal(["HTTPS", "IEEE 802.1X", "HTTPS 2 years"], _service.GetState().IssuingPolicies.Select(p => p.Name));
    }

    [Fact]
    public async Task A_profile_without_a_usable_policy_is_refused_on_save_and_issues_nothing()
    {
        _policies = await TestPolicies.EstStoreAsync();
        var config = _service.Store.Config;

        var none = await _service.SaveAsync(new SaveRequest(config), CancellationToken.None); // the profiles name no policy
        Assert.Equal(("profiles[0].policy", EstTexts.ChoosePolicy), (none.Field, none.Error));
        var dot1x = _policies.FirstOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Dot1xCertificate)!;
        var wrong = await _service.SaveAsync(new SaveRequest(config with { Profiles = [.. config.Profiles.Select(p => p with { PolicyId = dot1x.Id })] }), CancellationToken.None);
        Assert.Equal(("profiles[0].policy", EstTexts.PolicyGone), (wrong.Field, wrong.Error)); // an IEEE 802.1X policy for HTTPS

        // A stored profile whose policy is of another kind (settings written before the check): nothing is issued (fail closed).
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;
        await _service.Store.SaveConfigAsync(config with { Profiles = [.. config.Profiles.Select(p => p with { PolicyId = dot1x.Id })] }, CancellationToken.None);
        var refused = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(503, refused.Status);
        Assert.Contains(EstRefusals.NoPolicy, refused.Body, StringComparison.Ordinal);

        // A store that cannot be read: nobody signs in, nothing is issued.
        var damaged = new FakeSettings();
        await damaged.SetAsync(Oadm.Plugins.Pki.Policies.PolicyStore.SettingsKey, "{\"policies\":3}", CancellationToken.None);
        _policies = await TestPolicies.StoreAsync(settings: damaged);
        Assert.True(_policies.IsUnreadable);
        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);
        Assert.DoesNotContain(_ca.Requests, r => r.DeviceId is not null); // only the server's own certificate
    }

    [Fact]
    public async Task The_server_certificate_follows_the_policy_est_server()
    {
        _policies = await TestPolicies.EstStoreAsync();
        var policy = _policies.ServerCertificateFor(Oadm.Plugins.Pki.Policies.PolicyServers.Est)!;
        X509Certificate2? server = null;
        Assert.Equal(200, (await SendAsync("GET", "/.well-known/est/https/cacerts", clientCertificate: null, onServer: s => server = s)).Status);
        Assert.NotNull(server!.GetECDsaPublicKey());
        Assert.Equal(TimeSpan.FromDays(90), _ca.Requests[^1].Validity);
        var made = _ca.Requests.Count;

        // Other days alone: the certificate stays until its renewal; another key: a new one at once.
        await TestPolicies.AddAsync(_policies, policy with { ValidDays = 30 });
        Assert.Equal(200, (await SendAsync("GET", "/.well-known/est/https/cacerts", clientCertificate: null)).Status);
        Assert.Equal(made, _ca.Requests.Count);
        await TestPolicies.AddAsync(_policies, _policies.ServerCertificateFor(Oadm.Plugins.Pki.Policies.PolicyServers.Est)! with { Key = Oadm.Plugins.Pki.Policies.PolicyKeys.Rsa });
        Assert.Equal(200, (await SendAsync("GET", "/.well-known/est/https/cacerts", clientCertificate: null, onServer: s => server = s)).Status);
        Assert.Equal(2048, server!.GetRSAPublicKey()!.KeySize);
        Assert.Equal(TimeSpan.FromDays(30), _ca.Requests[^1].Validity);
    }

    [Fact]
    public async Task Cacerts_answers_the_oadm_ca_and_the_server_proves_it_is_oadm()
    {
        X509Certificate2? server = null;
        var response = await SendAsync("GET", "/.well-known/est/https/cacerts", clientCertificate: null, onServer: s => server = s);

        Assert.Equal(200, response.Status);
        Assert.Equal("application/pkcs7-mime", response.Headers["Content-Type"]);
        Assert.Equal("base64", response.Headers["Content-Transfer-Encoding"]);
        Assert.Equal([_ca.Certificate.RawData], EstMessages.ReadCertsOnly(Convert.FromBase64String(response.Body)));

        // The server's TLS certificate comes from the OADM CA and names this server.
        Assert.NotNull(server);
        Assert.Equal(_ca.Certificate.Subject, server.Issuer);
        var names = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(IPAddress.Parse("10.0.0.17"), names.EnumerateIPAddresses());
        Assert.Contains("oadm-server", names.EnumerateDnsNames());
        Assert.Equal("Running on port 9454", _service.GetState().StatusText);
    }

    [Fact]
    public void Issued_by_names_the_oadm_ca_by_its_certificate_when_pki_lists_no_cas()
    {
        Assert.Empty(_ca.AuthorityList); // a PKI without the list
        var name = _ca.Certificate.GetNameInfo(X509NameType.SimpleName, false);
        Assert.StartsWith("OADM Test CA", name, StringComparison.Ordinal);

        var state = _service.GetState();

        Assert.Equal(name, state.IssuingCa);
        Assert.Equal(3, state.PoliciesApply); // "All devices", "Axis device ID", "OADM CA"
        Assert.Null(state.PolicyProblem);
    }

    [Fact]
    public async Task A_camera_a_device_group_covers_gets_no_host_name_it_reports()
    {
        // A user of the group could have set the camera's host name (Network settings, VAPIX Commander, Web View).
        _groups.FakeScopes.Add("Building B", [_devices.List[0].Id]);
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;

        var response = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));

        Assert.Equal(200, response.Status);
        using var issued = X509CertificateLoader.LoadCertificate(EstMessages.ReadCertsOnly(Convert.FromBase64String(response.Body))[0]);
        Assert.Equal("CN=10.0.0.48", issued.Subject);
        var names = issued.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["axis-b8a44f631339.local"], names.EnumerateDnsNames());
        Assert.Equal([IPAddress.Parse("10.0.0.48")], names.EnumerateIPAddresses());
    }

    [Fact]
    public async Task A_managed_camera_enrolls_with_its_device_id_and_renews_with_its_new_certificate()
    {
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;

        var response = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));

        Assert.Equal(200, response.Status);
        Assert.StartsWith("application/pkcs7-mime; smime-type=certs-only", response.Headers["Content-Type"], StringComparison.Ordinal);
        var certificates = EstMessages.ReadCertsOnly(Convert.FromBase64String(response.Body));
        Assert.Equal(2, certificates.Count); // the leaf and the chain: Axis refuses a leaf alone
        using var issued = X509CertificateLoader.LoadCertificate(certificates[0]);
        Assert.Equal("CN=10.0.0.48", issued.Subject); // OADM's names, never the request's
        var names = issued.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("axis-b8a44f631339.local", names.EnumerateDnsNames());
        Assert.Contains("cam-entrance", names.EnumerateDnsNames());
        var request = Assert.Single(DeviceRequests());
        Assert.Equal((DeviceCertificateUse.Server, "EST", TimeSpan.FromDays(365)), (request.Use, request.Source, request.Validity));

        var enrollment = Assert.Single(_service.Store.Enrollments());
        Assert.Equal((true, EnrollmentKind.New, "https", "10.0.0.48"), (enrollment.Ok, enrollment.Kind, enrollment.Profile, enrollment.Device));
        Assert.Equal(1, _service.Store.CertificateCount);
        Assert.Equal(EstPluginInfo.EnrolledEvent, Assert.Single(_publisher.Fired).Type);

        // Renewal with the certificate it got (and its key).
        using var renewalIdentity = TestPki.Usable(issued.CopyWithPrivateKey(key));
        var (csr2, key2) = TestPki.Request();
        using var __ = key2;
        var renewal = await SendAsync("POST", "/.well-known/est/https/simplereenroll", renewalIdentity, Body(csr2));
        Assert.Equal(200, renewal.Status);
        Assert.Equal(EnrollmentKind.Renewal, _service.Store.Enrollments()[0].Kind);
        Assert.Equal(2, _service.Store.CertificateCount);

        // The camera retries with its first certificate (answers got lost), twice: its record is gone by then, it still renews.
        var retry = await SendAsync("POST", "/.well-known/est/https/simplereenroll", renewalIdentity, Body(csr2));
        Assert.Equal(200, retry.Status);
        Assert.Null(_service.Store.FindCertificate(issued.SerialNumber));
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simplereenroll", renewalIdentity, Body(csr2))).Status);
        Assert.Equal(2, _service.Store.CertificateCount);
        Assert.Single(_service.Store.Current());
    }

    [Fact]
    public async Task A_revoked_certificate_renews_nothing_and_the_refusal_is_listed()
    {
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;
        var enrolled = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(200, enrolled.Status);
        Assert.Equal(DeviceCertificatePurposes.Https, Assert.Single(DeviceRequests()).Purpose); // the use for PKI's list
        using var issued = X509CertificateLoader.LoadCertificate(EstMessages.ReadCertsOnly(Convert.FromBase64String(enrolled.Body))[0]);
        using var identity = TestPki.Usable(issued.CopyWithPrivateKey(key));
        _ca.Revoke(issued.SerialNumber);
        var (csr2, key2) = TestPki.Request();
        using var __ = key2;

        var renewal = await SendAsync("POST", "/.well-known/est/https/simplereenroll", identity, Body(csr2));

        Assert.Equal(403, renewal.Status);
        Assert.Single(DeviceRequests()); // nothing signed
        var refused = _service.Store.Enrollments()[0];
        Assert.Equal((false, EnrollmentKind.Renewal, "Certificate revoked on 2026-11-02 (Key compromised)"), (refused.Ok, refused.Kind, refused.Reason));
        Assert.StartsWith(EstRefusals.Revoked, refused.Reason, StringComparison.Ordinal);

        // The camera's own Axis device ID is never on the list: Set up gives it a new certificate.
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr2))).Status);
    }

    [Fact]
    public async Task A_certificate_of_the_pki_pages_list_never_renews_and_never_enrolls_whatever_it_names()
    {
        // A certificate of the OADM CA with the camera's axis-<serial>.local name, but one PKI lists as created or signed on
        // its page (not a device's certificate): it signs in as no camera (fail closed), and nothing is signed.
        var (csr, key) = TestPki.Request("CN=whatever");
        using var _ = key;
        var enrolled = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(200, enrolled.Status);
        using var issued = X509CertificateLoader.LoadCertificate(EstMessages.ReadCertsOnly(Convert.FromBase64String(enrolled.Body))[0]);
        Assert.Contains("axis-" + Serial.ToLowerInvariant() + ".local", issued.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        using var identity = TestPki.Usable(issued.CopyWithPrivateKey(key));
        _ca.NotDevice.Add(CertificateSerials.Normalize(issued.SerialNumber));
        var (csr2, key2) = TestPki.Request();
        using var __ = key2;

        foreach (var operation in new[] { "simplereenroll", "simpleenroll" })
        {
            var refused = await SendAsync("POST", "/.well-known/est/https/" + operation, identity, Body(csr2));
            Assert.Equal((401, EstRefusals.NotDeviceId), (refused.Status, refused.Body.Trim()));
        }

        Assert.Single(DeviceRequests()); // only the first enrollment
    }

    [Fact]
    public async Task Only_managed_cameras_with_a_device_id_get_certificates()
    {
        var (csr, key) = TestPki.Request();
        using var _ = key;
        using var stranger = TestPki.DeviceId(_intermediate, "ACCC8E000001");
        using var foreignRoot = TestPki.Root("Not Axis");
        using var forged = TestPki.DeviceId(foreignRoot, Serial);

        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", null, Body(csr))).Status);
        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", forged, Body(csr))).Status);
        var notManaged = await SendAsync("POST", "/.well-known/est/https/simpleenroll", stranger, Body(csr));
        Assert.Equal((403, EstRefusals.NotManaged), (notManaged.Status, notManaged.Body.Trim()));
        Assert.Empty(DeviceRequests());
        Assert.Equal("ACCC8E000001", Assert.Single(_service.Store.Enrollments()).Device); // the 401s are not listed
        Assert.Empty(_publisher.Fired); // refusals of unknown cameras are not device events
    }

    [Fact]
    public async Task A_camera_at_a_site_enrolls_through_the_site_relay_with_the_sites_own_certificate()
    {
        var site = _sites.Add("Building B");
        site.AllowedPorts.Add((SiteProtocol.Tcp, EstPluginInfo.DefaultPort));
        _devices.List[0] = new FakeDevice { Serial = Serial, Address = "192.168.5.20", SiteId = site.Id };

        var relay = await _service.ServeSiteAsync(site.Id, "192.168.5.2", CancellationToken.None);

        Assert.NotNull(relay);
        Assert.Equal((RelayState.Running, (Guid?)site.Id, EstPluginInfo.DefaultPort), (relay.State, relay.SiteId, relay.Port));
        var (csr, key) = TestPki.Request();
        using var _ = key;
        X509Certificate2? server = null;
        var response = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr), s => server = s, relay);
        Assert.Equal(200, response.Status);
        var names = server!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal([IPAddress.Parse("192.168.5.2")], names.EnumerateIPAddresses()); // the node's address, not the server's
        Assert.Equal("Building B", Assert.Single(_service.GetState().Listeners, l => l.SiteId == site.Id).Name);

        // Through the server's own listener the same camera is refused: it belongs to the site.
        Assert.Equal(403, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);

        // The site leaves the bound ones (its node was reset, or it was removed): its relay is released, its addresses stay
        // while a managed device is still there (a replaced node keeps the site and its cameras).
        _sites.Remove(site.Id);
        await Eventually.TrueAsync(() => ((InMemoryRelay)relay).IsReleased, "the relay of the removed site is released");
        await _service.CheckRenewalsAsync(CancellationToken.None);
        Assert.Single(_service.Store.Sites());

        // Removed for good: no managed device there any more.
        _devices.List.RemoveAll(d => d.SiteId == site.Id);
        await _service.CheckRenewalsAsync(CancellationToken.None);
        Assert.Empty(_service.Store.Sites());
    }

    [Fact]
    public async Task A_site_whose_node_has_not_allowed_the_port_says_so_on_the_page()
    {
        var site = _sites.Add("Warehouse");

        var relay = await _service.ServeSiteAsync(site.Id, "192.168.9.2", CancellationToken.None);

        Assert.Equal(RelayState.WaitingForApproval, relay!.State);
        var listener = Assert.Single(_service.GetState().Listeners, l => l.SiteId == site.Id);
        Assert.False(listener.Ok);
        Assert.Contains("waiting for approval in the OADM Site Node app", listener.Text, StringComparison.Ordinal);
        Assert.Equal("Running on port 9454", _service.GetState().StatusText); // this server's own listener still runs
    }

    [Theory]
    [InlineData("cam-entrance", true)]
    [InlineData("cam.site.example", true)]
    [InlineData("*.corp.example", false)]
    [InlineData("cam entrance", false)]
    [InlineData("-cam", false)]
    [InlineData("10.0.0.48", false)]
    [InlineData("", false)]
    public void Only_plain_host_names_go_into_a_certificate(string name, bool plain)
    {
        Assert.Equal(plain, EstEndpoint.IsPlainHostName(name));
        var names = EstEndpoint.DnsNames(new FakeDevice { Serial = Serial, Address = "10.0.0.48", HostName = name }, inAnyGroup: false);
        Assert.Equal(plain, names.Contains(name));
        Assert.Contains("axis-b8a44f631339.local", names);
    }

    [Fact]
    public async Task A_camera_at_a_site_enrolls_only_through_its_site()
    {
        _devices.List[0] = new FakeDevice { Serial = Serial, Address = "10.0.0.48", SiteId = Guid.NewGuid() };
        var (csr, key) = TestPki.Request();
        using var _ = key;

        var response = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));

        Assert.Equal((403, EstRefusals.OtherSite), (response.Status, response.Body.Trim()));
        Assert.Equal(EstPluginInfo.RefusedEvent, Assert.Single(_publisher.Fired).Type);
    }

    [Fact]
    public async Task A_certificate_naming_two_cameras_or_not_issued_by_the_oadm_ca_itself_signs_in_as_nobody()
    {
        var (csr, key) = TestPki.Request();
        using var _ = key;

        // A camera that reported another camera's axis name as its host name: its certificate names two serials.
        using var twoNames = Leaf(_ca.Certificate, "axis-accc8e000099.local", "axis-" + Serial.ToLowerInvariant() + ".local");
        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simplereenroll", twoNames, Body(csr))).Status);

        // Issued by another CA below the OADM CA's root, not by the OADM CA itself.
        using var other = TestPki.Intermediate(_ca.Certificate, "Corporate issuing CA");
        using var notDirect = Leaf(other, "axis-" + Serial.ToLowerInvariant() + ".local");
        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simplereenroll", notDirect, Body(csr))).Status);

        // The OADM CA's own certificate for this camera, without a record (e.g. from PKI): renews.
        using var own = Leaf(_ca.Certificate, "cam-entrance", "axis-" + Serial.ToLowerInvariant() + ".local");
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simplereenroll", own, Body(csr))).Status);
        Assert.DoesNotContain(DeviceRequests(), r => r.DnsNames.Contains("axis-accc8e000099.local"));
    }

    [Fact]
    public async Task A_camera_a_group_user_added_gets_no_host_name_and_its_enrollment_reaches_only_pages_that_see_it()
    {
        var device = new FakeDevice { Serial = Serial, Address = "10.0.0.48", HostName = "cam-entrance", AddedByAllDevices = false };
        _devices.List[0] = device;
        _events.DeviceList.Add(device);
        var (csr, key) = TestPki.Request("CN=cam-entrance");
        using var _ = key;

        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);

        var request = Assert.Single(DeviceRequests());
        Assert.Equal("10.0.0.48", request.CommonName);
        Assert.Equal(["10.0.0.48"], request.IpAddresses);
        Assert.Equal(["axis-b8a44f631339.local"], request.DnsNames); // no cam-entrance
        bool Sees(Oadm.Sdk.Plugins.ICallContext watcher) => _events.EventsSeenBy(watcher).Any(e => e.Topic == EstMethods.EnrollmentsTopic);
        Assert.True(Sees(_events.CallAsGroup([device.Id])));
        Assert.True(Sees(_events.CallAsAllDevices()));
        Assert.False(Sees(_events.CallAsGroup([Guid.NewGuid()])));
    }

    [Fact]
    public void Another_cameras_axis_name_reported_as_host_name_never_goes_into_a_certificate()
    {
        var device = new FakeDevice { Serial = Serial, Address = "axis-accc8e000099.local", HostName = "axis-accc8e000099" };

        Assert.Equal(["axis-" + Serial.ToLowerInvariant() + ".local"], EstEndpoint.DnsNames(device, inAnyGroup: false));
        Assert.True(Oadm.Plugins.Pki.Ca.CertificateNames.NamesOtherCamera("axis-ACCC8E000099.local", Serial));
        Assert.False(Oadm.Plugins.Pki.Ca.CertificateNames.NamesOtherCamera("axis-" + Serial + ".local", Serial));
        Assert.False(Oadm.Plugins.Pki.Ca.CertificateNames.NamesOtherCamera("axis-entrance", Serial));
    }

    /// <summary>A certificate signed by <paramref name="issuer"/> with these DNS names, with its key.</summary>
    private static X509Certificate2 Leaf(X509Certificate2 issuer, params string[] names)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=camera", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        return TestPki.Usable(signed.CopyWithPrivateKey(key));
    }

    [Fact]
    public async Task Unknown_paths_and_profiles_look_the_same_and_without_pro_or_ca_nothing_is_issued()
    {
        Assert.Equal(404, (await SendAsync("GET", "/.well-known/est/nope/cacerts", null)).Status);
        Assert.Equal(404, (await SendAsync("GET", "/.well-known/est/https/csrattrs", null)).Status);
        Assert.Equal(404, (await SendAsync("GET", "/index.html", null)).Status);

        _pro = false;
        var noPro = await SendAsync("GET", "/.well-known/est/https/cacerts", null);
        Assert.Equal((503, EstRefusals.NotPro), (noPro.Status, noPro.Body.Trim()));
        Assert.Equal(EstRefusals.NotPro, _service.GetState().StatusText);
    }

    [Fact]
    public async Task Sign_in_ca_policies_let_cameras_in_only_through_their_cas_for_new_certificates_or_renewals()
    {
        _ca.ListDefaults();
        var (csr, key) = TestPki.Request();
        using var _ = key;
        var axisName = "axis-" + Serial.ToLowerInvariant() + ".local";
        using var own = Leaf(_ca.Certificate, axisName);
        using var previousCa = TestPki.Root("OADM CA 2025");
        using var previousLeaf = Leaf(previousCa, axisName);
        async Task<int> StatusAsync(X509Certificate2 identity, string operation = "simplereenroll") =>
            (await SendAsync("POST", "/.well-known/est/https/" + operation, identity, Body(csr))).Status;
        var signIn = _policies!.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa);
        var axis = signIn.Single(p => p.CaId == CertificateAuthorityInfo.AxisDeviceIdId);
        var oadm = signIn.Single(p => p.CaId == CertificateAuthorityInfo.OadmId);

        // The defaults keep today's behaviour: the Axis device ID for new certificates (and renewals), the OADM CA for renewals.
        Assert.Equal(("Axis device ID", "new", "OADM CA", "renewal"), (axis.Name, axis.SignInFor, oadm.Name, oadm.SignInFor));
        Assert.Equal(200, await StatusAsync(_deviceId, "simpleenroll"));
        Assert.Equal(200, await StatusAsync(_deviceId));
        Assert.Equal(200, await StatusAsync(own));
        Assert.Equal(401, await StatusAsync(own, "simpleenroll")); // the OADM CA only renews
        _ca.AuthorityList.Add(new("oadm-previous:AB", "OADM CA 2025", CertificateAuthorityKind.PreviousOadm, [previousCa.RawData], previousCa.NotAfter.ToUniversalTime(), false));
        Assert.Equal(401, await StatusAsync(previousLeaf)); // no policy names it

        // A previous OADM CA for renewals; the Axis device ID for renewals only.
        await TestPolicies.AddAsync(_policies, new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "OADM CA 2025", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa, CaId = "oadm-previous:AB", SignInFor = "renewal",
        });
        Assert.Equal(200, await StatusAsync(previousLeaf));
        await TestPolicies.AddAsync(_policies, axis with { SignInFor = "renewal" });
        Assert.Equal(401, await StatusAsync(_deviceId, "simpleenroll"));
        Assert.Equal(200, await StatusAsync(_deviceId));

        // A CA that left PKI's list lets nothing in any more (fail closed).
        _ca.AuthorityList.RemoveAll(a => a.Kind == CertificateAuthorityKind.PreviousOadm);
        Assert.Equal(401, await StatusAsync(previousLeaf));

        // The same CA twice, a CA that never signs a camera in: refused on save.
        var twice = await _policies.SaveAsync(new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "Again", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa, CaId = CertificateAuthorityInfo.OadmId, SignInFor = "new",
        }, "admin", Oadm.Plugins.Pki.PkiService.CipherSuites, CancellationToken.None);
        Assert.Equal(("caId", "Another Sign-in CA policy has this CA."), (twice.Error?.Field, twice.Error?.Message));
        var imported = await _policies.SaveAsync(new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "Contoso", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa, CaId = "imported:AA", SignInFor = "new",
        }, "admin", Oadm.Plugins.Pki.PkiService.CipherSuites, CancellationToken.None);
        Assert.Equal(Oadm.Plugins.Pki.Policies.PolicyTexts.NoSignInCa, imported.Error?.Message);

        // No Sign-in CA policy: nothing signs in (fail closed), and the page says why.
        foreach (var policy in _policies.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa))
        {
            Assert.Null((await _policies.DeleteAsync(policy.Id, "admin", CancellationToken.None)).Error);
        }

        Assert.Equal(401, await StatusAsync(_deviceId, "simpleenroll"));
        Assert.Equal(401, await StatusAsync(own));
        Assert.Equal(EstTexts.NoSignInPolicy, _service.GetState().PolicyProblem);
        Assert.Equal(1, _service.GetState().PoliciesApply);
    }

    [Fact]
    public async Task Without_pki_policies_nothing_signs_in()
    {
        var (csr, key) = TestPki.Request();
        using var _ = key;

        _policies = null; // no PKI store (a PKI that started again offers a new one)
        var none = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal(401, none.Status);
        Assert.Equal(Oadm.Plugins.Pki.Policies.PolicyTexts.NotRunning, _service.GetState().PolicyProblem);

        var damaged = new FakeSettings();
        await damaged.SetAsync(Oadm.Plugins.Pki.Policies.PolicyStore.SettingsKey, "{\"policies\":3}", CancellationToken.None);
        _policies = await TestPolicies.StoreAsync(settings: damaged);
        Assert.Equal(401, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);
        Assert.Equal(Oadm.Plugins.Pki.Policies.PolicyTexts.Unreadable, _service.GetState().PolicyProblem);
        Assert.Empty(DeviceRequests());
    }

    [Fact]
    public async Task A_device_no_enrollment_policy_holds_is_refused_and_a_profile_not_allowed_answers_like_an_unknown_one()
    {
        var (csr, key) = TestPki.Request();
        using var _ = key;
        var store = _policies!;
        var all = store.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment).Single();
        Assert.Equal(("All devices", "all", true), (all.Name, all.ProfileScope, all.Devices!.AllDevices));
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr))).Status);

        // Only chosen devices: a camera none holds is refused (403) and listed.
        var other = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.99" };
        _devices.List.Add(other);
        await TestPolicies.AddAsync(store, all with { Name = "Others", Devices = new Oadm.Plugins.Pki.Policies.PolicyDevices { Serials = [other.Serial] } });
        var refused = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal((403, EstRefusals.NoEnrollmentPolicy), (refused.Status, refused.Body.Trim()));
        Assert.Equal((false, EstRefusals.NoEnrollmentPolicy), (_service.Store.Enrollments()[0].Ok, _service.Store.Enrollments()[0].Reason));
        Assert.Contains(_publisher.Fired, f => f.Type == EstPluginInfo.RefusedEvent);

        // A policy for the camera that allows dot1x only: https answers like an unknown profile (404), with the reason in the list.
        await TestPolicies.AddAsync(store, new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "Entrance", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment, ProfileScope = "chosen", Profiles = ["dot1x"],
            Devices = new Oadm.Plugins.Pki.Policies.PolicyDevices { Serials = [Serial] },
        });
        var unknown = await SendAsync("POST", "/.well-known/est/nosuch/simpleenroll", _deviceId, Body(csr));
        var notAllowed = await SendAsync("POST", "/.well-known/est/https/simpleenroll", _deviceId, Body(csr));
        Assert.Equal((unknown.Status, unknown.Body), (notAllowed.Status, notAllowed.Body)); // no probing: the same answer
        Assert.Equal(404, notAllowed.Status);
        Assert.Equal("The enrollment policy Entrance does not allow the profile https.", _service.Store.Enrollments()[0].Reason);
        Assert.Equal(200, (await SendAsync("POST", "/.well-known/est/dot1x/simpleenroll", _deviceId, Body(csr))).Status);

        // No enrollment for the camera: every profile, also renewals.
        var entrance = store.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment).Single(p => p.Name == "Entrance");
        await TestPolicies.AddAsync(store, entrance with { ProfileScope = "none", Profiles = null });
        Assert.Equal(404, (await SendAsync("POST", "/.well-known/est/dot1x/simplereenroll", _deviceId, Body(csr))).Status);
        Assert.Equal("The enrollment policy Entrance allows no enrollment.", _service.Store.Enrollments()[0].Reason);

        // A profile the EST server does not have is refused on save; the list says how many apply.
        store.ProfileNames = token => EstPolicies.ProfileNamesAsync(_settings, token);
        var bad = await store.SaveAsync(entrance with { Profiles = ["web"] }, "admin", Oadm.Plugins.Pki.PkiService.CipherSuites, CancellationToken.None);
        Assert.Equal(("profiles", "The EST server has no profile web."), (bad.Error?.Field, bad.Error?.Message));
        Assert.Equal(4, _service.GetState().PoliciesApply);
    }

    [Fact]
    public async Task Est_tells_pki_which_cas_it_uses_and_clears_it_when_it_stops()
    {
        // At the start (a PKI without the list): the CAs of the Sign-in CA policies.
        Assert.Equal("EST server", _ca.UsedBy[EstPluginInfo.PluginId].Name);
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId, CertificateAuthorityInfo.OadmId], _ca.UsedBy[EstPluginInfo.PluginId].Ids.Order(StringComparer.Ordinal));

        // Only the Axis device ID signs in: the OADM CA still counts, EST signs with it.
        _ca.ListDefaults();
        var oadm = _policies!.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa).Single(p => p.CaId == CertificateAuthorityInfo.OadmId);
        Assert.Null((await _policies.DeleteAsync(oadm.Id, "admin", CancellationToken.None)).Error);
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId, CertificateAuthorityInfo.OadmId], _ca.UsedBy[EstPluginInfo.PluginId].Ids.Order(StringComparer.Ordinal));

        // A CA PKI no longer holds is not reported; PKI's Changed reports again.
        _ca.AuthorityList.Add(new("oadm-previous:AB", "OADM CA 2025", CertificateAuthorityKind.PreviousOadm, [_root.RawData], DateTime.UtcNow.AddYears(1), false));
        await TestPolicies.AddAsync(_policies, new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "OADM CA 2025", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.SignInCa, CaId = "oadm-previous:AB", SignInFor = "renewal",
        });
        Assert.Contains("oadm-previous:AB", _ca.UsedBy[EstPluginInfo.PluginId].Ids);
        _ca.AuthorityList.RemoveAll(a => a.Kind == CertificateAuthorityKind.PreviousOadm);
        _ca.RaiseChanged();
        Assert.DoesNotContain("oadm-previous:AB", _ca.UsedBy[EstPluginInfo.PluginId].Ids);

        await _service.DisposeAsync();
        Assert.False(_ca.UsedBy.ContainsKey(EstPluginInfo.PluginId));
    }

    [Fact]
    public async Task Settings_are_checked_and_a_new_port_opens_a_new_listener()
    {
        _policies = null; // no PKI: the profiles' own validity counts (the profile checks come first)
        var bad = await _service.SaveAsync(new SaveRequest(new EstConfig { Port = 80 }), CancellationToken.None);
        Assert.Equal("port", bad.Field);
        var twice = await _service.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new("a", ProfileUse.Https), new("b", ProfileUse.Https)] }), CancellationToken.None);
        Assert.Equal(("profiles[1].use", "Another profile has this use."), (twice.Field, twice.Error));
        var name = await _service.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new("a b", ProfileUse.Https)] }), CancellationToken.None);
        Assert.Equal("profiles[0].name", name.Field);

        var saved = await _service.SaveAsync(new SaveRequest(new EstConfig { Port = 9455, Profiles = [new("web", ProfileUse.Https, 90)] }), CancellationToken.None);
        Assert.Null(saved.Error);
        Assert.Equal(2, _network.Listeners.Count);
        Assert.True(_network.Listeners.First().IsReleased);
        Assert.Equal(9455, _service.Listener!.Port);
        Assert.Contains("\"port\":9455", _settings.Values[EstStore.ConfigKey], StringComparison.Ordinal);
        Assert.Equal(404, (await SendAsync("GET", "/.well-known/est/https/cacerts", null)).Status);
        Assert.Equal(200, (await SendAsync("GET", "/.well-known/est/web/cacerts", null)).Status);
    }

    /// <summary>The requests for cameras (the server's own TLS certificate has no device).</summary>
    private List<DeviceCertificateRequest> DeviceRequests() => [.. _ca.Requests.Where(r => r.DeviceId is not null)];

    private static byte[] Body(byte[] csr) => Encoding.ASCII.GetBytes(Convert.ToBase64String(csr, Base64FormattingOptions.InsertLineBreaks));

    /// <summary>One request over TLS like a camera's EST client; the server certificate must chain to the OADM CA.</summary>
    private async Task<Response> SendAsync(string method, string path, X509Certificate2? clientCertificate, byte[]? body = null, Action<X509Certificate2>? onServer = null,
        ITcpRelay? via = null)
    {
        var relay = (InMemoryTcpRelay)(via ?? _service.Listener!);
        var stream = relay.Connect(new IPEndPoint(IPAddress.Parse("10.0.0.48"), 40000));
        await using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "10.0.0.17",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_ca.Certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                var server = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
                onServer?.Invoke(server);
                return chain.Build(server);
            },
        };
        if (clientCertificate is not null)
        {
            options.ClientCertificateContext = SslStreamCertificateContext.Create(clientCertificate, [X509CertificateLoader.LoadCertificate(_intermediate.RawData)], offline: true);
        }

        await tls.AuthenticateAsClientAsync(options);
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: 10.0.0.17:9454\r\nConnection: close\r\n");
        if (body is not null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: application/pkcs10\r\nContent-Transfer-Encoding: base64\r\nContent-Length: {body.Length}\r\n");
        }

        head.Append("\r\n");
        await tls.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (body is not null)
        {
            await tls.WriteAsync(body);
        }

        await tls.FlushAsync();
        using var answer = new MemoryStream();
        await tls.CopyToAsync(answer);
        var text = Encoding.ASCII.GetString(answer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var lines = text[..split].Split("\r\n");
        var headers = lines.Skip(1).Select(l => l.Split(": ", 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return new Response(int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), headers, text[(split + 4)..]);
    }

    private sealed record Response(int Status, Dictionary<string, string> Headers, string Body);

    internal sealed class Devices : IDeviceRepository
    {
        public List<IDeviceInfo> List { get; } = [];

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. List]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(List.Find(d => d.Id == id));
    }

    internal sealed class FakePublisher : Sdk.Events.IEventPublisher
    {
        public List<(string Type, Guid? Device)> Fired { get; } = [];

        public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null)
        {
            lock (Fired)
            {
                Fired.Add((typeId, deviceId));
            }
        }

        public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null)
        {
        }

        public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices)
        {
        }

        public bool IsWatched(string typeId) => true;
    }
}
