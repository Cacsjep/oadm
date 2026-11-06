using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki;

/// <summary>What a PKCS#12 file of "Install certificates" holds.</summary>
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

    /// <summary>From when the certificate with the key is valid.</summary>
    public DateTime? LeafNotBeforeUtc { get; init; }

    /// <summary>
    /// The certificate with the key may log in as a client (IEEE 802.1X): it has no extended key usage, or one with client
    /// authentication (or any use).
    /// </summary>
    public bool LeafAllowsClientAuth { get; init; } = true;
}

/// <summary>Reads PKCS#12 files (.pfx / .p12) and matches them to devices (shared by the dialog and the task).</summary>
public static class CertificateFiles
{
    /// <summary>Largest certificate file.</summary>
    public const int MaxBytes = 1024 * 1024;

    public const string WrongPassword = "The password is wrong or the file is damaged.";

    /// <summary>
    /// A certificate may start this long after the clock it is compared with: the allowance OADM's own certificates get by
    /// starting 5 minutes early (docs/spec/pki.md "Clock check before installing a certificate").
    /// </summary>
    public static readonly TimeSpan EarlyStart = CameraClock.CertificateStartBack;

    /// <summary>A certificate whose extended key usage leaves out client authentication cannot log in to IEEE 802.1X.</summary>
    public const string NotForClientAuth = "The certificate is not for client authentication, which IEEE 802.1X needs.";

    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string AnyUseOid = "2.5.29.37.0";

    /// <summary>
    /// Why a certificate that starts at <paramref name="notBeforeUtc"/> cannot be used at <paramref name="nowUtc"/> (the
    /// device's clock where it is known, else the server's): it is not valid yet, beyond <see cref="EarlyStart"/>. Else null.
    /// </summary>
    public static string? NotValidYetProblem(DateTime notBeforeUtc, DateTime nowUtc) =>
        notBeforeUtc > nowUtc + EarlyStart ? $"The certificate is not valid before {notBeforeUtc:yyyy-MM-dd HH:mm} UTC." : null;

    /// <summary>IEEE 802.1X: the refusal for a certificate that is not for client authentication (<see cref="NotForClientAuth"/>), or null.</summary>
    public static string? Dot1xProblem(Pkcs12Contents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return contents.HasLeaf && !contents.LeafAllowsClientAuth ? NotForClientAuth : null;
    }

    private static bool AllowsClientAuth(X509Certificate2 certificate)
    {
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList();
        return usages.Count == 0
            || usages.Exists(u => u.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is ClientAuthOid or AnyUseOid));
    }

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
            // Keys never touch the OS key store; macOS does not support ephemeral keys (PlatformNotSupportedException).
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            collection = X509CertificateLoader.LoadPkcs12Collection(bytes, password ?? string.Empty, flags);
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
            var notBefore = leaf.NotBefore.ToUniversalTime();
            return new Pkcs12Contents(
                CaCertificates.Fingerprint(leaf),
                leaf.Subject,
                notAfter,
                NamesOf(leaf),
                pems,
                notAfter < nowUtc ? $"The certificate expired on {notAfter:yyyy-MM-dd}." : NotValidYetProblem(notBefore, nowUtc))
            {
                LeafNotBeforeUtc = notBefore,
                LeafAllowsClientAuth = AllowsClientAuth(leaf),
            };
        }
        finally
        {
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// "CA certificates only": every certificate of the file goes into the device's CA store, so each must be a CA
    /// certificate. The refusal for the first one that is not ("cam48 is not a CA certificate. ..."), or null.
    /// </summary>
    public static string? NotCaProblem(Pkcs12Contents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        foreach (var pem in contents.CertificatePems)
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                if (!CaCertificates.IsCa(certificate, out _))
                {
                    return $"{CaCertificates.CommonName(certificate)} is not a CA certificate. Choose a file with CA certificates only.";
                }
            }
            catch (CryptographicException)
            {
                return "The file contains a certificate that cannot be read.";
            }
        }

        return null;
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
