using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>A CA in memory: certificate (public), the issuers above it and its private key (PKCS#8 PEM).</summary>
public sealed class CaMaterial : IDisposable
{
    public CaMaterial(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain, string? keyPkcs8Pem)
    {
        Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        KeyPkcs8Pem = keyPkcs8Pem;
        Id = CaCertificates.Fingerprint(certificate);
    }

    /// <summary>SHA-256 fingerprint (upper hex).</summary>
    public string Id { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>Issuers above the CA, nearest first.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>The private key; null when it could not be decrypted.</summary>
    public string? KeyPkcs8Pem { get; }

    public bool HasKey => KeyPkcs8Pem is not null;

    public string CertificatePem => CaCertificates.ToPem(Certificate);

    public IReadOnlyList<string> ChainPem => [.. Chain.Select(CaCertificates.ToPem)];

    /// <summary>The CA certificate with its private key (for signing and the backup). The caller disposes it.</summary>
    public X509Certificate2 WithPrivateKey()
    {
        var pem = KeyPkcs8Pem ?? throw new InvalidOperationException("The CA key is not available.");
        using (var rsa = Certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                using var key = RSA.Create();
                key.ImportFromPem(pem);
                return Certificate.CopyWithPrivateKey(key);
            }
        }

        using var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        return Certificate.CopyWithPrivateKey(ec);
    }

    public CaInfo Describe(string source) => new()
    {
        Id = Id,
        Subject = Certificate.Subject,
        CommonName = CaCertificates.CommonName(Certificate),
        Organization = CaCertificates.Organization(Certificate),
        Source = source,
        KeyType = CaCertificates.KeyType(Certificate),
        NotBeforeUtc = Certificate.NotBefore.ToUniversalTime(),
        NotAfterUtc = Certificate.NotAfter.ToUniversalTime(),
        Fingerprint = CaCertificates.ColonFingerprint(Id),
        IsIntermediate = !CaCertificates.IsSelfIssued(Certificate),
        ChainSubjects = [.. Chain.Select(CaCertificates.CommonName)],
        CertificatePem = CertificatePem,
    };

    public void Dispose()
    {
        Certificate.Dispose();
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }
    }
}
