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

    /// <summary>Signs a request. Throws <see cref="CertificateRequestRefusedException"/> with a plain reason.</summary>
    Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct);
}
