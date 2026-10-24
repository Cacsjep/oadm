using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Extra certificate authorities the server trusts when it rates device certificates (the Certificate column of the
/// device grid), e.g. the CA of the PKI core plugin: a device certificate that chains to one of them counts as Trusted
/// without any change to the OS trust store. Self-signed device certificates stay self-signed. Never affects
/// certificate pinning (whether OADM talks to a device).
/// </summary>
public interface ITrustAnchors
{
    /// <summary>
    /// Replaces this plugin's set of trust anchors (DER encoded certificates, public parts only; an empty list removes
    /// them). The host keeps one set per plugin and trusts the union of all sets. Devices show the new rating with their
    /// next full refresh.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C# plugins only; the name is part of the documented SDK.")]
    void Set(IReadOnlyList<byte[]> derCertificates);

    /// <summary>
    /// The CAs that issue device certificates OADM pins again when a device renews them (DER, the CA certificates
    /// themselves, never the roots above them): a new device certificate issued directly by one of them for the same
    /// device replaces the pin instead of showing Certificate changed. The host uses only the PKI plugin's set (the OADM
    /// CA); other plugins' sets are ignored. Default: nothing.
    /// </summary>
    void SetRenewalIssuers(IReadOnlyList<byte[]> derCertificates)
    {
    }

    /// <summary>
    /// The days before its end that a device's certificate counts as "expires soon" (PKI's Expiry warning policies, SDK
    /// 0.4.0): the device grid's Certificate expires chip of each device and <paramref name="defaultDays"/> for a new
    /// Compliance certificate check. Called again after every change (the host then sends every device row again). The host
    /// uses only the PKI plugin's; null (or none): 30 days. <paramref name="daysFor"/> must be fast (in memory) and thread-safe.
    /// Default: nothing.
    /// </summary>
    void SetExpiryWarning(Func<IDeviceInfo, int>? daysFor, int defaultDays)
    {
    }

    /// <summary>
    /// The OADM server's own TLS certificate (DER, the public part): PKI lists it read-only in its Certificates card (decision
    /// 2026-10-25). Null over plain HTTP or before it is loaded. Default: null.
    /// </summary>
    byte[]? ServerCertificate => null;
}
