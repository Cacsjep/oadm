using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The certificate authorities a client certificate may chain to (docs/spec/radius-server.md "Who passes"): the OADM CA with
/// the certificates above it and the previous OADM CAs with theirs, as one immutable set. Self-signed certificates are the
/// trust anchors (the OADM CA itself when it is a root, else the root of its chain), the rest intermediates. A device
/// certificate must be issued directly by one of the OADM CAs (<see cref="Issuers"/>): an OADM CA imported as an
/// intermediate of an enterprise root never makes the certificates of its sibling CAs pass. Rebuilt when PKI changes its CA
/// (and checked every minute), never per authentication.
/// </summary>
public sealed class CaTrust
{
    private CaTrust(string key, X509Certificate2Collection anchors, X509Certificate2Collection intermediates, IReadOnlySet<string> issuers, IDeviceCertificateAuthority? authority = null, long version = 0)
    {
        Key = key;
        Anchors = anchors;
        Intermediates = intermediates;
        Issuers = issuers;
        Authority = authority;
        Version = version;
    }

    /// <summary>No CA: nothing chains.</summary>
    public static CaTrust None { get; } = new(string.Empty, [], [], new HashSet<string>());

    /// <summary>The SHA-256 hashes of the OADM CAs themselves (the first certificate of the current and of each previous chain).</summary>
    public IReadOnlySet<string> Issuers { get; }

    /// <summary>A fingerprint of every certificate in the set (to tell whether a rebuild changed anything).</summary>
    public string Key { get; }

    public X509Certificate2Collection Anchors { get; }

    public X509Certificate2Collection Intermediates { get; }

    public bool IsEmpty => Anchors.Count == 0;

    /// <summary>The CA offer the set was built from (null: none). A new offer (PKI restarted) is a new set.</summary>
    public IDeviceCertificateAuthority? Authority { get; }

    /// <summary>
    /// The 802.1X server's count of the CA's <see cref="IDeviceCertificateAuthority.Changed"/> events when the set was built: a
    /// handshake compares the offer and this number (no hashing per handshake); the minute round compares <see cref="Key"/>.
    /// </summary>
    public long Version { get; }

    /// <summary>The key of the CA's chains now (cheap: hashes of the DER).</summary>
    public static string KeyOf(IDeviceCertificateAuthority? authority)
    {
        if (authority is null)
        {
            return string.Empty;
        }

        var chains = new List<IReadOnlyList<byte[]>> { authority.Chain };
        chains.AddRange(authority.PreviousChains);
        return string.Join('|', chains.Select(c => string.Join(',', c.Select(d => Convert.ToHexString(SHA256.HashData(d))[..16]))));
    }

    /// <summary>The set of <paramref name="authority"/> now (none without a CA).</summary>
    public static CaTrust Build(IDeviceCertificateAuthority? authority, long version = 0)
    {
        if (authority is null)
        {
            return None;
        }

        if (authority.Chain.Count == 0)
        {
            return new CaTrust(KeyOf(authority), [], [], new HashSet<string>(), authority, version); // a CA without a certificate: nothing chains
        }

        var anchors = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        var issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chains = new List<IReadOnlyList<byte[]>> { authority.Chain };
        chains.AddRange(authority.PreviousChains);
        foreach (var chain in chains)
        {
            var loaded = new List<X509Certificate2>();
            foreach (var der in chain)
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

            if (loaded.Count > 0 && chain.Count > 0 && loaded[0].RawData.AsSpan().SequenceEqual(chain[0]))
            {
                issuers.Add(loaded[0].GetCertHashString(HashAlgorithmName.SHA256)); // the OADM CA of this chain (an unreadable first entry is no issuer)
            }

            for (var i = 0; i < loaded.Count; i++)
            {
                var certificate = loaded[i];
                if (!seen.Add(certificate.Thumbprint))
                {
                    certificate.Dispose();
                    continue;
                }

                // The top of a chain without a root (an intermediate CA imported without its root) is the anchor itself.
                var anchor = IsSelfSigned(certificate) || i == loaded.Count - 1;
                (anchor ? anchors : intermediates).Add(certificate);
            }
        }

        return new CaTrust(KeyOf(authority), anchors, intermediates, issuers, authority, version);
    }

    private static bool IsSelfSigned(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
}

/// <summary>What the certificate check found out, handed back with the session (<see cref="ClientCertificateVerdict.Tag"/>).</summary>
/// <param name="DeviceId">The managed device the certificate names; null when it names none OADM manages.</param>
/// <param name="DeviceSerial">The serial the certificate names (12 hex digits), when it names one.</param>
/// <param name="CertificateSerial">The client certificate's serial number (<see cref="CertificateSerials.Normalize"/>).</param>
public sealed record CheckedCertificate(Guid? DeviceId, string? DeviceSerial, string? CertificateSerial);

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
/// chains to the OADM CA (or a previous OADM CA, or a root above them) and is not listed by PKI with another use than IEEE
/// 802.1X, or chains to the Axis device ID roots, names exactly one device by its <c>axis-&lt;serial&gt;.local</c> name (OADM
/// CA) or its device ID serial, OADM manages that device, at a site it is a device of the switch's site, and the
/// certificate is not on PKI's revocation list. The MAC address only adds a warning; the VLAN comes from the rules.
/// </summary>
public sealed partial class DeviceCertificateValidator(
    Func<CaTrust> trust,
    AxisDeviceId deviceIds,
    DeviceIndex devices,
    Func<IDeviceCertificateAuthority?> authority,
    Func<SwitchTable> switches,
    Func<VlanPlan> vlans,
    TimeProvider time) : IClientCertificateValidator
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
        var ca = authority();
        if (ca is null)
        {
            // Without PKI there is no revocation list: no certificate passes, Axis device ID ones neither (fail closed).
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NoCa, 80, new CheckedCertificate(null, null, certificateSerial));
        }

        var notAfter = leaf.NotAfter.ToUniversalTime();
        var notBefore = leaf.NotBefore.ToUniversalTime();
        if (now.UtcDateTime > notAfter)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.Expired(notAfter), 45, new CheckedCertificate(null, null, certificateSerial));
        }

        if (now.UtcDateTime < notBefore)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotYetValid(notBefore), 45, new CheckedCertificate(null, null, certificateSerial));
        }

        if (leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } usage
            && !usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is ClientAuthOid or AnyUsageOid))
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotForNetworkAccess, 42, new CheckedCertificate(null, null, certificateSerial));
        }

        string? serial;
        var trusted = trust();
        var oadmCa = ChainsTo(trusted, leaf, presented, now);
        if (oadmCa && !ReferenceEquals(trusted.Authority, ca))
        {
            // The trusted set is from a CA offer that is gone (PKI restarted in the meantime): without that CA's current
            // revocation list nothing of it passes (fail closed). A changed CA of the same offer rebuilds the set first.
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NoCa, 80, new CheckedCertificate(null, null, certificateSerial));
        }

        if (oadmCa)
        {
            // PKI lists every certificate the OADM CA issued with its use: one made for MQTT (also client authentication,
            // also axis-<serial>.local) is no network access certificate. Unlisted ones (from before the list) pass as before.
            if (ca.PurposeOf(certificateSerial) is { } purpose && !string.Equals(purpose, DeviceCertificatePurposes.Dot1x, StringComparison.Ordinal))
            {
                return ClientCertificateVerdict.Refuse(CertificateRefusals.NotDot1x, 42, new CheckedCertificate(null, null, certificateSerial));
            }

            var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames() ?? [];
            var serials = names.Select(AxisLocalSerial).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            serial = serials.Count == 1 ? serials[0] : null;
            if (serial is null)
            {
                return ClientCertificateVerdict.Refuse(CertificateRefusals.NamesNoDevice, 42, new CheckedCertificate(null, null, certificateSerial));
            }
        }
        else if (deviceIds.Verify(leaf, presented, now) is { } factorySerial)
        {
            serial = factorySerial;
        }
        else
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotTrusted, 48, new CheckedCertificate(null, null, certificateSerial));
        }

        var device = devices.BySerial(serial);
        if (ca.IsRevoked(certificateSerial))
        {
            var text = ca.FindRevocation(certificateSerial)?.Describe() ?? CertificateRefusals.Revoked;
            return ClientCertificateVerdict.Refuse(text, 44, new CheckedCertificate(device?.Id, serial, certificateSerial));
        }

        if (device is null)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.NotManaged(serial), 49, new CheckedCertificate(null, serial, certificateSerial));
        }

        var tag = new CheckedCertificate(device.Id, serial, certificateSerial);
        var site = switches().FindByKey(check.Client)?.SiteId;
        if (device.SiteId != site)
        {
            return ClientCertificateVerdict.Refuse(CertificateRefusals.OtherSite, 49, tag);
        }

        string? warning = null;
        if (check.Mac.IsValid && !string.Equals(check.Mac.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
        {
            warning = CertificateRefusals.MacMismatch(check.Mac.Normalized!, device.Serial.ToUpperInvariant());
        }

        return ClientCertificateVerdict.Accept(vlans().For(device), warning, tag);
    }

    /// <summary>
    /// The leaf chains to one of the OADM anchors now (the device's own intermediates may help), offline, and its direct
    /// issuer is one of the OADM CAs (<see cref="CaTrust.Issuers"/>), never another CA under the same root.
    /// </summary>
    internal static bool ChainsTo(CaTrust trust, X509Certificate2 leaf, IEnumerable<X509Certificate2> presented, DateTimeOffset now)
    {
        if (trust.IsEmpty)
        {
            return false;
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
            return chain.Build(leaf) && chain.ChainElements.Count >= 2 && trust.Issuers.Contains(chain.ChainElements[1].Certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (CryptographicException)
        {
            return false;
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

    /// <summary>The serial of an <c>axis-&lt;12 hex&gt;.local</c> name (upper case), else null.</summary>
    internal static string? AxisLocalSerial(string name) => AxisLocalName().Match(name) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    [GeneratedRegex("^axis-([0-9a-fA-F]{12})\\.local\\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AxisLocalName();
}
