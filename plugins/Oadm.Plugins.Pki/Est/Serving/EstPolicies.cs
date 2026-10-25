using System.Text.Json;

using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Serving;

/// <summary>What the certificates of a profile are issued with.</summary>
/// <param name="ValidDays">How long a certificate is valid (never past the CA's end).</param>
/// <param name="Key">The key the camera creates.</param>
/// <param name="PolicyName">The issuing policy ("HTTPS"); null without PKI's store.</param>
/// <param name="CaId">The policy's "Issued by" (decision 2026-10-25); null: the OADM CA.</param>
public sealed record ProfileIssuing(int ValidDays, ProfileKey Key, string? PolicyName, string? CaId = null);

/// <summary>
/// The EST profiles and PKI's issuing policies (docs/spec/pki.md "Issuing policies have no devices", est-server.md
/// "Profiles"): a profile names its issuing policy (HTTPS certificate for HTTPS and RTSPS, IEEE 802.1X certificate for IEEE
/// 802.1X and MQTT), whose valid days and key its certificates get. The profile name stays what the cameras use in their URL.
/// </summary>
public static class EstPolicies
{
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

        return PolicyOf(profile, store) is { } policy ? new ProfileIssuing(policy.ValidDays ?? PolicyLimits.DefaultValidDays, KeyOf(policy), policy.Name, policy.CaId) : null;
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

        var names = authorities.Where(a => a.Kind is CertificateAuthorityKind.Oadm or CertificateAuthorityKind.Imported).GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
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
    /// Every profile without a policy names the built-in policy of its kind (decision 2026-10-25: one issuing policy per kind
    /// by default; the profile's old validity and key are no longer read, no "EST HTTPS" policy is made). A profile keeps a
    /// policy it names. Settings that cannot be read are never overwritten. True when the settings were written.
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

        var profiles = config.Profiles.Select(p => p.PolicyId is null && store.FirstOf(EstProfile.PolicyKindOf(p.Use)) is { } builtIn ? p with { PolicyId = builtIn.Id } : p).ToList();
        if (profiles.SequenceEqual(config.Profiles))
        {
            return false;
        }

        await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(config with { Profiles = profiles }), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The owner of the clean-up of the issuing policies the EST conversion made before 2026-10-25.</summary>
    public const string CleanUpOwner = EstPluginInfo.PluginId + ":issuing-2026-10-25";

    /// <summary>
    /// Once (decision 2026-10-25: EST profiles use the built-in issuing policies): a profile that names a policy the EST
    /// conversion made ("EST HTTPS", "EST 802.1X", "EST " + the profile's name; also the spellings before the rename) names
    /// the built-in policy of its kind instead; then those policies are deleted where nothing else names them (another
    /// profile). Never on settings or a store that cannot be read. True when it ran now.
    /// </summary>
    public static async Task<bool> CleanUpAsync(IPluginSettings settings, PolicyStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        if (store.IsUnreadable || store.Converted.Contains(CleanUpOwner, StringComparer.Ordinal))
        {
            return false;
        }

        var json = await settings.GetAsync(EstStore.ConfigKey, ct).ConfigureAwait(false);
        if ((string.IsNullOrWhiteSpace(json) ? new EstConfig() : Read(json)) is not { } config)
        {
            return false;
        }

        var names = new HashSet<string>(["EST HTTPS", "EST 802.1X", "EST https", "EST dot1x"], StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Profiles)
        {
            names.Add(NameFor(profile));
            names.Add("EST " + profile.Name);
        }

        var all = store.All;
        var made = all.Where(p => PolicyKinds.IsDeviceIssuing(p.Kind) && names.Contains(p.Name.Trim()) && !PolicyStore.IsSystem(p, all)).Select(p => p.Id).ToHashSet();
        var profiles = config.Profiles.Select(p => p.PolicyId is { } id && made.Contains(id) && store.FirstOf(EstProfile.PolicyKindOf(p.Use)) is { } builtIn
            ? p with { PolicyId = builtIn.Id }
            : p).ToList();
        if (!profiles.SequenceEqual(config.Profiles))
        {
            await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(config with { Profiles = profiles }), ct).ConfigureAwait(false);
        }

        var named = profiles.Where(p => p.PolicyId is not null).Select(p => p.PolicyId!.Value).ToHashSet();
        return await store.RewriteOnceAsync(CleanUpOwner, list => [.. list.Where(p => !made.Contains(p.Id) || named.Contains(p.Id))], ct).ConfigureAwait(false);
    }

    /// <summary>"EST HTTPS", "EST 802.1X" for the two default profiles, "EST " + the name for the others (the names the conversion before 2026-10-25 gave).</summary>
    public static string NameFor(EstProfile profile) => "EST " + profile.Name.ToLowerInvariant() switch
    {
        "https" => "HTTPS",
        "dot1x" => "802.1X",
        _ => profile.Name,
    };

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
