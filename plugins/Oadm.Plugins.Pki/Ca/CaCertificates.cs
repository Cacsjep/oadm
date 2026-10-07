using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>Certificate helpers: fingerprints, names, PEM, key types. Only System.Security.Cryptography (cross-platform).</summary>
public static class CaCertificates
{
    /// <summary>SHA-256 of the DER certificate, upper hex without separators (the CA id).</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    /// <summary>"3F:A2:..." for display.</summary>
    public static string ColonFingerprint(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var builder = new StringBuilder(hex.Length * 3 / 2);
        for (var i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(hex, i, Math.Min(2, hex.Length - i));
        }

        return builder.ToString();
    }

    public static string CommonName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(cn) ? certificate.Subject : cn;
    }

    /// <summary>The O= of the subject, or null.</summary>
    public static string? Organization(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == "2.5.4.10")
            {
                return rdn.GetSingleElementValue();
            }
        }

        return null;
    }

    /// <summary>"RSA 4096", "ECDSA P-256", or the algorithm OID.</summary>
    public static string KeyType(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"RSA {rsa.KeySize}");
            }
        }

        using (var ecdsa = certificate.GetECDsaPublicKey())
        {
            if (ecdsa is not null)
            {
                return "ECDSA " + CurveName(ecdsa);
            }
        }

        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? "Unknown";
    }

    /// <summary>"P-256", "P-384", "P-521" or the curve's name.</summary>
    public static string CurveName(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var curve = key.ExportParameters(false).Curve;
        return curve.Oid?.Value switch
        {
            "1.2.840.10045.3.1.7" => "P-256",
            "1.3.132.0.34" => "P-384",
            "1.3.132.0.35" => "P-521",
            _ => curve.Oid?.FriendlyName ?? "unknown curve",
        };
    }

    public static string ToPem(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)) + "\n";
    }

    /// <summary>Every CERTIFICATE block of a PEM text, in order.</summary>
    public static List<X509Certificate2> ReadPemCertificates(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<X509Certificate2>();
        var rest = text.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            var label = rest[fields.Label];
            if (label.SequenceEqual("CERTIFICATE") || label.SequenceEqual("X509 CERTIFICATE") || label.SequenceEqual("TRUSTED CERTIFICATE"))
            {
                var der = Convert.FromBase64String(rest[fields.Base64Data].ToString());
                result.Add(X509CertificateLoader.LoadCertificate(der));
            }

            rest = rest[fields.Location.End..];
        }

        return result;
    }

    /// <summary>The first certificate of a PEM or DER file, or null.</summary>
    public static X509Certificate2? ReadSingleCertificate(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (LooksLikePem(bytes))
        {
            var all = ReadPemCertificates(Encoding.ASCII.GetString(bytes));
            foreach (var extra in all.Skip(1))
            {
                extra.Dispose();
            }

            return all.FirstOrDefault();
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The bytes are PEM text ("-----BEGIN ").</summary>
    public static bool LooksLikePem(ReadOnlySpan<byte> bytes) => bytes.IndexOf("-----BEGIN "u8) >= 0;

    /// <summary>BasicConstraints CA=true and, when KeyUsage is present, keyCertSign.</summary>
    public static bool IsCa(X509Certificate2 certificate, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basic is not { CertificateAuthority: true })
        {
            problem = "This certificate is not a CA certificate (it cannot issue certificates).";
            return false;
        }

        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
        {
            problem = "This CA certificate is not allowed to sign certificates.";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Subject and issuer DN are equal.</summary>
    public static bool IsSelfIssued(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }

    /// <summary>
    /// Orders <paramref name="others"/> from the issuer of <paramref name="ca"/> upwards (by subject = issuer name); certificates
    /// not on that path are left out.
    /// </summary>
    public static List<X509Certificate2> ChainAbove(X509Certificate2 ca, IEnumerable<X509Certificate2> others)
    {
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(others);
        var pool = others.Where(c => !string.Equals(c.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase)).ToList();
        var chain = new List<X509Certificate2>();
        var current = ca;
        while (!IsSelfIssued(current))
        {
            var issuer = pool.FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData));
            if (issuer is null)
            {
                break;
            }

            chain.Add(issuer);
            pool.Remove(issuer);
            current = issuer;
        }

        return chain;
    }

    public static CertificateSummary Summarize(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new CertificateSummary
        {
            Subject = certificate.Subject,
            CommonName = CommonName(certificate),
            Issuer = certificate.Issuer,
            NotBeforeUtc = certificate.NotBefore.ToUniversalTime(),
            NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Fingerprint = ColonFingerprint(Fingerprint(certificate)),
        };
    }

    /// <summary>A file name from a common name: characters invalid on any OS become "_".</summary>
    public static string FileNameFor(string commonName, string extension)
    {
        ArgumentNullException.ThrowIfNull(commonName);
        const string invalid = "\\/:*?\"<>|";
        var name = new string([.. commonName.Trim().Select(c => c < 0x20 || invalid.Contains(c, StringComparison.Ordinal) ? '_' : c)]).Trim('.', ' ');
        return (name.Length == 0 ? "ca" : name) + extension;
    }
}
