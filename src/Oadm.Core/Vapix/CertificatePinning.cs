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
/// </summary>
public sealed class CertificatePinning
{
    private readonly Lock _gate = new();
    private string? _observed;
    private string? _mismatch;

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

    /// <summary>Callback for <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c>.</summary>
    public bool ValidateCallback(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        return Validate(certificate);
    }
}
