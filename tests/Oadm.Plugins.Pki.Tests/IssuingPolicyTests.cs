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
        Assert.Equal(("IEEE 802.1X", 365, PolicyKeys.Ecc, 1, Dot1xIdentity.Mac, RadiusCaSource.Oadm), (dot1x.Name, dot1x.ValidDays!.Value, dot1x.Key, dot1x.EapolVersion!.Value, dot1x.Identity, dot1x.RadiusCa));
        // The EST server's certificate has its own policy (decision 2026-10-25); the 802.1X server's is part of its 802.1X
        // server policy (made by the 802.1X server's conversion, not started here).
        var est = Assert.Single(store.PoliciesOf(PolicyKinds.EstServer));
        Assert.Equal(("EST server", CertificateAuthorityInfo.OadmId, 90, PolicyKeys.Ecc), (est.Name, est.CaId, est.ValidDays!.Value, est.Key!));
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
        Assert.Equal(PolicyFields.EapolVersion, (await RefusedAsync(store, dot1x with { EapolVersion = 3 }))?.Field);
        Assert.Equal(PolicyFields.Identity, (await RefusedAsync(store, dot1x with { Identity = "ip" }))?.Field);
        Assert.Equal(new PolicyFieldError(PolicyFields.CustomIdentity, "Enter the identity."), await RefusedAsync(store, dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = " " }));
        Assert.Equal(PolicyFields.CustomIdentity, (await RefusedAsync(store, dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = "{mac}" }))?.Field);
        Assert.Equal(new PolicyFieldError(PolicyFields.RadiusCa, "Import the CA certificate of the RADIUS server."), await RefusedAsync(store, dot1x with { RadiusCa = RadiusCaSource.Imported }));
        Assert.Equal(new PolicyFieldError(PolicyFields.RadiusCa, PolicyTexts.RadiusCaUnreadable), await RefusedAsync(store, dot1x with { RadiusCa = RadiusCaSource.Imported, RadiusCaPem = "not a certificate" }));
        var custom = (await store.SaveAsync(dot1x with { Identity = Dot1xIdentity.Custom, CustomIdentity = " cam-{serial} " }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal("cam-{serial}", custom.CustomIdentity);
        var mac = (await store.SaveAsync(custom with { Identity = Dot1xIdentity.Mac }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal(string.Empty, mac.CustomIdentity); // only the fields that count are kept

        // The server policies: one each, the 802.1X server with ECC (its cipher suites follow the key).
        var server = (await store.SaveAsync(TestServerPolicy(), "admin", Suites, CancellationToken.None)).Saved!;
        Assert.Equal((CertificateAuthorityInfo.OadmId, 365, PolicyKeys.Ecc), (server.CaId, server.ValidDays!.Value, server.Key));
        Assert.Equal(new PolicyFieldError(PolicyFields.Key, PolicyTexts.Dot1xServerNeedsEcc), await RefusedAsync(store, server with { Key = PolicyKeys.Rsa }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.OneOnly(PolicyKinds.Dot1xServer)), await RefusedAsync(store, server with { Id = Guid.Empty, Name = "Second" }));
        var est = store.EstServerPolicy!;
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.OneOnly(PolicyKinds.EstServer)), await RefusedAsync(store, est with { Id = Guid.Empty, Name = "Second" }));
        Assert.Null(await RefusedAsync(store, est with { Key = PolicyKeys.Rsa, ValidDays = 30 }));
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
        Assert.Equal((397, Dot1xIdentity.Custom, "cam-{serial}", RadiusCaSource.Imported), (dot1x.ValidDays!.Value, dot1x.Identity, dot1x.CustomIdentity, dot1x.RadiusCa));
        Assert.True(PolicyRules.IsCertificatePem(dot1x.RadiusCaPem));
        Assert.Null(PolicyRules.Validate(dot1x, Suites));
        Assert.Equal(397, pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!.ValidDays);
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
            Assert.Equal(["oadm.pki", "oadm.radius", PkiService.IssuingOwner, PkiService.KindsOwner], pki.Policies.Converted);
            var server = pki.Policies.ServerPolicy!; // its certificate's defaults (stage 3 had none)
            Assert.Equal((CertificateAuthorityInfo.OadmId, 365, PolicyKeys.Ecc), (server.CaId, server.ValidDays!.Value, server.Key));
        }

        await using var again = await PkiHarness.StartAsync(settings);
        Assert.Equal(6, again.Policies.All.Count); // idempotent
    }
}
