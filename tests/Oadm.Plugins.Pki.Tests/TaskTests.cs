using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Parsers and request bodies against the recorded 10.0.0.48 answers (tests/Oadm.Plugins.Pki.Tests/Fixtures).</summary>
public sealed class DeviceApiTests
{
    [Fact]
    public void Web_server_tls_configuration_of_the_fixture()
    {
        var configuration = WebServerTls.Parse(PkiFixture.Read(PkiFixture.WebServer));

        Assert.True(configuration.Tls);
        Assert.Equal(ConnectionPolicy.HttpAndHttps, configuration.Policy);
        Assert.Equal(6, configuration.Ciphers.Count);
        Assert.Equal("ECDHE-ECDSA-AES128-GCM-SHA256", configuration.Ciphers[0]);
        Assert.Equal("Site Device HTTPS lUZuIXuu", configuration.CertificateAlias);
        Assert.Empty(configuration.CaCertificates);
    }

    [Fact]
    public void Set_request_sends_the_ciphers_back_and_escapes_aliases()
    {
        var configuration = WebServerTls.Parse(PkiFixture.Read(PkiFixture.WebServer)) with { Certificates = ["OADM <HTTPS> & co"], Policy = ConnectionPolicy.Http };
        var body = WebServerTls.BuildSet(configuration);

        Assert.StartsWith("<aweb:SetWebServerTlsConfiguration", body, StringComparison.Ordinal);
        Assert.Contains("<aweb:Configuration name=\"WebServer\"><aweb:Tls>true</aweb:Tls>", body, StringComparison.Ordinal); // unqualified = ter:TagMismatch on 12.11
        Assert.Contains("<aweb:Admin>Http</aweb:Admin>", body, StringComparison.Ordinal);
        Assert.Contains("<acert:Id>OADM &lt;HTTPS&gt; &amp; co</acert:Id>", body, StringComparison.Ordinal);
        Assert.Contains("<acert:Cipher>ECDHE-RSA-CHACHA20-POLY1305</acert:Cipher>", body, StringComparison.Ordinal);
        Assert.Contains("<acert:CACertificates/>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Soap_fault_becomes_the_device_text()
    {
        var ex = Assert.Throws<PkiDeviceException>(() => WebServerTls.Parse(PkiFixture.Read(PkiFixture.Fault)));
        Assert.Equal("Read web server settings: the device refused: Optional action not implemented (ter:ActionNotSupported)", ex.Message);
    }

    [Fact]
    public void Network_info_names_addresses_and_8021x()
    {
        var info = NetworkInfoApi.Parse(PkiFixture.Read(PkiFixture.NetworkInfo));

        Assert.Equal("axis-b8a44f631339", info.HostName);
        Assert.Null(info.Fqdn); // empty domain name
        Assert.Equal(["10.0.0.48"], info.Addresses);
        var dot1x = info.Dot1x!;
        Assert.Equal("eth0", dot1x.DeviceName);
        Assert.False(dot1x.Enabled);
        Assert.Equal("Stopped", dot1x.Status);
        Assert.Equal("Axis device ID ECC-P256 (802.1AR)", dot1x.CertClient);
        Assert.Equal("48", dot1x.Identity);
        Assert.Equal("EAPoLv3", dot1x.EapolVersion);
        Assert.Empty(dot1x.CertsCa);
    }

    [Fact]
    public void Discover_lists_cert_v1_and_lists_parse()
    {
        var support = CertApi.ParseDiscover(PkiFixture.Read(PkiFixture.Discover));
        Assert.Equal("1.1.2", support.Version);
        Assert.True(support.IsSupported);
        Assert.False(CertApi.ParseDiscover("{\"apis\":{}}").IsSupported);
        Assert.False(new CertApiSupport("1.0", "beta").IsSupported);
        Assert.False(new CertApiSupport("2.0", "released").IsSupported);

        var certificates = CertApi.ParseList(System.Text.Json.Nodes.JsonNode.Parse(PkiFixture.Read(PkiFixture.Certificates))!["data"]);
        Assert.Equal(4, certificates.Count);
        Assert.Contains(certificates, c => c.Alias == "Site Device HTTPS lUZuIXuu" && c.Keystore == "SE0");
        Assert.Equal("OADM%20HTTPS%20%28x%29%2Fy", CertApi.Escape("OADM HTTPS (x)/y"));
    }

    [Fact]
    public void Inventory_of_the_fixture_groups_and_marks_what_is_in_use()
    {
        var certificates = CertApi.ParseList(System.Text.Json.Nodes.JsonNode.Parse(PkiFixture.Read(PkiFixture.Certificates))!["data"]);
        var cas = CertApi.ParseList(System.Text.Json.Nodes.JsonNode.Parse(PkiFixture.Read(PkiFixture.CaCertificates))!["data"]);
        var list = CertificateInventory.Describe(
            certificates, cas, WebServerTls.Parse(PkiFixture.Read(PkiFixture.WebServer)), NetworkInfoApi.Parse(PkiFixture.Read(PkiFixture.NetworkInfo)).Dot1x, [], null);

        var https = list.Single(c => c.Alias == "Site Device HTTPS lUZuIXuu");
        Assert.Equal(CertificateKind.Server, https.Kind);
        Assert.Equal(["HTTPS"], https.InUse);
        Assert.Equal("Example Root CA", https.IssuedBy);
        Assert.False(https.FromOadm);
        Assert.False(https.Factory);

        var deviceId = list.Single(c => c.Alias == "Axis device ID ECC-P256 (802.1AR)");
        Assert.Equal(CertificateKind.Client, deviceId.Kind);
        Assert.True(deviceId.Factory);
        Assert.Equal(["802.1X"], deviceId.InUse); // configured client certificate (802.1X off)
        Assert.Equal(cas.Count, list.Count(c => c.Kind == CertificateKind.Ca));
        Assert.True(cas.Count > 30);
    }
}

/// <summary>A started PKI plugin, a fake 10.0.0.48 and a task context with the engine's step semantics.</summary>
internal sealed class TaskHarness : IAsyncDisposable
{
    private TaskHarness(PkiHarness pki, FakeTimeProvider time)
    {
        Pki = pki;
        Time = time;
        Camera = new FakeCamera();
        Device = new PkiFakeDevice(Guid.NewGuid());
    }

    public PkiHarness Pki { get; }

    public FakeTimeProvider Time { get; }

    public FakeCamera Camera { get; }

    public PkiFakeDevice Device { get; set; }

    public PkiTaskContext Context { get; set; } = null!;

    public static async Task<TaskHarness> StartAsync()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pki = await PkiHarness.StartAsync(time: time);
        PkiCompatibility.ClearCache();
        return new TaskHarness(pki, time);
    }

    public T Task<T>(string id)
        where T : ITaskPlugin => (T)Pki.Plugin.TaskPlugins.Single(t => t.Id == id);

    /// <summary>Runs the task on the fake like the engine (fresh step list); returns the exception, if any.</summary>
    public async Task<Exception?> RunAsync(string id, string? payload = null)
    {
        Time.Advance(TimeSpan.FromSeconds(2)); // a new alias second per run
        Context = new PkiTaskContext(Camera);
        var plugin = Pki.Plugin.TaskPlugins.Single(t => t.Id == id);
        try
        {
            await StepRun.RunAsync(Context.Steps, () => plugin.ExecuteAsync(Context, Device, payload, CancellationToken.None));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public string[] Lines => StepRun.Lines(Context.Steps);

    public string? Detail(string step) => StepRun.Detail(Context.Steps, step);

    public X509Certificate2 Served(string alias) => X509Certificate2.CreateFromPem(Camera.CertificatePem(alias));

    public ValueTask DisposeAsync() => Pki.DisposeAsync();
}

public sealed class HttpsTaskTests
{
    [Fact]
    public async Task Enable_issues_a_certificate_on_the_device_key_switches_the_web_server_and_follows_it()
    {
        await using var h = await TaskHarness.StartAsync();

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal(
        [
            "Check compatibility: Done", "Check device clock: Done", "Read web server settings: Done", "Read network settings: Done", "Install CA certificate: Done",
            "Create key on the device: Done", "Get certificate request: Done", "Sign certificate: Done", "Install certificate: Done",
            "Switch web server to the new certificate: Done", "Verify HTTPS: Done", "Remove previous OADM certificate: Skipped", "Completed: Done",
        ], h.Lines);
        var alias = h.Camera.WebServerAlias;
        Assert.StartsWith("OADM HTTPS ", alias, StringComparison.Ordinal);
        Assert.Equal(ConnectionPolicy.HttpAndHttps, h.Camera.Policy); // policy unchanged
        Assert.Contains("Site Device HTTPS lUZuIXuu", h.Camera.CertificateAliases); // never deletes what OADM did not issue

        // The certificate: OADM's subject and SAN, serverAuth, issued by the CA, served and pinned by OADM.
        using var served = h.Served(alias);
        var ca = h.Pki.Service.Ca!;
        Assert.Equal("CN=10.0.0.48", served.Subject);
        Assert.Equal(ca.Certificate.Subject, served.Issuer);
        var san = served.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal([IPAddress.Parse("10.0.0.48")], san.EnumerateIPAddresses());
        Assert.Equal(["axis-b8a44f631339", "axis-b8a44f631339.local"], san.EnumerateDnsNames());
        Assert.Contains(served.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == DeviceCertificateIssuer.ServerAuthOid);
        // The default policy "HTTPS" of a new server: ECC P-256 on the device, 365 days.
        Assert.Equal(["EC-P256"], h.Camera.KeyTypesCreated);
        Assert.Equal(256, served.GetECDsaPublicKey()!.KeySize);
        Assert.True((served.NotAfter.ToUniversalTime() - h.Time.GetUtcNow().UtcDateTime).TotalDays is > 364 and < 366);
        Assert.Equal("https", h.Context.Scheme);
        Assert.Equal(CaCertificates.Fingerprint(served), h.Context.PinnedFingerprint);
        Assert.Contains(h.Camera.CaAliases, a => a == CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, ca.Id));

        // Registry entry, and the page counts it.
        var issued = Assert.Single(await h.Pki.Service.Issued.ForDeviceAsync(h.Device.Id, CancellationToken.None));
        Assert.Equal(alias, issued.Alias);
        Assert.Equal(CertificatePurpose.Https, issued.Purpose);
        Assert.Equal(1, (await h.Pki.StateAsync()).DevicesWithCurrentCa);

        // Update: a new key and certificate, the previous OADM certificate is removed, the CA is already installed.
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        Assert.Equal("Already installed", h.Detail(PkiSteps.InstallCa));
        Assert.Equal(alias, h.Detail(PkiSteps.RemovePrevious));
        Assert.DoesNotContain(alias, h.Camera.CertificateAliases);
        Assert.NotEqual(alias, h.Camera.WebServerAlias);

        // Verified: the new one is current, the replaced one is revoked by OADM (Superseded) and left the store.
        var current = Assert.Single(await h.Pki.Service.Issued.ForDeviceAsync(h.Device.Id, CancellationToken.None));
        Assert.NotEqual(alias, current.Alias);
        Assert.False(current.Pending);
        Assert.True(h.Pki.Service.Issued.IsCurrent(current.SerialNumber));
        var revocation = h.Pki.Service.Issued.FindRevocation(issued.SerialNumber)!;
        Assert.Equal((Oadm.Sdk.Certificates.RevocationReason.Superseded, IssuedRegistry.OadmUser), (revocation.Reason, revocation.UserName));
        Assert.Equal("Certificate revoked on " + revocation.RevokedUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " (Replaced by a newer certificate)", revocation.Describe());
        Assert.NotNull(await h.Pki.Service.Issued.GetDerAsync(current.SerialNumber, CancellationToken.None)); // kept for View certificate
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("is replaced by a new one", StringComparison.Ordinal));
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("previous certificate (serial " + issued.SerialNumber + ") was revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_turns_an_http_only_device_to_http_and_https()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.Policy = ConnectionPolicy.Http;

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal(ConnectionPolicy.HttpAndHttps, h.Camera.Policy);
        Assert.Equal("https", h.Context.Scheme);
    }

    [Fact]
    public async Task Old_firmware_and_a_missing_web_server_service_change_nothing()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.HasCertApi = false;
        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);
        Assert.Equal(CertApi.NeedsNewerFirmware, ex!.Message);
        Assert.Equal("Check compatibility: Failed", h.Lines[0]);
        Assert.Empty(h.Camera.Writes);

        PkiCompatibility.ClearCache();
        h.Camera.HasCertApi = true;
        h.Camera.HasWebServerService = false;
        ex = await h.RunAsync(PkiTaskIds.HttpsEnable);
        Assert.Contains("web server settings cannot be read", ex!.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Writes);
    }

    [Fact]
    public async Task A_failed_request_after_the_key_was_created_removes_the_unfinished_key()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.FailPath = "/get_csr";
        var before = h.Camera.CertificateAliases.ToList();

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);

        Assert.Contains("Get certificate request: the device refused", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("Get certificate request: Failed", h.Lines);
        Assert.Contains("Switch web server to the new certificate: Skipped", h.Lines);
        Assert.Equal(before.Order(), h.Camera.CertificateAliases.Order());
        Assert.Equal("Site Device HTTPS lUZuIXuu", h.Camera.WebServerAlias);
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("unfinished key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_usable_ca_nothing_is_sent()
    {
        await using var h = await TaskHarness.StartAsync();
        await h.Pki.Plugin.StopAsync(CancellationToken.None);

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);

        Assert.Equal("The PKI plugin is not running. Nothing was changed.", ex!.Message);
        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task Disable_sets_http_only_keeps_the_certificates_and_switches_oadm_to_http()
    {
        await using var h = await TaskHarness.StartAsync();
        var aliases = h.Camera.CertificateAliases.ToList();

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsDisable));

        Assert.Equal(["Check compatibility: Done", "Read web server settings: Done", "Set HTTP only: Done", "Verify: Done", "Completed: Done"], h.Lines);
        Assert.Equal(ConnectionPolicy.Http, h.Camera.Policy);
        Assert.Equal(aliases, h.Camera.CertificateAliases.ToList());
        Assert.Equal("http", h.Context.Scheme);
        Assert.Single(h.Camera.Writes);

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsDisable));
        Assert.Equal("Already HTTP only", h.Detail(PkiSteps.SetHttpOnly));
    }

    [Fact]
    public async Task Disable_refuses_a_device_that_accepts_only_basic_over_http()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.Parameters[HttpsDisableTask.AuthenticationPolicyParameter] = "basic";

        var ex = await h.RunAsync(PkiTaskIds.HttpsDisable);

        Assert.Contains("only Basic authentication over HTTP", ex!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Writes);
        Assert.Equal(ConnectionPolicy.HttpAndHttps, h.Camera.Policy);
    }
}

public sealed class Dot1xTaskTests
{
    [Fact]
    public async Task Enable_installs_the_ca_issues_a_client_certificate_and_configures_eap_tls()
    {
        await using var h = await TaskHarness.StartAsync();

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));

        Assert.Equal(
        [
            "Check compatibility: Done", "Check device clock: Done", "Install CA certificates: Done", "Create key on the device: Done",
            "Get certificate request: Done", "Sign certificate: Done", "Install certificate: Done", "Set 802.1X configuration: Done",
            "Verify 802.1X settings: Done", "Remove previous OADM certificate: Skipped", "Completed: Done",
        ], h.Lines);
        var p = h.Camera.EapTlsParams;
        var alias = p["certClient"]!.GetValue<string>();
        Assert.StartsWith("OADM 802.1X ", alias, StringComparison.Ordinal);
        Assert.True(h.Camera.Dot1x["enabled"]!.GetValue<bool>());
        Assert.Equal(NetworkInfoApi.EapTlsMode, h.Camera.Dot1x["mode"]!.GetValue<string>());
        Assert.Equal("10.0.0.48", p["identity"]!.GetValue<string>()); // a new installation: the IP address (decision 2026-10-25)
        Assert.Equal("EAPoLv3", p["eapolVersion"]!.GetValue<string>());
        var caAlias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, h.Pki.Service.Ca!.Id);
        Assert.Equal([caAlias], p["certsCA"]!.AsArray().Select(n => n!.GetValue<string>()));
        using var certificate = h.Served(alias);
        Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == DeviceCertificateIssuer.ClientAuthOid);
        Assert.Equal("Site Device HTTPS lUZuIXuu", h.Camera.WebServerAlias); // HTTPS untouched
    }

    [Fact]
    public async Task Custom_identity_eapol_and_a_radius_server_ca_of_the_list_from_the_policy()
    {
        await using var h = await TaskHarness.StartAsync();
        using var radius = TestCa.Root("Corp RADIUS CA");
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(radius.RawData), "radius.crt"))).Ok);
        var policy = h.Pki.Service.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        var saved = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy with
        {
            EapolVersion = 2, Identity = Dot1xIdentity.Custom, CustomIdentity = "cam-{serial}@{hostName}", RadiusCaId = "imported:" + CaCertificates.Fingerprint(radius),
        });
        Assert.Null(saved.Error);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));

        var p = h.Camera.EapTlsParams;
        Assert.Equal("cam-B8A44F631339@axis-b8a44f631339", p["identity"]!.GetValue<string>());
        Assert.Equal("EAPoLv2", p["eapolVersion"]!.GetValue<string>());
        var radiusAlias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, CaCertificates.Fingerprint(radius)); // a CA of the list (decision 2026-10-25)
        Assert.Equal([radiusAlias], p["certsCA"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains(radiusAlias, h.Camera.CaAliases);
        Assert.Contains(CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, h.Pki.Service.Ca!.Id), h.Camera.CaAliases); // chain of the client cert
    }

    [Fact]
    public async Task An_intermediate_ca_without_its_root_is_refused_before_any_request()
    {
        await using var h = await TaskHarness.StartAsync();
        using var root = TestCa.Root("Acme Root CA");
        using var intermediate = TestCa.Intermediate(root, "Acme Issuing CA");
        var import = await h.Pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(TestCa.Pfx("pw", intermediate)), "acme.pfx", "pw", null, null));
        Assert.True(import.Ok, import.Error);

        // The IEEE 802.1X certificate policy issues with it ("Issued by", decision 2026-10-25).
        var dot1x = h.Pki.Policies.FirstOf(Policies.PolicyKinds.Dot1xCertificate)!;
        var saved = await h.Pki.Policies.SaveAsync(dot1x with { CaId = PkiService.ImportedAuthorityId(Ca.CaCertificates.Fingerprint(intermediate)) }, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.Null(saved.Error);

        var ex = await h.RunAsync(PkiTaskIds.Dot1xEnable);

        Assert.Contains("The CA chain is incomplete: the root above Acme Issuing CA is missing", ex!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task Disable_turns_8021x_off_and_keeps_the_certificates()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var aliases = h.Camera.CertificateAliases.ToList();

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xDisable));

        Assert.Equal(["Check compatibility: Done", "Set 802.1X off: Done", "Verify: Done", "Completed: Done"], h.Lines);
        Assert.False(h.Camera.Dot1x["enabled"]!.GetValue<bool>());
        Assert.Equal(aliases, h.Camera.CertificateAliases.ToList());

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xDisable));
        Assert.Equal("Already off", h.Detail(PkiSteps.SetDot1xOff));
    }
}

/// <summary>
/// The tasks confirm a new certificate only after their verify step (docs/spec/pki.md "Current and pending"): then the
/// previous one is revoked; a failed install or verify never revokes the previous one, and an IEEE 802.1X certificate the
/// camera may use is never revoked as unused (device safety, no lock-out).
/// </summary>
public sealed class CertificateConfirmationTaskTests
{
    private static Oadm.Sdk.Certificates.RevocationReason? ReasonOf(TaskHarness h, string serial) => h.Pki.Service.Issued.FindRevocation(serial)?.Reason;

    private static IssuedCertificate CurrentOf(TaskHarness h, string purpose) =>
        h.Pki.Service.Issued.ForDevice(h.Device).Single(e => e.Purpose == purpose && h.Pki.Service.Issued.IsCurrent(e.SerialNumber));

    [Fact]
    public async Task A_failed_web_server_switch_revokes_the_new_certificate_and_keeps_the_previous_one()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        var previous = CurrentOf(h, CertificatePurpose.Https);
        h.Camera.FailWebServerSwitch = true;

        Assert.NotNull(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Contains("Switch web server to the new certificate: Failed", h.Lines);
        Assert.Equal(previous.SerialNumber, CurrentOf(h, CertificatePurpose.Https).SerialNumber); // never revoked when the install failed
        Assert.Null(ReasonOf(h, previous.SerialNumber));
        var unused = h.Pki.Service.Issued.Revocations.Single();
        Assert.Equal((Oadm.Sdk.Certificates.RevocationReason.Other, IssuedRegistry.OadmUser), (unused.Reason, unused.UserName));
        Assert.Single(h.Pki.Service.Issued.ForDevice(h.Device)); // the unused one left the store
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("is not in use and was revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verified_ieee_8021x_certificate_revokes_the_previous_one()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var previous = CurrentOf(h, CertificatePurpose.Dot1x);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));

        Assert.NotEqual(previous.SerialNumber, CurrentOf(h, CertificatePurpose.Dot1x).SerialNumber);
        Assert.Equal(Oadm.Sdk.Certificates.RevocationReason.Superseded, ReasonOf(h, previous.SerialNumber));
    }

    [Fact]
    public async Task An_ieee_8021x_certificate_the_camera_may_use_is_never_revoked_when_the_task_fails_after_configuring()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var previous = CurrentOf(h, CertificatePurpose.Dot1x);
        h.Camera.FailDot1xAnswer = true; // applied, but the answer is an error

        Assert.NotNull(await h.RunAsync(PkiTaskIds.Dot1xEnable));

        Assert.Contains("Set 802.1X configuration: Failed", h.Lines);
        Assert.Empty(h.Pki.Service.Issued.Revocations); // neither the new one (the camera may use it) nor the previous one
        var pending = h.Pki.Service.Issued.ForDevice(h.Device).Single(e => e.Pending);
        Assert.True(pending.Delivered);
        Assert.Equal(previous.SerialNumber, CurrentOf(h, CertificatePurpose.Dot1x).SerialNumber);
    }

    [Fact]
    public async Task An_ieee_8021x_certificate_the_camera_does_not_use_is_revoked_and_the_previous_one_stays()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var previous = CurrentOf(h, CertificatePurpose.Dot1x);
        h.Camera.KeepDot1xClient = true; // the read-back names the previous client certificate

        await h.RunAsync(PkiTaskIds.Dot1xEnable);

        Assert.Contains("Verify 802.1X settings: Warning", h.Lines);
        Assert.Equal(previous.SerialNumber, CurrentOf(h, CertificatePurpose.Dot1x).SerialNumber);
        Assert.Null(ReasonOf(h, previous.SerialNumber));
        Assert.Equal(Oadm.Sdk.Certificates.RevocationReason.Other, Assert.Single(h.Pki.Service.Issued.Revocations).Reason);
        Assert.DoesNotContain(h.Pki.Service.Issued.ForDevice(h.Device), e => e.Pending);
    }
}

public sealed class RenewDeleteInstallTests
{
    [Fact]
    public async Task Renew_skips_purposes_without_an_oadm_certificate()
    {
        await using var h = await TaskHarness.StartAsync();

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));

        Assert.Equal("No OADM HTTPS certificate on this device", h.Detail(RenewTask.RenewHttpsStep));
        Assert.Equal("No OADM 802.1X certificate on this device", h.Detail(RenewTask.RenewDot1xStep));
        Assert.Empty(h.Camera.Writes);
    }

    [Fact]
    public async Task Renew_replaces_both_oadm_certificates_and_removes_the_old_ones()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var oldHttps = h.Camera.WebServerAlias;
        var oldDot1x = h.Camera.EapTlsParams["certClient"]!.GetValue<string>();

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));

        Assert.Contains("HTTPS: Verify HTTPS: Done", h.Lines);
        Assert.Contains("IEEE 802.1X: Verify 802.1X settings: Done", h.Lines);
        Assert.Contains("Check device clock: Done", h.Lines);
        Assert.NotEqual(oldHttps, h.Camera.WebServerAlias);
        Assert.NotEqual(oldDot1x, h.Camera.EapTlsParams["certClient"]!.GetValue<string>());
        Assert.DoesNotContain(oldHttps, h.Camera.CertificateAliases);
        Assert.DoesNotContain(oldDot1x, h.Camera.CertificateAliases);
        // Only the two new ones stay in the store; the replaced ones are on the revocation list.
        var stored = await h.Pki.Service.Issued.ForDeviceAsync(h.Device.Id, CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.True(h.Pki.Service.Issued.IsCurrent(e.SerialNumber)));
        Assert.Equal(2, h.Pki.Service.Issued.Revocations.Count(r => r.Reason == Oadm.Sdk.Certificates.RevocationReason.Superseded));
        Assert.Equal("https", h.Context.Scheme);
    }

    [Fact]
    public async Task Delete_removes_chosen_certificates_and_refuses_used_or_factory_ones()
    {
        await using var h = await TaskHarness.StartAsync();
        var task = h.Task<DeleteCertificatesTask>(PkiTaskIds.Delete);
        h.Camera.AddCertificate("old one", h.Camera.CertificatePem("Site Device HTTPS lUZuIXuu"));

        string Payload(params CertificateRef[] refs) =>
            PkiJson.Serialize(new DeletePayload { Devices = new Dictionary<Guid, IReadOnlyList<CertificateRef>> { [h.Device.Id] = refs } });

        Assert.Equal("Delete certificate old one", task.GetTaskName(Payload(new CertificateRef("old one", false))));
        Assert.Equal("Delete 2 certificates", task.GetTaskName(Payload(new CertificateRef("old one", false), new CertificateRef("COMODO ECC Certification Authority", true))));

        Assert.Null(await h.RunAsync(PkiTaskIds.Delete, Payload(new CertificateRef("old one", false), new CertificateRef("COMODO ECC Certification Authority", true))));
        Assert.Equal(
        [
            "Check compatibility: Done", "Read certificates: Done", "Delete certificate old one: Done",
            "Delete certificate COMODO ECC Certification Authority: Done", "Verify: Done", "Completed: Done",
        ], h.Lines);
        Assert.DoesNotContain("old one", h.Camera.CertificateAliases);
        Assert.DoesNotContain("COMODO ECC Certification Authority", h.Camera.CaAliases);

        // In use by HTTPS, or an Axis factory certificate: nothing is deleted, not even the valid choice.
        var ex = await h.RunAsync(PkiTaskIds.Delete, Payload(new CertificateRef("Site Device HTTPS lUZuIXuu", false)));
        Assert.Equal("\"Site Device HTTPS lUZuIXuu\" is in use by HTTPS. Nothing was changed.", ex!.Message);
        var writes = h.Camera.Writes.Count;
        ex = await h.RunAsync(PkiTaskIds.Delete, Payload(new CertificateRef("COMODO RSA Certification Authority", true), new CertificateRef("Axis device ID RSA-2048 (802.1AR)", false)));
        Assert.Contains("Axis factory certificate", ex!.Message, StringComparison.Ordinal);
        Assert.Equal(writes, h.Camera.Writes.Count);
        Assert.Contains("COMODO RSA Certification Authority", h.Camera.CaAliases);

        // A device without a choice in the payload: nothing to do.
        var other = Payload(new CertificateRef("x", false));
        h.Device = h.Device with { Id = Guid.NewGuid() };
        Assert.Null(await h.RunAsync(PkiTaskIds.Delete, other));
        Assert.Equal("No certificate of this device was chosen", h.Detail(PkiSteps.CheckCompatibility));
    }

    [Fact]
    public async Task A_deleted_oadm_certificate_stays_in_the_store_but_leaves_the_list()
    {
        await using var h = await TaskHarness.StartAsync();
        using var issued = RevocationTests.Issue(h.Pki.Service.Ca!, h.Device.Serial, client: false);
        h.Camera.AddCertificate("OADM HTTPS 20240101-120000", Oadm.Plugins.Pki.Ca.CaCertificates.ToPem(issued));
        var payload = PkiJson.Serialize(new DeletePayload { Devices = new Dictionary<Guid, IReadOnlyList<CertificateRef>> { [h.Device.Id] = [new CertificateRef("OADM HTTPS 20240101-120000", false)] } });

        Assert.Null(await h.RunAsync(PkiTaskIds.Delete, payload));

        var entry = h.Pki.Service.Issued.Find(issued.SerialNumber)!; // found by the read before the delete
        Assert.NotNull(entry.RemovedFromDeviceUtc);
        var rows = (await h.Pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest())).Rows;
        Assert.DoesNotContain(rows, r => r.SerialNumber == Oadm.Sdk.Certificates.CertificateSerials.Normalize(issued.SerialNumber));
        Assert.False(h.Pki.Service.Issued.IsRevoked(issued.SerialNumber)); // a copy could be in use: Revoke can still take it
        Assert.Contains(Oadm.Sdk.Certificates.CertificateSerials.Normalize(issued.SerialNumber), h.Pki.Service.Issued.RevocableOf(h.Device));
    }

    [Fact]
    public async Task Query_lists_the_certificates_read_only()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        var before = h.Camera.Writes.Count;
        var ctx = new PkiTaskContext(h.Camera);

        var reply = PkiJson.Deserialize<CertificateListReply>(await h.Task<ViewCertificatesTask>(PkiTaskIds.View).QueryAsync(ctx, h.Device, PkiQueries.ListCertificates, null, CancellationToken.None));

        Assert.Null(reply.Error);
        var oadm = reply.Certificates.Single(c => c.Alias == h.Camera.WebServerAlias);
        Assert.True(oadm.FromOadm);
        Assert.Equal(["HTTPS"], oadm.InUse);
        Assert.Equal(CertificateKind.Server, oadm.Kind);
        Assert.True(reply.Certificates.Single(c => c.Alias.StartsWith(CertificateDeployment.CaAliasPrefix, StringComparison.Ordinal)).FromOadm);
        Assert.Equal(before, h.Camera.Writes.Count);

        PkiCompatibility.ClearCache();
        h.Camera.HasCertApi = false;
        reply = PkiJson.Deserialize<CertificateListReply>(await h.Task<DeleteCertificatesTask>(PkiTaskIds.Delete).QueryAsync(ctx, h.Device with { Id = Guid.NewGuid() }, PkiQueries.ListCertificates, null, CancellationToken.None));
        Assert.Equal("Needs AXIS OS 11.11 or later.", reply.Error);
    }

    [Fact]
    public async Task Install_manually_installs_the_matched_pkcs12_and_switches_https()
    {
        await using var h = await TaskHarness.StartAsync();
        using var ca = TestCa.Root("Customer CA");
        using var leaf = LeafWithKey(ca, "10.0.0.48");
        var pfx = TestCa.Pfx("secret", leaf);
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.Https, Password = "secret", Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")] });
        var task = h.Task<InstallCertificatesTask>(PkiTaskIds.Install);
        Assert.Equal("Install certificate cam48.pfx", task.GetTaskName(payload));

        var ex = await RunWithFileAsync(h, payload, pfx);

        Assert.Null(ex);
        Assert.Equal(
        [
            "Check compatibility: Done", "Read certificate file: Done", "Read web server settings: Done", "Install certificate: Done",
            "Switch web server to the new certificate: Done", "Verify HTTPS: Done", "Completed: Done",
        ], h.Lines);
        Assert.StartsWith("OADM import ", h.Camera.WebServerAlias, StringComparison.Ordinal);
        Assert.Equal(CaCertificates.Fingerprint(leaf), h.Context.PinnedFingerprint);
        Assert.DoesNotContain(h.Context.LogEntries, e => e.Message.Contains("secret", StringComparison.Ordinal));

        // Wrong password: nothing changes.
        var before = h.Camera.Writes.Count;
        ex = await RunWithFileAsync(h, payload.Replace("\"secret\"", "\"wrong\"", StringComparison.Ordinal), pfx);
        Assert.Equal("cam48.pfx: The password is wrong or the file is damaged. Nothing was changed.", ex!.Message);
        Assert.Equal(before, h.Camera.Writes.Count);

        // A device without a matched file ends with a warning, nothing sent.
        h.Device = h.Device with { Id = Guid.NewGuid() };
        Assert.Null(await RunWithFileAsync(h, payload, pfx));
        Assert.Equal(["No certificate file matches this device. Nothing was changed."], h.Context.Warnings);
        Assert.Equal(before, h.Camera.Writes.Count);
    }

    [Fact]
    public async Task Install_ca_only_adds_the_file_certificates_as_ca_certificates()
    {
        await using var h = await TaskHarness.StartAsync();
        using var ca = TestCa.Root("Customer CA");
        using var publicCa = X509CertificateLoader.LoadCertificate(ca.RawData);
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.CaOnly, Password = "pw", Files = [new InstallFile(h.Device.Id, "f1", "ca.p12")] });

        Assert.Null(await RunWithFileAsync(h, payload, TestCa.Pfx("pw", publicCa)));

        Assert.Equal(["Check compatibility: Done", "Read certificate file: Done", "Install CA certificates: Done", "Completed: Done"], h.Lines);
        Assert.Contains(CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, CaCertificates.Fingerprint(ca)), h.Camera.CaAliases);
    }

    [Fact]
    public async Task Install_certificates_for_8021x_installs_the_policys_radius_server_ca_also_after_a_new_oadm_ca()
    {
        // The 802.1X server keeps its certificate of the previous OADM CA: no refusal (decision 2026-10-25, the user turns
        // 802.1X off on the switch first).
        await using var h = await TaskHarness.StartAsync();
        await h.Pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        var server = Radius.Server.RadiusPolicies.DefaultServerPolicy() with { CertificateId = h.Pki.Policies.EstServerPolicy!.CertificateId };
        Assert.Null((await h.Pki.Policies.SaveAsync(server, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true))).Ok);
        using var ca = TestCa.Root("Customer CA");
        using var leaf = LeafWithKey(ca, "10.0.0.48");
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.Dot1x, Password = "secret", Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")] });

        var ex = await RunWithFileAsync(h, payload, TestCa.Pfx("secret", leaf));

        Assert.Null(ex);
        Assert.DoesNotContain("Check compatibility: Failed", h.Lines);
    }

    [Fact]
    public void Certificate_files_match_devices_by_mac_ip_or_host_name()
    {
        Assert.True(CertificateFiles.Matches(["axis-b8a44f631339"], "B8A44F631339", "10.0.0.9", null));
        Assert.True(CertificateFiles.Matches(["B8:A4:4F:63:13:39"], "B8A44F631339", "10.0.0.9", null));
        Assert.True(CertificateFiles.Matches(["cam", "10.0.0.48"], "ACCC8E000001", "10.0.0.48", null));
        Assert.True(CertificateFiles.Matches(["cam1.example.com"], "ACCC8E000001", "10.0.0.48", "cam1"));
        Assert.True(CertificateFiles.Matches(["cam1.example.com"], "ACCC8E000001", "cam1.example.com:443", null));
        Assert.False(CertificateFiles.Matches(["10.0.0.49", "cam2.example.com"], "ACCC8E000001", "10.0.0.48", "cam1"));
    }

    internal static X509Certificate2 LeafWithKey(X509Certificate2 issuer, string ip)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + ip, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), [0x01, 0x02, 0x03]);
        return signed.CopyWithPrivateKey(key);
    }

    private static async Task<Exception?> RunWithFileAsync(TaskHarness h, string payload, byte[] pfx)
    {
        h.Time.Advance(TimeSpan.FromSeconds(2));
        var ctx = new PkiTaskContext(h.Camera);
        ctx.MemoryFiles.Files["f1"] = ("cam48.pfx", pfx);
        var plugin = h.Pki.Plugin.TaskPlugins.Single(t => t.Id == PkiTaskIds.Install);
        h.Context = ctx;
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, h.Device, payload, CancellationToken.None));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

public sealed class TaskPluginInfoTests
{
    [Fact]
    public async Task Nine_security_tasks_with_short_names_and_cached_can_run()
    {
        await using var pki = await PkiHarness.StartAsync();
        var tasks = pki.Plugin.TaskPlugins;

        Assert.Equal(
            ["Enable HTTPS", "Disable HTTPS", "Enable IEEE 802.1X", "Disable IEEE 802.1X", "View certificates", "Delete certificates", "Install certificates", "Install CA certificates", "Renew certificates"],
            tasks.Select(t => t.DisplayName));
        Assert.All(tasks, t => Assert.Equal(TaskGroups.Security, t.Group));
        Assert.All(tasks, t => Assert.True(t.DisplayName.Length <= TaskPluginNames.MaxDisplayNameLength));
        Assert.All(tasks, t => Assert.False(t.ShowInToolbar));
        Assert.All(tasks, t => Assert.True(t.ShowInMenus));
        Assert.Equal(
            [PkiTaskIds.HttpsEnable, PkiTaskIds.HttpsDisable, PkiTaskIds.Dot1xEnable, PkiTaskIds.View, PkiTaskIds.Delete, PkiTaskIds.Install, PkiTaskIds.InstallCa],
            tasks.Where(t => t.RequiresDialog).Select(t => t.Id));
        Assert.Equal(["Enable HTTPS", "Disable HTTPS", "Enable IEEE 802.1X", "Disable IEEE 802.1X", "Renew certificates"],
            tasks.Where(t => t.Id is not (PkiTaskIds.View or PkiTaskIds.Delete or PkiTaskIds.Install or PkiTaskIds.InstallCa)).Select(t => t.GetTaskName(null)));

        var device = new PkiFakeDevice(Guid.NewGuid());
        Assert.All(tasks, t => Assert.True(t.CanRun(device)));
        Assert.All(tasks, t => Assert.False(t.CanRun(device with { Firmware = "11.10.61" })));
        Assert.All(tasks, t => Assert.False(t.CanRun(device with { Firmware = "10.12.236" })));
        Assert.True(tasks[0].CanRun(device with { Firmware = "11.11.135" }));
        var noNetworkSettings = device with { Apis = [new("param-cgi", "1.0")] };
        Assert.False(tasks.Single(t => t.Id == PkiTaskIds.Dot1xEnable).CanRun(noNetworkSettings));
        Assert.True(tasks.Single(t => t.Id == PkiTaskIds.HttpsEnable).CanRun(noNetworkSettings));

        // Reasons of the greyed menu entries.
        Assert.All(tasks, t => Assert.Equal("Needs AXIS OS 11.11 or later (this device has 11.9.65)", t.NotSupportedReason(device with { Firmware = "11.9.65" })));
        Assert.All(tasks, t => Assert.Equal("Needs AXIS OS 11.11 or later", t.NotSupportedReason(device with { Firmware = "" })));
        Assert.Equal("Needs the network settings API (this device has 12.11.77)", tasks.Single(t => t.Id == PkiTaskIds.Dot1xEnable).NotSupportedReason(noNetworkSettings with { Firmware = "12.11.77" }));
        Assert.Equal("Needs the network settings API (this device has 12.11.77)", tasks.Single(t => t.Id == PkiTaskIds.Dot1xDisable).NotSupportedReason(noNetworkSettings with { Firmware = "12.11.77" }));
        Assert.Null(tasks.Single(t => t.Id == PkiTaskIds.HttpsEnable).NotSupportedReason(noNetworkSettings));
    }

    [Fact]
    public void A_device_reports_only_ip_addresses_and_never_another_cameras_axis_name()
    {
        var names = CertificateNames.For("10.0.0.48", "B8A44F631339", "axis-accc8e000001", "axis-accc8e000001.local",
            ["10.0.0.48", "fe80::1", "axis-accc8e000001.local", "mail.example.com"], trustNames: true);

        Assert.Equal(["axis-b8a44f631339.local"], names.DnsNames);
        Assert.Equal(["10.0.0.48", "fe80::1"], names.IpAddresses);
    }

    [Fact]
    public async Task Signing_refuses_weak_keys_and_caps_the_validity_at_the_ca_end()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = pki.Service.Ca!;
        var names = CertificateNames.For("10.0.0.48", "B8A44F631339", "cam", null, [], trustNames: true);
        using var weak = RSA.Create(1024);
        var weakCsr = new CertificateRequest("CN=x", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var ex = Assert.Throws<CertificateRequestRejectedException>(() => DeviceCertificateIssuer.Sign(ca, weakCsr, names, CertificatePurpose.Https, DateTimeOffset.UtcNow, TimeSpan.FromDays(365)));
        Assert.Contains("RSA 1024", ex.Message, StringComparison.Ordinal);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=whatever the device says", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        using var signed = DeviceCertificateIssuer.Sign(ca, csr, names, CertificatePurpose.Dot1x, DateTimeOffset.UtcNow, TimeSpan.FromDays(365 * 40));
        Assert.Equal("CN=10.0.0.48", signed.Subject);
        Assert.Equal(ca.Certificate.NotAfter, signed.NotAfter);
        Assert.Throws<CertificateRequestRejectedException>(() => DeviceCertificateIssuer.Sign(ca, "garbage", names, CertificatePurpose.Https, DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));
        Assert.Equal(["IP:10.0.0.48", "DNS:cam", "DNS:axis-b8a44f631339.local"], names.DeviceSubjectAltNames);
    }
}
