using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// PKI's EST policies (docs/spec/pki.md "Policies", stage 6): the kinds Enrollment and Sign-in CA (checks, defaults,
/// overlaps), what the EST server makes of them (<see cref="EstRules"/>) and the conversion of "Sign in with" and today's
/// enrollment (fixtures of OADM 4.1 and of the format before the policies; once, merging into policies made before).
/// </summary>
public sealed class EnrollmentPolicyTests
{
    private static readonly CertificateAuthorityInfo Previous = new("oadm-previous:AB", "OADM CA 2025", CertificateAuthorityKind.PreviousOadm, [], DateTime.UtcNow.AddYears(1), false);
    private static readonly CertificateAuthorityInfo Axis = new(CertificateAuthorityInfo.AxisDeviceIdId, "Axis device ID", CertificateAuthorityKind.AxisDeviceId, [], DateTime.UtcNow.AddYears(30), false);
    private static readonly CertificateAuthorityInfo Imported = new("imported:CC", "Contoso Root", CertificateAuthorityKind.Imported, [], DateTime.UtcNow.AddYears(5), false);

    private readonly List<IDeviceInfo> _devices =
    [
        new FakeDevice { Serial = "B8A44F631339", Address = "10.0.0.48", Tags = ["PTZ"] },
        new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" },
    ];

    private Task<PolicyStore> StoreAsync(FakeSettings? estSettings = null, IProtectedTags? tags = null) =>
        TestPolicies.EstStoreAsync(estSettings, authorities: () => [TestPolicies.OadmCa, Axis, Previous, Imported],
            devices: _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices]), tags: tags ?? new FakeCoreContext().FakeProtectedTags);

    private static async Task<FakeSettings> FixtureAsync(string file)
    {
        var settings = new FakeSettings();
        await settings.SetAsync(EstStore.ConfigKey, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), CancellationToken.None);
        return settings;
    }

    private static Task<PolicySaveResult> SaveAsync(PolicyStore store, PkiPolicy policy) => store.SaveAsync(policy, "admin", PkiService.CipherSuites, CancellationToken.None);

    private static PkiPolicy Enrollment(string name, PolicyDevices devices, string scope = PolicyProfiles.All, params string[] profiles) =>
        new() { Name = name, Kind = PolicyKinds.Enrollment, Devices = devices, ProfileScope = scope, Profiles = profiles };

    private static PolicyDevices Serials(params string[] serials) => new() { Serials = serials };

    [Fact]
    public async Task The_settings_of_oadm_41_become_all_devices_and_the_two_sign_in_cas_once()
    {
        var settings = await FixtureAsync("est-config-4.1.json"); // no "Sign in with" in 4.1: the Axis device ID and the OADM CA
        var store = await StoreAsync(settings);

        var enrollment = Assert.Single(store.PoliciesOf(PolicyKinds.Enrollment));
        Assert.Equal((PolicyLimits.AllDevicesName, PolicyProfiles.All, true), (enrollment.Name, enrollment.ProfileScope, enrollment.Devices!.AllDevices));
        Assert.Equal([("Axis device ID", CertificateAuthorityInfo.AxisDeviceIdId, PolicySignIn.New), ("OADM CA", CertificateAuthorityInfo.OadmId, PolicySignIn.Renewal)],
            store.PoliciesOf(PolicyKinds.SignInCa).Select(p => (p.Name, p.CaId!, p.SignInFor!)));
        Assert.Contains(EstPolicies.RulesOwner, store.Converted);
        Assert.All(store.All, p => Assert.Null(PolicyRules.Validate(p, PkiService.CipherSuites))); // policies like any other

        // Once: a second run adds nothing, also after the user changed them.
        var count = store.All.Count;
        Assert.False(await EstPolicies.ConvertRulesAsync(settings, store, CancellationToken.None));
        var changed = (await SaveAsync(store, enrollment with { Devices = Serials("ACCC8E000002") })).Saved!; // the system policy is changed, never deleted
        Assert.False(await EstPolicies.ConvertRulesAsync(settings, store, CancellationToken.None));
        Assert.Equal(count, store.All.Count);
        Assert.Equal(["ACCC8E000002"], Assert.Single(store.PoliciesOf(PolicyKinds.Enrollment)).Devices!.Serials);
        Assert.Equal(changed.Id, store.PoliciesOf(PolicyKinds.Enrollment)[0].Id);

        // The rows of the card.
        var rows = store.Rows(_devices);
        Assert.Equal(["Cameras sign in to the EST server with their Axis device ID for new certificates and renewals.", "Cameras with a certificate from OADM Root CA TEST may sign in to renew it."], rows.Where(r => r.Policy.Kind == PolicyKinds.SignInCa).Select(r => r.Summary));
        Assert.All(rows.Where(r => r.Policy.Kind == PolicyKinds.SignInCa), r => Assert.Equal((PolicyGroups.Est, "EST sign-in CA"), (r.Group, r.KindName)));
    }

    [Fact]
    public async Task Sign_in_with_of_before_the_policies_becomes_one_sign_in_ca_policy_per_ticked_ca()
    {
        var store = await StoreAsync(await FixtureAsync("est-config-stage4.json"));

        // The previous OADM CA by its name, for renewals; a CA PKI no longer holds let nothing in: nothing converted.
        Assert.Equal([("Axis device ID", PolicySignIn.New), ("OADM CA", PolicySignIn.Renewal), ("OADM CA 2025", PolicySignIn.Renewal)],
            store.PoliciesOf(PolicyKinds.SignInCa).Select(p => (p.Name, p.SignInFor!)));
        Assert.DoesNotContain(store.All, p => p.CaId == "oadm-previous:GONE");

        // Only the Axis device ID ticked: no renewals with the OADM CA, as before.
        var axisOnly = new FakeSettings();
        await axisOnly.SetAsync(EstStore.ConfigKey, EstJson.Serialize(new EstConfig { SignInWith = [CertificateAuthorityInfo.AxisDeviceIdId] }), CancellationToken.None);
        var rules = EstRules.Of(await StoreAsync(axisOnly));
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId], rules.SignInCas(EnrollmentKind.New));
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId], rules.SignInCas(EnrollmentKind.Renewal)); // a CA for new certificates renews too
    }

    [Fact]
    public async Task A_late_conversion_merges_into_the_est_policies_made_before()
    {
        // PKI ran first and the user made EST policies before the EST server's core plugin converted (no doubles).
        var store = await TestPolicies.StoreAsync(authorities: () => [TestPolicies.OadmCa, Axis], devices: _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices]),
            tags: new FakeCoreContext().FakeProtectedTags);
        Assert.Null((await SaveAsync(store, new PkiPolicy { Name = "Own CA", Kind = PolicyKinds.SignInCa, CaId = CertificateAuthorityInfo.OadmId, SignInFor = PolicySignIn.New })).Error);
        Assert.Null((await SaveAsync(store, Enrollment("PTZ", new PolicyDevices { Tags = ["PTZ"] }, PolicyProfiles.Chosen, "https"))).Error);

        Assert.True(await EstPolicies.ConvertRulesAsync(new FakeSettings(), store, CancellationToken.None));

        var signIn = store.PoliciesOf(PolicyKinds.SignInCa);
        Assert.Equal([("Own CA", PolicySignIn.Renewal), ("Axis device ID", PolicySignIn.New)], signIn.Select(p => (p.Name, p.SignInFor!))); // the stricter of the two
        Assert.Equal(["PTZ"], store.PoliciesOf(PolicyKinds.Enrollment).Select(p => p.Name)); // no "All devices" next to it
    }

    [Fact]
    public async Task Settings_that_cannot_be_read_convert_nothing_and_a_damaged_store_is_never_written()
    {
        var damaged = new FakeSettings();
        await damaged.SetAsync(EstStore.ConfigKey, "{\"profiles\":7}", CancellationToken.None);
        var store = await TestPolicies.StoreAsync();
        Assert.False(await EstPolicies.ConvertRulesAsync(damaged, store, CancellationToken.None));
        Assert.DoesNotContain(EstPolicies.RulesOwner, store.Converted); // tried again at the next start
        Assert.Empty(store.PoliciesOf(PolicyKinds.SignInCa));
        Assert.Equal(EstTexts.NoSignInPolicy, EstRules.Of(store).Problem); // fail closed meanwhile

        var unreadable = new FakeSettings();
        await unreadable.SetAsync(PolicyStore.SettingsKey, "{\"policies\":3}", CancellationToken.None);
        var broken = await TestPolicies.StoreAsync(settings: unreadable);
        Assert.False(await EstPolicies.ConvertRulesAsync(new FakeSettings(), broken, CancellationToken.None));
        Assert.Equal("{\"policies\":3}", await unreadable.GetAsync(PolicyStore.SettingsKey, CancellationToken.None));
    }

    [Fact]
    public async Task The_kinds_check_their_fields()
    {
        var store = await StoreAsync();
        var all = store.PoliciesOf(PolicyKinds.Enrollment).Single();

        Assert.Equal(PolicyTexts.ChooseProfiles, (await SaveAsync(store, all with { ProfileScope = PolicyProfiles.Chosen, Profiles = [] })).Error?.Message);
        Assert.Equal(PolicyTexts.ChooseProfileScope, (await SaveAsync(store, all with { ProfileScope = "some" })).Error?.Message);
        Assert.Equal(PolicyTexts.ChooseDevices, (await SaveAsync(store, Enrollment("None", new PolicyDevices()))).Error?.Message);
        Assert.Equal(PolicyTexts.ChooseCa, (await SaveAsync(store, new PkiPolicy { Name = "X", Kind = PolicyKinds.SignInCa, SignInFor = PolicySignIn.New })).Error?.Message);
        Assert.Equal(PolicyTexts.ChooseSignInFor, (await SaveAsync(store, new PkiPolicy { Name = "X", Kind = PolicyKinds.SignInCa, CaId = "oadm" })).Error?.Message);
        Assert.Equal(PolicyTexts.NoSignInCa, (await SaveAsync(store, new PkiPolicy { Name = "X", Kind = PolicyKinds.SignInCa, CaId = Imported.Id, SignInFor = PolicySignIn.New })).Error?.Message);
        Assert.Equal(PolicyTexts.SignInCaTaken, (await SaveAsync(store, new PkiPolicy { Name = "X", Kind = PolicyKinds.SignInCa, CaId = "OADM", SignInFor = PolicySignIn.New })).Error?.Message);
        Assert.Null((await SaveAsync(store, new PkiPolicy { Name = "OADM CA 2025", Kind = PolicyKinds.SignInCa, CaId = Previous.Id, SignInFor = PolicySignIn.Renewal })).Error);

        // The editor's choices: the Axis device ID, the OADM CA and its previous CA; never an imported CA.
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId, CertificateAuthorityInfo.OadmId, Previous.Id], store.SignInCaChoices().Select(c => c.Id));

        // Only the fields of the kind are kept; chosen profiles without doubles.
        var normal = PolicyRules.Normalize(Enrollment(" A ", Serials("b8a44f631339"), PolicyProfiles.Chosen, "https", "HTTPS", " dot1x ") with { CaId = "oadm", SignInFor = "new" });
        Assert.Equal(("A", null, null), (normal.Name, normal.CaId, normal.SignInFor));
        Assert.Equal(["https", "dot1x"], normal.Profiles!);
        Assert.Equal(["B8A44F631339"], normal.Devices!.Serials);
        Assert.Null(PolicyRules.Normalize(all with { ProfileScope = PolicyProfiles.All, Profiles = ["https"] }).Profiles);
        Assert.Equal("Profiles https and dot1x", PolicyTexts.EnrollmentSummary(normal));
        Assert.Equal("These cameras may get certificates from the EST server for the profiles https and dot1x.", PolicyTexts.EnrollmentSentence(normal));
    }

    [Fact]
    public async Task Enrollment_policies_never_overlap_on_save_and_a_later_overlap_names_the_narrower_one()
    {
        var store = await StoreAsync();
        var all = store.PoliciesOf(PolicyKinds.Enrollment).Single();

        // "All devices" overlaps every other Enrollment policy.
        var ptz = await SaveAsync(store, Enrollment("PTZ", new PolicyDevices { Tags = ["PTZ"] }, PolicyProfiles.Chosen, "https"));
        Assert.Equal(PolicyTexts.OverlapAll(PolicyLimits.AllDevicesName, "PTZ"), ptz.Error?.Message);
        Assert.Null((await SaveAsync(store, all with { Devices = Serials("ACCC8E000002") })).Error);
        Assert.Null((await SaveAsync(store, Enrollment("PTZ", new PolicyDevices { Tags = ["PTZ"] }, PolicyProfiles.Chosen, "https"))).Error);
        var twice = await SaveAsync(store, Enrollment("Entrance", Serials("B8A44F631339"), PolicyProfiles.None));
        Assert.Equal(PolicyTexts.Overlap("10.0.0.48", 0, "Entrance", "PTZ"), twice.Error?.Message);

        // A tag added later makes an overlap: both rows warn and name the narrower policy, which applies.
        _devices[1] = new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49", Tags = ["PTZ"] };
        var warnings = store.Rows(_devices).Where(r => r.Policy.Kind == PolicyKinds.Enrollment).Select(r => r.Warning).ToList();
        Assert.All(warnings, w => Assert.Equal(PolicyTexts.Overlap("10.0.0.49", 0, PolicyLimits.AllDevicesName, "PTZ") + " PTZ applies.", w));
        var check = EstRules.Of(store).Check(_devices[1], "dot1x");
        Assert.Equal((false, 404, EstTexts.ProfileNotAllowed("PTZ", "dot1x")), (check.Allowed, check.Status, check.Reason)); // every policy that holds it must allow it
        Assert.True(EstRules.Of(store).Check(_devices[1], "HTTPS").Allowed);
    }

    [Fact]
    public async Task The_est_rules_check_devices_and_profiles_and_fail_closed()
    {
        var store = await StoreAsync();
        var rules = EstRules.Of(store);
        Assert.Equal(4, rules.Count); // with the EST server policy (its certificate, decision 2026-10-25)
        Assert.Null(rules.Problem);
        Assert.True(rules.Check(_devices[0], "https").Allowed);

        // A device none holds: 403; the tags of an Enrollment policy match only while the host protects them.
        var all = store.PoliciesOf(PolicyKinds.Enrollment).Single();
        Assert.Null((await SaveAsync(store, all with { Name = "PTZ", Devices = new PolicyDevices { Tags = ["PTZ"] } })).Error);
        rules = EstRules.Of(store);
        Assert.True(rules.Check(_devices[0], "https").Allowed);
        Assert.Equal((false, 403, EstRefusals.NoEnrollmentPolicy), (rules.Check(_devices[1], "https").Allowed, rules.Check(_devices[1], "https").Status, rules.Check(_devices[1], "https").Reason));
        var unprotected = await TestPolicies.StoreAsync(settings: await SettingsOfAsync(store), devices: _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices]));
        Assert.False(unprotected.TagsProtected);
        Assert.False(EstRules.Of(unprotected).Check(_devices[0], "https").Allowed); // fail closed: a group user could add the tag

        // The first Enrollment policy is a system policy: it is never deleted.
        Assert.Equal(PolicyTexts.SystemPolicy, (await store.DeleteAsync(all.Id, "admin", CancellationToken.None)).Error?.Message);

        // No Enrollment policy at all (a stored list without one): every device refused, the page says why.
        var withoutEnrollment = new FakeSettings();
        await withoutEnrollment.SetAsync(PolicyStore.SettingsKey,
            PkiJson.Serialize(new StoredPolicies { Policies = [.. store.All.Where(p => p.Kind != PolicyKinds.Enrollment)], Converted = store.Converted }), CancellationToken.None);
        var none = await TestPolicies.StoreAsync(settings: withoutEnrollment, devices: _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices]),
            tags: new FakeCoreContext().FakeProtectedTags);
        Assert.NotEmpty(none.PoliciesOf(PolicyKinds.SignInCa));
        Assert.Empty(none.PoliciesOf(PolicyKinds.Enrollment));
        rules = EstRules.Of(none);
        Assert.Equal(EstTexts.NoEnrollmentPolicies, rules.Problem);
        Assert.Equal(403, rules.Check(_devices[0], "https").Status);

        // No PKI, or a store that cannot be read: nothing signs in.
        Assert.Empty(EstRules.Of(null).SignInCas(EnrollmentKind.New));
        Assert.Equal(PolicyTexts.NotRunning, EstRules.Of(null).Problem);
        Assert.Equal(403, EstRules.Of(null).Check(_devices[0], "https").Status);
    }

    /// <summary>The stored policies of <paramref name="store"/> in settings of their own.</summary>
    private static async Task<FakeSettings> SettingsOfAsync(PolicyStore store)
    {
        var settings = new FakeSettings();
        await settings.SetAsync(PolicyStore.SettingsKey, PkiJson.Serialize(new StoredPolicies { Policies = store.All, Converted = store.Converted }), CancellationToken.None);
        return settings;
    }
}
