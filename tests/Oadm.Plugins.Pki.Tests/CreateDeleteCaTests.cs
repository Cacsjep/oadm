using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Create CA and Delete CA on the server (decision 2026-10-26, items 1 to 6, 13 and 14): a root or an intermediate CA with
/// the chosen key and years, refusals under the fields, Delete refused while a policy names the CA or it signed a listed CA,
/// a deleted CA's certificates stay, a deleted first-start CA or Axis device ID CA stays deleted after a restart, the Key
/// column and Install in trusted root store on every root CA.
/// </summary>
public sealed class CreateDeleteCaTests
{
    private static async Task<string> CreateAsync(PkiHarness pki, string name, string key = CaKeys.Ecc, string? issuer = null, int years = 5)
    {
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest(name, issuer is not null, issuer, key, years));
        Assert.True(reply.Ok, reply.Error ?? string.Join(", ", reply.Errors?.Values ?? []));
        return pki.Service.Authorities().Single(a => a.Name == name).Id;
    }

    private static CertificateAuthorityInfo Authority(PkiHarness pki, string id) => pki.Service.Authorities().Single(a => a.Id == id);

    /// <summary>The row id of a CA (<c>ca:&lt;SHA-256&gt;</c> of its certificate).</summary>
    private static string RowOf(PkiHarness pki, string id) => "ca:" + Convert.ToHexString(SHA256.HashData(Authority(pki, id).Certificates[0]));

    private static async Task<List<IssuedCertificateInfo>> CaRowsAsync(PkiHarness pki) =>
        [.. (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest())).Rows.Where(r => r.Kind == ListedCertificateKind.Authority)];

    private static Task<PkiReply> DeleteAsync(PkiHarness pki, string rowId) => pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(rowId));

    /// <summary>Points every policy that names <paramref name="from"/> to <paramref name="to"/> (issuing, RADIUS server CA); trusted and sign-in CA policies of it are deleted.</summary>
    private static async Task MovePoliciesAsync(PkiHarness pki, string from, string to)
    {
        foreach (var policy in pki.Policies.NamingCa(from))
        {
            if (policy.Kind is PolicyKinds.AllowCa or PolicyKinds.SignInCa)
            {
                Assert.Null((await pki.Policies.DeleteAsync(policy.Id, "admin", CancellationToken.None)).Error);
                continue;
            }

            var current = pki.Policies.Find(policy.Id)!;
            var moved = current with
            {
                CaId = string.Equals(current.CaId ?? CertificateAuthorityInfo.OadmId, from, StringComparison.OrdinalIgnoreCase) ? to : current.CaId,
                RadiusCaId = string.Equals(current.RadiusCaId, from, StringComparison.OrdinalIgnoreCase) ? to : current.RadiusCaId,
            };
            var reply = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, moved);
            Assert.Null(reply.Error);
        }

        Assert.Empty(pki.Policies.NamingCa(from));
    }

    [Fact]
    public async Task Create_ca_makes_a_root_or_an_intermediate_with_the_chosen_key_and_years_and_keeps_them_after_a_restart()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        string root, issuing, site;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            root = await CreateAsync(pki, "Contoso Root CA", CaKeys.Ecc, years: 5);
            issuing = await CreateAsync(pki, "Contoso Issuing CA", CaKeys.Rsa2048, issuer: root, years: 3);
            site = await CreateAsync(pki, "Building B CA", CaKeys.Rsa3072, issuer: issuing, years: 2); // signed by an intermediate

            // The root: self-signed, ECC P-256, the years asked for.
            using (var certificate = X509CertificateLoader.LoadCertificate(Authority(pki, root).Certificates[0]))
            {
                Assert.Equal(certificate.Subject, certificate.Issuer);
                Assert.Equal(256, certificate.GetECDsaPublicKey()!.KeySize);
                Assert.True(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
                Assert.InRange((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays, 365 * 5 - 2, 365 * 5 + 3);
            }

            // The intermediates: their chain up to the root, the chosen RSA size.
            var issuingChain = Authority(pki, issuing).Certificates.Select(X509CertificateLoader.LoadCertificate).ToList();
            Assert.Equal(2, issuingChain.Count);
            Assert.Equal(issuingChain[1].Subject, issuingChain[0].Issuer);
            Assert.Equal(Authority(pki, root).Certificates[0], issuingChain[1].RawData);
            Assert.Equal(2048, issuingChain[0].GetRSAPublicKey()!.KeySize);
            var siteChain = Authority(pki, site).Certificates.Select(X509CertificateLoader.LoadCertificate).ToList();
            Assert.Equal(3, siteChain.Count);
            Assert.Equal("CN=Contoso Issuing CA", siteChain[0].Issuer);
            Assert.Equal(3072, siteChain[0].GetRSAPublicKey()!.KeySize);

            // Each can issue at once; its row: the key type, Root CA or Intermediate CA.
            Assert.All(new[] { root, issuing, site }, id => Assert.NotNull(pki.Service.IssuingCa(id)));
            var rows = await CaRowsAsync(pki);
            Assert.Equal(("ECC P-256", true), (rows.Single(r => r.AuthorityId == root).KeyText, rows.Single(r => r.AuthorityId == root).IsRootCa));
            Assert.Equal(("RSA 2048", false), (rows.Single(r => r.AuthorityId == issuing).KeyText, rows.Single(r => r.AuthorityId == issuing).IsRootCa));
            Assert.Equal(("RSA 3072", false), (rows.Single(r => r.AuthorityId == site).KeyText, rows.Single(r => r.AuthorityId == site).IsRootCa));
            foreach (var id in new[] { root, issuing, site })
            {
                var row = new Oadm.Sdk.Client.Controls.IssuedCertificateRow(rows.Single(r => r.AuthorityId == id));
                Assert.Equal([row.Source.IsRootCa ? "Root CA" : "Intermediate CA"], row.Badges);
            }
        }

        // Kept encrypted: after a restart the same CAs, still with their key, and EST may sign cameras in with them.
        await using var again = await PkiHarness.StartAsync(settings, secrets);
        Assert.All(new[] { root, issuing, site }, id => Assert.NotNull(again.Service.IssuingCa(id)));
        Assert.Equal(3, again.Service.Authorities().Count(a => a.Id is var id && (id == root || id == issuing || id == site)));
        Assert.Contains(again.Policies.SignInCaChoices(), c => c.Id == root);
    }

    [Fact]
    public void The_create_ca_key_presets_rsa_4096_like_the_first_start_ca()
    {
        Assert.Equal(CaKeys.Rsa4096, CaKeys.Default);
        Assert.Equal(4096, PkiPluginInfo.CaKeySize); // the first-start root CA
        Assert.Equal(4096, CaKeys.RsaSize(CaKeys.Rsa4096));
        Assert.Null(CaKeys.RsaSize(CaKeys.Ecc));
        Assert.Equal(["ECC P-256", "RSA 2048", "RSA 3072", "RSA 4096"], CaKeys.All.Select(CaKeys.Name));
    }

    [Fact]
    public async Task Create_ca_refuses_wrong_input_under_its_fields_and_stores_nothing()
    {
        await using var pki = await PkiHarness.StartAsync();
        using (var contoso = TestCa.Root("Contoso Root"))
        {
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(contoso.RawData), "contoso.cer"))).Ok);
        }

        var before = pki.Service.Authorities().Count;
        var stored = await pki.Settings.GetAsync(PkiStore.ImportedCasKey, CancellationToken.None);
        Task<PkiReply> Create(CreateCaRequest request) => pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, request);

        var empty = (await Create(new CreateCaRequest(" ", false, null, "dsa", 0))).Errors!;
        Assert.Equal("Enter a name.", empty[PkiFields.CommonName]);
        Assert.Equal("Choose a key.", empty[PkiFields.Key]);
        Assert.Equal(PkiValidation.ValidityYears(0), empty[PkiFields.ValidityYears]);
        Assert.NotNull((await Create(new CreateCaRequest("Long", false, null, CaKeys.Ecc, PkiPluginInfo.MaxValidityYears + 1))).Errors![PkiFields.ValidityYears]);

        // Signed by: a CA with its key; a CA certificate without its key or an unknown id cannot sign.
        var contosoId = pki.Service.Authorities().Single(a => a.Name == "Contoso Root").Id;
        Assert.Equal("Choose a CA with its key.", (await Create(new CreateCaRequest("Sub", true, contosoId, CaKeys.Ecc, 5))).Errors![PkiFields.Issuer]);
        Assert.Equal("Choose a CA with its key.", (await Create(new CreateCaRequest("Sub", true, "imported:nope", CaKeys.Ecc, 5))).Errors![PkiFields.Issuer]);
        Assert.Equal("Choose a CA with its key.", (await Create(new CreateCaRequest("Sub", true, CertificateAuthorityInfo.AxisDeviceIdId, CaKeys.Ecc, 5))).Errors![PkiFields.Issuer]);

        // A name another CA has.
        Assert.Equal("Another CA has this name.", (await Create(new CreateCaRequest("oadm root ca server01", false, null, CaKeys.Ecc, 5))).Errors![PkiFields.CommonName]);

        Assert.Equal(before, pki.Service.Authorities().Count);
        Assert.Equal(stored, await pki.Settings.GetAsync(PkiStore.ImportedCasKey, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_ca_is_refused_while_a_policy_names_it_or_it_signed_a_listed_ca()
    {
        await using var pki = await PkiHarness.StartAsync();
        var naming = pki.Policies.NamingCa(CertificateAuthorityInfo.OadmId);
        Assert.NotEmpty(naming); // the built-in issuing policies issue with the first-start CA
        Assert.Equal(PolicyTexts.UsedByPolicy(naming[0].Name), (await DeleteAsync(pki, RowOf(pki, CertificateAuthorityInfo.OadmId))).Error);
        Assert.Equal("Used by the policy HTTPS. Change or delete the policy first.", PolicyTexts.UsedByPolicy("HTTPS"));

        var root = await CreateAsync(pki, "Site Root");
        var issuing = await CreateAsync(pki, "Site Issuing", issuer: root);
        Assert.Equal("It signed Site Issuing. Delete Site Issuing first.", (await DeleteAsync(pki, RowOf(pki, root))).Error);
        Assert.Contains(pki.Service.Authorities(), a => a.Id == root);

        // An issuing policy that names the intermediate keeps it, too.
        var https = pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, https with { CaId = issuing })).Error);
        Assert.Equal(PolicyTexts.UsedByPolicy(https.Name), (await DeleteAsync(pki, RowOf(pki, issuing))).Error);
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, pki.Policies.Find(https.Id)! with { CaId = CertificateAuthorityInfo.OadmId })).Error);

        // The intermediate first, then the root.
        Assert.True((await DeleteAsync(pki, RowOf(pki, issuing))).Ok);
        var rootRow = RowOf(pki, root);
        Assert.True((await DeleteAsync(pki, rootRow)).Ok);
        Assert.DoesNotContain(pki.Service.Authorities(), a => a.Id == root || a.Id == issuing);
        Assert.Equal("This CA is no longer in the list.", (await DeleteAsync(pki, rootRow)).Error);
    }

    [Fact]
    public async Task A_deleted_ca_keeps_its_certificates_and_a_deleted_first_start_ca_is_never_made_again()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        var records = new MemoryPluginRecords(); // the certificate list survives the restart
        var camera = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        string serial;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets, records: records, devices: [camera]))
        {
            var first = Authority(pki, CertificateAuthorityInfo.OadmId);
            serial = (await RevocationTests.SignAsync(new PkiCertificateAuthority(pki.Service), camera.Id, camera.Serial)).SerialNumber;
            var created = await CreateAsync(pki, "Contoso Root CA");
            await MovePoliciesAsync(pki, CertificateAuthorityInfo.OadmId, created);

            Assert.True((await DeleteAsync(pki, RowOf(pki, CertificateAuthorityInfo.OadmId))).Ok);

            // Gone from the list (it issues nothing any more); its certificates stay, nothing else is deleted or refused.
            Assert.DoesNotContain(pki.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Oadm);
            Assert.Null(pki.Service.IssuingCa(CertificateAuthorityInfo.OadmId));
            Assert.NotNull(pki.Service.Issued.Find(serial));
            Assert.False(pki.Service.Issued.IsRevoked(serial));
            var rows = (await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest())).Rows;
            Assert.Contains(rows, r => r.SerialNumber == CertificateSerials.Normalize(serial));
            Assert.Contains(pki.Service.Authorities(), a => a.Id == created);
            Assert.DoesNotContain(pki.Policies.IssuingCaChoices(), c => c.Id == CertificateAuthorityInfo.OadmId); // no placeholder of a CA being made
            Assert.DoesNotContain(first.Name, (await CaRowsAsync(pki)).Select(r => r.Name));
        }

        // A restart makes no new first-start CA in its place.
        await using var again = await PkiHarness.StartAsync(settings, secrets, records: records, devices: [camera]);
        await again.Service.Background;
        var state = await again.StateAsync();
        Assert.False(state.IsGenerating);
        Assert.DoesNotContain(again.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Oadm);
        Assert.Null(again.Service.Ca);
        Assert.NotNull(again.Service.Issued.Find(serial));
    }

    [Fact]
    public async Task The_first_start_ca_is_made_once_and_kept_on_every_restart()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        string id;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            id = pki.Service.Ca!.Id;
            Assert.Single(pki.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Oadm);
        }

        for (var i = 0; i < 2; i++)
        {
            await using var again = await PkiHarness.StartAsync(settings, secrets);
            Assert.Equal(id, again.Service.Ca!.Id);
            Assert.Single(again.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Oadm);
        }
    }

    [Fact]
    public async Task Deleted_axis_device_id_cas_stay_deleted_after_a_restart()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        int all;
        string deleted;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            foreach (var policy in pki.Policies.NamingCa(CertificateAuthorityInfo.AxisDeviceIdId))
            {
                Assert.Null((await pki.Policies.DeleteAsync(policy.Id, "admin", CancellationToken.None)).Error);
            }

            all = Authority(pki, CertificateAuthorityInfo.AxisDeviceIdId).Certificates.Count;
            var intermediate = (await CaRowsAsync(pki)).First(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId && !r.IsRootCa);
            deleted = intermediate.Id!;

            Assert.True((await DeleteAsync(pki, deleted)).Ok);
            Assert.Equal(all - 1, Authority(pki, CertificateAuthorityInfo.AxisDeviceIdId).Certificates.Count);
            Assert.DoesNotContain(await CaRowsAsync(pki), r => r.Id == deleted);
        }

        await using var again = await PkiHarness.StartAsync(settings, secrets);
        Assert.DoesNotContain(await CaRowsAsync(again), r => r.Id == deleted);
        Assert.Equal(all - 1, Authority(again, CertificateAuthorityInfo.AxisDeviceIdId).Certificates.Count);
    }

    [Fact]
    public async Task The_key_column_shows_the_key_no_key_or_that_the_key_cannot_be_read()
    {
        var settings = new InMemoryPluginSettingsProvider();
        using var acme = TestCa.Root("Acme Root CA");
        using var acmeIssuing = TestCa.Intermediate(acme);
        using var acmePublic = X509CertificateLoader.LoadCertificate(acme.RawData);
        await using (var pki = await PkiHarness.StartAsync(settings, TestSecrets.Create()))
        {
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(TestCa.Pfx("secret", acmeIssuing, acmePublic)), "acme.pfx", "secret", null, null))).Ok);
            using (var contoso = TestCa.Root("Contoso Root"))
            {
                Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(contoso.RawData), "contoso.cer"))).Ok);
            }

            var rows = await CaRowsAsync(pki);
            Assert.Equal("RSA 2048", rows.Single(r => r.AuthorityKind == CertificateAuthorityKind.Oadm).KeyText);
            Assert.StartsWith("RSA ", rows.Single(r => r.Name == "Acme Issuing CA").KeyText, StringComparison.Ordinal);
            Assert.Equal(CaKeyTexts.None, rows.Single(r => r.Name == "Contoso Root").KeyText);
            Assert.All(rows.Where(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId), r => Assert.Equal(CaKeyTexts.None, r.KeyText));
        }

        // Another master key (a copied data folder): the CAs stored with a key say so; the certificates without one still say No key.
        await using var other = await PkiHarness.StartAsync(settings, TestSecrets.Create());
        var after = await CaRowsAsync(other);
        Assert.Equal(CaKeyTexts.Unreadable, after.Single(r => r.AuthorityKind == CertificateAuthorityKind.Oadm).KeyText);
        Assert.Equal(CaKeyTexts.Unreadable, after.Single(r => r.Name == "Acme Issuing CA").KeyText);
        Assert.Equal(CaKeyTexts.None, after.Single(r => r.Name == "Contoso Root").KeyText);
        Assert.Equal(("No key", "Key cannot be read"), (CaKeyTexts.None, CaKeyTexts.Unreadable));
    }

    [Fact]
    public async Task Install_in_trusted_root_store_is_offered_on_every_root_ca_and_never_on_an_intermediate()
    {
        await using var pki = await PkiHarness.StartAsync();
        var root = await CreateAsync(pki, "Site Root");
        await CreateAsync(pki, "Site Issuing", issuer: root);
        using (var contoso = TestCa.Root("Contoso Root"))
        {
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(contoso.RawData), "contoso.cer"))).Ok);
        }

        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        using var vm = new PkiViewModel(ctx, new FakeClientTrust());
        await vm.LoadAsync();
        await vm.Authorities.LoadAsync();

        var rows = vm.Authorities.Rows.ToList();
        Assert.Contains(rows, r => r.Name == "Site Root" && r.IsRootCa);
        Assert.Contains(rows, r => r.Name == "Contoso Root" && r.IsRootCa);
        Assert.Contains(rows, r => r.Name == "Site Issuing" && !r.IsRootCa);
        Assert.All(rows, r => Assert.Equal(r.IsRootCa, vm.InstallTrustCommand.CanExecute(r)));
        Assert.All(rows, r => Assert.Equal(r.IsRootCa, vm.Authorities.MenuOf(r).Any(a => a.Text == "Install in trusted root store")));
    }
}
