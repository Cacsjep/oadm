using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Core.Vapix;

/// <summary>
/// Trust-on-first-use certificate check for one device. Axis devices ship self-signed
/// certificates, so chain validation is ignored and the SHA-256 fingerprint is pinned instead.
/// With no pinned fingerprint every certificate is accepted and its fingerprint is captured in
/// <see cref="ObservedFingerprint"/> so the caller can store it. With a pinned fingerprint a
/// different certificate is rejected and <see cref="MismatchFingerprint"/> is set; the
/// <see cref="VapixClient"/> turns that into a <see cref="CertificateChangedException"/>.
/// Independently of pinning, every presented certificate is described in
/// <see cref="ObservedCertificate"/> (subject, issuer, validity, chain trust against the OS store,
/// host name match) by a separate chain build; that never changes the pinning decision.
/// </summary>
public sealed class CertificatePinning
{
    private readonly Lock _gate = new();
    private string? _observed;
    private string? _mismatch;
    private CertificateInfo? _certificate;
    private X509Certificate2? _presented;
    private X509Certificate2[] _presentedChain = [];
    private string? _host;
    private long _anchorVersion;

    public CertificatePinning(string? pinnedFingerprint = null)
    {
        PinnedFingerprint = string.IsNullOrWhiteSpace(pinnedFingerprint) ? null : Normalize(pinnedFingerprint);
    }

    /// <summary>Expected fingerprint (upper hex, no separators), or null for trust on first use.</summary>
    public string? PinnedFingerprint { get; }

    /// <summary>Fingerprint of the last certificate the device presented.</summary>
    public string? ObservedFingerprint
    {
        get
        {
            lock (_gate)
            {
                return _observed;
            }
        }
    }

    /// <summary>Fingerprint of the last rejected certificate, null when no mismatch was seen.</summary>
    public string? MismatchFingerprint
    {
        get
        {
            lock (_gate)
            {
                return _mismatch;
            }
        }
    }

    /// <summary>
    /// Description of the last certificate the device presented (any handshake, pinned or not). With
    /// <see cref="TrustAnchors"/> the chain trust is evaluated again when the anchors changed since the handshake
    /// (pooled connections are reused, so a new handshake may not come soon).
    /// </summary>
    public CertificateInfo? ObservedCertificate
    {
        get
        {
            lock (_gate)
            {
                if (TrustAnchors is { } anchors && _presented is { } presented && anchors.Current.Version != _anchorVersion)
                {
                    var current = anchors.Current;
                    try
                    {
                        _certificate = CertificateTrustEvaluator.Describe(presented, _presentedChain, _host, CustomTrustRoots, current.Certificates);
                    }
                    catch (CryptographicException)
                    {
                        // keep the earlier description
                    }

                    _anchorVersion = current.Version;
                }

                return _certificate;
            }
        }
    }

    /// <summary>Trust roots for <see cref="ObservedCertificate"/> instead of the OS store (tests only).</summary>
    public X509Certificate2Collection? CustomTrustRoots { get; init; }

    /// <summary>
    /// Extra trust anchors (e.g. the CA of the PKI plugin) for <see cref="ObservedCertificate"/>: a certificate that
    /// chains to one of them is Trusted. Never changes the pinning decision.
    /// </summary>
    public TrustAnchorRegistry? TrustAnchors { get; init; }

    /// <summary>SHA-256 fingerprint as upper-case hex without separators.</summary>
    public static string ComputeFingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>Accepts "AA:BB:..", "aa bb .." or "AABB.." and returns upper hex without separators.</summary>
    public static string Normalize(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new string(fingerprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    }

    /// <summary>Validates a presented certificate. Returns false only on a pin mismatch or a missing certificate.</summary>
    public bool Validate(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            lock (_gate)
            {
                _mismatch = PinnedFingerprint is null ? null : string.Empty;
            }

            return false;
        }

        var fingerprint = ComputeFingerprint(certificate);
        lock (_gate)
        {
            _observed = fingerprint;
            if (PinnedFingerprint is null || string.Equals(PinnedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _mismatch = null;
                return true;
            }

            _mismatch = fingerprint;
            return false;
        }
    }

    /// <summary>
    /// Records the presented certificate in <see cref="ObservedCertificate"/> and then applies the pin
    /// (<see cref="Validate(X509Certificate?)"/>). <paramref name="presentedChain"/> supplies the
    /// intermediates the device sent; <paramref name="host"/> is used for the name match only.
    /// </summary>
    public bool Validate(X509Certificate2? certificate, IEnumerable<X509Certificate2>? presentedChain, string? host)
    {
        if (certificate is not null)
        {
            CertificateInfo? info;
            var anchors = TrustAnchors?.Current;
            IReadOnlyList<X509Certificate2>? chain = presentedChain as IReadOnlyList<X509Certificate2> ?? presentedChain?.ToList();
            try
            {
                info = CertificateTrustEvaluator.Describe(certificate, chain, host, CustomTrustRoots, anchors?.Certificates);
            }
            catch (CryptographicException)
            {
                info = null; // describing is best effort, pinning below still decides
            }

            // With anchors, keep copies of the presented certificates to rate them again when the anchors change.
            X509Certificate2? presented = null;
            X509Certificate2[] copies = [];
            if (anchors is not null)
            {
                presented = X509CertificateLoader.LoadCertificate(certificate.RawData);
                copies = chain is null ? [] : [.. chain.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))];
            }

            lock (_gate)
            {
                _certificate = info;
                _presented = presented;
                _presentedChain = copies;
                _host = host;
                _anchorVersion = anchors?.Version ?? 0;
            }
        }

        return Validate(certificate);
    }

    /// <summary>Callback for <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c>.</summary>
    public bool ValidateCallback(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var presented = chain?.ChainElements.Select(e => e.Certificate).ToList();
        return Validate(certificate, presented, request?.RequestUri?.Host);
    }
}
