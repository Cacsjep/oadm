using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>Subject and alternative names of a device certificate.</summary>
/// <param name="CommonName">The address OADM uses (host part).</param>
/// <param name="IpAddresses">IP addresses of the device (always in the SAN).</param>
/// <param name="DnsNames">Host name, FQDN, <c>axis-&lt;serial&gt;.local</c>.</param>
public sealed record CertificateNames(string CommonName, IReadOnlyList<string> IpAddresses, IReadOnlyList<string> DnsNames)
{
    /// <summary>RFC 4514 subject for the device API ("CN=10.0.0.48").</summary>
    public string Subject => "CN=" + EscapeRdn(CommonName);

    /// <summary>The device API's SAN list ("IP:10.0.0.48", "DNS:axis-...").</summary>
    public IReadOnlyList<string> DeviceSubjectAltNames => [.. IpAddresses.Select(a => "IP:" + a), .. DnsNames.Select(d => "DNS:" + d)];

    /// <summary>
    /// Names for a device: CN = the OADM address (host part), SAN = its IP addresses (OADM address when it is one + the
    /// device's own), host name, FQDN and <c>axis-&lt;serial&gt;.local</c>, without duplicates.
    /// </summary>
    /// <param name="trustNames">
    /// A user with all devices added the device (<c>IDeviceInfo.AddedByAllDevices</c>). False: no host name, FQDN or
    /// address name goes into the certificate (a user limited to a group could control the DNS at a site), only the IP
    /// addresses and <c>axis-&lt;serial&gt;.local</c>; the CN is then the OADM address when it is an IP address, else the
    /// first IP address or the axis name.
    /// </param>
    public static CertificateNames For(string oadmAddress, string serial, string? hostName, string? fqdn, IEnumerable<string> deviceAddresses, bool trustNames)
    {
        ArgumentNullException.ThrowIfNull(oadmAddress);
        ArgumentNullException.ThrowIfNull(serial);
        var host = HostPart(oadmAddress);
        var axisName = "axis-" + serial.ToLowerInvariant() + ".local";
        var hostIsIp = IPAddress.TryParse(host, out _);
        var ips = new List<string>();
        var dns = new List<string>();
        void AddName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var trimmed = name.Trim().Trim('[', ']');
            if (IPAddress.TryParse(trimmed, out var ip))
            {
                if (ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !ips.Contains(ip.ToString()))
                {
                    ips.Add(ip.ToString());
                }
            }
            else if (!dns.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                dns.Add(trimmed);
            }
        }

        AddName(trustNames || hostIsIp ? host : null);
        // The addresses the device reports itself: IP addresses only, never a name (a camera could name another camera).
        foreach (var address in deviceAddresses ?? [])
        {
            if (address is not null && IPAddress.TryParse(address.Trim().Trim('[', ']'), out _))
            {
                AddName(address);
            }
        }

        // The device reports its host name and FQDN itself: only plain names go into a certificate every OADM device trusts,
        // never another camera's axis name (EST maps an axis-<serial>.local name to a camera).
        if (trustNames)
        {
            AddName(IsPlainHostName(hostName) && !NamesOtherCamera(hostName!, serial) ? hostName : null);
            AddName(IsPlainHostName(fqdn) && !NamesOtherCamera(fqdn!, serial) ? fqdn : null);
        }

        AddName(axisName);
        var commonName = trustNames || hostIsIp ? host : ips.Count > 0 ? ips[0] : axisName;
        return new CertificateNames(commonName, ips, dns);
    }

    /// <summary>An <c>axis-&lt;12 hex&gt;</c> name (any domain) of a serial other than <paramref name="serial"/>.</summary>
    public static bool NamesOtherCamera(string name, string serial)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length < 17 || !trimmed.StartsWith("axis-", StringComparison.OrdinalIgnoreCase) || (trimmed.Length > 17 && trimmed[17] != '.'))
        {
            return false;
        }

        var candidate = trimmed[5..17];
        return candidate.All(char.IsAsciiHexDigit) && !string.Equals(candidate, serial, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Letters, digits and hyphens in labels of 1 to 63 characters, dot separated, at most 253: no "*", no spaces.</summary>
    public static bool IsPlainHostName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim().TrimEnd('.');
        return trimmed.Length <= 253
            && trimmed.Split('.').All(label => label.Length is >= 1 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    /// <summary>"10.0.0.48" from "10.0.0.48", "cam.example.com:8443", "[fd00::1]:443", "https://cam".</summary>
    public static string HostPart(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var trimmed = address.Trim();
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var ip))
        {
            return ip.ToString();
        }

        var text = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "http://" + trimmed;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : trimmed;
    }

    private static string EscapeRdn(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal).Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal);
}

/// <summary>A device's certificate request cannot be used (message = user text).</summary>
public sealed class CertificateRequestRejectedException : Exception
{
    public CertificateRequestRejectedException()
    {
    }

    public CertificateRequestRejectedException(string message)
        : base(message)
    {
    }

    public CertificateRequestRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Signs a device certificate request with the CA (spec "Certificate content"): OADM's own subject and SAN, never the
/// CSR's; the CSR's public key must be RSA >= 2048 or EC; EKU serverAuth (HTTPS) or clientAuth (802.1X); KeyUsage
/// digitalSignature + keyEncipherment; AKI = CA SKI; serial 16 random bytes; validity from now - 5 min, capped at the
/// CA's end. Only System.Security.Cryptography.
/// </summary>
public static class DeviceCertificateIssuer
{
    /// <summary>EKU serverAuth.</summary>
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>EKU clientAuth.</summary>
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Signs; returns the public certificate. The caller disposes it.</summary>
    /// <exception cref="CertificateRequestRejectedException">The request is unreadable, badly signed or has a weak key.</exception>
    public static X509Certificate2 Sign(CaMaterial ca, string csrPem, CertificateNames names, string purpose, DateTimeOffset now, TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(names);
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateRequestRejectedException("The device's certificate request cannot be read or its signature is wrong: " + ex.Message);
        }

        CheckKey(csr.PublicKey);
        var request = new CertificateRequest(new X500DistinguishedName(names.Subject), csr.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(string.Equals(purpose, CertificatePurpose.Dot1x, StringComparison.Ordinal) ? ClientAuthOid : ServerAuthOid)], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ip in names.IpAddresses)
        {
            san.AddIpAddress(IPAddress.Parse(ip));
        }

        foreach (var dns in names.DnsNames)
        {
            san.AddDnsName(dns);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notBefore = now.AddMinutes(-5);
        var notAfter = now + validity;
        var caEnd = new DateTimeOffset(ca.Certificate.NotAfter.ToUniversalTime());
        if (notAfter > caEnd)
        {
            notAfter = caEnd;
        }

        var caStart = new DateTimeOffset(ca.Certificate.NotBefore.ToUniversalTime());
        if (notBefore < caStart)
        {
            notBefore = caStart;
        }

        if (notAfter <= notBefore)
        {
            throw new CertificateRequestRejectedException("The CA is no longer valid. Generate or import a new CA first.");
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01); // positive, 16 bytes
        // The signature generator of the CA key: the device key may be of another algorithm (EC key, RSA CA).
        using var issuer = ca.WithPrivateKey();
        using var rsa = issuer.GetRSAPrivateKey();
        using var ecdsa = rsa is null ? issuer.GetECDsaPrivateKey() : null;
        var generator = rsa is not null
            ? X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1)
            : X509SignatureGenerator.CreateForECDsa(ecdsa ?? throw new CertificateRequestRejectedException("The CA key type is not supported."));
        using var signed = request.Create(issuer.SubjectName, generator, notBefore, notAfter, serial);
        return X509CertificateLoader.LoadCertificate(signed.RawData);
    }

    /// <summary>RSA >= 2048 or EC P-256 / P-384 / P-521.</summary>
    public static void CheckKey(PublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using (var rsa = key.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                if (rsa.KeySize < 2048)
                {
                    throw new CertificateRequestRejectedException(string.Create(CultureInfo.InvariantCulture, $"The device created a weak key (RSA {rsa.KeySize}); at least RSA 2048 is needed."));
                }

                return;
            }
        }

        using var ec = key.GetECDsaPublicKey();
        if (ec is null)
        {
            throw new CertificateRequestRejectedException("The device's key type is not supported (RSA or EC is needed).");
        }

        string? curve;
        try
        {
            curve = ec.ExportParameters(false).Curve.Oid?.Value;
        }
        catch (CryptographicException)
        {
            curve = null;
        }

        // P-256, P-384, P-521 (by OID): smaller or unknown curves are weak or not supported everywhere.
        if (curve is not ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35"))
        {
            throw new CertificateRequestRejectedException("The device created an EC key on another curve; P-256 or larger is needed.");
        }
    }

    /// <summary>SHA-256 fingerprint (upper hex), serial (upper hex) and end of validity of a certificate.</summary>
    public static (string Fingerprint, string Serial, DateTime NotAfterUtc) Describe(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return (CaCertificates.Fingerprint(certificate), certificate.SerialNumber.ToUpperInvariant(), certificate.NotAfter.ToUniversalTime());
    }
}
