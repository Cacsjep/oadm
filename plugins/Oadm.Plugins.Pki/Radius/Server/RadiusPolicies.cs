using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Radius.Server;

/// <summary>A CA the 802.1X server trusts, from an Allow CA policy.</summary>
public sealed record TrustedCaChoice(string Id, bool OnlyManaged = true);

/// <summary>The CAs the 802.1X server trusts now (the Allow CA policies of PKI); a CA left out is not trusted.</summary>
public sealed record TrustedCaSettings
{
    public IReadOnlyList<TrustedCaChoice> Cas { get; init; } = [];
}

/// <summary>
/// The 802.1X server's part of PKI's policies (docs/spec/pki.md "Policies"): the cipher suites of the 802.1X server policy,
/// its defaults and TLS check, and the rules the server compiles from the store (<see cref="RadiusRuleSet"/>).
/// </summary>
public static class RadiusPolicies
{
    /// <summary>Every cipher suite the server can offer, in the page's order (ECDHE_RSA suites listed but not usable with the ECDSA server key).</summary>
    public static IReadOnlyList<PolicyCipherSuite> CipherSuites { get; } =
        [.. TlsCipherSuites.All.Select(s => new PolicyCipherSuite(s.Id, s.Name, s.Group == CipherSuiteGroup.LegacyCbc, s.Group == CipherSuiteGroup.Tls13,
            s.Key is null || s.Key == ServerIdentityKeeper.KeyKind))];

    /// <summary>
    /// The 802.1X server policy of a new server: its certificate from the OADM CA (365 days, ECC), TLS 1.2, the default cipher
    /// suites, 1,020 bytes (RFC 3748 3.1), the switch decides reauthentication.
    /// </summary>
    public static PkiPolicy DefaultServerPolicy() => new()
    {
        Name = PolicyLimits.ServerPolicyName,
        Kind = PolicyKinds.Dot1xServer,
        CaId = CertificateAuthorityInfo.OadmId,
        ValidDays = PolicyLimits.DefaultValidDays,
        Key = PolicyKeys.Ecc,
        MinimumTls = PolicyRules.DefaultMinimumTls,
        LargestEapPacket = PolicyRules.DefaultEapPacket,
        ReauthMinutes = null,
    };

    /// <summary>
    /// The TLS settings of an 802.1X server policy: its minimum version and the cipher suites of that minimum the server's key
    /// can use (decision 2026-10-25: no selection); the defaults without one (deleted).
    /// </summary>
    public static TlsSettings TlsOf(PkiPolicy? policy) =>
        policy is null || VersionOf(policy.MinimumTls) is not { } minimum ? TlsSettings.Default : new TlsSettings(minimum, DefaultSuites(minimum).ToHashSet());

    /// <summary>The default cipher suites for <paramref name="minimum"/>: those the server's certificate can use (<see cref="PolicyRules.DefaultSuites"/>).</summary>
    public static IReadOnlyList<int> DefaultSuites(TlsVersion minimum) => PolicyRules.DefaultSuites(CipherSuites, TextOf(minimum));

    internal static string TextOf(TlsVersion version) => version switch
    {
        TlsVersion.Tls10 => "1.0",
        TlsVersion.Tls11 => "1.1",
        TlsVersion.Tls13 => "1.3",
        _ => "1.2",
    };

    /// <summary>The TLS check of an 802.1X server policy: a known minimum version (the suites follow it); null when it can be used.</summary>
    public static PolicyFieldError? ValidateTls(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return VersionOf(policy.MinimumTls) is null ? new(PolicyFields.MinimumTls, PolicyRules.ChooseTls) : null;
    }

    internal static TlsVersion? VersionOf(string? text) => PolicyRules.MinimumIndex(text) switch
    {
        0 => TlsVersion.Tls10,
        1 => TlsVersion.Tls11,
        2 => TlsVersion.Tls12,
        3 => TlsVersion.Tls13,
        _ => null,
    };

    /// <summary>The largest EAP packet of the policy (the kind's default without one).</summary>
    public static int LargestEapPacketOf(PkiPolicy? policy) =>
        Math.Clamp(policy?.LargestEapPacket ?? PolicyRules.DefaultEapPacket, PolicyRules.MinEapPacket, PolicyRules.MaxEapPacket);

    /// <summary>Reauthentication of the policy (null: the switch decides).</summary>
    public static TimeSpan? ReauthenticationOf(PkiPolicy? policy) =>
        policy?.ReauthMinutes is { } minutes and >= PolicyLimits.MinReauthMinutes and <= PolicyLimits.MaxReauthMinutes ? TimeSpan.FromMinutes(minutes) : null;

    /// <summary>The 802.1X policies that apply (the page's "6 policies apply"): 802.1X server, 802.1X trusted CA, VLAN and Block.</summary>
    public static int CountApplying(PolicyStore? store) => store?.All.Count(p => PolicyKinds.IsDot1x(p.Kind)) ?? 0;

    /// <summary>
    /// The policies of the 802.1X kinds a new server gets (docs/spec/pki.md "Converting existing settings", owner
    /// <c>oadm.radius</c>): the 802.1X server policy and the default 802.1X trusted CA policies.
    /// </summary>
    public static IReadOnlyList<PkiPolicy> DefaultPolicies(IReadOnlyList<CertificateAuthorityInfo> authorities) =>
        [DefaultServerPolicy(), .. DefaultAllowCaPolicies(authorities)];

    /// <summary>
    /// The CA's real name in PKI, like EST's sign-in CA policies ("OADM Root CA SERVER01", "Axis device ID", an imported CA by
    /// its name). A CA PKI does not list: "Axis device ID", "OADM Root CA" for the first-start root CA still being made, else
    /// its id (the policy then matches nothing).
    /// </summary>
    private static string CaPolicyName(string id, IReadOnlyList<CertificateAuthorityInfo> authorities)
    {
        var name = authorities.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        return string.Equals(id, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase) ? PolicyStore.AxisDeviceIdName
            : string.Equals(id, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) ? PolicyStore.IssuingCaName
            : id;
    }

    /// <summary>
    /// The default Allow CA policies of a new server: the first-start root CA under its real name and the Axis device ID, both
    /// only managed.
    /// </summary>
    public static IReadOnlyList<PkiPolicy> DefaultAllowCaPolicies(IReadOnlyList<CertificateAuthorityInfo> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        return
        [
            new PkiPolicy { Name = CaPolicyName(CertificateAuthorityInfo.OadmId, authorities), Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.OadmId, OnlyManaged = true },
            new PkiPolicy { Name = CaPolicyName(CertificateAuthorityInfo.AxisDeviceIdId, authorities), Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.AxisDeviceIdId, OnlyManaged = true },
        ];
    }
}

/// <summary>
/// What the 802.1X server built from PKI's policies at one <see cref="PolicyStore.Version"/>: the trusted CAs (Allow CA),
/// the plan of VLAN, Block and Revoke, and the server policy's TLS, packet size and reauthentication. Immutable; the server
/// builds a new one before the next handshake when the store's version moved (fail closed while a change is applied: a
/// handshake never runs with half of a change). Without the store (PKI stopped) nothing is trusted and nothing passes.
/// </summary>
public sealed class RadiusRuleSet
{
    private RadiusRuleSet(PolicyStore? store, long version, TrustedCaSettings selection, PolicyPlan plan, PkiPolicy? server)
    {
        Store = store;
        Version = version;
        Selection = selection;
        Plan = plan;
        Server = server;
        Tls = RadiusPolicies.TlsOf(server);
        LargestEapPacket = RadiusPolicies.LargestEapPacketOf(server);
        Reauthentication = RadiusPolicies.ReauthenticationOf(server);
    }

    /// <summary>No store: nothing trusted, every device refused (the plan's <see cref="PolicyPlan.Unavailable"/>).</summary>
    public static RadiusRuleSet None { get; } = new(null, -1, new TrustedCaSettings(), PolicyPlan.Unavailable, null);

    public PolicyStore? Store { get; }

    public long Version { get; }

    /// <summary>The trusted CAs of the Allow CA policies (an instance per build: the trusted set compares it by reference).</summary>
    public TrustedCaSettings Selection { get; }

    public PolicyPlan Plan { get; }

    public PkiPolicy? Server { get; }

    public TlsSettings Tls { get; }

    public int LargestEapPacket { get; }

    public TimeSpan? Reauthentication { get; }

    /// <summary>The rules of <paramref name="store"/> now.</summary>
    public static RadiusRuleSet Of(PolicyStore? store)
    {
        if (store is null)
        {
            return None;
        }

        var version = store.Version;
        if (store.IsUnreadable)
        {
            return new RadiusRuleSet(store, version, new TrustedCaSettings(), PolicyPlan.Unreadable, null); // fail closed, never the defaults
        }

        var selection = new TrustedCaSettings
        {
            Cas = [.. store.PoliciesOf(PolicyKinds.AllowCa).Where(p => !string.IsNullOrWhiteSpace(p.CaId)).Select(p => new TrustedCaChoice(p.CaId!, p.OnlyManaged ?? true))],
        };
        return new RadiusRuleSet(store, version, selection, new PolicyPlan(store.All, store.TagsProtected), store.ServerPolicy);
    }

    /// <summary>Still the rules of <paramref name="store"/> now.</summary>
    public bool IsCurrent(PolicyStore? store) => ReferenceEquals(store, Store) && (store is null || store.Version == Version);
}
