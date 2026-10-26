using Oadm.Sdk.Certificates;
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
            case PolicyKinds.Dot1xServer or PolicyKinds.EstServer when string.IsNullOrWhiteSpace(policy.CertificateId):
                return new(PolicyFields.Certificate, PolicyTexts.ChooseCertificate);
            case PolicyKinds.Dot1xServer when ValidateServer(policy, suites) is { } server:
                return server;
            case PolicyKinds.HttpsCertificate or PolicyKinds.Dot1xCertificate when ValidateIssuing(policy) is { } issuing:
                return issuing;
            case PolicyKinds.SignInCa when string.IsNullOrWhiteSpace(policy.CaId):
                return new(PolicyFields.Ca, PolicyTexts.ChooseCa);
            case PolicyKinds.SignInCa when !PolicySignIn.IsKnown(policy.SignInFor):
                return new(PolicyFields.SignInFor, PolicyTexts.ChooseSignInFor);
            case PolicyKinds.Enrollment when !PolicyProfiles.IsKnown(policy.ProfileScope):
                return new(PolicyFields.ProfileScope, PolicyTexts.ChooseProfileScope);
            case PolicyKinds.Enrollment when policy.ProfileScope == PolicyProfiles.Chosen && !(policy.Profiles?.Any(p => !string.IsNullOrWhiteSpace(p)) ?? false):
                return new(PolicyFields.Profiles, PolicyTexts.ChooseProfiles);
        }

        if (PolicyKinds.HasDevices(policy.Kind))
        {
            if (policy.Devices is null || policy.Devices.IsEmpty)
            {
                return new(PolicyFields.Devices, PolicyTexts.ChooseDevices);
            }
        }

        return null;
    }

    /// <summary>
    /// The 802.1X server policy's fields: TLS version, the largest EAP packet, reauthentication. The cipher suites follow the
    /// minimum version (decision 2026-10-25: no selection, <see cref="DefaultSuites"/>).
    /// </summary>
#pragma warning disable IDE0060 // The suites stay in the signature for the callers; nothing is chosen any more.
    private static PolicyFieldError? ValidateServer(PkiPolicy policy, IReadOnlyList<PolicyCipherSuite> suites)
#pragma warning restore IDE0060
    {
        if (MinimumIndex(policy.MinimumTls) is null)
        {
            return new(PolicyFields.MinimumTls, ChooseTls);
        }

        if (policy.LargestEapPacket is not (>= MinEapPacket and <= MaxEapPacket))
        {
            return new(PolicyFields.LargestEapPacket, EapPacketRange);
        }

        return policy.ReauthMinutes is { } minutes && minutes is < PolicyLimits.MinReauthMinutes or > PolicyLimits.MaxReauthMinutes
            ? new(PolicyFields.ReauthMinutes, PolicyTexts.ReauthRange)
            : null;
    }

    /// <summary>
    /// The fields of an issuing policy: a CA named ("Issued by"; whether it can issue, the store checks against PKI's CAs),
    /// the valid days, the key; IEEE 802.1X: EAPOL version, EAP identity and the RADIUS server CA (the rules of the former
    /// IEEE 802.1X card).
    /// </summary>
    public static PolicyFieldError? ValidateIssuing(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.CaId))
        {
            return new(PolicyFields.Ca, PolicyTexts.ChooseCa);
        }

        // Only a CA that can hold a key issues: never the Axis device ID or a previous OADM CA.
        if (string.Equals(policy.CaId, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase)
            || policy.CaId.StartsWith("oadm-previous:", StringComparison.OrdinalIgnoreCase))
        {
            return new(PolicyFields.Ca, PolicyTexts.CannotIssue);
        }

        if (policy.ValidDays is not (>= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays))
        {
            return new(PolicyFields.ValidDays, PolicyTexts.ValidDaysRange);
        }

        if (!PolicyKeys.IsKnown(policy.Key))
        {
            return new(PolicyFields.Key, PolicyTexts.ChooseKey);
        }

        if (policy.Kind != PolicyKinds.Dot1xCertificate)
        {
            return null;
        }

        if (PkiValidation.EapolVersion(policy.EapolVersion ?? 0) is { } eapol)
        {
            return new(PolicyFields.EapolVersion, eapol);
        }

        if (PkiValidation.Identity(policy.Identity) is { } identity)
        {
            return new(PolicyFields.Identity, identity);
        }

        if (PkiValidation.CustomIdentity(policy.Identity, policy.CustomIdentity) is { } custom)
        {
            return new(PolicyFields.CustomIdentity, custom);
        }

        // A CA of the list (decision 2026-10-25); the store checks that it is still listed.
        return string.IsNullOrWhiteSpace(policy.RadiusCaId) ? new(PolicyFields.RadiusCa, PolicyTexts.ChooseRadiusCa) : null;
    }

    /// <summary>The text is one readable certificate in PEM.</summary>
    public static bool IsCertificatePem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > PkiPluginInfo.MaxImportBytes)
        {
            return false;
        }

        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return false;
        }
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
    /// The cipher suites of the 802.1X server for a minimum version (decision 2026-10-25: they follow the minimum, no
    /// selection): every suite listed for the minimum (the old CBC suites only below TLS 1.2) that the server's certificate
    /// can use, in id order.
    /// </summary>
    public static IReadOnlyList<int> DefaultSuites(IReadOnlyList<PolicyCipherSuite> all, string? minimumTls) =>
        [.. Listed(all, minimumTls).Where(s => s.Usable).Select(s => s.Id).Order()];

    /// <summary>
    /// The policy in one form: trimmed name, only the fields of its kind, the devices without doubles ("All devices" alone; sites: "This server" is
    /// <see cref="PolicyDevices.ThisServer"/>, serials upper case), Allow CA's "Only devices OADM manages" on unless off.
    /// </summary>
    public static PkiPolicy Normalize(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var kind = policy.Kind?.Trim() ?? string.Empty;
        var dot1x = kind == PolicyKinds.Dot1xCertificate;
        var enrollment = kind == PolicyKinds.Enrollment;
        var scope = enrollment ? policy.ProfileScope?.Trim() : null;
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
            CaId = kind is PolicyKinds.AllowCa or PolicyKinds.SignInCa || PolicyKinds.IsIssuing(kind) ? policy.CaId?.Trim() : null,
            CertificateId = PolicyKinds.IsServer(kind) && !string.IsNullOrWhiteSpace(policy.CertificateId) ? policy.CertificateId.Trim() : null,
            OnlyManaged = kind == PolicyKinds.AllowCa ? policy.OnlyManaged ?? true : null,
            MinimumTls = kind == PolicyKinds.Dot1xServer ? policy.MinimumTls?.Trim() : null,
            CipherSuites = null, // no longer read: the suites follow the minimum TLS version (decision 2026-10-25)
            LargestEapPacket = kind == PolicyKinds.Dot1xServer ? policy.LargestEapPacket : null,
            ReauthMinutes = kind == PolicyKinds.Dot1xServer ? policy.ReauthMinutes : null,
            Days = kind == PolicyKinds.ExpiryWarning ? policy.Days : null,
            ValidDays = PolicyKinds.IsIssuing(kind) ? policy.ValidDays : null,
            Key = PolicyKinds.IsIssuing(kind) ? policy.Key?.Trim().ToLowerInvariant() : null,
            EapolVersion = dot1x ? policy.EapolVersion : null,
            Identity = dot1x ? policy.Identity?.Trim() : null,
            CustomIdentity = dot1x ? policy.Identity?.Trim() == Dot1xIdentity.Custom ? policy.CustomIdentity?.Trim() ?? string.Empty : string.Empty : null,
            RadiusCaId = dot1x && !string.IsNullOrWhiteSpace(policy.RadiusCaId) ? policy.RadiusCaId.Trim() : null,
            RadiusCa = dot1x ? policy.RadiusCa?.Trim() : null,
            RadiusCaPem = dot1x && policy.RadiusCa?.Trim() == RadiusCaSource.Imported ? policy.RadiusCaPem : null,
            AlsoRevoke = null, // decision 2026-10-26: Block only refuses; a stored "Also revoke" is dropped on save
            Server = null, // only Server certificate policies of before 2026-10-25 had it (the conversion reads the stored ones)
            ProfileScope = scope,
            Profiles = enrollment && scope == PolicyProfiles.Chosen
                ? [.. (policy.Profiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]
                : null,
            SignInFor = kind == PolicyKinds.SignInCa ? policy.SignInFor?.Trim() : null,
            SavedBy = policy.SavedBy,
            Revision = policy.Revision,
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
    /// <param name="useTags">False: the tags match no device (tags the host does not protect).</param>
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

    /// <summary>
    /// The matcher of <paramref name="policy"/>: VLAN and Enrollment by tag only while the host protects the tags.
    /// </summary>
    public static PolicyMatcher Of(PkiPolicy policy, bool tagsProtected = true)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new PolicyMatcher(policy.Devices, tagsProtected || !PolicyKinds.GrantsByTag(policy.Kind));
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
