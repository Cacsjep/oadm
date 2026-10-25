using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The issuing policies (docs/spec/pki.md "Policies", stage 4): HTTPS certificate, IEEE 802.1X certificate and Server
/// certificate, their checks, the defaults of a new server, the refused deletes and the conversion of PKI's settings.
/// </summary>
public sealed class IssuingPolicyTests
{
    private static readonly IReadOnlyList<PolicyCipherSuite> Suites = PkiService.CipherSuites;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static PkiPolicy Https(string name, int? days = 365, string? key = PolicyKeys.Ecc, string? ca = CertificateAuthorityInfo.OadmId) =>
        new() { Name = name, Kind = PolicyKinds.HttpsCertificate, CaId = ca, ValidDays = days, Key = key };

    private static PkiPolicy TestServerPolicy() => Radius.Server.RadiusPolicies.DefaultServerPolicy();

    private static async Task<PolicyFieldError?> RefusedAsync(PolicyStore store, PkiPolicy policy) => (await store.SaveAsync(policy, "admin", Suites, CancellationToken.None)).Error;

    [Fact]
    public async Task A_new_server_starts_with_the_default_issuing_policies()
    {
        await using var pki = await PkiHarness.StartAsync();
        var store = pki.Policies;

        var https = Assert.Single(store.PoliciesOf(PolicyKinds.HttpsCertificate));
        Assert.Equal(("HTTPS", CertificateAuthorityInfo.OadmId, 365, PolicyKeys.Ecc), (https.Name, https.CaId, https.ValidDays!.Value, https.Key));
        var dot1x = Assert.Single(store.PoliciesOf(PolicyKinds.Dot1xCertificate));
        // A new installation: EAPOL version 3 and the IP address as identity, the OADM CA as RADIUS server CA (decision 2026-10-25).
        Assert.Equal(("IEEE 802.1X", 365, PolicyKeys.Ecc, 3, Dot1xIdentity.IpAddress, CertificateAuthorityInfo.OadmId),
            (dot1x.Name, dot1x.ValidDays!.Value, dot1x.Key, dot1x.EapolVersion!.Value, dot1x.Identity, dot1x.RadiusCaId));
        // The EST server's policy picks a certificate of the list (decision 2026-10-25); none before OADM PRO makes one.
        var est = Assert.Single(store.PoliciesOf(PolicyKinds.EstServer));
        Assert.Equal(("EST server", null, null), (est.Name, est.CertificateId, est.ValidDays));
        Assert.DoesNotContain(store.All, p => p.Kind == PolicyKinds.LegacyServerCertificate);
        Assert.All(store.All.Where(p => PolicyKinds.IsIssuing(p.Kind)), p => Assert.Null(PolicyRules.Validate(p, Suites)));
        Assert.Contains(PkiService.IssuingOwner, store.Converted);
        Assert.Equal(365, pki.Service.DefaultValidDays);
    }

    [Fact]
    public async Task Every_issuing_policy_is_checked_like_the_fields_it_replaces()
    {
        await using var pki = await PkiHarness.StartAsync();
        var store = pki.Policies;

        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.ChooseCa), await RefusedAsync(store, Https("No CA", ca: null)));
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.CannotIssue), await RefusedAsync(store, Https("Factory", ca: CertificateAuthorityInfo.AxisDeviceIdId)));
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.CannotIssue), await RefusedAsync(store, Https("Gone", ca: "imported:AB12")));
        Assert.Equal(new PolicyFieldError(PolicyFields.ValidDays, PolicyTexts.ValidDaysRange), await RefusedAsync(store, Https("Zero", days: 0)));
        Assert.Equal(new PolicyFieldError(PolicyFields.ValidDays, PolicyTexts.ValidDaysRange), await RefusedAsync(store, Https("Long", days: 3651)));
        Assert.Equal(new PolicyFieldError(PolicyFields.Key, PolicyTexts.ChooseKey), await RefusedAsync(store, Https("Dsa", key: "dsa")));
        Assert.Equal(new PolicyFieldError(PolicyFields.Name, PolicyTexts.NameTaken(PolicyKinds.HttpsCertificate)), await RefusedAsync(store, Https("https")));
        var twoYears = (await store.SaveAsync(Https("HTTPS 2 years", 730, PolicyKeys.Rsa), "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal((730, PolicyKeys.Rsa), (twoYears.ValidDays!.Value, twoYears.Key));
        Assert.Null(twoYears.Devices); // issuing policies have no devices
        Assert.Equal("OADM Root CA SERVER01, 730 days, RSA", PolicyStore.IssuingSummary(twoYears, new Dictionary<string, string> { ["oadm"] = "OADM Root CA SERVER01" }));

        // IEEE 802.1X: the rules of the former IEEE 802.1X card.
        var dot1x = store.FirstOf(PolicyKinds.Dot1xCertificate)!;
        Assert.Equal(PolicyFields.EapolVersion, (await RefusedAsync(store, dot1x with { EapolVersion = 4 }))?.Field);
        Assert.Equal(PolicyFields.Identity, (await RefusedAsync(store, dot1x with { Identity = "ip" }))?.Field);
        Assert.Equal(new PolicyFieldError(PolicyFields.CustomIdentity, "Enter the identity."), await RefusedAsync(store, dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = " " }));
        Assert.Equal(PolicyFields.CustomIdentity, (await RefusedAsync(store, dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = "{mac}" }))?.Field);
        Assert.Equal(new PolicyFieldError(PolicyFields.RadiusCa, PolicyTexts.ChooseRadiusCa), await RefusedAsync(store, dot1x with { RadiusCaId = "imported:gone" }));
        var custom = (await store.SaveAsync(dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = " cam-{serial} " }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal("cam-{serial}", custom.CustomIdentity);
        var mac = (await store.SaveAsync(custom with { Identity = Dot1xIdentity.Mac }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal(string.Empty, mac.CustomIdentity); // only the fields that count are kept

        // The server policies: one each, a certificate of the list (decision 2026-10-25; OADM PRO made the shared one).
        Assert.Equal(PolicyFields.Certificate, (await RefusedAsync(store, TestServerPolicy() with { CertificateId = null }))?.Field);
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        var est = store.EstServerPolicy!;
        Assert.NotNull(est.CertificateId);
        var server = (await store.SaveAsync(TestServerPolicy() with { CertificateId = est.CertificateId }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal(est.CertificateId, server.CertificateId);
        Assert.Equal(new PolicyFieldError(PolicyFields.Certificate, PolicyTexts.CertificateGone), await RefusedAsync(store, server with { CertificateId = "gone" }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.OneOnly(PolicyKinds.Dot1xServer)), await RefusedAsync(store, server with { Id = Guid.Empty, Name = "Second" }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.OneOnly(PolicyKinds.EstServer)), await RefusedAsync(store, est with { Id = Guid.Empty, Name = "Second" }));
    }

    [Fact]
    public async Task The_tasks_and_servers_keep_their_policies_and_an_est_profile_its_own()
    {
        await using var pki = await PkiHarness.StartAsync();
        var store = pki.Policies;
        var https = store.FirstOf(PolicyKinds.HttpsCertificate)!;
        var dot1x = store.FirstOf(PolicyKinds.Dot1xCertificate)!;

        // The first policy of each issuing kind is a system policy: it stays (the tasks always have one).
        Assert.Equal(PolicyTexts.SystemPolicy, (await store.DeleteAsync(https.Id, "admin", CancellationToken.None)).Error?.Message);
        Assert.Equal("OADM needs this policy. Change its values instead.", (await store.DeleteAsync(dot1x.Id, "admin", CancellationToken.None)).Error?.Message);
        Assert.Equal(PolicyTexts.SystemPolicy, (await store.DeleteAsync(store.EstServerPolicy!.Id, "admin", CancellationToken.None)).Error?.Message);

        // A second HTTPS policy is the user's: it can go, unless an EST profile names it.
        var twoYears = (await store.SaveAsync(Https("HTTPS 2 years", 730), "admin", Suites, CancellationToken.None)).Saved!;
        Assert.False(PolicyStore.IsSystem(twoYears, store.All));
        store.ProfileUses = _ => Task.FromResult<IReadOnlyList<(Guid, string)>>([(twoYears.Id, "https"), (twoYears.Id, "rtsps")]);
        Assert.Equal("Used by the EST profiles https and rtsps.", (await store.DeleteAsync(twoYears.Id, "admin", CancellationToken.None)).Error?.Message);
        Assert.Equal("Used by the EST profile https.", PolicyTexts.UsedByProfiles(["https"]));
        Assert.Equal(PolicyTexts.SystemPolicy, (await store.DeleteAsync(https.Id, "admin", CancellationToken.None)).Error?.Message); // still the system policy
        store.ProfileUses = _ => Task.FromResult<IReadOnlyList<(Guid, string)>>([]);
        Assert.Null((await store.DeleteAsync(twoYears.Id, "admin", CancellationToken.None)).Error);
        Assert.Equal(https.Id, Assert.Single(store.PoliciesOf(PolicyKinds.HttpsCertificate)).Id); // the dialogs preselect the oldest
    }

    [Fact]
    public async Task The_settings_of_oadm_41_become_the_issuing_policies_once()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var own = settings.GetSettings(PkiPluginInfo.PluginId);
        await own.SetAsync(PkiStore.ConfigKey, Fixture("pki-config-4.1.json"), CancellationToken.None);

        await using (var pki = await PkiHarness.StartAsync(settings))
        {
            var https = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.HttpsCertificate));
            Assert.Equal(("HTTPS", 730, PolicyKeys.Rsa), (https.Name, https.ValidDays!.Value, https.Key)); // RSA: what the tasks made on the devices before
            var dot1x = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.Dot1xCertificate));
            Assert.Equal(("IEEE 802.1X", 730, PolicyKeys.Rsa, 2, Dot1xIdentity.Mac, RadiusCaSource.Oadm),
                (dot1x.Name, dot1x.ValidDays!.Value, dot1x.Key, dot1x.EapolVersion!.Value, dot1x.Identity, dot1x.RadiusCa));
            Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.EstServer));
            Assert.Equal(730, pki.Service.Config.DeviceCertValidityDays); // the old settings stay (no longer read)
            Assert.Null((await pki.Policies.SaveAsync(https with { ValidDays = 100 }, "admin", Suites, CancellationToken.None)).Error);
        }

        await using var again = await PkiHarness.StartAsync(settings);
        Assert.Equal(100, Assert.Single(again.Policies.PoliciesOf(PolicyKinds.HttpsCertificate)).ValidDays); // converted once: the change stays
        Assert.Single(again.Policies.PoliciesOf(PolicyKinds.Dot1xCertificate));
        Assert.Single(again.Policies.PoliciesOf(PolicyKinds.EstServer));
        Assert.Equal(Fixture("pki-config-4.1.json"), await own.GetAsync(PkiStore.ConfigKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_custom_identity_and_an_imported_radius_ca_of_oadm_41_stay()
    {
        var settings = new InMemoryPluginSettingsProvider();
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.ConfigKey, Fixture("pki-config-4.1-imported-radius.json"), CancellationToken.None);

        await using var pki = await PkiHarness.StartAsync(settings);

        var dot1x = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.Dot1xCertificate));
        Assert.Equal((397, Dot1xIdentity.Custom, "cam-{serial}"), (dot1x.ValidDays!.Value, dot1x.Identity, dot1x.CustomIdentity));
        // The imported RADIUS server CA of this fixture is an Axis device ID intermediate CA ("Axis device ID Intermediate CA
        // ECC 1"): it becomes an imported CA of its own, never the whole Axis device ID entry (the camera would then trust
        // every Axis root and intermediate as RADIUS server CA).
        using var intermediate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            System.Text.Json.JsonDocument.Parse(Fixture("pki-config-4.1-imported-radius.json")).RootElement.GetProperty("dot1x").GetProperty("radiusCaPem").GetString());
        Assert.Equal(PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(intermediate)), dot1x.RadiusCaId);
        var listed = Assert.Single(pki.Service.Authorities(), a => a.Id == dot1x.RadiusCaId);
        Assert.Equal((CertificateAuthorityKind.Imported, false), (listed.Kind, listed.Issues));
        Assert.Equal([intermediate.RawData], listed.Certificates);
        Assert.Null(PolicyRules.Validate(dot1x, Suites));
        var radius = Tasks.CertificateDeployment.RadiusCas(dot1x, pki.Service);
        Assert.Equal(CaCertificates.Fingerprint(intermediate), Assert.Single(radius).Fingerprint); // exactly the chosen CA
        Assert.Contains(PkiService.RadiusCaOwner, pki.Policies.Converted);
        Assert.Equal(397, pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!.ValidDays);
    }

    [Fact]
    public async Task The_radius_ca_conversion_runs_after_every_ca_is_loaded_and_keeps_the_imported_cas()
    {
        // A server with an OADM CA, a previous OADM CA and an imported CA with its key.
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        string oadmPem;
        string importedId;
        await using (var first = await PkiHarness.StartAsync(settings, secrets))
        {
            Assert.True((await first.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true))).Ok);
            using var root = TestCa.Root("Contoso Root");
            using var rootPublic = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(root.RawData);
            Assert.True((await first.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(TestCa.Pfx("secret", root)), "contoso.pfx", "secret", null, null))).Ok);
            oadmPem = first.Service.Ca!.CertificatePem;
            importedId = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(rootPublic));
            Assert.Single(first.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.PreviousOadm);
        }

        // An update from OADM 4.1 whose IEEE 802.1X settings imported the OADM CA as RADIUS server CA: the policies are made
        // again from these settings at the next start.
        var own = settings.GetSettings(PkiPluginInfo.PluginId);
        var importedBefore = await own.GetAsync(PkiStore.ImportedCasKey, CancellationToken.None);
        await own.SetAsync(PolicyStore.SettingsKey, null, CancellationToken.None);
        await own.SetAsync(PkiStore.ConfigKey, PkiJson.Serialize(new PkiConfig
        {
            Dot1x = new Dot1xConfig { RadiusCa = RadiusCaSource.Imported, RadiusCaPem = oadmPem },
        }), CancellationToken.None);

        await using var pki = await PkiHarness.StartAsync(settings, secrets);

        // The PEM is the OADM CA's: the policy names the OADM CA, no duplicate imported CA, the imported CA keeps its key.
        Assert.Equal(CertificateAuthorityInfo.OadmId, pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!.RadiusCaId);
        Assert.Equal(importedId, Assert.Single(pki.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Imported).Id);
        Assert.NotNull(pki.Service.IssuingCa(importedId));
        Assert.Single(pki.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.PreviousOadm);
        Assert.Equal(importedBefore, await own.GetAsync(PkiStore.ImportedCasKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_server_of_stage_3_gets_the_issuing_policies_after_its_own()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var own = settings.GetSettings(PkiPluginInfo.PluginId);
        await own.SetAsync(PolicyStore.SettingsKey, Fixture("pki-policies-stage3.json"), CancellationToken.None);
        await own.SetAsync(PkiStore.ConfigKey, Fixture("pki-config-4.1.json"), CancellationToken.None);

        await using (var pki = await PkiHarness.StartAsync(settings))
        {
            Assert.Equal(
                ["All devices", "802.1X server", "OADM CA", "HTTPS", "IEEE 802.1X", "EST server"],
                pki.Policies.All.Select(p => p.Name));
            Assert.Equal(45, pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Days); // nothing of stage 3 converted again
            Assert.Equal(["oadm.pki", "oadm.radius", PkiService.IssuingOwner, PkiService.KindsOwner, PkiService.RadiusCaOwner], pki.Policies.Converted);
            var server = pki.Policies.ServerPolicy!; // its certificate's defaults (stage 3 had none)
            Assert.Equal((CertificateAuthorityInfo.OadmId, 365, PolicyKeys.Ecc), (server.CaId, server.ValidDays!.Value, server.Key));
        }

        await using var again = await PkiHarness.StartAsync(settings);
        Assert.Equal(6, again.Policies.All.Count); // idempotent
    }
}
