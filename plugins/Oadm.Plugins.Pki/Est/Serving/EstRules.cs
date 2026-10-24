using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Serving;

/// <summary>The answer of <see cref="EstRules.Check"/>: allowed, or the HTTP status and the reason listed in Enrollments.</summary>
/// <param name="Status">403 (no Enrollment policy holds the device) or 404 (the profile is not allowed: the answer of an unknown profile).</param>
public sealed record EnrollmentCheck(bool Allowed, int Status, string? Reason)
{
    public static EnrollmentCheck Ok { get; } = new(true, 200, null);
}

/// <summary>
/// PKI's EST policies compiled for the server (docs/spec/pki.md "Policies", group EST): the CAs a camera may sign in with
/// (Sign-in CA: for new certificates, which may renew too, or for renewals only) and the profiles each device may use
/// (Enrollment: a device no Enrollment policy holds is refused; of several that hold it, a profile must be allowed by every
/// one). Without PKI's store, or with a store that cannot be read, nothing signs in and no device enrolls (fail closed).
/// </summary>
public sealed class EstRules
{
    private readonly (PolicyMatcher Matcher, PkiPolicy Policy)[] _enrollment;
    private readonly HashSet<string> _newCas;
    private readonly HashSet<string> _renewalCas;

    private EstRules(PolicyStore? store, long version, (PolicyMatcher, PkiPolicy)[] enrollment, HashSet<string> newCas, HashSet<string> renewalCas, int count, string? problem)
    {
        Store = store;
        Version = version;
        _enrollment = enrollment;
        _newCas = newCas;
        _renewalCas = renewalCas;
        Count = count;
        Problem = problem;
    }

    /// <summary>Nothing signs in, no device enrolls; <paramref name="problem"/> on the page.</summary>
    public static EstRules Closed(string problem) =>
        new(null, -1, [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), 0, problem);

    /// <summary>The rules of <paramref name="store"/> now (null: no PKI, fail closed).</summary>
    public static EstRules Of(PolicyStore? store)
    {
        if (store is null)
        {
            return Closed(PolicyTexts.NotRunning);
        }

        var version = store.Version;
        if (store.IsUnreadable)
        {
            return new(store, version, [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), 0, PolicyTexts.Unreadable);
        }

        var signIn = store.PoliciesOf(PolicyKinds.SignInCa);
        var enrollment = store.PoliciesOf(PolicyKinds.Enrollment);
        var newCas = new HashSet<string>(signIn.Where(p => p.SignInFor == PolicySignIn.New).Select(p => p.CaId ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var renewalCas = new HashSet<string>(signIn.Where(p => PolicySignIn.IsKnown(p.SignInFor)).Select(p => p.CaId ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var problem = signIn.Count == 0 ? EstTexts.NoSignInPolicy : enrollment.Count == 0 ? EstTexts.NoEnrollmentPolicies : null;
        return new(store, version, [.. enrollment.Select(p => (PolicyMatcher.Of(p, store.TagsProtected), p))], newCas, renewalCas, signIn.Count + enrollment.Count + store.PoliciesOf(PolicyKinds.EstServer).Count, problem); // the EST server policy applies too (its certificate)
    }

    /// <summary>The store these rules were built from (null: none).</summary>
    public PolicyStore? Store { get; }

    /// <summary>The store's version these rules were built from.</summary>
    public long Version { get; }

    /// <summary>The EST policies (Enrollment and Sign-in CA): the page's "2 policies apply".</summary>
    public int Count { get; }

    /// <summary>Why cameras cannot enroll at all ("There is no Sign-in CA policy..."); null when the rules let some in.</summary>
    public string? Problem { get; }

    /// <summary>The ids of PKI's CAs a camera may sign in with for <paramref name="kind"/> (a new certificate, or a renewal).</summary>
    public IReadOnlySet<string> SignInCas(EnrollmentKind kind) => kind == EnrollmentKind.Renewal ? _renewalCas : _newCas;

    /// <summary>Every CA a Sign-in CA policy names (PKI shows the EST server in their Used by).</summary>
    public IReadOnlySet<string> AllSignInCas => _renewalCas;

    /// <summary>
    /// May <paramref name="device"/> use <paramref name="profile"/>: no Enrollment policy holds it (403 "No enrollment policy
    /// for this device."), one that holds it does not allow the profile (404, the answer of an unknown profile, no probing;
    /// the reason names the policy), else allowed. O(policies) per request, each O(1).
    /// </summary>
    public EnrollmentCheck Check(IDeviceInfo device, string profile)
    {
        ArgumentNullException.ThrowIfNull(device);
        var held = false;
        foreach (var (matcher, policy) in _enrollment)
        {
            if (!matcher.Holds(device))
            {
                continue;
            }

            held = true;
            var allowed = policy.ProfileScope switch
            {
                PolicyProfiles.All => true,
                PolicyProfiles.Chosen => (policy.Profiles ?? []).Contains(profile, StringComparer.OrdinalIgnoreCase),
                _ => false,
            };
            if (!allowed)
            {
                return new EnrollmentCheck(false, 404, policy.ProfileScope == PolicyProfiles.Chosen
                    ? EstTexts.ProfileNotAllowed(policy.Name, profile)
                    : EstTexts.EnrollmentOff(policy.Name));
            }
        }

        return held ? EnrollmentCheck.Ok : new EnrollmentCheck(false, 403, EstRefusals.NoEnrollmentPolicy);
    }

    /// <summary>The ids of the CAs PKI holds now among <paramref name="ids"/> (a PKI without its list: all of them).</summary>
    internal static IEnumerable<string> Held(IEnumerable<string> ids, IReadOnlyList<CertificateAuthorityInfo> authorities) =>
        authorities.Count == 0 ? ids : ids.Where(id => authorities.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)));
}
