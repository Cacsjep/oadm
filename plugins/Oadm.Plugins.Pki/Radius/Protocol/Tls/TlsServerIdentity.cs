using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Oadm.Plugins.Radius.Protocol.Tls;

/// <summary>The kind of server key, which decides the TLS 1.2 cipher suites (ECDHE_ECDSA or ECDHE_RSA).</summary>
public enum ServerKeyKind
{
    Ecdsa,
    Rsa,
}

/// <summary>
/// The server certificate of the 802.1X server (issued by the OADM CA, docs/spec/radius-server.md "Server certificate"):
/// the chain (leaf first, DER) and its private key, ready for BouncyCastle. Immutable; a renewal is a new identity.
/// </summary>
public sealed class TlsServerIdentity
{
    private TlsServerIdentity(IReadOnlyList<byte[]> chain, AsymmetricKeyParameter privateKey, ServerKeyKind kind, int keyBits)
    {
        Chain = chain;
        PrivateKey = privateKey;
        Kind = kind;
        KeyBits = keyBits;
    }

    /// <summary>The certificate chain, leaf first (DER).</summary>
    public IReadOnlyList<byte[]> Chain { get; }

    public ServerKeyKind Kind { get; }

    /// <summary>The curve size of an EC key (256, 384, 521) or the modulus size of an RSA key.</summary>
    public int KeyBits { get; }

    internal AsymmetricKeyParameter PrivateKey { get; }

    /// <summary>From DER certificates (leaf first) and the leaf's private key as PKCS#8 (EC P-256/P-384/P-521 or RSA).</summary>
    public static TlsServerIdentity Create(IReadOnlyList<byte[]> chain, ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("The chain needs the server certificate.", nameof(chain));
        }

        var key = PrivateKeyFactory.CreateKey(pkcs8PrivateKey.ToArray());
        return key switch
        {
            ECPrivateKeyParameters ec => new TlsServerIdentity([.. chain], ec, ServerKeyKind.Ecdsa, ec.Parameters.Curve.FieldSize),
            RsaKeyParameters { IsPrivate: true } rsa => new TlsServerIdentity([.. chain], rsa, ServerKeyKind.Rsa, rsa.Modulus.BitLength),
            _ => throw new ArgumentException("The server key must be an EC or RSA private key.", nameof(pkcs8PrivateKey)),
        };
    }

    /// <summary>From a .NET certificate with its (exportable) private key and the issuing certificates up to the CA.</summary>
    public static TlsServerIdentity FromCertificate(X509Certificate2 certificate, IEnumerable<X509Certificate2>? issuers = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] pkcs8;
        using (var ec = certificate.GetECDsaPrivateKey())
        using (var rsa = ec is null ? certificate.GetRSAPrivateKey() : null)
        {
            pkcs8 = ec?.ExportPkcs8PrivateKey() ?? rsa?.ExportPkcs8PrivateKey() ?? throw new ArgumentException("The certificate has no EC or RSA private key.", nameof(certificate));
        }

        try
        {
            List<byte[]> chain = [certificate.RawData, .. (issuers ?? []).Select(c => c.RawData)];
            return Create(chain, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
