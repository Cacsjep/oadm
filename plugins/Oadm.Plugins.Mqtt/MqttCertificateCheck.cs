using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Plugins.Mqtt;

/// <summary>
/// The check of a broker's certificate. MQTTnet's own check is never used: the fix of it (#2254) is not in 5.2.0, and this one behaves
/// the same with both versions.
/// </summary>
public static class MqttCertificateCheck
{
    public const string NoCertificate = "The broker sent no certificate.";

    public const string WrongName = "The broker's certificate does not match the host name.";

    public const string Expired = "The broker's certificate has expired or is not valid yet.";

    public const string NotTrusted = "The broker's certificate is not trusted. Choose a Trusted CA or turn on Allow untrusted certificates.";

    public const string NotFromTrustedCa = "The broker's certificate is not from the chosen Trusted CA.";

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Null when the broker's certificate passes, else why not.</summary>
    /// <param name="certificate">What the broker sent.</param>
    /// <param name="presented">The chain of the TLS layer: it holds the intermediates the broker sent.</param>
    /// <param name="errors">The verdict of the TLS layer: the operating system's roots and the host name.</param>
    /// <param name="allowUntrusted">Allow untrusted certificates.</param>
    /// <param name="trusted">The chosen Trusted CA and its issuers; empty without one.</param>
    public static string? Problem(
        X509Certificate? certificate,
        X509Chain? presented,
        SslPolicyErrors errors,
        bool allowUntrusted,
        IReadOnlyList<X509Certificate2> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        if (allowUntrusted)
        {
            return null;
        }

        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return NoCertificate;
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return WrongName;
        }

        if (trusted.Count > 0)
        {
            return CheckAgainst(certificate, presented, trusted);
        }

        return errors == SslPolicyErrors.None ? null : presented is not null && HasExpired(presented) ? Expired : NotTrusted;
    }

    private static string? CheckAgainst(X509Certificate certificate, X509Chain? presented, IReadOnlyList<X509Certificate2> trusted)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        chain.ChainPolicy.CustomTrustStore.AddRange(trusted.ToArray());
        if (presented is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf) ? null : HasExpired(chain) ? Expired : NotFromTrustedCa;
    }

    private static bool HasExpired(X509Chain chain) =>
        chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.NotTimeValid));
}
