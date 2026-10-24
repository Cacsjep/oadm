using System.Text.Json;

using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Serving;

/// <summary>What the certificates of a profile are issued with.</summary>
/// <param name="ValidDays">How long a certificate is valid (never past the CA's end).</param>
/// <param name="Key">The key the camera creates.</param>
/// <param name="PolicyName">The issuing policy ("HTTPS"); null without PKI's store.</param>
public sealed record ProfileIssuing(int ValidDays, ProfileKey Key, string? PolicyName);

/// <summary>
/// The EST profiles and PKI's issuing policies (docs/spec/pki.md "Issuing policies have no devices", est-server.md
/// "Profiles"): a profile names its issuing policy (HTTPS certificate for HTTPS and RTSPS, IEEE 802.1X certificate for IEEE
/// 802.1X and MQTT), whose valid days and key its certificates get. The profile name stays what the cameras use in their URL.
/// </summary>
public static class EstPolicies
{
    /// <summary>The owner of the conversion of the profiles' validity and key (stage 4; "oadm.est" stays for the EST policies of stage 6).</summary>
    public const string Owner = EstPluginInfo.PluginId + ":profiles";

    /// <summary>The owner of the conversion of "Sign in with" and today's enrollment into Sign-in CA and Enrollment policies (stage 6).</summary>
    public const string RulesOwner = EstPluginInfo.PluginId;

    /// <summary>The policy of <paramref name="profile"/>: the one it names when that is of its use's kind; without a name the first of the kind. Null: none (fail closed).</summary>
    public static PkiPolicy? PolicyOf(EstProfile profile, PolicyStore? store)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (store is null || store.IsUnreadable)
        {
            return null;
        }

        var kind = EstProfile.PolicyKindOf(profile.Use);
        return profile.PolicyId is { } id
            ? store.Find(id) is { } policy && policy.Kind == kind ? policy : null
            : store.FirstOf(kind);
    }

    /// <summary>
    /// What a certificate of <paramref name="profile"/> is issued with: its policy's valid days and key. Without PKI's store
    /// (no PKI: then there is no CA either, nothing is issued; tests with a CA of their own) the profile's own values. Null: the
    /// store is there but the policy cannot be read (the store is damaged or the policy is gone): nothing is issued.
    /// </summary>
    public static ProfileIssuing? IssuingOf(EstProfile profile, PolicyStore? store)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (store is null)
        {
            return new ProfileIssuing(profile.ValidityDays, profile.Key, null);
        }

        return PolicyOf(profile, store) is { } policy ? new ProfileIssuing(policy.ValidDays ?? PolicyLimits.DefaultValidDays, KeyOf(policy), policy.Name) : null;
    }

    /// <summary>The key the camera creates for the profile (the device ID it signs in with is of that key).</summary>
    public static ProfileKey KeyOf(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Key == PolicyKeys.Rsa ? ProfileKey.Rsa : ProfileKey.Ecc;
    }

    /// <summary>The issuing policies a profile can name, for the page.</summary>
    public static IReadOnlyList<IssuingPolicyChoice> Choices(PolicyStore? store, IReadOnlyList<CertificateAuthorityInfo> authorities)
    {
        if (store is null || store.IsUnreadable)
        {
            return [];
        }

        var names = authorities.Where(a => a.Kind == CertificateAuthorityKind.Oadm).GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        return [.. store.All.Where(p => PolicyKinds.IsDeviceIssuing(p.Kind)).Select(p => new IssuingPolicyChoice(p.Id, p.Name, p.Kind, PolicyStore.IssuingSummary(p, names)))];
    }

    /// <summary>The profiles in the stored settings that name a policy (PKI refuses to delete those policies).</summary>
    public static async Task<IReadOnlyList<(Guid PolicyId, string Profile)>> UsesAsync(IPluginSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = Read(await settings.GetAsync(EstStore.ConfigKey, ct).ConfigureAwait(false));
        return config is null ? [] : [.. config.Profiles.Where(p => p.PolicyId is not null).Select(p => (p.PolicyId!.Value, p.Name))];
    }

    /// <summary>The names of the profiles in the stored settings (an Enrollment policy chooses among them); none when they cannot be read.</summary>
    public static async Task<IReadOnlyList<string>> ProfileNamesAsync(IPluginSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = Read(await settings.GetAsync(EstStore.ConfigKey, ct).ConfigureAwait(false));
        return config is null ? [] : [.. config.Profiles.Select(p => p.Name)];
    }

    /// <summary>
    /// Once (docs/spec/pki.md "Converting existing settings"): the EST server's rules of before the policies become PKI's EST
    /// policies: the Enrollment policy "All devices" (every device OADM manages, every profile: as before) and one Sign-in CA
    /// policy per CA of "Sign in with" (the Axis device ID for new certificates, the OADM CA and previous OADM CAs for
    /// renewals; OADM 4.1 had no "Sign in with": the Axis device ID and the OADM CA). A conversion that runs after EST policies
    /// were made merges (<see cref="PolicyStore.ConvertOnceAsync"/>): no second policy of a CA, no "All devices" next to
    /// Enrollment policies made before. Settings that cannot be read convert nothing (fail closed; the next start tries again).
    /// True when it converted now.
    /// </summary>
    public static async Task<bool> ConvertRulesAsync(IPluginSettings settings, PolicyStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        if (store.IsUnreadable || store.Converted.Contains(RulesOwner, StringComparer.Ordinal))
        {
            return false;
        }

        if (Read(await settings.GetAsync(EstStore.ConfigKey, ct).ConfigureAwait(false)) is not { } config)
        {
            return false;
        }

        return await store.ConvertOnceAsync(RulesOwner, _ => Task.FromResult(RulesOf(config, store.SignInCaChoices())), ct).ConfigureAwait(false);
    }

    /// <summary>The Enrollment and Sign-in CA policies of <paramref name="config"/> (see <see cref="ConvertRulesAsync"/>).</summary>
    internal static IReadOnlyList<PkiPolicy> RulesOf(EstConfig config, IReadOnlyList<PolicyCaChoice> signInCas)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(signInCas);
        var list = new List<PkiPolicy>
        {
            new() { Name = PolicyLimits.AllDevicesName, Kind = PolicyKinds.Enrollment, Devices = PolicyDevices.Everything, ProfileScope = PolicyProfiles.All },
        };
        foreach (var id in (config.SignInWith ?? EstConfig.DefaultSignInWith).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A CA PKI no longer holds let nothing in (a previous OADM CA that ended): nothing to convert.
            if (signInCas.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } ca)
            {
                continue;
            }

            var axis = ca.Kind == CertificateAuthorityKind.AxisDeviceId;
            list.Add(new PkiPolicy
            {
                Name = axis ? PolicyStore.AxisDeviceIdName : ca.Kind == CertificateAuthorityKind.Oadm ? PolicyStore.IssuingCaName : ca.Name,
                Kind = PolicyKinds.SignInCa,
                CaId = ca.Id,
                SignInFor = axis ? PolicySignIn.New : PolicySignIn.Renewal,
            });
        }

        return list;
    }

    /// <summary>
    /// Once (docs/spec/pki.md "Converting existing settings"): every profile without a policy names one with its validity and
    /// key: the default ("https" -> "HTTPS") when its values are the same, else a new policy named after the profile ("EST
    /// https"). Idempotent: a profile that names a policy stays; a run after a crash finds the policies made before by their
    /// values. The old validity and key stay in the profile (no longer read). Settings that cannot be read are never
    /// overwritten. True when the settings were written.
    /// </summary>
    public static async Task<bool> ConvertAsync(IPluginSettings settings, PolicyStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        if (store.IsUnreadable)
        {
            return false;
        }

        var json = await settings.GetAsync(EstStore.ConfigKey, ct).ConfigureAwait(false);
        var config = string.IsNullOrWhiteSpace(json) ? new EstConfig() : Read(json);
        if (config is null || config.Profiles.All(p => p.PolicyId is not null))
        {
            return false;
        }

        var chosen = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await store.ConvertOnceAsync(Owner, _ =>
        {
            var added = new List<PkiPolicy>();
            foreach (var profile in config.Profiles.Where(p => p.PolicyId is null))
            {
                if (Matching([.. store.All, .. added], profile) is { } match)
                {
                    chosen[profile.Name] = match.Id;
                    continue;
                }

                var made = NewPolicy(profile, store);
                added.Add(made);
                chosen[profile.Name] = made.Id;
            }

            return Task.FromResult<IReadOnlyList<PkiPolicy>>(added);
        }, ct).ConfigureAwait(false);

        var profiles = config.Profiles.Select(p =>
        {
            if (p.PolicyId is not null)
            {
                return p;
            }

            // Converted before (a crash between the two writes): the policy made then has the same values.
            var id = chosen.TryGetValue(p.Name, out var known) && store.Find(known) is not null ? known
                : Matching(store.All, p)?.Id ?? store.FirstOf(EstProfile.PolicyKindOf(p.Use))?.Id;
            return id is null ? p : p with { PolicyId = id };
        }).ToList();
        if (profiles.SequenceEqual(config.Profiles))
        {
            return false;
        }

        await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(config with { Profiles = profiles }), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A policy of the profile's kind with its validity and key from the OADM CA: the default name first ("HTTPS", "IEEE
    /// 802.1X"), then the one named after the profile, then the oldest.
    /// </summary>
    internal static PkiPolicy? Matching(IReadOnlyList<PkiPolicy> policies, EstProfile profile)
    {
        var kind = EstProfile.PolicyKindOf(profile.Use);
        var key = profile.Key == ProfileKey.Rsa ? PolicyKeys.Rsa : PolicyKeys.Ecc;
        var same = policies.Where(p => p.Kind == kind && p.ValidDays == profile.ValidityDays && p.Key == key
            && string.Equals(p.CaId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase)).ToList();
        var defaultName = kind == PolicyKinds.Dot1xCertificate ? PolicyLimits.Dot1xName : PolicyLimits.HttpsName;
        return same.FirstOrDefault(p => string.Equals(p.Name, defaultName, StringComparison.OrdinalIgnoreCase))
            ?? same.FirstOrDefault(p => string.Equals(p.Name, NameFor(profile), StringComparison.OrdinalIgnoreCase))
            ?? same.FirstOrDefault();
    }

    /// <summary>"EST https".</summary>
    public static string NameFor(EstProfile profile) => "EST " + profile.Name;

    /// <summary>A new policy with the profile's validity and key; an IEEE 802.1X one takes the 802.1X settings of the first of its kind (EST does not use them).</summary>
    private static PkiPolicy NewPolicy(EstProfile profile, PolicyStore store)
    {
        var kind = EstProfile.PolicyKindOf(profile.Use);
        var days = profile.ValidityDays is >= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays ? profile.ValidityDays : PolicyLimits.DefaultValidDays;
        var template = store.FirstOf(kind);
        return new PkiPolicy
        {
            Id = Guid.NewGuid(),
            Name = NameFor(profile),
            Kind = kind,
            CaId = CertificateAuthorityInfo.OadmId,
            ValidDays = days,
            Key = profile.Key == ProfileKey.Rsa ? PolicyKeys.Rsa : PolicyKeys.Ecc,
            EapolVersion = kind == PolicyKinds.Dot1xCertificate ? template?.EapolVersion ?? 1 : null,
            Identity = kind == PolicyKinds.Dot1xCertificate ? template?.Identity ?? Oadm.Plugins.Pki.Dot1xIdentity.Mac : null,
            CustomIdentity = kind == PolicyKinds.Dot1xCertificate ? template?.CustomIdentity : null,
            RadiusCa = kind == PolicyKinds.Dot1xCertificate ? template?.RadiusCa ?? Oadm.Plugins.Pki.RadiusCaSource.Oadm : null,
            RadiusCaPem = kind == PolicyKinds.Dot1xCertificate ? template?.RadiusCaPem : null,
        };
    }

    private static EstConfig? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new EstConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<EstConfig>(json, EstJson.Options) is { Profiles: not null } config ? config : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
