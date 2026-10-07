using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// Creates a self-signed root CA with <see cref="CertificateRequest"/> (no BouncyCastle): RSA (4096 by default), SHA-256
/// with PKCS#1 v1.5 signatures, 16 random serial bytes (positive), NotBefore = now - 5 min, BasicConstraints CA (critical,
/// no path length limit), KeyUsage keyCertSign + cRLSign (critical), SubjectKeyIdentifier and AuthorityKeyIdentifier.
/// </summary>
public static class CaGenerator
{
    /// <summary>Runs on the calling thread: RSA 4096 takes up to a few seconds, call it off the request thread.</summary>
    public static CaMaterial Generate(string commonName, string? organization, int validityYears, DateTimeOffset now, int keySize = PkiPluginInfo.CaKeySize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentOutOfRangeException.ThrowIfLessThan(validityYears, 1);

        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(commonName.Trim());
        if (!string.IsNullOrWhiteSpace(organization))
        {
            name.AddOrganizationName(organization.Trim());
        }

        var subject = name.Build();
        using var key = RSA.Create(keySize);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var ski = new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false);
        request.CertificateExtensions.Add(ski);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(ski));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        if (serial[0] == 0)
        {
            serial[0] = 1; // 16 significant bytes
        }

        var notBefore = now.AddMinutes(-5);
        using var signed = request.Create(subject, X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1), notBefore, now.AddYears(validityYears), serial);
        var certificate = X509CertificateLoader.LoadCertificate(signed.RawData);
        return new CaMaterial(certificate, [], key.ExportPkcs8PrivateKeyPem());
    }
}
