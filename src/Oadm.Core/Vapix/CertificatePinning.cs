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
    private string? _renewed;
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

    /// <summary>
    /// The device's serial number (MAC, <c>Device.Serial</c>) for accepting renewals: a new server certificate issued
    /// directly by an OADM CA (<see cref="TrustAnchorSnapshot.RenewalIssuers"/>, the PKI plugin's CAs only, never the roots
    /// above them), valid now and naming the device (<c>axis-&lt;serial&gt;.local</c>) is accepted instead of a pin
    /// mismatch (<see cref="RenewedFingerprint"/>). Null: every other certificate is a mismatch.
    /// </summary>
    public string? DeviceSerial { get; init; }

    /// <summary>The clock for the renewal check.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Fingerprint of a certificate accepted as a renewal by an OADM CA (<see cref="DeviceSerial"/>); the caller pins it.
    /// Null while the device presents the pinned certificate.
    /// </summary>
    public string? RenewedFingerprint
    {
        get
        {
            lock (_gate)
            {
                return _renewed;
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

            if (PinnedFingerprint is { } pinned && IsRenewal(certificate, anchors))
            {
                var fingerprint = ComputeFingerprint(certificate);
                lock (_gate)
                {
                    _observed = fingerprint;
                    _mismatch = null;
                    _renewed = string.Equals(pinned, fingerprint, StringComparison.Ordinal) ? null : fingerprint;
                }

                return true;
            }
        }

        return Validate(certificate);
    }

    /// <summary>
    /// A server certificate an OADM CA issued directly for this device: valid now, serverAuth, names
    /// <c>axis-&lt;serial&gt;.local</c> (docs/spec/est-server.md "Certificate renewals and pinning").
    /// </summary>
    private bool IsRenewal(X509Certificate2 certificate, TrustAnchorSnapshot? anchors)
    {
        if (anchors is not { RenewalIssuers.Count: > 0 } || string.IsNullOrWhiteSpace(DeviceSerial))
        {
            return false;
        }

        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is not { } eku
            || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1"))
        {
            return false; // only server certificates
        }

        var now = Time.GetUtcNow().UtcDateTime;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        var name = "axis-" + DeviceSerial.Trim().ToLowerInvariant() + ".local";
        try
        {
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            if (san is null || !san.EnumerateDnsNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return CertificateTrustEvaluator.IssuedDirectlyBy(certificate, anchors.RenewalIssuers, anchors.Certificates, now);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Callback for <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c>.</summary>
    public bool ValidateCallback(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var presented = chain?.ChainElements.Select(e => e.Certificate).ToList();
        return Validate(certificate, presented, request?.RequestUri?.Host);
    }

    /// <summary>Callback for <c>SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback</c> (the VAPIX client).</summary>
    public bool ValidateSslCallback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var presented = chain?.ChainElements.Select(e => e.Certificate).ToList();
        var certificate2 = certificate as X509Certificate2 ?? (certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        return Validate(certificate2, presented, (sender as SslStream)?.TargetHostName);
    }
}
