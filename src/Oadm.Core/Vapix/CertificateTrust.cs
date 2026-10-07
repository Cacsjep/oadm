using System.Security.Cryptography.X509Certificates;

namespace Oadm.Core.Vapix;

/// <summary>How far the server trusts the TLS certificate a device presents. Only the chain is evaluated, never the host name.</summary>
public enum CertificateTrust
{
    /// <summary>Not checked yet, or the device is HTTP only.</summary>
    Unknown = 0,

    /// <summary>The chain builds to a root in the server OS trust store.</summary>
    Trusted = 1,

    /// <summary>Subject equals issuer and the chain fails only because that root is not trusted.</summary>
    SelfSigned = 2,

    /// <summary>Any other chain failure, e.g. issued by a private CA that is not in the trust store.</summary>
    Untrusted = 3,

    /// <summary>The certificate is past its NotAfter date.</summary>
    Expired = 4,
}

/// <summary>
/// What the server learned from the certificate of one TLS handshake.
/// <see cref="ChainTrust"/> is the time-independent chain result; use <see cref="TrustAt"/> for the
/// displayed trust, which turns into <see cref="CertificateTrust.Expired"/> once <see cref="NotAfterUtc"/> passed.
/// </summary>
/// <param name="Fingerprint">SHA-256, upper hex without separators.</param>
/// <param name="Subject">Subject distinguished name.</param>
/// <param name="Issuer">Issuer distinguished name.</param>
/// <param name="NotBeforeUtc">Start of validity (UTC).</param>
/// <param name="NotAfterUtc">End of validity (UTC).</param>
/// <param name="ChainTrust">Trusted, SelfSigned or Untrusted; evaluated ignoring validity dates.</param>
/// <param name="NameMatches">The host name or IP address we connected to is in the SAN (or CN). Null when unknown.</param>
public sealed record CertificateInfo(
    string Fingerprint,
    string Subject,
    string Issuer,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    CertificateTrust ChainTrust,
    bool? NameMatches)
{
    /// <summary>Trust at a point in time: <see cref="CertificateTrust.Expired"/> after NotAfter, else <see cref="ChainTrust"/>.</summary>
    public CertificateTrust TrustAt(DateTimeOffset now) => now.UtcDateTime > NotAfterUtc ? CertificateTrust.Expired : ChainTrust;
}

/// <summary>
/// Classifies a presented certificate with a separate <see cref="X509Chain"/> build (system trust
/// store, no revocation check, no downloads). Independent of TOFU pinning: pinning decides whether
/// we talk to the device, this only reports how a browser-like client would see the certificate.
/// Cross-platform: uses only <see cref="X509Chain"/> and <see cref="X509Certificate2"/> APIs.
/// </summary>
public static class CertificateTrustEvaluator
{
    /// <summary>Builds the full <see cref="CertificateInfo"/> for a presented certificate.</summary>
    /// <param name="certificate">Leaf certificate presented by the device.</param>
    /// <param name="intermediates">Other certificates the device sent (from the handshake chain), may be null.</param>
    /// <param name="host">Host name or IP address we connected to, for <see cref="CertificateInfo.NameMatches"/>.</param>
    /// <param name="customRoots">Roots to trust instead of the system store (tests); null uses the OS store.</param>
    public static CertificateInfo Describe(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2>? intermediates = null,
        string? host = null,
        X509Certificate2Collection? customRoots = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new CertificateInfo(
            CertificatePinning.ComputeFingerprint(certificate),
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            EvaluateChain(certificate, intermediates, customRoots),
            MatchesHost(certificate, host));
    }

    /// <summary>
    /// Chain trust ignoring validity dates and host name: Trusted, SelfSigned or Untrusted.
    /// Expiry is applied later with <see cref="CertificateInfo.TrustAt"/>.
    /// </summary>
    public static CertificateTrust EvaluateChain(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2>? intermediates = null,
        X509Certificate2Collection? customRoots = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid;
        if (customRoots is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(customRoots);
        }

        if (intermediates is not null)
        {
            foreach (var extra in intermediates)
            {
                if (!string.Equals(extra.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    chain.ChainPolicy.ExtraStore.Add(extra);
                }
            }
        }

        try
        {
            if (chain.Build(certificate))
            {
                return CertificateTrust.Trusted;
            }

            var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
            const X509ChainStatusFlags ignored = X509ChainStatusFlags.NoError
                | X509ChainStatusFlags.NotTimeValid
                | X509ChainStatusFlags.CtlNotTimeValid;
            var remaining = flags & ~ignored;
            return IsSelfIssued(certificate) && remaining == X509ChainStatusFlags.UntrustedRoot
                ? CertificateTrust.SelfSigned
                : CertificateTrust.Untrusted;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                if (!ReferenceEquals(element.Certificate, certificate))
                {
                    element.Certificate.Dispose();
                }
            }
        }
    }

    /// <summary>Subject and issuer distinguished names are identical.</summary>
    public static bool IsSelfIssued(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }

    /// <summary>The host name or IP address is covered by the SAN (or the CN when there is no SAN). Null for no host.</summary>
    public static bool? MatchesHost(X509Certificate2 certificate, string? host)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var name = host.Trim().TrimStart('[').TrimEnd(']');
        try
        {
            return certificate.MatchesHostname(name, allowWildcards: true, allowCommonName: true);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
