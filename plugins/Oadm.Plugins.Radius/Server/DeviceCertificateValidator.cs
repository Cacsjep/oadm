using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>A CA of the Trusted CAs selection that issued a client certificate directly.</summary>
/// <param name="Id">PKI's id.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="OnlyManaged">"Only devices OADM manages".</param>
public sealed record TrustedIssuer(string Id, CertificateAuthorityKind Kind, bool OnlyManaged)
{
    /// <summary>The current or a previous OADM CA: PKI's list of issued certificates and its revocation list apply.</summary>
    public bool IsOadm => Kind is CertificateAuthorityKind.Oadm or CertificateAuthorityKind.PreviousOadm;
}

/// <summary>
/// The CAs a client certificate may chain to (docs/spec/radius-server.md "Who passes"): the CAs checked under Trusted CAs
/// (PKI's <see cref="IDeviceCertificateAuthority.Authorities"/>; previous OADM CAs follow the OADM CA), as one immutable set.
/// Self-signed certificates are the trust anchors (a CA list without one anchors at its last certificate), the rest
/// intermediates. A certificate must be issued directly by a checked CA (<see cref="Issuers"/>): an OADM CA imported as an
/// intermediate of an enterprise root never makes the certificates of its sibling CAs pass. The Axis device ID is checked
/// against its roots and intermediates (<see cref="DeviceIds"/>). A CA PKI no longer lists is not in the set (fail closed).
/// Rebuilt when PKI changes its CAs or the selection changes (and checked every minute), never per authentication.
/// </summary>
public sealed class CaTrust
{
    private CaTrust(string key, X509Certificate2Collection anchors, X509Certificate2Collection intermediates, IReadOnlyDictionary<string, TrustedIssuer> issuers,
        AxisDeviceId? deviceIds, TrustedIssuer? deviceIdIssuer, IReadOnlyList<string> usedIds, IDeviceCertificateAuthority? authority = null, long version = 0)
    {
        Key = key;
        Anchors = anchors;
        Intermediates = intermediates;
        Issuers = issuers;
        DeviceIds = deviceIds;
        DeviceIdIssuer = deviceIdIssuer;
        UsedIds = usedIds;
        Authority = authority;
        Version = version;
    }

    /// <summary>No CA: nothing chains.</summary>
    public static CaTrust None { get; } = new(string.Empty, [], [], new Dictionary<string, TrustedIssuer>(), null, null, []);

    /// <summary>The checked CAs that issue directly, by the SHA-256 hash of their certificate.</summary>
    public IReadOnlyDictionary<string, TrustedIssuer> Issuers { get; }

    /// <summary>The Axis device ID roots and intermediates when it is checked; null otherwise.</summary>
    public AxisDeviceId? DeviceIds { get; }

    public TrustedIssuer? DeviceIdIssuer { get; }

    /// <summary>The ids of the CAs in the set (PKI's "Used by").</summary>
    public IReadOnlyList<string> UsedIds { get; }

    /// <summary>A fingerprint of the CAs and the selection (to tell whether a rebuild changed anything).</summary>
    public string Key { get; }

    public X509Certificate2Collection Anchors { get; }

    public X509Certificate2Collection Intermediates { get; }

    public bool IsEmpty => Anchors.Count == 0;

    /// <summary>The CA offer the set was built from (null: none). A new offer (PKI restarted) is a new set.</summary>
    public IDeviceCertificateAuthority? Authority { get; }

    /// <summary>
    /// The 802.1X server's count of the CA's <see cref="IDeviceCertificateAuthority.Changed"/> events and selection saves when
    /// the set was built: a handshake compares the offer and this number (no hashing per handshake); the minute round
    /// compares <see cref="Key"/>.
    /// </summary>
    public long Version { get; }

    /// <summary>The default selection: the OADM CA (with its previous ones) and the Axis device ID, both only managed devices.</summary>
    public static TrustedCaSettings DefaultSelection { get; } = new()
    {
        Cas = [new TrustedCaChoice(CertificateAuthorityInfo.OadmId), new TrustedCaChoice(CertificateAuthorityInfo.AxisDeviceIdId)],
    };

    /// <summary>The id of a previous OADM CA derived from an older PKI offer: "oadm-previous:&lt;sha256 of the CA&gt;".</summary>
    public static string PreviousId(byte[] der) => "oadm-previous:" + Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();

    /// <summary>
    /// PKI's CAs. An offer without the list (an older PKI) gives the OADM CA from its chain, the previous ones and the Axis
    /// device ID of the plugin (<paramref name="fallbackDeviceIds"/>).
    /// </summary>
    public static IReadOnlyList<CertificateAuthorityInfo> AuthoritiesOf(IDeviceCertificateAuthority? authority, AxisDeviceId fallbackDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(fallbackDeviceIds);
        if (authority is null)
        {
            return [];
        }

        if (authority.Authorities is { Count: > 0 } listed)
        {
            return listed;
        }

        var list = new List<CertificateAuthorityInfo>();
        if (authority.Chain is { Count: > 0 } chain)
        {
            list.Add(new CertificateAuthorityInfo(CertificateAuthorityInfo.OadmId, "OADM CA", CertificateAuthorityKind.Oadm, chain, NotAfterOf(chain[0]), true));
        }

        foreach (var previous in authority.PreviousChains)
        {
            if (previous.Count > 0)
            {
                list.Add(new CertificateAuthorityInfo(PreviousId(previous[0]), "Previous OADM CA", CertificateAuthorityKind.PreviousOadm, previous, NotAfterOf(previous[0]), false));
            }
        }

        var roots = fallbackDeviceIds.Roots.Select(r => r.RawData).ToList();
        list.Add(new CertificateAuthorityInfo(CertificateAuthorityInfo.AxisDeviceIdId, "Axis device ID", CertificateAuthorityKind.AxisDeviceId, roots,
            roots.Count == 0 ? DateTime.MaxValue : fallbackDeviceIds.Roots.Max(r => r.NotAfter.ToUniversalTime()), false) { });
        return list;
    }

    /// <summary>The checked CAs of <paramref name="selection"/> in <paramref name="authorities"/>: id -> only managed (previous OADM CAs follow the OADM CA).</summary>
    public static Dictionary<string, bool> Chosen(IReadOnlyList<CertificateAuthorityInfo> authorities, TrustedCaSettings selection)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        ArgumentNullException.ThrowIfNull(selection);
        var choices = (selection.Cas ?? []).Where(c => c is not null).GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().OnlyManaged, StringComparer.Ordinal);
        var chosen = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var ca in authorities)
        {
            var id = ca.Kind == CertificateAuthorityKind.PreviousOadm ? CertificateAuthorityInfo.OadmId : ca.Id;
            if (choices.TryGetValue(id, out var onlyManaged))
            {
                chosen[ca.Id] = onlyManaged;
            }
        }

        return chosen;
    }

    /// <summary>The key of the CAs and the selection now (cheap: hashes of the DER).</summary>
    public static string KeyOf(IDeviceCertificateAuthority? authority, TrustedCaSettings selection, AxisDeviceId fallbackDeviceIds)
    {
        if (authority is null)
        {
            return string.Empty;
        }

        var authorities = AuthoritiesOf(authority, fallbackDeviceIds);
        var chosen = Chosen(authorities, selection);
        return string.Join('|', authorities.Where(a => chosen.ContainsKey(a.Id)).Select(a =>
            a.Id + (chosen[a.Id] ? "+" : "-") + string.Join(',', a.Certificates.Select(d => Convert.ToHexString(SHA256.HashData(d))[..16]))));
    }

    /// <summary>The set of <paramref name="authority"/>'s CAs that <paramref name="selection"/> checks (none without a CA).</summary>
    public static CaTrust Build(IDeviceCertificateAuthority? authority, TrustedCaSettings selection, AxisDeviceId fallbackDeviceIds, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(fallbackDeviceIds);
        if (authority is null)
        {
            return None;
        }

        var key = KeyOf(authority, selection, fallbackDeviceIds);
        var authorities = AuthoritiesOf(authority, fallbackDeviceIds);
        var derived = authority.Authorities is not { Count: > 0 };
        var chosen = Chosen(authorities, selection);
        var anchors = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        var issuers = new Dictionary<string, TrustedIssuer>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AxisDeviceId? deviceIds = null;
        TrustedIssuer? deviceIdIssuer = null;
        foreach (var ca in authorities)
        {
            if (!chosen.TryGetValue(ca.Id, out var onlyManaged))
            {
                continue;
            }

            var issuer = new TrustedIssuer(ca.Id, ca.Kind, onlyManaged);
            var loaded = Load(ca.Certificates);
            if (ca.Kind == CertificateAuthorityKind.AxisDeviceId)
            {
                deviceIds = derived ? fallbackDeviceIds : new AxisDeviceId(loaded);
                deviceIdIssuer = issuer;
                continue;
            }

            if (loaded.Count > 0 && ca.Certificates.Count > 0 && loaded[0].RawData.AsSpan().SequenceEqual(ca.Certificates[0]))
            {
                issuers[loaded[0].GetCertHashString(HashAlgorithmName.SHA256)] = issuer; // the CA itself (an unreadable first entry is no issuer)
            }

            var anySelfSigned = loaded.Any(IsSelfSigned);
            for (var i = 0; i < loaded.Count; i++)
            {
                var certificate = loaded[i];
                if (!seen.Add(certificate.Thumbprint))
                {
                    certificate.Dispose();
                    continue;
                }

                // The top of a chain without a root (an intermediate CA imported without its root) is the anchor itself.
                var anchor = IsSelfSigned(certificate) || (!anySelfSigned && i == loaded.Count - 1);
                (anchor ? anchors : intermediates).Add(certificate);
            }
        }

        return new CaTrust(key, anchors, intermediates, issuers, deviceIds, deviceIdIssuer, [.. chosen.Keys.Order(StringComparer.Ordinal)], authority, version);
    }

    private static List<X509Certificate2> Load(IReadOnlyList<byte[]> certificates)
    {
        var loaded = new List<X509Certificate2>();
        foreach (var der in certificates)
        {
            try
            {
                loaded.Add(X509CertificateLoader.LoadCertificate(der));
            }
            catch (CryptographicException)
            {
                // an unreadable entry is not trusted
            }
        }

        return loaded;
    }

    private static DateTime NotAfterOf(byte[] der)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return certificate.NotAfter.ToUniversalTime();
        }
        catch (CryptographicException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool IsSelfSigned(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
}

/// <summary>What the certificate check found out, handed back with the session (<see cref="ClientCertificateVerdict.Tag"/>).</summary>
/// <param name="DeviceId">The managed device the certificate names; null when it names none OADM manages.</param>
/// <param name="DeviceSerial">The serial the certificate names (12 hex digits), when it names one.</param>
/// <param name="CertificateSerial">The client certificate's serial number (<see cref="CertificateSerials.Normalize"/>).</param>
public sealed record CheckedCertificate(Guid? DeviceId, string? DeviceSerial, string? CertificateSerial)
{
    /// <summary>
    /// The certificate's subject when it passed without a managed device (a CA without "Only devices OADM manages"): the
    /// Device column of the session and the log shows it.
    /// </summary>
    public string? Subject { get; init; }
}

/// <summary>The refusal texts of the certificate check (one phrase per concept).</summary>
public static class CertificateRefusals
{
    public const string Unreadable = "Client certificate could not be read";
    public const string NotForNetworkAccess = "Client certificate not allowed for network access";
    public const string NamesNoDevice = "Client certificate names no device";
    public const string OtherSite = "The device belongs to another site";
    public const string Revoked = "Certificate revoked";
    public const string NoCa = "The PKI has no certificate authority";

    /// <summary>A certificate PKI lists with another use (MQTT, HTTPS, RTSPS); TLS alert 42 (bad certificate).</summary>
    public const string NotDot1x = "Certificate is not an IEEE 802.1X certificate";

    /// <summary>"Client certificate not issued by a trusted CA (TLS alert 48, unknown CA)".</summary>
    public static string NotTrusted => TlsAlertTexts.Describe(48, TlsAlertDirection.SentByOadm);

    /// <summary>"Certificate of a device OADM does not manage (ACCC8E123456)".</summary>
    public static string NotManaged(string serial) => $"Certificate of a device OADM does not manage ({serial})";

    /// <summary>"Blocked by policy Building C" (TLS alert 49).</summary>
    public static string Blocked(string policy) => $"Blocked by policy {policy}";

    /// <summary>"Certificate revoked by policy Building C" (TLS alert 44).</summary>
    public static string RevokedByPolicy(string policy) => $"Certificate revoked by policy {policy}";

    /// <summary>"Client certificate expired on 2026-09-30".</summary>
    public static string Expired(DateTime notAfterUtc) => "Client certificate expired on " + notAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"Client certificate not valid before 2026-09-30".</summary>
    public static string NotYetValid(DateTime notBeforeUtc) => "Client certificate not valid before " + notBeforeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"MAC address AC:CC:8E:65:43:21 does not match the certificate of ACCC8E123456".</summary>
    public static string MacMismatch(string mac, string serial) => $"MAC address {mac} does not match the certificate of {serial}";
}

/// <summary>
/// Who passes (docs/spec/radius-server.md "Who passes"), called inside the TLS handshake with in-memory lookups only: PKI's
/// CA is there (else nothing passes), the leaf is valid now, made for client authentication (or without extended key usage),
/// issued directly by a CA checked under Trusted CAs (or chains to the Axis device ID when it is checked), and for an OADM CA
/// not listed by PKI with another use than IEEE 802.1X and not revoked. With "Only devices OADM manages" (the default) it
/// names exactly one managed device (<c>axis-&lt;serial&gt;.local</c> or the device ID serial), at a site a device of the
/// switch's site; without it any certificate of that CA passes (the log shows its subject). No block or revoke policy holds
/// the device. The MAC address only adds a warning; the VLAN comes from the policies.
/// </summary>
public sealed partial class DeviceCertificateValidator(
    Func<CaTrust> trust,
    DeviceIndex devices,
    Func<IDeviceCertificateAuthority?> authority,
    Func<SwitchTable> switches,
    Func<PolicyPlan> policies,
    TimeProvider time,
    Action<string, RadiusPolicy>? revokeByPolicy = null) : IClientCertificateValidator
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string AnyUsageOid = "2.5.29.37.0";

    public ClientCertificateVerdict Validate(ClientCertificateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        X509Certificate2 leaf;
        try
        {
            leaf = X509CertificateLoader.LoadCertificate(check.Chain[0]);
        }
        catch (CryptographicException)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.Unreadable, 42);
        }

        var presented = new List<X509Certificate2>();
        try
        {
            foreach (var der in check.Chain.Skip(1).Take(8))
            {
                try
                {
                    presented.Add(X509CertificateLoader.LoadCertificate(der));
                }
                catch (CryptographicException)
                {
                    // an unreadable intermediate helps no chain
                }
            }

            return Check(check, leaf, presented);
        }
        finally
        {
            leaf.Dispose();
            foreach (var certificate in presented)
            {
                certificate.Dispose();
            }
        }
    }

    private ClientCertificateVerdict Check(ClientCertificateCheck check, X509Certificate2 leaf, List<X509Certificate2> presented)
    {
        var now = time.GetUtcNow();
        var certificateSerial = CertificateSerials.Normalize(leaf.SerialNumber);
        var none = new CheckedCertificate(null, null, certificateSerial);
        var ca = authority();
        if (ca is null)
        {
            // Without PKI there is no revocation list: no certificate passes, Axis device ID ones neither (fail closed).
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NoCa, 80, none);
        }

        var notAfter = leaf.NotAfter.ToUniversalTime();
        var notBefore = leaf.NotBefore.ToUniversalTime();
        if (now.UtcDateTime > notAfter)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.Expired(notAfter), 45, none);
        }

        if (now.UtcDateTime < notBefore)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotYetValid(notBefore), 45, none);
        }

        if (leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } usage
            && !usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is ClientAuthOid or AnyUsageOid))
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotForNetworkAccess, 42, none);
        }

        string? serial;
        var trusted = trust();
        var issuer = IssuerOf(trusted, leaf, presented, now);
        if (issuer is not null && !ReferenceEquals(trusted.Authority, ca))
        {
            // The trusted set is from a CA offer that is gone (PKI restarted in the meantime): without that CA's current
            // revocation list nothing of it passes (fail closed). A changed CA of the same offer rebuilds the set first.
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NoCa, 80, none);
        }

        if (issuer is { IsOadm: true })
        {
            // PKI lists every certificate the OADM CA issued with its use: one made for MQTT (also client authentication,
            // also axis-<serial>.local) is no network access certificate. Unlisted ones (from before the list) pass as before.
            if (ca.PurposeOf(certificateSerial) is { } purpose && !string.Equals(purpose, DeviceCertificatePurposes.Dot1x, StringComparison.Ordinal))
            {
                return ClientCertificateVerdict.Refuse(CertificateRefusals.NotDot1x, 42, none);
            }

            serial = LocalSerial(leaf);
            if (serial is null && issuer.OnlyManaged)
            {
                return ClientCertificateVerdict.Refuse(CertificateRefusals.NamesNoDevice, 42, none);
            }
        }
        else if (issuer is not null)
        {
            serial = LocalSerial(leaf) ?? AxisDeviceId.SerialOf(leaf); // an imported CA: the device's name or a serial in the subject
        }
        else if (trusted.DeviceIds?.Verify(leaf, presented, now) is { } factorySerial && ReferenceEquals(trusted.Authority, ca))
        {
            serial = factorySerial;
            issuer = trusted.DeviceIdIssuer;
        }
        else
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotTrusted, 48, none);
        }

        var device = serial is null ? null : devices.BySerial(serial);
        if (issuer!.IsOadm && ca.IsRevoked(certificateSerial))
        {
            var text = ca.FindRevocation(certificateSerial)?.Describe() ?? CertificateRefusals.Revoked;
            return ClientCertificateVerdict.Refuse(text, 44, new CheckedCertificate(device?.Id, serial, certificateSerial));
        }

        var plan = policies();
        if (device is null)
        {
            if (issuer.OnlyManaged)
            {
                return serial is null
                    ? ClientCertificateVerdict.Refuse(CertificateRefusals.NamesNoDevice, 42, none)
                    : ClientCertificateVerdict.Refuse(CertificateRefusals.NotManaged(serial), 49, new CheckedCertificate(null, serial, certificateSerial));
            }

            // A CA without "Only devices OADM manages": any valid certificate of it passes; the log shows its subject.
            return ClientCertificateVerdict.Accept(plan.VlanFor(null), null, new CheckedCertificate(null, serial, certificateSerial) { Subject = leaf.Subject });
        }

        var tag = new CheckedCertificate(device.Id, serial, certificateSerial);
        var site = switches().FindByKey(check.Client)?.SiteId;
        if (device.SiteId != site)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.OtherSite, 49, tag);
        }

        if (plan.RefusalFor(device) is { } refusal)
        {
            if (refusal.Kind == PolicyKinds.Block)
            {
                return ClientCertificateVerdict.Refuse(CertificateRefusals.Blocked(refusal.Name), 49, tag);
            }

            if (issuer.IsOadm)
            {
                revokeByPolicy?.Invoke(certificateSerial, refusal); // a new certificate of the policy's devices: revoked too
            }

            return ClientCertificateVerdict.Refuse(CertificateRefusals.RevokedByPolicy(refusal.Name), 44, tag);
        }

        string? warning = null;
        if (check.Mac.IsValid && !string.Equals(check.Mac.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
        {
            warning = CertificateRefusals.MacMismatch(check.Mac.Normalized!, device.Serial.ToUpperInvariant());
        }

        return ClientCertificateVerdict.Accept(plan.VlanFor(device), warning, tag);
    }

    /// <summary>The one serial of the <c>axis-&lt;serial&gt;.local</c> names; null for none or two.</summary>
    private static string? LocalSerial(X509Certificate2 leaf)
    {
        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames() ?? [];
        var serials = names.Select(AxisLocalSerial).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return serials.Count == 1 ? serials[0] : null;
    }

    /// <summary>The checked CA that issued the leaf directly (it chains to the set's anchors now, offline), else null.</summary>
    internal static TrustedIssuer? IssuerOf(CaTrust trust, X509Certificate2 leaf, IEnumerable<X509Certificate2> presented, DateTimeOffset now)
    {
        if (trust.IsEmpty)
        {
            return null;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trust.Anchors);
        chain.ChainPolicy.ExtraStore.AddRange(trust.Intermediates);
        var owned = new HashSet<X509Certificate2>(ReferenceEqualityComparer.Instance) { leaf };
        owned.UnionWith(trust.Anchors);
        owned.UnionWith(trust.Intermediates);
        foreach (var extra in presented)
        {
            chain.ChainPolicy.ExtraStore.Add(extra);
            owned.Add(extra);
        }

        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        try
        {
            return chain.Build(leaf) && chain.ChainElements.Count >= 2
                && trust.Issuers.TryGetValue(chain.ChainElements[1].Certificate.GetCertHashString(HashAlgorithmName.SHA256), out var issuer)
                ? issuer
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                if (!owned.Contains(element.Certificate))
                {
                    element.Certificate.Dispose();
                }
            }
        }
    }

    /// <summary>The leaf was issued directly by a checked CA of <paramref name="trust"/>.</summary>
    internal static bool ChainsTo(CaTrust trust, X509Certificate2 leaf, IEnumerable<X509Certificate2> presented, DateTimeOffset now) =>
        IssuerOf(trust, leaf, presented, now) is not null;

    /// <summary>The serial of an <c>axis-&lt;12 hex&gt;.local</c> name (upper case), else null.</summary>
    internal static string? AxisLocalSerial(string name) => AxisLocalName().Match(name) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    [GeneratedRegex("^axis-([0-9a-fA-F]{12})\\.local\\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AxisLocalName();
}
