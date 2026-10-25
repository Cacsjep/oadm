using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>PKI's policies for the EST tests: a store with the default issuing policies of a new server.</summary>
internal static class TestPolicies
{
    /// <summary>The OADM CA of the test store.</summary>
    public static CertificateAuthorityInfo OadmCa { get; } = new(CertificateAuthorityInfo.OadmId, "OADM Root CA TEST", CertificateAuthorityKind.Oadm, [], DateTime.UtcNow.AddYears(5), true);

    public static async Task<PolicyStore> StoreAsync(bool usedBefore = false, FakeSettings? settings = null, Func<IReadOnlyList<CertificateAuthorityInfo>>? authorities = null,
        Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>>? devices = null, IProtectedTags? tags = null)
    {
        var store = new PolicyStore(settings ?? new FakeSettings(), devices ?? (_ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([])), () => [],
            authorities ?? (() => [OadmCa]), tags);
        await store.LoadAsync(CancellationToken.None);
        await store.ConvertOnceAsync(PkiService.IssuingOwner, _ => Task.FromResult(PkiService.ConvertIssuing(new PkiConfig(), usedBefore)), CancellationToken.None);
        return store;
    }

    /// <summary>
    /// A store of a new server with the EST policies too (stage 6): the Enrollment policy "All devices" and the Sign-in CA
    /// policies "Axis device ID" (new) and "OADM CA" (renewals), converted from <paramref name="estSettings"/> (none: a new server).
    /// </summary>
    public static async Task<PolicyStore> EstStoreAsync(FakeSettings? estSettings = null, FakeSettings? settings = null, Func<IReadOnlyList<CertificateAuthorityInfo>>? authorities = null,
        Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>>? devices = null, IProtectedTags? tags = null)
    {
        var store = await StoreAsync(settings: settings, authorities: authorities, devices: devices, tags: tags);
        Assert.True(await EstPolicies.ConvertRulesAsync(estSettings ?? new FakeSettings(), store, CancellationToken.None));
        return store;
    }

    public static async Task<PkiPolicy> AddAsync(PolicyStore store, PkiPolicy policy)
    {
        var result = await store.SaveAsync(policy, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.Null(result.Error);
        return result.Saved!;
    }
}

/// <summary>
/// EST profiles name an issuing policy (docs/spec/pki.md "Issuing policies have no devices", est-server.md "Profiles"):
/// the conversion of the profiles of OADM 4.1 (once, idempotent), the profile's policy, the page's choices.
/// </summary>
public sealed class PolicyTests
{
    /// <summary>The EST settings of OADM 4.1: https as the default (365 days, ECC), dot1x with its own validity and key.</summary>
    private const string Config41 = """
        {"port":9454,"interfaceId":"all","interfaceName":"All interfaces","profiles":[
        {"name":"https","use":"Https","validityDays":365,"key":"Ecc","enabled":true},
        {"name":"dot1x","use":"Dot1x","validityDays":730,"key":"Rsa","enabled":true},
        {"name":"rtsps","use":"Rtsps","validityDays":365,"key":"Ecc","enabled":false}],
        "enrollmentsKept":500,"signInWith":["axis-device-id","oadm"]}
        """;

    [Fact]
    public async Task The_profiles_of_oadm_41_name_the_built_in_policy_of_their_kind_once()
    {
        // Decision 2026-10-25: no issuing policies of their own for EST; the profiles use the built-in ones.
        var store = await TestPolicies.StoreAsync();
        var settings = new FakeSettings();
        await settings.SetAsync(EstStore.ConfigKey, Config41, CancellationToken.None);
        var before = store.All.Count;

        Assert.True(await EstPolicies.ConvertAsync(settings, store, CancellationToken.None));

        var config = EstJson.Deserialize<EstConfig>(await settings.GetAsync(EstStore.ConfigKey, CancellationToken.None));
        var byName = config.Profiles.ToDictionary(p => p.Name, p => store.Find(p.PolicyId!.Value)!);
        Assert.Equal(("HTTPS", "HTTPS", "IEEE 802.1X"), (byName["https"].Name, byName["rtsps"].Name, byName["dot1x"].Name));
        Assert.Equal(before, store.All.Count); // nothing made
        Assert.Equal(730, config.Profiles.Single(p => p.Name == "dot1x").ValidityDays); // the old values stay, no longer read

        // Once: a second run changes nothing.
        var json = await settings.GetAsync(EstStore.ConfigKey, CancellationToken.None);
        Assert.False(await EstPolicies.ConvertAsync(settings, store, CancellationToken.None));
        Assert.Equal(json, await settings.GetAsync(EstStore.ConfigKey, CancellationToken.None));

        // PKI never deletes a policy a profile names.
        Assert.Contains((byName["dot1x"].Id, "dot1x"), await EstPolicies.UsesAsync(settings, CancellationToken.None));
    }

    [Fact]
    public async Task The_clean_up_points_the_profiles_at_the_built_in_policies_and_deletes_the_est_copies_once()
    {
        var store = await TestPolicies.StoreAsync();
        var builtIn = store.FirstOf(PolicyKinds.Dot1xCertificate)!;
        var copy = await TestPolicies.AddAsync(store, builtIn with { Id = Guid.Empty, Name = "EST 802.1X", ValidDays = 730 });
        var mine = await TestPolicies.AddAsync(store, store.FirstOf(PolicyKinds.HttpsCertificate)! with { Id = Guid.Empty, Name = "Lab HTTPS", ValidDays = 90 });
        var settings = new FakeSettings();
        var config = EstJson.Deserialize<EstConfig>(Config41);
        config = config with
        {
            Profiles = [.. config.Profiles.Select(p => p with { PolicyId = p.Name == "dot1x" ? copy.Id : mine.Id })],
        };
        await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(config), CancellationToken.None);

        Assert.True(await EstPolicies.CleanUpAsync(settings, store, CancellationToken.None));

        var after = EstJson.Deserialize<EstConfig>(await settings.GetAsync(EstStore.ConfigKey, CancellationToken.None));
        Assert.Equal(builtIn.Id, after.Profiles.Single(p => p.Name == "dot1x").PolicyId);
        Assert.Equal(mine.Id, after.Profiles.Single(p => p.Name == "https").PolicyId); // a policy the user made stays chosen
        Assert.Null(store.Find(copy.Id));
        Assert.NotNull(store.Find(mine.Id));
        Assert.False(await EstPolicies.CleanUpAsync(settings, store, CancellationToken.None)); // once
    }

    [Fact]
    public async Task A_new_server_and_settings_that_cannot_be_read()
    {
        var store = await TestPolicies.StoreAsync();
        var fresh = new FakeSettings();
        Assert.True(await EstPolicies.ConvertAsync(fresh, store, CancellationToken.None)); // the ready-made profiles: the defaults
        var config = EstJson.Deserialize<EstConfig>(await fresh.GetAsync(EstStore.ConfigKey, CancellationToken.None));
        Assert.Equal(["HTTPS", "IEEE 802.1X"], config.Profiles.Select(p => store.Find(p.PolicyId!.Value)!.Name));

        var damaged = new FakeSettings();
        await damaged.SetAsync(EstStore.ConfigKey, "{\"profiles\":7}", CancellationToken.None);
        Assert.False(await EstPolicies.ConvertAsync(damaged, store, CancellationToken.None));
        Assert.Equal("{\"profiles\":7}", await damaged.GetAsync(EstStore.ConfigKey, CancellationToken.None)); // never overwritten
    }

    [Fact]
    public async Task A_profile_issues_with_its_policy_and_without_one_of_its_kind_nothing()
    {
        var store = await TestPolicies.StoreAsync();
        var twoYears = await TestPolicies.AddAsync(store, new PkiPolicy { Name = "HTTPS 2 years", Kind = PolicyKinds.HttpsCertificate, CaId = "oadm", ValidDays = 730, Key = PolicyKeys.Rsa });
        var dot1x = store.FirstOf(PolicyKinds.Dot1xCertificate)!;

        Assert.Equal(new ProfileIssuing(730, ProfileKey.Rsa, "HTTPS 2 years", "oadm"), EstPolicies.IssuingOf(new EstProfile("https", ProfileUse.Https) { PolicyId = twoYears.Id }, store));
        Assert.Equal(new ProfileIssuing(730, ProfileKey.Rsa, "HTTPS 2 years", "oadm"), EstPolicies.IssuingOf(new EstProfile("rtsps", ProfileUse.Rtsps) { PolicyId = twoYears.Id }, store));
        Assert.Equal("IEEE 802.1X", EstPolicies.IssuingOf(new EstProfile("mqtt", ProfileUse.Mqtt) { PolicyId = dot1x.Id }, store)!.PolicyName);
        Assert.Null(EstPolicies.IssuingOf(new EstProfile("https", ProfileUse.Https) { PolicyId = dot1x.Id }, store)); // another kind
        Assert.Null(EstPolicies.IssuingOf(new EstProfile("https", ProfileUse.Https) { PolicyId = Guid.NewGuid() }, store)); // gone
        Assert.Equal(99, EstPolicies.IssuingOf(new EstProfile("https", ProfileUse.Https, ValidityDays: 99), null)!.ValidDays); // no PKI: its own
        Assert.Equal("HTTPS", EstPolicies.IssuingOf(new EstProfile("https", ProfileUse.Https), store)!.PolicyName); // none named yet: the first of its kind

        var choices = EstPolicies.Choices(store, [new CertificateAuthorityInfo("oadm", "OADM Root CA TEST", CertificateAuthorityKind.Oadm, [], DateTime.UtcNow.AddYears(1), true)]);
        Assert.Equal(["HTTPS", "IEEE 802.1X", "HTTPS 2 years"], choices.Select(c => c.Name)); // no server certificates
        Assert.Equal("OADM Root CA TEST, 730 days, RSA", choices[2].Summary);
    }
}
