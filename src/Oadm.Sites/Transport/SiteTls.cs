using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Sites.Transport;

/// <summary>
/// TLS of the link between server and site node: TLS 1.3, or 1.2 where the OS has no 1.3 (Windows Server 2019 and Windows
/// 10 1809; decision 2026-10-19), forward-secret AEAD suites only where the OS lets us choose (Linux). Both sides present
/// a self-signed certificate and each side checks the other's fingerprint (SHA-256 of the certificate) only; dates and
/// names are ignored (a site node often has no clock).
/// </summary>
public static class SiteTls
{
    /// <summary>
    /// Every handshake must end within this time. SslStream has no timeout of its own: a peer that stops in the middle
    /// (an OS without a common protocol was seen to) would hold the accepting side forever.
    /// </summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private const SslProtocols Protocols = SslProtocols.Tls13 | SslProtocols.Tls12;

    /// <summary>A new identity: ECDSA P-256, self-signed, valid 100 years.</summary>
    public static X509Certificate2 CreateIdentity(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + Escape(name)), key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var cert = request.CreateSelfSigned(now, now.AddYears(100));
        // A PFX round trip gives an ephemeral key that SslStream can use on every OS.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>The fingerprint the sides pin: SHA-256 of the certificate (32 bytes).</summary>
    public static byte[] Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.GetRawCertData());
    }

    /// <summary>The fingerprint as upper case hex (64 characters), as stored and shown.</summary>
    public static string FingerprintHex(X509Certificate certificate) => Convert.ToHexString(Fingerprint(certificate));

    /// <summary>
    /// Options of the side that accepts the TCP connection (TLS server). <paramref name="accept"/> decides on the other side's
    /// certificate fingerprint (null = no certificate): pinned, or anything while pairing.
    /// </summary>
    public static SslServerAuthenticationOptions ServerOptions(X509Certificate2 identity, Func<byte[]?, bool> accept) => new()
    {
        ServerCertificate = identity,
        ClientCertificateRequired = true,
        EnabledSslProtocols = Protocols,
        CipherSuitesPolicy = Suites(),
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, cert, _, _) => accept(cert is null ? null : Fingerprint(cert)),
    };

    /// <summary>Options of the side that opens the TCP connection (TLS client).</summary>
    public static SslClientAuthenticationOptions ClientOptions(X509Certificate2 identity, Func<byte[]?, bool> accept) => new()
    {
        TargetHost = "oadm-site",
        ClientCertificates = [identity],
        LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
        EnabledSslProtocols = Protocols,
        CipherSuitesPolicy = Suites(),
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, cert, _, _) => accept(cert is null ? null : Fingerprint(cert)),
    };

    // Only Linux lets an application choose (Windows and macOS throw); the identities are ECDSA, so these are all a link uses.
    private static CipherSuitesPolicy? Suites() => OperatingSystem.IsLinux()
        ? new CipherSuitesPolicy(
        [
            TlsCipherSuite.TLS_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        ])
        : null;

    private static string Escape(string name) =>
        new([.. name.Where(c => !char.IsControl(c) && c is not (',' or '=' or '+' or '"' or '\\' or '<' or '>' or ';'))]);
}
