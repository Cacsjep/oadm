using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The SOAP certificate client and the network settings of older AXIS OS against the recorded answers of 6.53, 8.40, 9.80,
/// 10.12, 11.9 and 11.11 (tests/Oadm.Plugins.Pki.Tests/Fixtures/Legacy, sanitized: no real serials, no other products).
/// </summary>
public sealed partial class LegacyDeviceApiTests
{
    private static string Legacy(string version, string name) => PkiFixture.Read($"Legacy/{version}/{name}");

    /// <summary>The body content of a recorded request (what OADM's builders must produce).</summary>
    private static string RecordedBody(string version, string name) => BodyRegex().Match(Legacy(version, name)).Groups[1].Value;

    [GeneratedRegex("<SOAP-ENV:Body>(.*)</SOAP-ENV:Body>", RegexOptions.Singleline)]
    private static partial Regex BodyRegex();

    [Theory]
    [InlineData("6.53")]
    [InlineData("8.40")]
    public void Certificates_parse_as_pem_with_their_ids(string version)
    {
        var list = CertSoapApi.ParseCertificates(Legacy(version, "GetCertificates.xml"), "NvtCertificate");

        Assert.Equal(["OADM research signed", "OADM research p12 aes", "OADM research p12 3des", "OADM research upload"], list.Select(c => c.Alias));
        Assert.All(list, c =>
        {
            using var certificate = X509Certificate2.CreateFromPem(c.Pem);
            Assert.Equal("CN=oadm-test", certificate.Subject);
        });
        Assert.Empty(CertSoapApi.ParseCertificates(Legacy("6.53", "GetCertificates-empty.xml"), "NvtCertificate"));
    }

    [Fact]
    public void Ca_certificates_of_653_parse()
    {
        var list = CertSoapApi.ParseCertificates(Legacy("6.53", "GetCACertificates.xml"), "CACertificate");

        Assert.True(list.Count > 10);
        Assert.Contains(list, c => c.Alias == "DigiCert_Global_Root_G2");
        Assert.All(list, c => Assert.Equal(64, CertificateDeployment.Fingerprint(c.Pem).Length));
    }

    [Fact]
    public void The_web_server_of_653_has_three_roles_and_the_set_request_names_all_of_them()
    {
        var web = WebServerTls.Parse(Legacy("6.53", "GetWebServerTlsConfiguration.xml"));
        Assert.False(web.Tls);
        Assert.Equal((ConnectionPolicy.Http, ConnectionPolicy.Http, ConnectionPolicy.Http), (web.Policy, web.OperatorPolicy, web.ViewerPolicy));
        Assert.True(web.HasRoles);
        Assert.Null(web.CertificateAlias);

        // Enable HTTPS: every role HTTP and HTTPS, like the request that worked on the camera.
        var set = web.WithPolicies(ConnectionPolicy.Enabled) with { Tls = true, Certificates = ["OADM research signed"] };
        var body = WebServerTls.BuildSet(set);
        var recorded = RecordedBody("6.53", "SetWebServerTlsConfiguration-3roles.req.xml");
        Assert.Contains("<aweb:ConnectionPolicies><aweb:Admin>HttpAndHttps</aweb:Admin><aweb:Operator>HttpAndHttps</aweb:Operator><aweb:Viewer>HttpAndHttps</aweb:Viewer></aweb:ConnectionPolicies>", recorded, StringComparison.Ordinal);
        Assert.Equal(
            recorded.Replace("<aweb:SetWebServerTlsConfiguration>", string.Empty, StringComparison.Ordinal),
            body.Replace($"<aweb:SetWebServerTlsConfiguration xmlns:aweb=\"{WebServerTls.WebNs}\" xmlns:acert=\"{WebServerTls.CertNs}\">", string.Empty, StringComparison.Ordinal));

        // Admin alone is refused there: the device's text.
        var ex = Assert.Throws<PkiDeviceException>(() => DeviceSoap.Body(Legacy("6.53", "SetWebServerTlsConfiguration-admin-only-fault.xml"), "Set web server settings"));
        Assert.Equal("Set web server settings: the device refused: Invalid argument (ter:InvalidArgs)", ex.Message);
    }

    [Theory]
    [InlineData("8.40", ConnectionPolicy.HttpAndHttps, "Default (self-signed)")]
    [InlineData("9.80", ConnectionPolicy.HttpAndHttps, "Default (self-signed)")]
    [InlineData("11.9", ConnectionPolicy.Http, "Axis device ID ECC-P256 (802.1AR)")]
    [InlineData("11.11", ConnectionPolicy.HttpAndHttps, "Default (self-signed)")]
    public void The_web_server_from_840_has_the_admin_policy_only(string version, string policy, string alias)
    {
        var web = WebServerTls.Parse(Legacy(version, "GetWebServerTlsConfiguration.xml"));

        Assert.Equal(policy, web.Policy);
        Assert.False(web.HasRoles);
        Assert.Equal(alias, web.CertificateAlias);
        Assert.Equal(8, web.Ciphers.Count);
        Assert.DoesNotContain("<aweb:Operator>", WebServerTls.BuildSet(web), StringComparison.Ordinal);
        Assert.Contains("<aweb:Operator>", WebServerTls.BuildSet(web.WithRoles()), StringComparison.Ordinal); // before 7.10
    }

    [Fact]
    public void Key_request_and_signed_certificate_requests_match_the_recorded_ones()
    {
        var notBefore = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(RecordedBody("6.53", "CreateCertificate2.req.xml"), CertSoapApi.BuildCreate("OADM research tmp", "oadm-test", notBefore, notBefore.AddYears(1)));
        Assert.Equal(RecordedBody("6.53", "GetPkcs10Request2.req.xml"), CertSoapApi.BuildCsr("OADM research tmp", "oadm-test"));
        Assert.Equal(RecordedBody("6.53", "DeleteCertificates.req.xml"), CertSoapApi.BuildDelete("OADM research tmp"));

        // LoadCertificates / LoadCACertificates: the DER of the PEM, under the id.
        var load = RecordedBody("6.53", "LoadCertificates.req.xml");
        var der = Regex.Match(load, "<tt:Data>([^<]+)</tt:Data>").Groups[1].Value;
        var pem = new string(PemEncoding.Write("CERTIFICATE", Convert.FromBase64String(der))) + "\n";
        Assert.Equal(load, CertSoapApi.BuildLoad("LoadCertificates", "NVTCertificate", "OADM research signed", pem));
        var loadCa = RecordedBody("6.53", "LoadCACertificates.req.xml");
        var caPem = new string(PemEncoding.Write("CERTIFICATE", Convert.FromBase64String(Regex.Match(loadCa, "<tt:Data>([^<]+)</tt:Data>").Groups[1].Value))) + "\n";
        Assert.Equal(loadCa, CertSoapApi.BuildLoad("LoadCACertificates", "CACertificate", "OADM research CA 3", caPem));

        // PKCS#12 (password in the body only; recorded with the file and the password replaced).
        Assert.Equal(RecordedBody("6.53", "LoadPkcs12.req.xml"), CertSoapApi.BuildPkcs12("OADM research p12 aes", "[redacted p12]", "secret"));
        Assert.Equal("<acertificates:GetCertSet><acertificates:CertSetName>EAPTLS_WIRED</acertificates:CertSetName></acertificates:GetCertSet>", CertSoapApi.BuildGetCertSet(CertSoapApi.WiredDot1xSet));
    }

    [Fact]
    public void Key_and_request_answers_parse()
    {
        var csr = CertSoapApi.ParseCsr(Legacy("6.53", "GetPkcs10Request2.xml"));
        var request = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        Assert.Equal("CN=oadm-test", request.SubjectName.Name);
        Assert.Equal(2048, request.PublicKey.GetRSAPublicKey()!.KeySize); // the SOAP service makes RSA 2048

        var empty = CertSoapApi.ParseCertSet(Legacy("6.53", "GetCertSet-EAPTLS_WIRED-empty.xml"));
        Assert.Empty(empty.Certificates.Concat(empty.CaCertificates).Concat(empty.TrustedCertificates));
        DeviceSoap.Body(Legacy("6.53", "CreateCertificate2.xml"), "Create key"); // no fault
        DeviceSoap.Body(Legacy("6.53", "LoadCertificates.xml"), "Install certificate");
        DeviceSoap.Body(Legacy("6.53", "SetCertSet.xml"), "Set 802.1X configuration");
    }

    [Theory]
    [InlineData("6.53", "LoadPkcs12-ecc-fault.xml", "Install certificate: the device refused: Action failed (ter:Action)")]
    [InlineData("8.40", "DeleteCertificates-in-use-fault.xml", "Install certificate: the device refused: Certificate in use (ter:OperationProhibited)")]
    [InlineData("10.12", "LoadCertificates-same-id-fault.xml", "Install certificate: the device refused: Invalid ID (ter:InvalidArgVal)")]
    [InlineData("10.12", "LoadCACertificates-dup-fault.xml", "Install certificate: the device refused: Invalid CA certificate ID (ter:InvalidArgVal)")]
    [InlineData("10.12", "CreateCertificate2-keytype-fault.xml", "Install certificate: the device refused: Tag mismatch (ter:TagMismatch)")]
    public void Faults_become_the_devices_text(string version, string name, string message) =>
        Assert.Equal(message, Assert.Throws<PkiDeviceException>(() => DeviceSoap.Body(Legacy(version, name), "Install certificate")).Message);

    [Fact]
    public void Ecc_pkcs12_loads_on_840_and_the_id_comes_back()
    {
        var body = DeviceSoap.Body(Legacy("8.40", "LoadPkcs12-ecc.xml"), "Install certificate");
        Assert.Contains("OADM research p12 ecc", body.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6.53", "OADM research p12 aes", new[] { "OADM research CA 3" })]
    [InlineData("8.40", "OADM research p12 aes", new[] { "OADM research CA 3" })]
    [InlineData("9.80", "OADM research upload", new[] { "OADM research CA 3", "DigiCert Global Root G2" })]
    [InlineData("10.12", "cert2", new[] { "OADM Test CA 3" })]
    [InlineData("11.11", "OADM research rest", new[] { "OADM research CA 3", "Axis device ID Root CA RSA" })]
    public void The_8021x_certificate_set_parses(string version, string client, string[] cas)
    {
        var set = CertSoapApi.ParseCertSet(Legacy(version, "GetCertSet-EAPTLS_WIRED.xml"));

        Assert.Equal([client], set.Certificates);
        Assert.Equal(cas, set.CaCertificates);
        Assert.Empty(set.TrustedCertificates);
    }

    [Theory]
    [InlineData("6.53", "OADM research p12 aes", new[] { "OADM research CA 3" })]
    [InlineData("9.80", "OADM research upload", new[] { "OADM research CA 3", "DigiCert Global Root G2" })]
    [InlineData("11.11", "OADM research rest", new[] { "OADM research CA 3", "Axis device ID Root CA RSA" })]
    public void The_set_request_matches_the_ones_that_bound_on_the_camera(string version, string client, string[] cas) =>
        Assert.Equal(RecordedBody(version, "SetCertSet-EAPTLS_WIRED.req.xml"), CertSoapApi.BuildSetCertSet(CertSoapApi.WiredDot1xSet, new CertSet([client], cas, [])));

    [Theory]
    [InlineData("9.80", "1.6", "EAPoLv2")] // recorded after a set with EAPOL version 2
    [InlineData("10.12", "1.15", null)]
    [InlineData("11.9", "1.29", null)]
    [InlineData("11.11", "1.33", null)]
    public void Network_settings_before_138_name_no_certificates_so_the_certificate_set_binds_them(string version, string listed, string? eapol)
    {
        var info = NetworkInfoApi.Parse(Legacy(version, "network_settings-getNetworkInfo.json"));
        var dot1x = info.Dot1x!;

        Assert.False(dot1x.ReportsCertificates);
        Assert.Null(dot1x.CertClient);
        Assert.Equal(NetworkInfoApi.EapTlsMode, dot1x.Mode);
        if (eapol is not null)
        {
            Assert.Equal(eapol, dot1x.EapolVersion);
        }

        Assert.Equal(Dot1xApiKind.NetworkSettingsAndCertSet, PkiCompatibility.Dot1xApiOf([new("network-settings", listed)], dot1x));
        Assert.DoesNotContain(info.Addresses, a => a.StartsWith("fe80", StringComparison.Ordinal));

        // AXIS OS 12.11 names them: network settings alone (today's path).
        var current = NetworkInfoApi.Parse(PkiFixture.Read(PkiFixture.NetworkInfo)).Dot1x!;
        Assert.True(current.ReportsCertificates);
        Assert.Equal(Dot1xApiKind.NetworkSettings, PkiCompatibility.Dot1xApiOf(PkiFixture.Apis, current)); // lists 1.37, reports them
        Assert.Equal(Dot1xApiKind.NetworkSettings, PkiCompatibility.Dot1xApiOf([new("network-settings", "1.38")], dot1x));
        Assert.Equal(Dot1xApiKind.ParametersAndCertSet, PkiCompatibility.Dot1xApiOf([new("param-cgi", "1.0")], null));
    }

    [Fact]
    public void The_8021x_parameters_of_840_and_older()
    {
        var dot1x = Dot1xSettings.FromParameters(PkiFixture.Parameters("paramcgi-Network.Interface.I0.dot1x.txt"))!;
        Assert.False(dot1x.Enabled);
        Assert.Equal("48", dot1x.Identity);
        Assert.Equal("EAPoLv3", dot1x.EapolVersion);
        Assert.Equal("Stopped", dot1x.Status);
        Assert.Null(Dot1xSettings.FromParameters(new Dictionary<string, string>()));

        // The update the camera took (form body, never in the URL).
        Assert.Equal(
            Legacy("6.53", "paramcgi-dot1x-update.req.txt").Trim(),
            Dot1xSettings.UpdateBody([(Dot1xSettings.IdentityParameter, "oadm-param"), (Dot1xSettings.EapolParameter, "2"), (Dot1xSettings.EnabledParameter, "yes")]));
    }

    [Fact]
    public void Rest_cert_v1_is_used_only_released()
    {
        Assert.False(CertApi.ParseDiscover(Legacy("11.9", "config-discover.json")).IsSupported); // beta
        Assert.True(CertApi.ParseDiscover(Legacy("11.11", "config-discover.json")).IsSupported);
    }
}

/// <summary>The capability decision (docs/spec/pki.md "Older AXIS OS"), one place, every family.</summary>
public sealed class CompatibilityMatrixTests
{
    private static readonly CertApiSupport Released = new("1.1.2", "released");
    private static readonly CertApiSupport Beta = new("1.0.0-beta.1", "beta");
    private static readonly CertApiSupport None = new(null, null);

    [Theory]
    [InlineData("6.53.4", false, true, 2)]
    [InlineData("7.10.1", false, false, 2)]
    [InlineData("8.40.104", false, false, 2)]
    [InlineData("9.80.3", false, false, 2)]
    [InlineData("10.1.0", false, false, 3)]
    [InlineData("10.12.236", false, false, 3)]
    [InlineData("11.9.65", true, false, 3)]
    public void Soap_from_653(string firmware, bool beta, bool allRoles, int eapol)
    {
        var support = PkiCompatibility.Decide(firmware, beta ? Beta : None);

        Assert.Equal(CertificateApiKind.Soap, support.Certificates);
        Assert.Equal(allRoles, support.WebServerAllRoles);
        Assert.Equal(eapol, support.MaxEapolVersion);
        Assert.Equal($"AXIS OS {firmware} · SOAP certificates", support.Describe());
    }

    [Theory]
    [InlineData("11.11.232")]
    [InlineData("12.11.77")]
    [InlineData(null)] // the version is not needed for REST
    public void Rest_when_released(string? firmware)
    {
        var support = PkiCompatibility.Decide(firmware, Released);

        Assert.Equal(CertificateApiKind.Rest, support.Certificates);
        Assert.False(support.WebServerAllRoles);
        Assert.Equal(3, support.MaxEapolVersion);
        Assert.Equal("cert 1.1.2", support.Describe());
    }

    [Theory]
    [InlineData("6.50.5", PkiCompatibility.NeedsNewerFirmware)]
    [InlineData("5.51.7", PkiCompatibility.NeedsNewerFirmware)]
    [InlineData(null, PkiCompatibility.UnknownFirmware)]
    [InlineData("", PkiCompatibility.UnknownFirmware)]
    [InlineData("unknown", PkiCompatibility.UnknownFirmware)]
    [InlineData("12", PkiCompatibility.UnknownFirmware)]
    public void Older_unknown_or_unreadable_versions_refuse(string? firmware, string message) =>
        Assert.Equal(message, Assert.Throws<DeviceNotCompatibleException>(() => PkiCompatibility.Decide(firmware, None)).Message);

    [Fact]
    public void Eapol_3_needs_101()
    {
        var policy = new PkiPolicy { Name = "IEEE 802.1X", Kind = PolicyKinds.Dot1xCertificate, EapolVersion = 3 };
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => PkiCompatibility.RequireEapol(policy, PkiCompatibility.Decide("9.80.3", None)));
        Assert.Equal("EAPOL version 3 needs AXIS OS 10.1 or later. Choose a policy with EAPOL version 1 or 2. Nothing was changed.", ex.Message);
        PkiCompatibility.RequireEapol(policy with { EapolVersion = 2 }, PkiCompatibility.Decide("6.53.4", None));
        PkiCompatibility.RequireEapol(policy, PkiCompatibility.Decide("10.12.236", None));
        PkiCompatibility.RequireEapol(policy, PkiCompatibility.Decide(null, Released));
    }
}

/// <summary>Every certificate task on the older AXIS OS families against <see cref="FakeCamera.Legacy"/>.</summary>
public sealed class LegacyTaskTests
{
    public static TheoryData<string> Versions => new() { "6.53", "8.40", "9.80", "10.12", "11.9", "11.11" };

    public static TheoryData<string> SoapVersions => new() { "6.53", "8.40", "9.80", "10.12", "11.9" };

    private static async Task<PkiPolicy> SaveAsync(TaskHarness h, PkiPolicy policy)
    {
        var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy);
        Assert.Null(reply.Error);
        return reply.Saved!;
    }

    /// <summary>The first HTTPS and IEEE 802.1X policies with RSA keys and EAPOL version 2 (what older AXIS OS takes).</summary>
    private static async Task UseRsaAsync(TaskHarness h, int eapol = 2)
    {
        await SaveAsync(h, h.Pki.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)! with { Key = PolicyKeys.Rsa });
        await SaveAsync(h, h.Pki.Service.Policies.FirstOf(PolicyKinds.Dot1xCertificate)! with { Key = PolicyKeys.Rsa, EapolVersion = eapol });
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Enable_https_issues_on_the_device_key_and_follows_it(string version)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));

        // The default policy has ECC: refused before any write (RSA only through the SOAP service, 11.11 lists no key types).
        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);
        Assert.Equal("The device cannot create an ECC key. Choose a policy with RSA. Nothing was changed.", ex!.Message);
        Assert.Empty(h.Camera.Writes);

        await UseRsaAsync(h);
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        var noNetworkSettings = version is "6.53" or "8.40";
        Assert.Equal(
        [
            "Check compatibility: Done", "Check device clock: Done", "Read web server settings: Done", noNetworkSettings ? "Read network settings: Skipped" : "Read network settings: Done",
            "Install CA certificate: Done", "Create key on the device: Done", "Get certificate request: Done", "Sign certificate: Done", "Install certificate: Done",
            "Switch web server to the new certificate: Done", "Verify HTTPS: Done", "Remove previous OADM certificate: Skipped", "Completed: Done",
        ], h.Lines);
        var alias = h.Camera.WebServerAlias;
        Assert.StartsWith("OADM HTTPS ", alias, StringComparison.Ordinal);
        Assert.Equal(ConnectionPolicy.HttpAndHttps, h.Camera.Policy); // 6.53 and 11.9 were HTTP only
        using var served = h.Served(alias);
        Assert.Equal(h.Pki.Service.Ca!.Certificate.Subject, served.Issuer);
        Assert.Equal(2048, served.GetRSAPublicKey()!.KeySize);
        Assert.Equal("https", h.Context.Scheme);
        Assert.Equal(CaCertificates.Fingerprint(served), h.Context.PinnedFingerprint);
        Assert.False(Assert.Single(await h.Pki.Service.Issued.ForDeviceAsync(h.Device.Id, CancellationToken.None)).Pending);

        if (version == "11.11")
        {
            Assert.Equal(["RSA-2048"], h.Camera.KeyTypesCreated); // REST
            Assert.Empty(h.Camera.SoapKeysCreated);
        }
        else
        {
            // The key under its placeholder, the signed certificate under the alias, the placeholder deleted.
            Assert.Equal([alias + DeviceCertificates.KeySuffix], h.Camera.SoapKeysCreated);
            Assert.DoesNotContain(alias + DeviceCertificates.KeySuffix, h.Camera.CertificateAliases);
            Assert.Empty(h.Camera.KeyTypesCreated);
            Assert.StartsWith("AXIS OS ", h.Detail(PkiSteps.CheckCompatibility), StringComparison.Ordinal);
        }

        if (version == "6.53")
        {
            Assert.Equal((ConnectionPolicy.HttpAndHttps, ConnectionPolicy.HttpAndHttps), (h.Camera.OperatorPolicy, h.Camera.ViewerPolicy));
        }

        // Again: a new certificate replaces it and the previous one leaves the device.
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        Assert.Equal("Already installed", h.Detail(PkiSteps.InstallCa));
        Assert.Equal(alias, h.Detail(PkiSteps.RemovePrevious));
        Assert.DoesNotContain(alias, h.Camera.CertificateAliases);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Enable_8021x_binds_the_certificates_and_verifies_them(string version)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));
        await UseRsaAsync(h);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));

        Assert.Equal(
        [
            "Check compatibility: Done", "Check device clock: Done", "Install CA certificates: Done", "Create key on the device: Done",
            "Get certificate request: Done", "Sign certificate: Done", "Install certificate: Done", "Set 802.1X configuration: Done",
            "Verify 802.1X settings: Done", "Remove previous OADM certificate: Skipped", "Completed: Done",
        ], h.Lines);
        var caAlias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, h.Pki.Service.Ca!.Id);
        var client = Assert.Single(h.Camera.WiredSet.Certificates);
        Assert.StartsWith("OADM 802.1X ", client, StringComparison.Ordinal);
        Assert.Equal([caAlias], h.Camera.WiredSet.CaCertificates);
        using (var certificate = h.Served(client))
        {
            Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == DeviceCertificateIssuer.ClientAuthOid);
        }

        if (version is "6.53" or "8.40")
        {
            // No network settings API: the parameters.
            Assert.Equal("yes", h.Camera.Parameters[Dot1xSettings.EnabledParameter]);
            Assert.Equal("2", h.Camera.Parameters[Dot1xSettings.EapolParameter]);
            Assert.Equal("10.0.0.48", h.Camera.Parameters[Dot1xSettings.IdentityParameter]);
            Assert.DoesNotContain(h.Camera.Requests, r => r.Body.Contains("setWired8021XConfiguration", StringComparison.Ordinal));
        }
        else
        {
            Assert.True(h.Camera.Dot1x["enabled"]!.GetValue<bool>());
            Assert.Equal("EAPoLv2", h.Camera.EapTlsParams["eapolVersion"]!.GetValue<string>());
            Assert.Equal("10.0.0.48", h.Camera.EapTlsParams["identity"]!.GetValue<string>());
            var set = h.Camera.Requests.FindIndex(r => r.Body.Contains("<acertificates:SetCertSet>", StringComparison.Ordinal));
            var on = h.Camera.Requests.FindIndex(r => r.Body.Contains("setWired8021XConfiguration", StringComparison.Ordinal));
            Assert.True(set >= 0 && set < on, "The certificates are bound before 802.1X is switched on.");
            Assert.DoesNotContain("certClient", h.Camera.Requests[on].Body, StringComparison.Ordinal);
        }

        // Verified: current, not pending.
        var issued = Assert.Single(await h.Pki.Service.Issued.ForDeviceAsync(h.Device.Id, CancellationToken.None));
        Assert.Equal(client, issued.Alias);
        Assert.False(issued.Pending);

        // Disable: off, the certificates stay bound.
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xDisable));
        Assert.Equal(["Check compatibility: Done", "Set 802.1X off: Done", "Verify: Done", "Completed: Done"], h.Lines);
        Assert.Equal([client], h.Camera.WiredSet.Certificates);
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xDisable));
        Assert.Equal("Already off", h.Detail(PkiSteps.SetDot1xOff));
    }

    [Theory]
    [InlineData("6.53", false)]
    [InlineData("8.40", false)]
    [InlineData("9.80", false)]
    [InlineData("10.12", true)]
    [InlineData("11.9", true)]
    [InlineData("11.11", true)]
    public async Task Eapol_3_only_from_101(string version, bool works)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));
        await UseRsaAsync(h, eapol: 3);

        var ex = await h.RunAsync(PkiTaskIds.Dot1xEnable);

        if (works)
        {
            Assert.Null(ex);
        }
        else
        {
            Assert.StartsWith("EAPOL version 3 needs AXIS OS 10.1 or later.", ex!.Message, StringComparison.Ordinal);
            Assert.Equal("Check compatibility: Failed", h.Lines[0]);
            Assert.Empty(h.Camera.Writes);
        }
    }

    [Fact]
    public async Task Disable_https_on_653_sets_every_role_to_http()
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy("6.53"));
        await UseRsaAsync(h);
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsDisable));

        Assert.Equal((ConnectionPolicy.Http, ConnectionPolicy.Http, ConnectionPolicy.Http), (h.Camera.Policy, h.Camera.OperatorPolicy, h.Camera.ViewerPolicy));
        Assert.Equal("http", h.Context.Scheme);
    }

    [Theory]
    [InlineData("6.53")]
    [InlineData("9.80")]
    [InlineData("11.11")]
    public async Task Renew_replaces_both_oadm_certificates(string version)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));
        await UseRsaAsync(h);
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var https = h.Camera.WebServerAlias;
        var dot1x = h.Camera.WiredSet.Certificates.Single();

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));

        Assert.Contains("HTTPS: Verify HTTPS: Done", h.Lines);
        Assert.Contains("IEEE 802.1X: Verify 802.1X settings: Done", h.Lines);
        Assert.NotEqual(https, h.Camera.WebServerAlias);
        Assert.NotEqual(dot1x, h.Camera.WiredSet.Certificates.Single());
        Assert.DoesNotContain(https, h.Camera.CertificateAliases);
        Assert.DoesNotContain(dot1x, h.Camera.CertificateAliases);
    }

    [Theory]
    [MemberData(nameof(SoapVersions))]
    public async Task Install_ca_certificates_view_and_delete_through_soap(string version)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));
        using var root = TestCa.Root("Acme Root CA");
        var payload = PkiJson.Serialize(new InstallCaPayload { Certificates = [new CaCertificatePayload("Acme Root CA", CaCertificates.ToPem(root))] });

        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, payload));
        var alias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, CaCertificates.Fingerprint(root));
        Assert.Contains(alias, h.Camera.CaAliases);
        Assert.Equal("1 installed, 0 already there", h.Detail(InstallCaTask.VerifyCas));
        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, payload));
        Assert.Equal("Already installed", h.Detail("Install CA certificate Acme Root CA"));

        // View (read only): the SOAP lists with what uses them.
        await UseRsaAsync(h);
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var writes = h.Camera.Writes.Count;
        var reply = PkiJson.Deserialize<CertificateListReply>(await h.Task<ViewCertificatesTask>(PkiTaskIds.View).QueryAsync(new PkiTaskContext(h.Camera), h.Device, PkiQueries.ListCertificates, null, CancellationToken.None));
        Assert.Null(reply.Error);
        var client = reply.Certificates.Single(c => c.Alias == h.Camera.WiredSet.Certificates.Single());
        Assert.Equal(["802.1X"], client.InUse);
        Assert.True(client.FromOadm);
        Assert.Contains(reply.Certificates, c => c.Alias == alias && c.Kind == CertificateKind.Ca && c.InUse.Count == 0);
        Assert.Equal(writes, h.Camera.Writes.Count);

        // Delete: the unused CA certificate goes; the one 802.1X uses is refused before any delete.
        var delete = new DeletePayload { Devices = new Dictionary<Guid, IReadOnlyList<CertificateRef>> { [h.Device.Id] = [new CertificateRef(alias, true)] } };
        Assert.Null(await h.RunAsync(PkiTaskIds.Delete, PkiJson.Serialize(delete)));
        Assert.DoesNotContain(alias, h.Camera.CaAliases);
        var inUse = new DeletePayload { Devices = new Dictionary<Guid, IReadOnlyList<CertificateRef>> { [h.Device.Id] = [new CertificateRef(client.Alias, false)] } };
        var ex = await h.RunAsync(PkiTaskIds.Delete, PkiJson.Serialize(inUse));
        Assert.Contains("is in use by 802.1X", ex!.Message, StringComparison.Ordinal);
        Assert.Contains(client.Alias, h.Camera.CertificateAliases);
    }

    [Theory]
    [InlineData("8.40")]
    [InlineData("9.80")]
    public async Task Install_certificates_loads_the_pkcs12_through_soap_and_switches_https(string version)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy(version));
        using var ca = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.Https, Password = "secret", Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")] });

        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, payload, TestCa.Pfx("secret", leaf)));

        Assert.StartsWith(CertificateDeployment.ImportAliasPrefix, h.Camera.WebServerAlias, StringComparison.Ordinal);
        Assert.Equal(CaCertificates.Fingerprint(leaf), h.Context.PinnedFingerprint);
        Assert.Contains(h.Camera.Requests, r => r.Body.Contains("<acert:Password>secret</acert:Password>", StringComparison.Ordinal)); // in the body only
    }

    [Fact]
    public async Task The_radius_server_ca_changes_in_the_certificate_set_below_network_settings_138()
    {
        // EST's "Set the RADIUS server CA" (AXIS OS 12.10 lists network settings before 1.38) and Renew use this.
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy("11.11"));
        await UseRsaAsync(h);
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var client = h.Camera.WiredSet.Certificates.Single();
        h.Camera.AddCaCertificate("Other RADIUS CA", CaCertificates.ToPem(TestCa.Root("Other RADIUS CA")));

        var state = await Dot1xSettings.ReadAsync(h.Camera, h.Camera.ApiList, CancellationToken.None);
        Assert.Equal(Dot1xApiKind.NetworkSettingsAndCertSet, state.Api);
        Assert.Equal(client, state.Dot1x!.CertClient);
        await Dot1xSettings.SetCaCertificatesAsync(h.Camera, state, h.Camera.ApiList, ["Other RADIUS CA"], CancellationToken.None);

        Assert.Equal([client], h.Camera.WiredSet.Certificates); // the client certificate stays
        Assert.Equal(["Other RADIUS CA"], h.Camera.WiredSet.CaCertificates);
        Assert.True(h.Camera.Dot1x["enabled"]!.GetValue<bool>()); // still on
    }

    [Fact]
    public async Task A_failed_load_removes_the_key_placeholder_again()
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy("10.12"));
        await UseRsaAsync(h);
        h.Camera.FailSoap = "LoadCertificates";
        var before = h.Camera.CertificateAliases.ToList();

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);

        Assert.Contains("Install certificate: the device refused: Action failed", ex!.Message, StringComparison.Ordinal);
        Assert.Equal(before.Order(), h.Camera.CertificateAliases.Order());
        Assert.Equal("Default (self-signed)", h.Camera.WebServerAlias);
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("was removed again", StringComparison.Ordinal));
        Assert.True(h.Pki.Service.Issued.ForDevice(h.Device.Id).All(e => h.Pki.Service.Issued.IsRevoked(e.SerialNumber))); // never in use
    }

    [Fact]
    public async Task Axis_os_120_keeps_todays_path()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        Assert.DoesNotContain(h.Camera.Requests, r => r.Path == DeviceSoap.ServicesPath && !r.Body.Contains("WebServerTls", StringComparison.Ordinal));
        Assert.Contains(h.Camera.Requests, r => r.Body.Contains("\"certClient\"", StringComparison.Ordinal));
    }
}
