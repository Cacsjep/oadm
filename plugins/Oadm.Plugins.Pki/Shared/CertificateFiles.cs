using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki;

/// <summary>What a PKCS#12 file of "Install certificates manually" holds.</summary>
public sealed record Pkcs12Contents(
    string? LeafFingerprint,
    string? LeafSubject,
    DateTime? LeafNotAfterUtc,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> CertificatePems,
    string? Error)
{
    /// <summary>A certificate with its private key (HTTPS / 802.1X need one).</summary>
    public bool HasLeaf => LeafFingerprint is not null;
}

/// <summary>Reads PKCS#12 files (.pfx / .p12) and matches them to devices (shared by the dialog and the task).</summary>
public static class CertificateFiles
{
    /// <summary>Largest certificate file.</summary>
    public const int MaxBytes = 1024 * 1024;

    public const string WrongPassword = "The password is wrong or the file is damaged.";

    /// <summary>Reads the file; <see cref="Pkcs12Contents.Error"/> says why it cannot be used. Nothing is stored.</summary>
    public static Pkcs12Contents Read(byte[] bytes, string? password, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return Failed(bytes.Length == 0 ? "The file is empty." : "The file is larger than 1 MB.");
        }

        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(bytes, password ?? string.Empty, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return Failed(WrongPassword);
        }

        try
        {
            if (collection.Count == 0)
            {
                return Failed("The file contains no certificate.");
            }

            var leaves = collection.Where(c => c.HasPrivateKey).ToList();
            var pems = collection.Select(CaCertificates.ToPem).ToList();
            if (leaves.Count != 1)
            {
                return new Pkcs12Contents(null, null, null, [], pems, leaves.Count == 0 ? null : "The file contains more than one certificate with a private key.");
            }

            var leaf = leaves[0];
            var notAfter = leaf.NotAfter.ToUniversalTime();
            return new Pkcs12Contents(
                CaCertificates.Fingerprint(leaf),
                leaf.Subject,
                notAfter,
                NamesOf(leaf),
                pems,
                notAfter < nowUtc ? $"The certificate expired on {notAfter:yyyy-MM-dd}." : null);
        }
        finally
        {
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>Common name and subject alternative names (DNS and IP) of a certificate.</summary>
    public static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = new List<string>();
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!string.IsNullOrWhiteSpace(cn))
        {
            names.Add(cn.Trim());
        }

        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// True when one of the certificate names is the device's MAC address (serial, any common notation, also inside
    /// "axis-&lt;serial&gt;"), its IP address or its host name / FQDN.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> names, string serial, string address, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(names);
        var mac = Hex(serial);
        var host = CertificateNames.HostPart(address);
        foreach (var name in names)
        {
            var text = name.Trim();
            if (mac.Length == 12 && Hex(text).Contains(mac, StringComparison.Ordinal))
            {
                return true;
            }

            if (IPAddress.TryParse(text, out var ip) && IPAddress.TryParse(host, out var deviceIp) && ip.Equals(deviceIp))
            {
                return true;
            }

            if (!IPAddress.TryParse(text, out _)
                && (string.Equals(text, host, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(hostName) && (string.Equals(text, hostName, StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith(hostName + ".", StringComparison.OrdinalIgnoreCase)))))
            {
                return true;
            }
        }

        return false;
    }

    private static string Hex(string text) => new([.. text.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);

    private static Pkcs12Contents Failed(string error) => new(null, null, null, [], [], error);
}
