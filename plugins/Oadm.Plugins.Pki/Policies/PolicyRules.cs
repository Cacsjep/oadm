using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Policies;

/// <summary>
/// The checks of one policy on its own (name, the kind's fields, the devices), the same on the page (errors below the fields)
/// and in the store, which then checks it against the others (names, one only, CAs, sites, overlaps).
/// </summary>
public static class PolicyRules
{
    /// <summary>The minimum TLS versions of the 802.1X server policy, oldest first.</summary>
    public static IReadOnlyList<string> TlsVersions { get; } = ["1.0", "1.1", "1.2", "1.3"];

    public const string DefaultMinimumTls = "1.2";
    public const int MinEapPacket = 500;
    public const int MaxEapPacket = 1_400;
    public const int DefaultEapPacket = 1_020; // RFC 3748 3.1: the EAP MTU every link guarantees
    public const int MinVlan = 1;
    public const int MaxVlan = 4094;

    public const string ChooseTls = "Choose a TLS version.";
    public const string CheckCipherSuite = "Check at least one cipher suite for the chosen TLS versions.";
    public const string EapPacketRange = "Use 500 to 1,400 bytes.";

    /// <summary>
    /// The first error of <paramref name="policy"/> on its own; null when it is fine. <paramref name="suites"/>: the cipher
    /// suites the 802.1X server lists (the server policy's check; empty skips it).
    /// </summary>
    public static PolicyFieldError? Validate(PkiPolicy policy, IReadOnlyList<PolicyCipherSuite> suites)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(suites);
        var name = policy.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new(PolicyFields.Name, PolicyTexts.EnterName);
        }

        if (name.Length > PolicyLimits.MaxNameLength)
        {
            return new(PolicyFields.Name, PolicyTexts.NameTooLong);
        }

        if (!PolicyKinds.IsKnown(policy.Kind))
        {
            return new(PolicyFields.Kind, PolicyTexts.ChooseKind);
        }

        switch (policy.Kind)
        {
            case PolicyKinds.Vlan when policy.Vlan is not (>= MinVlan and <= MaxVlan):
                return new(PolicyFields.Vlan, PolicyTexts.VlanRange);
            case PolicyKinds.ExpiryWarning when policy.Days is not (>= PolicyLimits.MinExpiryDays and <= PolicyLimits.MaxExpiryDays):
                return new(PolicyFields.Days, PolicyTexts.DaysRange);
            case PolicyKinds.AllowCa when string.IsNullOrWhiteSpace(policy.CaId):
                return new(PolicyFields.Ca, PolicyTexts.ChooseCa);
            case PolicyKinds.Dot1xServer when ValidateServer(policy, suites) is { } server:
                return server;
        }

        if (PolicyKinds.HasDevices(policy.Kind))
        {
            if (policy.Devices is null || policy.Devices.IsEmpty)
            {
                return new(PolicyFields.Devices, PolicyTexts.ChooseDevices);
            }

            if (policy.Devices.AllDevices && !PolicyKinds.AllowsAllDevices(policy.Kind))
            {
                return new(PolicyFields.Devices, PolicyTexts.RevokeNoAll);
            }

            if (PolicyKinds.NoTags(policy.Kind) && (policy.Devices.Tags?.Any(t => !string.IsNullOrWhiteSpace(t)) ?? false))
            {
                return new(PolicyFields.Devices, PolicyTexts.RevokeNoTags);
            }
        }

        return null;
    }

    /// <summary>The 802.1X server policy's fields: TLS version, a usable cipher suite, the largest EAP packet, reauthentication.</summary>
    private static PolicyFieldError? ValidateServer(PkiPolicy policy, IReadOnlyList<PolicyCipherSuite> suites)
    {
        if (MinimumIndex(policy.MinimumTls) is not { } minimum)
        {
            return new(PolicyFields.MinimumTls, ChooseTls);
        }

        if (suites.Count > 0)
        {
            var known = suites.ToDictionary(s => s.Id);
            var chosen = policy.CipherSuites ?? [];
            var usable = chosen.Any(id => known.TryGetValue(id, out var suite) && suite.Usable && Fits(suite, minimum));
            if (!usable || !chosen.All(known.ContainsKey))
            {
                return new(PolicyFields.CipherSuites, CheckCipherSuite);
            }
        }

        if (policy.LargestEapPacket is not (>= MinEapPacket and <= MaxEapPacket))
        {
            return new(PolicyFields.LargestEapPacket, EapPacketRange);
        }

        return policy.ReauthMinutes is { } minutes && minutes is < PolicyLimits.MinReauthMinutes or > PolicyLimits.MaxReauthMinutes
            ? new(PolicyFields.ReauthMinutes, PolicyTexts.ReauthRange)
            : null;
    }

    /// <summary>0 for "1.0" .. 3 for "1.3"; null for anything else.</summary>
    public static int? MinimumIndex(string? minimumTls)
    {
        var index = minimumTls is null ? -1 : TlsVersions.ToList().IndexOf(minimumTls);
        return index < 0 ? null : index;
    }

    /// <summary>The suites the page lists for a minimum version: the old CBC suites only below TLS 1.2.</summary>
    public static IReadOnlyList<PolicyCipherSuite> Listed(IReadOnlyList<PolicyCipherSuite> all, string? minimumTls)
    {
        ArgumentNullException.ThrowIfNull(all);
        var minimum = MinimumIndex(minimumTls) ?? 2;
        return [.. all.Where(s => !s.Legacy || minimum < 2)];
    }

    /// <summary>
    /// The default cipher suites of the 802.1X server policy for a minimum version (the one source of the default: a new
    /// server, "Reset to default", the fallback of a policy the server cannot use): every suite listed for the minimum that
    /// the server's certificate can use, in id order.
    /// </summary>
    public static IReadOnlyList<int> DefaultSuites(IReadOnlyList<PolicyCipherSuite> all, string? minimumTls) =>
        [.. Listed(all, minimumTls).Where(s => s.Usable).Select(s => s.Id).Order()];

    private static bool Fits(PolicyCipherSuite suite, int minimum) => suite.Tls13 || (suite.Legacy ? minimum < 2 : minimum <= 2);

    /// <summary>
    /// The policy in one form: trimmed name, only the fields of its kind, the devices without doubles ("All devices" alone; sites: "This server" is
    /// <see cref="PolicyDevices.ThisServer"/>, serials upper case), Allow CA's "Only devices OADM manages" on unless off.
    /// </summary>
    public static PkiPolicy Normalize(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var kind = policy.Kind?.Trim() ?? string.Empty;
        PolicyDevices? devices = null;
        if (PolicyKinds.HasDevices(kind))
        {
            var given = policy.Devices ?? new PolicyDevices();
            var sites = (given.SiteIds ?? []).Distinct().ToList();
            devices = given.AllDevices ? PolicyDevices.Everything : new PolicyDevices
            {
                ThisServer = given.ThisServer || sites.Contains(Guid.Empty),
                SiteIds = [.. sites.Where(id => id != Guid.Empty)],
                Tags = [.. (given.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
                Serials = [.. Serials(given.Serials)],
                ExceptSerials = [.. Serials(given.ExceptSerials)],
            };
        }

        return new PkiPolicy
        {
            Id = policy.Id,
            Name = policy.Name?.Trim() ?? string.Empty,
            Kind = kind,
            Devices = devices,
            Vlan = kind == PolicyKinds.Vlan ? policy.Vlan : null,
            CaId = kind == PolicyKinds.AllowCa ? policy.CaId?.Trim() : null,
            OnlyManaged = kind == PolicyKinds.AllowCa ? policy.OnlyManaged ?? true : null,
            MinimumTls = kind == PolicyKinds.Dot1xServer ? policy.MinimumTls?.Trim() : null,
            CipherSuites = kind == PolicyKinds.Dot1xServer ? [.. (policy.CipherSuites ?? []).Distinct().Order()] : null,
            LargestEapPacket = kind == PolicyKinds.Dot1xServer ? policy.LargestEapPacket : null,
            ReauthMinutes = kind == PolicyKinds.Dot1xServer ? policy.ReauthMinutes : null,
            Days = kind == PolicyKinds.ExpiryWarning ? policy.Days : null,
            SavedBy = policy.SavedBy,
        };

        static IEnumerable<string> Serials(IReadOnlyList<string>? serials) =>
            (serials ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal);
    }
}

/// <summary>The devices of a policy compiled for lookups in O(1) per device (hash sets of sites, tags and serials).</summary>
public sealed class PolicyMatcher
{
    private readonly bool _all;
    private readonly bool _thisServer;
    private readonly HashSet<Guid> _sites;
    private readonly HashSet<string> _tags;
    private readonly HashSet<string> _serials;
    private readonly HashSet<string> _except;

    /// <param name="devices">The chosen sites, tags and devices.</param>
    /// <param name="useTags">False: the tags match no device (Revoke, or tags the host does not protect).</param>
    public PolicyMatcher(PolicyDevices? devices, bool useTags = true)
    {
        devices ??= new PolicyDevices();
        _all = devices.AllDevices;
        _thisServer = devices.ThisServer;
        _sites = [.. devices.SiteIds ?? []];
        _tags = useTags
            ? new HashSet<string>((devices.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _serials = new HashSet<string>((devices.Serials ?? []).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        _except = new HashSet<string>((devices.ExceptSerials ?? []).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The matcher of <paramref name="policy"/>: Revoke never by tag.</summary>
    public static PolicyMatcher Of(PkiPolicy policy, bool tagsProtected = true)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new PolicyMatcher(policy.Devices, !PolicyKinds.NoTags(policy.Kind) && (tagsProtected || policy.Kind != PolicyKinds.Vlan));
    }

    /// <summary>"All devices": holds every device, also one OADM does not manage.</summary>
    public bool HoldsEveryDevice => _all;

    public bool Holds(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_except.Contains(device.Serial))
        {
            return false;
        }

        if (_all || _serials.Contains(device.Serial) || (device.SiteId is { } site ? _sites.Contains(site) : _thisServer))
        {
            return true;
        }

        if (_tags.Count == 0)
        {
            return false;
        }

        foreach (var tag in device.Tags)
        {
            if (_tags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tag names of <paramref name="policies"/>' devices, case-insensitive, distinct.</summary>
    public static IReadOnlyList<string> TagsOf(IEnumerable<PkiPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return [.. policies.SelectMany(p => p.Devices?.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
