using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Several issuing CAs (docs/spec/pki.md "Certificate authorities", decisions 2026-10-25): every import adds a CA (the
/// certificate only, or with its key: then it can issue), the key encrypted with the master key like the OADM CA's, policies
/// pick a CA as "Issued by", signing follows the policy, a CA a policy uses stays, the Used in column.
/// </summary>
public sealed class SeveralCasTests
{
    private static readonly IReadOnlyList<PolicyCipherSuite> Suites = PkiService.CipherSuites;

    private static ImportRequest WithKey(X509Certificate2 ca, params X509Certificate2[] chain) =>
        new(Convert.ToBase64String(TestCa.Pfx("secret", [ca, .. chain.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))])), "ca.pfx", "secret", null, null);

    private static ImportAuthorityRequest PublicOnly(X509Certificate2 ca) => new(Convert.ToBase64String(ca.RawData), "ca.cer");

    [Fact]
    public async Task A_ca_certificate_is_added_and_does_not_issue_and_its_key_added_later_makes_it_issue()
    {
        await using var pki = await PkiHarness.StartAsync();
        var oadm = pki.Service.Ca!.Id;
        using var root = TestCa.Root("Contoso Root");
        var id = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(root));

        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, PublicOnly(root))).Ok);
        var listed = Assert.Single(pki.Service.Authorities(), a => a.Id == id);
        Assert.False(listed.Issues);
        Assert.Null(pki.Service.IssuingCa(id));
        Assert.DoesNotContain(pki.Policies.IssuingCaChoices(), c => c.Id == id);
        Assert.Single(pki.Anchors.Current.Certificates); // a CA certificate only is not trusted by the server (as before)

        // The same CA with its key: it gets the key, stays one row and can issue; the OADM CA stays.
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, WithKey(root))).Ok);
        Assert.True(Assert.Single(pki.Service.Authorities(), a => a.Id == id).Issues);
        Assert.NotNull(pki.Service.IssuingCa(id));
        Assert.Contains(pki.Policies.IssuingCaChoices(), c => c.Id == id && c.Name == "Contoso Root");
        Assert.Equal(oadm, pki.Service.Ca!.Id);
        Assert.Equal(2, pki.Anchors.Current.Certificates.Count);
        Assert.Equal(2, pki.Anchors.Current.RenewalIssuers.Count); // EST renewals of its certificates pin without "Certificate changed"
    }

    [Fact]
    public async Task The_key_of_an_imported_ca_is_stored_encrypted_and_read_again_only_with_the_master_key()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        using var root = TestCa.Root("Contoso Root");
        var id = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(root));
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, WithKey(root))).Ok);
        }

        var stored = await settings.GetSettings(PkiPluginInfo.PluginId).GetAsync(PkiStore.ImportedCasKey, CancellationToken.None);
        Assert.Contains("keyProtected", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("MII", PkiJson.Deserialize<List<StoredImportedCa>>(stored)[0].KeyProtected!, StringComparison.Ordinal); // no plain DER/PEM body

        // The same master key (a restart, a restored server backup with its key): it issues again.
        await using (var again = await PkiHarness.StartAsync(settings, secrets))
        {
            Assert.NotNull(again.Service.IssuingCa(id));
        }

        // Another master key: still listed, it cannot issue (fail closed, never another CA instead).
        await using var other = await PkiHarness.StartAsync(settings, TestSecrets.Create());
        Assert.False(Assert.Single(other.Service.Authorities(), a => a.Id == id).Issues);
        Assert.Null(other.Service.IssuingCa(id));
    }

    [Fact]
    public async Task Signing_follows_the_issuer_the_caller_names_and_sends_the_chain_kept_at_import()
    {
        var camera = new Oadm.Sdk.Testing.FakeDevice { Serial = "B8A44F631339" };
        await using var pki = await PkiHarness.StartAsync(devices: [camera]);
        using var root = TestCa.Root("Acme Root CA");
        using var intermediate = TestCa.Intermediate(root);
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, WithKey(intermediate, root))).Ok);
        var id = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(intermediate));
        var authority = new PkiCertificateAuthority(pki.Service);

        var request = new DeviceCertificateRequest(RevocationTests.Csr(), "10.0.0.48", ["10.0.0.48"], ["axis-b8a44f631339.local"], DeviceCertificateUse.Server)
        {
            DeviceId = camera.Id,
            Validity = TimeSpan.FromDays(30),
            Purpose = DeviceCertificatePurposes.Https,
        };
        var imported = await authority.SignAsync(request with { IssuerId = id, Purpose = DeviceCertificatePurposes.Rtsps }, CancellationToken.None);
        using (var leaf = X509CertificateLoader.LoadCertificate(imported.Certificate))
        {
            Assert.Equal(intermediate.Subject, leaf.Issuer);
        }

        Assert.Equal([intermediate.RawData, root.RawData], imported.Chain);
        Assert.Equal(CaCertificates.Fingerprint(intermediate), pki.Service.Issued.Find(imported.SerialNumber)!.CaId, ignoreCase: true);

        // No issuer named: the OADM CA, as before.
        var oadm = await authority.SignAsync(request, CancellationToken.None);
        using (var leaf = X509CertificateLoader.LoadCertificate(oadm.Certificate))
        {
            Assert.Equal(pki.Service.Ca!.Certificate.Subject, leaf.Issuer);
        }

        // A CA without its key cannot issue.
        using var other = TestCa.Root("Contoso Root");
        await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, PublicOnly(other));
        var refused = await Assert.ThrowsAsync<CertificateRequestRefusedException>(() =>
            authority.SignAsync(request with { IssuerId = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(other)) }, CancellationToken.None));
        Assert.Equal("The CA of this policy cannot issue certificates. Choose another CA in the policy.", refused.Message);

        // The certificate list names the imported CA as issuer and groups its device certificates under it.
        var byIssuer = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { GroupBy = IssuedCertificateGrouping.Issuer });
        Assert.Contains(byIssuer.Groups, g => g.Key == id && g.Count >= 1);
        var issuedBy = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest { IssuedBy = id, NoAuthorities = true });
        Assert.Contains(issuedBy.Rows, r => CertificateSerials.Normalize(r.SerialNumber) == CertificateSerials.Normalize(imported.SerialNumber) && r.IssuedBy == "Acme Issuing CA");
    }

    [Fact]
    public async Task Only_the_current_certificate_oadm_issued_names_its_camera_for_an_est_renewal()
    {
        // Decision 2026-10-25: EST renewals with the camera's current certificate, whatever CA of PKI issued it.
        var camera = new Oadm.Sdk.Testing.FakeDevice { Serial = "B8A44F631339" };
        await using var pki = await PkiHarness.StartAsync(devices: [camera]);
        using var root = TestCa.Root("Acme Root CA");
        using var intermediate = TestCa.Intermediate(root);
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, WithKey(intermediate, root))).Ok);
        var authority = new PkiCertificateAuthority(pki.Service);
        var current = authority; // the store EST asks (ICurrentCertificates)
        var request = new DeviceCertificateRequest(RevocationTests.Csr(), "10.0.0.48", ["10.0.0.48"], ["axis-b8a44f631339.local"], DeviceCertificateUse.Server)
        {
            DeviceId = camera.Id,
            Validity = TimeSpan.FromDays(30),
            Source = IssuedCertificateSources.Est,
            IssuerId = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(intermediate)),
        };

        // Pending (the answer not written yet): not current.
        var first = await authority.SignAsync(request, CancellationToken.None);
        Assert.Null(await current.CurrentCameraAsync(first.Certificate, CancellationToken.None));
        await authority.DeliveredAsync(first.SerialNumber, CancellationToken.None);
        Assert.Equal("B8A44F631339", await current.CurrentCameraAsync(first.Certificate, CancellationToken.None));

        // The same CA, a certificate issued outside OADM (naming the camera): never.
        using var elsewhere = TestCa.Leaf(intermediate);
        Assert.Null(await current.CurrentCameraAsync(elsewhere.RawData, CancellationToken.None));

        // A newer one became current: the older one no longer names the camera (it is revoked as replaced).
        var second = await authority.SignAsync(request, CancellationToken.None);
        await authority.DeliveredAsync(second.SerialNumber, CancellationToken.None);
        Assert.Null(await current.CurrentCameraAsync(first.Certificate, CancellationToken.None));
        Assert.Equal("B8A44F631339", await current.CurrentCameraAsync(second.Certificate, CancellationToken.None));

        // Revoked: no longer.
        await authority.RevokeAsync([second.SerialNumber], RevocationReason.KeyCompromised, "admin", CancellationToken.None);
        Assert.Null(await current.CurrentCameraAsync(second.Certificate, CancellationToken.None));
        Assert.Null(await current.CurrentCameraAsync([1, 2, 3], CancellationToken.None));
    }

    [Fact]
    public async Task An_issuing_policy_picks_any_ca_with_its_key_and_that_ca_cannot_be_removed_while_used()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var root = TestCa.Root("Contoso Root");
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, WithKey(root))).Ok);
        var id = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(root));
        var https = pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;

        var saved = await pki.Policies.SaveAsync(https with { CaId = id }, "admin", Suites, CancellationToken.None);
        Assert.Null(saved.Error);
        Assert.Equal("Enable HTTPS issues certificates from Contoso Root, valid 365 days, with an ECC P-256 key.", pki.Policies.Rows([]).Single(r => r.Policy.Id == https.Id).Summary);
        Assert.Same(pki.Service.IssuingCa(id), Tasks.CertificateDeployment.RequireCa(pki.Service, saved.Saved));

        // Used by a policy: Remove is refused with the policy's name; Used in names it.
        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest("ca:" + CaCertificates.Fingerprint(root)));
        Assert.Equal(PolicyTexts.UsedByPolicy("HTTPS"), refused.Error);
        Assert.Equal(["HTTPS"], pki.Service.UsedIn(pki.Service.Authorities().Single(a => a.Id == id)));
        var row = (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest())).Rows.Single(r => r.AuthorityId == id);
        Assert.Equal(["HTTPS"], row.UsedIn);
        Assert.True(row.CanIssue);

        // Back to the OADM CA: the CA is free and can go.
        Assert.Null((await pki.Policies.SaveAsync(saved.Saved! with { CaId = CertificateAuthorityInfo.OadmId }, "admin", Suites, CancellationToken.None)).Error);
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest("ca:" + CaCertificates.Fingerprint(root)))).Ok);
        Assert.Null(pki.Service.IssuingCa(id));
        Assert.Single(pki.Anchors.Current.Certificates);
    }

    [Fact]
    public void The_kinds_convert_server_certificates_into_the_server_policies_and_revoke_into_block()
    {
        var dot1xServer = new PkiPolicy { Id = Guid.NewGuid(), Name = "802.1X server", Kind = PolicyKinds.Dot1xServer, MinimumTls = "1.3", LargestEapPacket = 1200 };
        var stored = new List<PkiPolicy>
        {
            new() { Id = Guid.NewGuid(), Name = "HTTPS", Kind = PolicyKinds.HttpsCertificate, CaId = "oadm", ValidDays = 365, Key = PolicyKeys.Ecc },
            new() { Id = Guid.NewGuid(), Name = "802.1X server", Kind = PolicyKinds.LegacyServerCertificate, CaId = "oadm", ValidDays = 200, Key = PolicyKeys.Ecc, Server = PolicyServers.Dot1x },
            new() { Id = Guid.NewGuid(), Name = "EST server", Kind = PolicyKinds.LegacyServerCertificate, CaId = "oadm", ValidDays = 60, Key = PolicyKeys.Rsa, Server = PolicyServers.Est },
            dot1xServer,
            new() { Id = Guid.NewGuid(), Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Tags = ["Storage"] } },
            new() { Id = Guid.NewGuid(), Name = "Stolen", Kind = PolicyKinds.LegacyRevoke, Devices = new PolicyDevices { Serials = ["ACCC8E000001"] }, SavedBy = "admin" },
            new() { Id = Guid.NewGuid(), Name = "Storage room", Kind = PolicyKinds.LegacyRevoke, Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } },
        };

        var converted = PolicyStore.ConvertKinds(stored, Radius.Server.RadiusPolicies.DefaultServerPolicy);

        Assert.Equal(
            [("HTTPS", PolicyKinds.HttpsCertificate), ("EST server", PolicyKinds.EstServer), ("802.1X server", PolicyKinds.Dot1xServer),
             ("Storage room", PolicyKinds.Block), ("Stolen", PolicyKinds.Block), ("Storage room (2)", PolicyKinds.Block)],
            converted.Select(p => (p.Name, p.Kind)));
        var est = converted[1];
        Assert.Equal((stored[2].Id, "oadm", 60, PolicyKeys.Rsa), (est.Id, est.CaId!, est.ValidDays!.Value, est.Key!)); // the user's values stay
        var server = converted[2];
        Assert.Equal((dot1xServer.Id, "1.3", 1200, 200, PolicyKeys.Ecc), (server.Id, server.MinimumTls!, server.LargestEapPacket!.Value, server.ValidDays!.Value, server.Key!));
        Assert.Equal([false, true, true], converted.Where(p => p.Kind == PolicyKinds.Block).Select(p => p.AlsoRevoke!.Value));
        Assert.Equal("admin", converted[4].SavedBy);
        Assert.Equal(["ACCC8E000001"], converted[4].Devices!.Serials);
        Assert.All(converted, p => Assert.True(PolicyKinds.IsKnown(p.Kind)));

        // Converted again: nothing changes (idempotent).
        Assert.Equal(converted, PolicyStore.ConvertKinds(converted, Radius.Server.RadiusPolicies.DefaultServerPolicy));

        // An 802.1X server policy deleted on an older build: one with the TLS defaults takes the certificate's values.
        var without = PolicyStore.ConvertKinds([stored[1]], Radius.Server.RadiusPolicies.DefaultServerPolicy);
        Assert.Equal([PolicyKinds.Dot1xServer, PolicyKinds.EstServer], without.Select(p => p.Kind));
        Assert.Equal((stored[1].Id, 200, PolicyRules.DefaultMinimumTls), (without[0].Id, without[0].ValidDays!.Value, without[0].MinimumTls!));
    }

    [Fact]
    public async Task The_kinds_convert_once_on_start_and_never_a_list_that_cannot_be_read()
    {
        var stored = new StoredPolicies
        {
            Policies =
            [
                new() { Id = Guid.NewGuid(), Name = "EST server", Kind = PolicyKinds.LegacyServerCertificate, CaId = "oadm", ValidDays = 45, Key = PolicyKeys.Ecc, Server = PolicyServers.Est },
                new() { Id = Guid.NewGuid(), Name = "Stolen", Kind = PolicyKinds.LegacyRevoke, Devices = new PolicyDevices { Serials = ["ACCC8E000001"] } },
            ],
            Converted = [PkiPluginInfo.PluginId, PkiService.IssuingOwner],
        };
        var settings = new InMemoryPluginSettingsProvider();
        var own = settings.GetSettings(PkiPluginInfo.PluginId);
        await own.SetAsync(PolicyStore.SettingsKey, PkiJson.Serialize(stored), CancellationToken.None);

        await using (var pki = await PkiHarness.StartAsync(settings))
        {
            Assert.Equal([(PolicyKinds.EstServer, 45), (PolicyKinds.Block, 0)], pki.Policies.All.Select(p => (p.Kind, p.ValidDays ?? 0)));
            Assert.True(pki.Policies.FirstOf(PolicyKinds.Block)!.AlsoRevoke);
            Assert.Contains(PkiService.KindsOwner, pki.Policies.Converted);

            // A change made since stays: the conversion runs once.
            Assert.Null((await pki.Policies.SaveAsync(pki.Policies.FirstOf(PolicyKinds.Block)! with { AlsoRevoke = false }, "admin", Suites, CancellationToken.None)).Error);
        }

        await using (var again = await PkiHarness.StartAsync(settings))
        {
            Assert.False(again.Policies.FirstOf(PolicyKinds.Block)!.AlsoRevoke);
            Assert.Equal(2, again.Policies.All.Count);
        }

        // A stored list that cannot be read is never rewritten.
        var damaged = new InMemoryPluginSettingsProvider();
        await damaged.GetSettings(PkiPluginInfo.PluginId).SetAsync(PolicyStore.SettingsKey, "{\"policies\":5}", CancellationToken.None);
        await using var broken = await PkiHarness.StartAsync(damaged);
        Assert.True(broken.Policies.IsUnreadable);
        Assert.Equal("{\"policies\":5}", await damaged.GetSettings(PkiPluginInfo.PluginId).GetAsync(PolicyStore.SettingsKey, CancellationToken.None));
        Assert.False(await broken.Policies.RewriteOnceAsync("test", list => [], CancellationToken.None));
    }

    [Fact]
    public void The_used_in_column_shows_the_first_name_then_the_count_and_every_name_as_tooltip()
    {
        Assert.Empty(IssuedCertificateTexts.UsedInBadges([]));
        Assert.Equal(["HTTPS"], IssuedCertificateTexts.UsedInBadges(["HTTPS"]));
        Assert.Equal(["HTTPS", "+3"], IssuedCertificateTexts.UsedInBadges(["HTTPS", "IEEE 802.1X", "802.1X server", "OADM CA"]));
        var row = new IssuedCertificateRow(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority, UsedIn = ["HTTPS", "IEEE 802.1X", "802.1X server"] });
        Assert.Equal("HTTPS" + Environment.NewLine + "IEEE 802.1X" + Environment.NewLine + "802.1X server", row.UsedInTip);
        Assert.Null(new IssuedCertificateRow(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority }).UsedInTip);
    }
}
