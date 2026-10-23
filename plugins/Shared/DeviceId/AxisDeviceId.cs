using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.DeviceIds;

/// <summary>
/// The factory Axis device ID certificate of a camera (IEEE 802.1AR, AXIS Edge Vault): checked against the Axis device ID
/// root CAs shipped with the plugin (RSA and ECC, as Axis publishes them with their intermediates), and the camera's serial
/// number read from its subject (X.520 serialNumber). Linked as source into the EST server and the 802.1X server, each with
/// the PEM files of this folder as embedded resources (<c>Oadm.Plugins.Shared.DeviceId.*.pem</c>).
/// </summary>
public sealed class AxisDeviceId
{
    private const string SerialNumberOid = "2.5.4.5";

    private readonly X509Certificate2Collection _roots = [];
    private readonly X509Certificate2Collection _intermediates = [];

    /// <summary>The Axis device ID CAs embedded in the plugin.</summary>
    public AxisDeviceId()
        : this(LoadEmbedded())
    {
    }

    /// <summary>Other CAs (tests): self-signed ones are roots, the rest intermediates.</summary>
    public AxisDeviceId(IEnumerable<X509Certificate2> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        foreach (var certificate in authorities)
        {
            (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ? _roots : _intermediates).Add(certificate);
        }
    }

    /// <summary>The roots in use.</summary>
    public X509Certificate2Collection Roots => _roots;

    /// <summary>
    /// The camera's serial number (upper-case hex) when <paramref name="certificate"/> is a valid Axis device ID certificate
    /// at <paramref name="now"/>; null otherwise. <paramref name="presented"/>: the intermediates the camera sent.
    /// </summary>
    public string? Verify(X509Certificate2 certificate, IEnumerable<X509Certificate2>? presented, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.ExtraStore.AddRange(_intermediates);
        foreach (var extra in presented ?? [])
        {
            if (!extra.RawData.AsSpan().SequenceEqual(certificate.RawData))
            {
                chain.ChainPolicy.ExtraStore.Add(extra);
            }
        }

        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        try
        {
            if (!chain.Build(certificate))
            {
                return null;
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                if (!ReferenceEquals(element.Certificate, certificate))
                {
                    element.Certificate.Dispose();
                }
            }
        }

        return SerialOf(certificate);
    }

    /// <summary>The serial number of the subject (X.520 serialNumber, the camera's 12 hex digits), upper case; null without one.</summary>
    public static string? SerialOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements || rdn.GetSingleElementType().Value != SerialNumberOid)
            {
                continue;
            }

            var value = rdn.GetSingleElementValue();
            if (Normalize(value) is { } serial)
            {
                return serial;
            }
        }

        return null;
    }

    /// <summary>The first run of 12 hex digits ("B8A44F631339"), upper case; null without one.</summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var run = 0;
        for (var i = 0; i < value.Length; i++)
        {
            run = Uri.IsHexDigit(value[i]) ? run + 1 : 0;
            if (run == 12 && (i + 1 == value.Length || !Uri.IsHexDigit(value[i + 1])))
            {
                return value.Substring(i - 11, 12).ToUpperInvariant();
            }
        }

        return null;
    }

    private static List<X509Certificate2> LoadEmbedded()
    {
        var certificates = new List<X509Certificate2>();
        var assembly = typeof(AxisDeviceId).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Oadm.Plugins.Shared.DeviceId.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var pem = reader.ReadToEnd();
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(pem);
            certificates.AddRange(collection);
        }

        return certificates;
    }
}
