using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Est.Serving;

/// <summary>Describes a certificate for the page's certificate details, in plain names (no OIDs where a name exists).</summary>
public static class CertificateDescriber
{
    private static readonly Dictionary<string, string> NameParts = new(StringComparer.Ordinal)
    {
        ["2.5.4.3"] = "Common name",
        ["2.5.4.10"] = "Organization",
        ["2.5.4.11"] = "Organization unit",
        ["2.5.4.7"] = "Locality",
        ["2.5.4.8"] = "State",
        ["2.5.4.6"] = "Country",
        ["2.5.4.9"] = "Street",
        ["2.5.4.17"] = "Postal code",
        ["2.5.4.5"] = "Serial number",
        ["1.2.840.113549.1.9.1"] = "Email",
    };

    private static readonly Dictionary<string, string> Purposes = new(StringComparer.Ordinal)
    {
        ["1.3.6.1.5.5.7.3.1"] = "Server authentication",
        ["1.3.6.1.5.5.7.3.2"] = "Client authentication",
        ["1.3.6.1.5.5.7.3.3"] = "Code signing",
        ["1.3.6.1.5.5.7.3.4"] = "Email protection",
        ["1.3.6.1.5.5.7.3.8"] = "Time stamping",
        ["1.3.6.1.5.5.7.3.9"] = "OCSP signing",
    };

    private static readonly (X509KeyUsageFlags Flag, string Name)[] KeyUses =
    [
        (X509KeyUsageFlags.DigitalSignature, "Digital signature"),
        (X509KeyUsageFlags.NonRepudiation, "Non-repudiation"),
        (X509KeyUsageFlags.KeyEncipherment, "Key encipherment"),
        (X509KeyUsageFlags.DataEncipherment, "Data encipherment"),
        (X509KeyUsageFlags.KeyAgreement, "Key agreement"),
        (X509KeyUsageFlags.KeyCertSign, "Certificate signing"),
        (X509KeyUsageFlags.CrlSign, "Revocation list signing"),
    ];

    public static CertificateDescription Describe(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        var uses = new List<string>();
        var names = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            switch (extension)
            {
                case X509KeyUsageExtension usage:
                    uses.AddRange(KeyUses.Where(k => usage.KeyUsages.HasFlag(k.Flag)).Select(k => k.Name));
                    break;
                case X509EnhancedKeyUsageExtension purposes:
                    foreach (var oid in purposes.EnhancedKeyUsages)
                    {
                        uses.Add(oid.Value is { } value && Purposes.TryGetValue(value, out var name) ? name : oid.FriendlyName ?? oid.Value ?? string.Empty);
                    }

                    break;
                case X509SubjectAlternativeNameExtension alternative:
                    names.AddRange(alternative.EnumerateIPAddresses().Select(a => "IP " + a));
                    names.AddRange(alternative.EnumerateDnsNames().Select(d => "DNS " + d));
                    break;
            }
        }

        return new CertificateDescription
        {
            SerialNumber = Pairs(certificate.SerialNumber),
            Subject = Parts(certificate.SubjectName),
            Issuer = Parts(certificate.IssuerName),
            NotBeforeUtc = certificate.NotBefore.ToUniversalTime(),
            NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Key = KeyOf(certificate),
            Signature = SignatureOf(certificate.SignatureAlgorithm),
            Version = certificate.Version,
            Uses = uses,
            Names = names,
            Fingerprint = Pairs(Convert.ToHexString(SHA256.HashData(der))),
        };
    }

    /// <summary>The parts of a name in its order: known parts by name, others by their OID's name.</summary>
    private static List<NameValue> Parts(X500DistinguishedName name)
    {
        var parts = new List<NameValue>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
            {
                continue;
            }

            var type = rdn.GetSingleElementType();
            var label = type.Value is { } oid && NameParts.TryGetValue(oid, out var known) ? known : type.FriendlyName ?? type.Value ?? string.Empty;
            parts.Add(new NameValue(label, rdn.GetSingleElementValue() ?? string.Empty));
        }

        return parts;
    }

    private static string KeyOf(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"RSA {rsa.KeySize}");
        }

        using var ecc = certificate.GetECDsaPublicKey();
        if (ecc is null)
        {
            return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? string.Empty;
        }

        return ecc.KeySize switch
        {
            256 => "ECC P-256",
            384 => "ECC P-384",
            521 => "ECC P-521",
            _ => string.Create(CultureInfo.InvariantCulture, $"ECC {ecc.KeySize}"),
        };
    }

    private static string SignatureOf(Oid algorithm) => algorithm.Value switch
    {
        "1.2.840.10045.4.3.2" => "SHA-256 with ECDSA",
        "1.2.840.10045.4.3.3" => "SHA-384 with ECDSA",
        "1.2.840.10045.4.3.4" => "SHA-512 with ECDSA",
        "1.2.840.113549.1.1.11" => "SHA-256 with RSA",
        "1.2.840.113549.1.1.12" => "SHA-384 with RSA",
        "1.2.840.113549.1.1.13" => "SHA-512 with RSA",
        "1.2.840.113549.1.1.10" => "RSA-PSS",
        _ => algorithm.FriendlyName ?? algorithm.Value ?? string.Empty,
    };

    /// <summary>"7DFD05" -> "7D FD 05".</summary>
    private static string Pairs(string hex) =>
        string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
