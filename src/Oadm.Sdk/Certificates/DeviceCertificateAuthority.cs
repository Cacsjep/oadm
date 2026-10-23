namespace Oadm.Sdk.Certificates;

/// <summary>What a device certificate is for: its extended key usage.</summary>
public enum DeviceCertificateUse
{
    /// <summary>A server certificate (HTTPS, RTSPS, MQTT over TLS): serverAuth.</summary>
    Server = 0,

    /// <summary>A client certificate (IEEE 802.1X, MQTT client): clientAuth.</summary>
    Client = 1,
}

/// <summary>
/// A certificate request for <see cref="IDeviceCertificateAuthority.SignAsync"/>. The subject and alternative names come
/// from OADM, never from the request; the request only gives the public key and proves it holds the private key.
/// </summary>
/// <param name="Csr">The PKCS#10 request, DER.</param>
/// <param name="CommonName">The subject's common name.</param>
/// <param name="IpAddresses">IP addresses for the alternative names.</param>
/// <param name="DnsNames">Host names for the alternative names, e.g. <c>axis-&lt;serial&gt;.local</c>.</param>
/// <param name="Use">Server or client certificate.</param>
public sealed record DeviceCertificateRequest(
    ReadOnlyMemory<byte> Csr,
    string CommonName,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsNames,
    DeviceCertificateUse Use)
{
    /// <summary>The managed device the certificate is for; it is listed with the device's certificates. Null for OADM's own.</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>How long it is valid; null = the CA's setting for device certificates. Always ends with the CA.</summary>
    public TimeSpan? Validity { get; init; }

    /// <summary>Who asked for it, for the CA's list ("EST"); null = the CA's own tasks.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The use in the list of issued certificates (SDK 0.4.0, one of <see cref="DeviceCertificatePurposes"/>: HTTPS, IEEE
    /// 802.1X, MQTT, RTSPS); null = from <see cref="Use"/> (HTTPS for a server, IEEE 802.1X for a client certificate).
    /// </summary>
    public string? Purpose { get; init; }
}

/// <summary>A signed certificate.</summary>
/// <param name="Certificate">The certificate, DER.</param>
/// <param name="Chain">The CA certificate and the certificates above it, DER, issuing CA first.</param>
/// <param name="SerialNumber">The serial number, upper-case hex.</param>
/// <param name="NotAfterUtc">The end of validity.</param>
public sealed record IssuedDeviceCertificate(byte[] Certificate, IReadOnlyList<byte[]> Chain, string SerialNumber, DateTime NotAfterUtc);

/// <summary>The request was refused: unreadable, wrong signature, a weak key or digest, or the CA cannot sign.</summary>
public sealed class CertificateRequestRefusedException : Exception
{
    /// <summary>Without a reason.</summary>
    public CertificateRequestRefusedException()
    {
    }

    /// <summary>With the plain reason.</summary>
    public CertificateRequestRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>With the plain reason and the error behind it.</summary>
    public CertificateRequestRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The OADM CA, offered by the PKI plugin through <see cref="Plugins.IPluginServices"/>: one CA signs every device
/// certificate. Keys RSA 2048 or more, or EC P-256 or more; requests signed with SHA-1 or MD5 are refused.
/// </summary>
public interface IDeviceCertificateAuthority
{
    /// <summary>A CA with its private key exists, so <see cref="SignAsync"/> can sign.</summary>
    bool IsReady { get; }

    /// <summary>The CA certificate and the certificates above it, DER, issuing CA first; empty without a CA.</summary>
    IReadOnlyList<byte[]> Chain { get; }

    /// <summary>Raised after the CA was generated, imported or replaced. Runs on a server thread; keep it short.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Signs a request. Throws <see cref="CertificateRequestRefusedException"/> with a plain reason. A certificate with a
    /// <see cref="DeviceCertificateRequest.DeviceId"/> goes into the list of issued certificates.
    /// </summary>
    Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct);

    // ------------------------------------------------------------------ SDK 0.4.0: issued certificates and revocation

    /// <summary>
    /// True when the certificate with <paramref name="serialNumber"/> is on the revocation list (any notation, see
    /// <see cref="CertificateSerials.Normalize"/>). A hash lookup in memory: ask it for every authentication or renewal.
    /// Default (a CA without a list): false.
    /// </summary>
    bool IsRevoked(string serialNumber) => false;

    /// <summary>The revocation of <paramref name="serialNumber"/>, null when it is not revoked (for the refusal's text).</summary>
    CertificateRevocation? FindRevocation(string serialNumber) => null;

    /// <summary>The revocation list: revoked certificates that could still be valid, newest first.</summary>
    IReadOnlyList<CertificateRevocation> Revocations => [];

    /// <summary>
    /// Raised after certificates were revoked or left the revocation list. Runs on a server thread; keep it short. The event
    /// belongs to this offer: subscribe again when <see cref="Plugins.IPluginServices.Find{T}"/> returns another instance
    /// (PKI restarted), and ask <see cref="IsRevoked"/> of the current offer.
    /// </summary>
    event EventHandler? RevocationChanged
    {
        add { }
        remove { }
    }

    /// <summary>One page of the list of issued certificates (filters, search, newest first). Default: an empty page.</summary>
    Task<IssuedCertificatePage> ListIssuedAsync(IssuedCertificateQuery query, CancellationToken ct) => Task.FromResult(IssuedCertificatePage.Empty);

    /// <summary>The certificate (DER) with <paramref name="serialNumber"/> when the list holds it; null otherwise.</summary>
    Task<byte[]?> GetIssuedCertificateAsync(string serialNumber, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Puts the certificates on the revocation list (no undo; only for users with all devices: the caller checks). Unknown
    /// and expired serials are counted, never added. Raises <see cref="RevocationChanged"/> once. Default: not supported.
    /// </summary>
    Task<RevokeResult> RevokeAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        throw new NotSupportedException("This certificate authority keeps no revocation list.");
}
