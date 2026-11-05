using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Actions;

namespace Oadm.Sdk.Certificates;

/// <summary>
/// The certificates recipients can use (SDK 0.12.0), offered by PKI through <see cref="Plugins.IPluginServices"/>. By convention
/// only the host reads it: a plugin gets its certificates in <see cref="RecipientSessionRequest"/>. Nothing enforces that: any core
/// plugin in the process can find this service, private keys included.
/// </summary>
public interface IRecipientCertificates
{
    /// <summary>Certificates with a key OADM holds that have not ended; the value is the id a field stores.</summary>
    Task<IReadOnlyList<ActionChoice>> ClientCertificatesAsync(CancellationToken ct);

    /// <summary>The CAs and the imported certificates a server can be checked against.</summary>
    Task<IReadOnlyList<ActionChoice>> TrustedCertificatesAsync(CancellationToken ct);

    /// <summary>
    /// The certificate, then its issuers; null when it is gone. <paramref name="withKey"/> true: with its private key when OADM holds
    /// one (a client certificate). False: never with a key (a trusted certificate). The caller disposes them.
    /// </summary>
    Task<IReadOnlyList<X509Certificate2>?> LoadAsync(string id, bool withKey, CancellationToken ct);
}

/// <summary>Which recipients use which certificates, offered by the host to PKI: a used certificate cannot be deleted.</summary>
public interface IRecipientCertificateUse
{
    /// <summary>The sorted recipient names per certificate id.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> UsesAsync(CancellationToken ct);
}
