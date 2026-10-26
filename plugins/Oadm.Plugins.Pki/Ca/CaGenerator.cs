using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// Creates a CA with <see cref="CertificateRequest"/> (no BouncyCastle): a self-signed root or an intermediate signed by a CA
/// with its key (Create CA, decision 2026-10-26). RSA (2048, 3072, 4096; 4096 by default) with SHA-256 and PKCS#1 v1.5
/// signatures, or ECC P-256 with ECDSA SHA-256; 16 random serial bytes (positive), NotBefore = now - 5 min, BasicConstraints
/// CA (critical, no path length limit), KeyUsage keyCertSign + cRLSign (critical), SubjectKeyIdentifier and
/// AuthorityKeyIdentifier (the issuer's key for an intermediate).
/// </summary>
public static class CaGenerator
{
    /// <summary>A self-signed RSA root CA. Runs on the calling thread: RSA 4096 takes up to a few seconds, call it off the request thread.</summary>
    public static CaMaterial Generate(string commonName, string? organization, int validityYears, DateTimeOffset now, int keySize = PkiPluginInfo.CaKeySize) =>
        Generate(commonName, organization, validityYears, now, CaKeys.Rsa4096, null, keySize);

    /// <summary>
    /// A root CA (<paramref name="issuer"/> null) or an intermediate CA signed by <paramref name="issuer"/> (it needs its key;
    /// the new CA ends at the latest with it and carries its chain). <paramref name="key"/> is one of <see cref="CaKeys"/>.
    /// Runs on the calling thread.
    /// </summary>
    public static CaMaterial Generate(string commonName, string? organization, int validityYears, DateTimeOffset now, string key, CaMaterial? issuer) =>
        Generate(commonName, organization, validityYears, now, key, issuer, null);

    private static CaMaterial Generate(string commonName, string? organization, int validityYears, DateTimeOffset now, string key, CaMaterial? issuer, int? rsaSizeOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentOutOfRangeException.ThrowIfLessThan(validityYears, 1);
        if (issuer is { HasKey: false })
        {
            throw new InvalidOperationException("The CA that signs needs its key.");
        }

        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(commonName.Trim());
        if (!string.IsNullOrWhiteSpace(organization))
        {
            name.AddOrganizationName(organization.Trim());
        }

        var subject = name.Build();
        using var rsa = (rsaSizeOverride ?? CaKeys.RsaSize(key)) is { } size ? RSA.Create(size) : null;
        using var ec = rsa is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = rsa is not null
            ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, ec!, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var ski = new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false);
        request.CertificateExtensions.Add(ski);
        request.CertificateExtensions.Add(issuer is null
            ? X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(ski)
            : X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        if (serial[0] == 0)
        {
            serial[0] = 1; // 16 significant bytes
        }

        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddYears(validityYears);
        X509Certificate2 signed;
        if (issuer is null)
        {
            var generator = rsa is not null ? X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa(ec!);
            signed = request.Create(subject, generator, notBefore, notAfter, serial);
        }
        else
        {
            var end = new DateTimeOffset(issuer.Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (end < notAfter)
            {
                notAfter = end; // never beyond the CA that signs
            }

            using var issuerWithKey = issuer.WithPrivateKey();
            using var issuerRsa = issuerWithKey.GetRSAPrivateKey();
            using var issuerEc = issuerRsa is null ? issuerWithKey.GetECDsaPrivateKey() : null;
            var generator = issuerRsa is not null
                ? X509SignatureGenerator.CreateForRSA(issuerRsa, RSASignaturePadding.Pkcs1)
                : X509SignatureGenerator.CreateForECDsa(issuerEc ?? throw new InvalidOperationException("The key of the CA that signs is not supported."));
            signed = request.Create(issuer.Certificate.SubjectName, generator, notBefore, notAfter, serial);
        }

        using (signed)
        {
            var certificate = X509CertificateLoader.LoadCertificate(signed.RawData);
            var chain = issuer is null
                ? []
                : new[] { issuer.Certificate }.Concat(issuer.Chain).Select(c => X509CertificateLoader.LoadCertificate(c.RawData)).ToList();
            var pem = rsa is not null ? rsa.ExportPkcs8PrivateKeyPem() : ec!.ExportPkcs8PrivateKeyPem();
            return new CaMaterial(certificate, chain, pem);
        }
    }

    /// <summary>
    /// Why <paramref name="issuer"/> cannot sign another CA: not a CA certificate with keyCertSign, or a path length limit of 0;
    /// null when it can.
    /// </summary>
    public static string? CannotSignCa(X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        var constraints = issuer.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (constraints is null || !constraints.CertificateAuthority)
        {
            return "This CA cannot sign other CAs.";
        }

        if (constraints.HasPathLengthConstraint && constraints.PathLengthConstraint == 0)
        {
            return "This CA may not sign other CAs.";
        }

        var usage = issuer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is not null && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) ? "This CA cannot sign other CAs." : null;
    }
}
