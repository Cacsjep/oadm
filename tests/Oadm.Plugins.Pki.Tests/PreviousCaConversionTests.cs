using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Decision 2026-10-26 (item 16): previous OADM CAs are ordinary CAs. Nothing follows the first-start CA implicitly any more;
/// once on update, the 802.1X trusted CA policy of the first-start CA gets one policy per previous CA that is still valid, with
/// its settings, so every camera passes as before. Runs once (marked in the stored policies) and is safe on a new server.
/// </summary>
public sealed class PreviousCaConversionTests
{
    private static PkiPolicy AllowCa(string name, string caId, bool onlyManaged) =>
        new() { Id = Guid.NewGuid(), Name = name, Kind = PolicyKinds.AllowCa, CaId = caId, OnlyManaged = onlyManaged };

    private static StoredPreviousCa Previous(X509Certificate2 ca) =>
        new() { Id = CaCertificates.Fingerprint(ca), CertificatePem = CaCertificates.ToPem(ca), ReplacedUtc = DateTime.UtcNow.AddYears(-1) };

    /// <summary>A server of a build before the decision: the 802.1X server converted, "OADM CA" trusts the OADM CA for every device.</summary>
    private static async Task<InMemoryPluginSettingsProvider> UpdatedServerAsync(params X509Certificate2[] previous)
    {
        var settings = new InMemoryPluginSettingsProvider();
        var pki = settings.GetSettings(PkiPluginInfo.PluginId);
        var policies = new StoredPolicies
        {
            Policies = [AllowCa("OADM CA", CertificateAuthorityInfo.OadmId, onlyManaged: false), AllowCa("Axis device ID", CertificateAuthorityInfo.AxisDeviceIdId, onlyManaged: true)],
            Converted = [PolicyStore.RadiusOwner],
        };
        await pki.SetAsync(PolicyStore.SettingsKey, PkiJson.Serialize(policies), CancellationToken.None);
        await pki.SetAsync(PkiStore.PreviousCasKey, PkiJson.Serialize(previous.Select(Previous).ToList()), CancellationToken.None);
        return settings;
    }

    [Fact]
    public async Task Each_previous_ca_still_valid_gets_its_own_trusted_ca_policy_with_the_settings_of_the_first_start_cas_policy()
    {
        using var older = TestCa.Root("OADM Root CA OLD");
        using var old = TestCa.Root("OADM Root CA OLD"); // the same name: made unique
        using var ended = TestCa.Root("OADM Root CA ENDED", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-2));
        var settings = await UpdatedServerAsync(older, old, ended);

        await using var pki = await PkiHarness.StartAsync(settings);

        var allowCa = pki.Policies.PoliciesOf(PolicyKinds.AllowCa);
        var olderId = PkiService.PreviousAuthorityId(CaCertificates.Fingerprint(older));
        var oldId = PkiService.PreviousAuthorityId(CaCertificates.Fingerprint(old));
        Assert.Equal(["OADM CA", "OADM Root CA OLD", "OADM Root CA OLD (2)", "Axis device ID"], allowCa.Select(p => p.Name));
        Assert.Equal([CertificateAuthorityInfo.OadmId, olderId, oldId, CertificateAuthorityInfo.AxisDeviceIdId], allowCa.Select(p => p.CaId));
        Assert.All(allowCa.Take(3), p => Assert.False(p.OnlyManaged)); // the settings of "OADM CA"
        Assert.DoesNotContain(allowCa, p => p.Name.Contains("ENDED", StringComparison.Ordinal)); // an ended CA is no longer listed: nothing to trust
        Assert.Contains(PolicyStore.PreviousCasOwner, pki.Policies.Converted);

        // The 802.1X server trusts each previous CA exactly as the first-start CA's policy did before (then it followed it).
        var chosen = CaTrust.Chosen(pki.Service.Authorities(), RadiusRuleSet.Of(pki.Policies).Selection);
        Assert.False(chosen[olderId]);
        Assert.False(chosen[oldId]);
        Assert.Equal(chosen[CertificateAuthorityInfo.OadmId], chosen[olderId]);

        // The page names the policies in each CA's Used in, and the previous CA cannot be deleted while one names it.
        Assert.Equal(["OADM Root CA OLD"], pki.Service.UsedIn(pki.Service.Authorities().Single(a => a.Id == allowCa[1].CaId)));
    }

    [Fact]
    public async Task The_conversion_runs_once_a_policy_deleted_afterwards_stays_deleted()
    {
        using var old = TestCa.Root("OADM Root CA OLD");
        var settings = await UpdatedServerAsync(old);
        var secrets = TestSecrets.Create();
        Guid converted;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            converted = pki.Policies.PoliciesOf(PolicyKinds.AllowCa).Single(p => p.Name == "OADM Root CA OLD").Id;
            Assert.Null((await pki.Policies.DeleteAsync(converted, "admin", CancellationToken.None)).Error);
        }

        await using var again = await PkiHarness.StartAsync(settings, secrets);
        Assert.Equal(["OADM CA", "Axis device ID"], again.Policies.PoliciesOf(PolicyKinds.AllowCa).Select(p => p.Name));
        Assert.False(await again.Policies.ConvertPreviousCasOnceAsync(CancellationToken.None));
        Assert.Single(again.Policies.Converted, PolicyStore.PreviousCasOwner);
    }

    [Fact]
    public async Task A_new_server_waits_for_the_8021x_conversion_and_then_changes_nothing()
    {
        await using var pki = await PkiHarness.StartAsync();
        Assert.DoesNotContain(PolicyStore.PreviousCasOwner, pki.Policies.Converted); // no trusted CA policies yet: nothing marked

        await pki.Policies.ConvertOnceAsync(PolicyStore.RadiusOwner, _ => Task.FromResult(RadiusPolicies.DefaultAllowCaPolicies(pki.Service.Authorities())), CancellationToken.None);
        var before = pki.Policies.All.ToList();
        Assert.True(await pki.Policies.ConvertPreviousCasOnceAsync(CancellationToken.None));

        Assert.Equal(before, pki.Policies.All);
        Assert.Contains(PolicyStore.PreviousCasOwner, pki.Policies.Converted);
    }

    [Fact]
    public void A_previous_ca_a_policy_names_already_or_a_list_without_the_first_start_cas_policy_stays_as_it_is()
    {
        var previous = new CertificateAuthorityInfo("oadm-previous:AA", "OADM Root CA OLD", CertificateAuthorityKind.PreviousOadm, [], DateTime.UtcNow.AddYears(1), false);
        var authorities = new[] { previous };
        var named = new List<PkiPolicy> { AllowCa("OADM CA", CertificateAuthorityInfo.OadmId, true), AllowCa("Old", previous.Id, false) };
        Assert.Equal(named, PolicyStore.ConvertPreviousCas(named, authorities));

        var withoutOadm = new List<PkiPolicy> { AllowCa("Axis device ID", CertificateAuthorityInfo.AxisDeviceIdId, true) };
        Assert.Equal(withoutOadm, PolicyStore.ConvertPreviousCas(withoutOadm, authorities));

        var once = PolicyStore.ConvertPreviousCas([AllowCa("OADM CA", CertificateAuthorityInfo.OadmId, true)], authorities);
        var added = Assert.Single(once, p => p.CaId == previous.Id);
        Assert.Equal("OADM Root CA OLD", added.Name);
        Assert.True(added.OnlyManaged);
        Assert.Equal(once, PolicyStore.ConvertPreviousCas(once, authorities)); // idempotent
    }

    [Fact]
    public async Task A_previous_ca_is_an_ordinary_choice_of_an_8021x_trusted_ca_policy()
    {
        using var old = TestCa.Root("OADM Root CA OLD");
        await using var pki = await PkiHarness.StartAsync(await UpdatedServerAsync(old));
        var id = PkiService.PreviousAuthorityId(CaCertificates.Fingerprint(old));

        Assert.Contains(pki.Policies.CaChoices(), c => c.Id == id && c.Name == "OADM Root CA OLD");
        Assert.DoesNotContain(pki.Policies.UsingCa(CertificateAuthorityInfo.OadmId), p => p.CaId == id); // the first-start CA's policy covers only that CA
        Assert.Single(pki.Policies.UsingCa(id));
    }
}
