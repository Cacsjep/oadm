using System.Globalization;

using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Server;

/// <summary>A CA the 802.1X server trusts, from an Allow CA policy (previous OADM CAs follow the OADM CA).</summary>
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

    /// <summary>The 802.1X server policy of a new server: TLS 1.2, the default cipher suites, 1,020 bytes (RFC 3748 3.1), the switch decides reauthentication.</summary>
    public static PkiPolicy DefaultServerPolicy() => new()
    {
        Name = PolicyLimits.ServerPolicyName,
        Kind = PolicyKinds.Dot1xServer,
        MinimumTls = PolicyRules.DefaultMinimumTls,
        CipherSuites = [.. TlsCipherSuites.DefaultFor(TlsVersion.Tls12).Order()],
        LargestEapPacket = PolicyRules.DefaultEapPacket,
        ReauthMinutes = null,
    };

    /// <summary>The TLS settings of an 802.1X server policy; the defaults without one (deleted) or with one that cannot be used.</summary>
    public static TlsSettings TlsOf(PkiPolicy? policy)
    {
        if (policy is null || VersionOf(policy.MinimumTls) is not { } minimum)
        {
            return TlsSettings.Default;
        }

        var settings = new TlsSettings(minimum, (policy.CipherSuites ?? []).ToHashSet());
        return settings.Validate(ServerIdentityKeeper.KeyKind) is null ? settings : new TlsSettings(minimum, TlsCipherSuites.DefaultFor(minimum));
    }

    /// <summary>The server key's check of an 802.1X server policy (only ECDHE_RSA suites with the ECDSA key); null when it can be used.</summary>
    public static PolicyFieldError? ValidateTls(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (VersionOf(policy.MinimumTls) is not { } minimum)
        {
            return new(PolicyFields.MinimumTls, PolicyRules.ChooseTls);
        }

        var error = new TlsSettings(minimum, (policy.CipherSuites ?? []).ToHashSet()).Validate(ServerIdentityKeeper.KeyKind);
        return error is null ? null : new(PolicyFields.CipherSuites, error);
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

    /// <summary>The 802.1X policies that apply (the page's "6 policies apply"): 802.1X server, Allow CA, VLAN, Block and Revoke.</summary>
    public static int CountApplying(PolicyStore? store) => store?.All.Count(p => PolicyKinds.IsDot1x(p.Kind)) ?? 0;

    // ------------------------------------------------------------------ the settings of OADM 4.1 and of development builds

    /// <summary>
    /// The 802.1X server's settings from before the policies, made into policies once (docs/spec/pki.md "Converting existing
    /// settings"): Advanced (setting <c>config</c>) -> the 802.1X server policy; Trusted CAs (<c>trustedCas</c>; none stored =
    /// the OADM CA and the Axis device ID, only managed) -> one Allow CA per checked CA; the policies (<c>policies</c>) or OADM
    /// 4.1's VLAN rules (<c>vlans</c>, only when Assign VLANs was on) -> VLAN, Block and Revoke policies with the same names,
    /// devices and order; a Default VLAN that applied -> a last VLAN policy "All devices" (every device, also of sites added
    /// later and devices OADM does not manage), so no device changes its VLAN. Nothing stored: the defaults of the kinds. Nothing is dropped; the old settings stay as they are.
    /// </summary>
    public static async Task<IReadOnlyList<PkiPolicy>> ConvertAsync(IPluginSettings settings, IReadOnlyList<CertificateAuthorityInfo> authorities, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(authorities);
        var store = new RadiusStore(settings);
        var list = new List<PkiPolicy> { ServerPolicyOf(await store.LegacyAdvancedAsync(ct).ConfigureAwait(false)) };

        var trusted = await store.TrustedCasAsync(ct).ConfigureAwait(false) ?? CaTrust.DefaultSelection;
        foreach (var choice in (trusted.Cas ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id)).DistinctBy(c => c.Id, StringComparer.Ordinal))
        {
            list.Add(new PkiPolicy { Name = CaPolicyName(choice.Id, authorities), Kind = PolicyKinds.AllowCa, CaId = choice.Id, OnlyManaged = choice.OnlyManaged });
        }

        var allDevices = PolicyDevices.Everything; // also sites added later and devices OADM does not manage, as the Default VLAN gave them
        if (await store.PoliciesAsync(ct).ConfigureAwait(false) is { } stored)
        {
            foreach (var policy in stored.Policies ?? [])
            {
                if (policy is null || policy.Kind is not (LegacyPolicyKinds.Vlan or LegacyPolicyKinds.Block or LegacyPolicyKinds.Revoke))
                {
                    continue;
                }

                list.Add(new PkiPolicy
                {
                    Id = policy.Id,
                    Name = policy.Name,
                    Kind = policy.Kind switch { LegacyPolicyKinds.Block => PolicyKinds.Block, LegacyPolicyKinds.Revoke => PolicyKinds.Revoke, _ => PolicyKinds.Vlan },
                    Vlan = policy.Vlan,
                    Devices = policy.Devices ?? new PolicyDevices(),
                    SavedBy = policy.SavedBy,
                });
            }

            if (stored.DefaultVlan is { } everyone && (stored.Policies ?? []).Any(p => p?.Kind == LegacyPolicyKinds.Vlan))
            {
                list.Add(new PkiPolicy { Name = PolicyLimits.AllDevicesName, Kind = PolicyKinds.Vlan, Vlan = everyone, Devices = allDevices });
            }
        }
        else if (await store.VlansAsync(ct).ConfigureAwait(false) is { Enabled: true } vlans)
        {
            foreach (var rule in vlans.Rules ?? [])
            {
                PolicyDevices devices;
                if (rule.Kind == VlanRuleKinds.Site && Guid.TryParse(rule.RefId, out var site))
                {
                    devices = site == Guid.Empty ? new PolicyDevices { ThisServer = true } : new PolicyDevices { SiteIds = [site] };
                }
                else if (rule.Kind == VlanRuleKinds.Tag && !string.IsNullOrWhiteSpace(rule.RefId))
                {
                    devices = new PolicyDevices { Tags = [rule.RefId.Trim()] };
                }
                else
                {
                    continue;
                }

                var name = (rule.RefName ?? rule.RefId ?? string.Empty).Trim();
                list.Add(new PkiPolicy
                {
                    Name = name.Length == 0 ? "VLAN " + rule.Vlan.ToString(CultureInfo.InvariantCulture) : name,
                    Kind = PolicyKinds.Vlan,
                    Vlan = rule.Vlan,
                    Devices = devices,
                });
            }

            if (vlans.DefaultVlan is { } everyone)
            {
                list.Add(new PkiPolicy { Name = PolicyLimits.AllDevicesName, Kind = PolicyKinds.Vlan, Vlan = everyone, Devices = allDevices });
            }
        }

        return list;
    }

    /// <summary>The 802.1X server policy of the stored Advanced settings (unreadable or missing parts: the kind's defaults).</summary>
    private static PkiPolicy ServerPolicyOf(LegacyAdvanced? advanced)
    {
        var policy = DefaultServerPolicy();
        if (advanced is null)
        {
            return policy;
        }

        var minimum = VersionOf(advanced.MinimumTls) is { } version ? advanced.MinimumTls! : PolicyRules.DefaultMinimumTls;
        var suites = (advanced.CipherSuites ?? []).Distinct().Order().ToList();
        var tls = new TlsSettings(VersionOf(minimum)!.Value, suites.ToHashSet());
        if (suites.Count == 0 || tls.Validate(ServerIdentityKeeper.KeyKind) is not null)
        {
            suites = [.. TlsCipherSuites.DefaultFor(VersionOf(minimum)!.Value).Order()];
        }

        return policy with
        {
            MinimumTls = minimum,
            CipherSuites = suites,
            LargestEapPacket = advanced.LargestFragment is { } fragment ? Math.Clamp(fragment, PolicyRules.MinEapPacket, PolicyRules.MaxEapPacket) : PolicyRules.DefaultEapPacket,
        };
    }

    /// <summary>"OADM CA", "Axis device ID", an imported CA by its name (a CA PKI no longer lists: its id, the policy then matches nothing).</summary>
    private static string CaPolicyName(string id, IReadOnlyList<CertificateAuthorityInfo> authorities)
    {
        if (string.Equals(id, CertificateAuthorityInfo.OadmId, StringComparison.Ordinal))
        {
            return "OADM CA";
        }

        if (string.Equals(id, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.Ordinal))
        {
            return "Axis device ID";
        }

        var name = authorities.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
        return string.IsNullOrWhiteSpace(name) ? id : name.Trim();
    }

    /// <summary>The default Allow CA policies of a new server: the OADM CA (with its previous ones) and the Axis device ID, both only managed.</summary>
    public static IReadOnlyList<PkiPolicy> DefaultAllowCaPolicies() =>
    [
        new PkiPolicy { Name = "OADM CA", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.OadmId, OnlyManaged = true },
        new PkiPolicy { Name = "Axis device ID", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.AxisDeviceIdId, OnlyManaged = true },
    ];
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

// ------------------------------------------------------------------ stored forms of OADM 4.1 and of development builds

/// <summary>The kinds of the policies of development builds (setting <c>policies</c>).</summary>
public static class LegacyPolicyKinds
{
    public const string Vlan = "vlan";
    public const string Block = "block";
    public const string Revoke = "revoke";
}

/// <summary>A policy of the 802.1X page of development builds (setting <c>policies</c>).</summary>
public sealed record LegacyRadiusPolicy
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = LegacyPolicyKinds.Vlan;

    public int? Vlan { get; init; }

    public PolicyDevices? Devices { get; init; }

    public string? SavedBy { get; init; }
}

/// <summary>The policies of development builds and their Default VLAN (applied only while a VLAN policy existed).</summary>
public sealed record LegacyPolicySettings
{
    public IReadOnlyList<LegacyRadiusPolicy> Policies { get; init; } = [];

    public int? DefaultVlan { get; init; }
}

/// <summary>Kinds of a VLAN rule of OADM 4.1.</summary>
public static class VlanRuleKinds
{
    public const string Site = "site";
    public const string Tag = "tag";
}

/// <summary>A VLAN rule of OADM 4.1: a site (<see cref="Guid.Empty"/> = This server) or a tag gets a VLAN; the first match won.</summary>
public sealed record VlanRule(string Kind, string RefId, string? RefName, int Vlan);

/// <summary>The VLAN rules of OADM 4.1 (setting <c>vlans</c>).</summary>
public sealed record VlanSettings
{
    public bool Enabled { get; init; }

    public IReadOnlyList<VlanRule> Rules { get; init; } = [];

    public int? DefaultVlan { get; init; }
}

/// <summary>The Advanced settings stored in <c>config</c> by OADM 4.1 and development builds (read once for the conversion).</summary>
public sealed record LegacyAdvanced
{
    public string? MinimumTls { get; init; }

    public IReadOnlyList<int>? CipherSuites { get; init; }

    public int? LargestFragment { get; init; }
}
