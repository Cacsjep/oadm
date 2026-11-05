using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The certificates recipients use (docs/spec/recipient-sessions.md "Security"): the choices of the recipient dialog, the
/// certificate of a session and what keeps a certificate from Delete. The value of a choice is the row's id in the
/// Certificates list. Both lists are at most a few hundred rows read from memory.
/// </summary>
public sealed partial class PkiService
{
    /// <summary>A chain longer than any real one: CAs that signed each other end here.</summary>
    private const int MaxRecipientChain = 8;

    /// <summary>The host's list of what recipients use; null or a null result: nothing uses a certificate.</summary>
    public Func<IRecipientCertificateUse?>? RecipientCertificateUse { get; set; }

    /// <summary>The recipients per certificate id, read once per request.</summary>
    internal async Task<RecipientUses> RecipientUsesAsync(CancellationToken ct) =>
        RecipientCertificateUse?.Invoke() is { } host ? new RecipientUses(await host.UsesAsync(ct).ConfigureAwait(false)) : RecipientUses.None;

    /// <summary>
    /// Rows with a key OADM holds that have not ended and are still accepted. No CA, and no certificate of a running server:
    /// the servers keep those keys to themselves.
    /// </summary>
    internal IReadOnlyList<ActionChoice> RecipientClientCertificates()
    {
        var now = Now.UtcDateTime;
        return [.. _local.List.Where(l => l.HasKey && l.NotAfterUtc > now && !_issued.IsRevoked(l.Serial))
            .Select(l => new ActionChoice(l.RowId, ChoiceText(l.Stored.Name, l.IssuerName, l.NotAfterUtc)))];
    }

    /// <summary>The CAs of the list, then the imported certificates (the self-signed servers common with OPC UA).</summary>
    internal IReadOnlyList<ActionChoice> RecipientTrustedCertificates() =>
    [
        .. Issuers().AuthorityRows.Select(ca => new ActionChoice(ca.Row.Id!, ChoiceText(ca.Row.Name!, ca.Row.IssuedBy, ca.Row.NotAfterUtc))),
        .. _local.List.Where(l => l.Stored.Kind == ListedCertificateKind.Imported)
            .Select(l => new ActionChoice(l.RowId, ChoiceText(l.Stored.Name, l.IssuerName, l.NotAfterUtc))),
    ];

    /// <summary>
    /// The certificate of a row, with its key when OADM holds one, then its issuers; null when it left the list, is no longer
    /// accepted or its key cannot be read. The caller disposes them; the decrypted key stays only inside the certificate.
    /// </summary>
    internal IReadOnlyList<X509Certificate2>? LoadRecipientCertificate(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.StartsWith(AuthorityRowPrefix, StringComparison.Ordinal))
        {
            return Issuers().AuthorityRows.FirstOrDefault(r => r.Row.Id == id) is { } ca ? WithIssuers(X509CertificateLoader.LoadCertificate(ca.Der)) : null;
        }

        if (!id.StartsWith(LocalCertificates.RowPrefix, StringComparison.Ordinal)
            || _local.Find(id[LocalCertificates.RowPrefix.Length..]) is not { } local || _issued.IsRevoked(local.Serial))
        {
            return null;
        }

        if (!local.HasKey)
        {
            return WithIssuers(X509CertificateLoader.LoadCertificate(local.Der));
        }

        if (_local.KeyOf(local) is not { } pkcs8)
        {
            return null;
        }

        try
        {
            return WithIssuers(LocalCertificates.WithKey(local.Der, pkcs8));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>The certificate first, then the issuers PKI lists up to the root: an issuer it does not list ends the chain.</summary>
    private List<X509Certificate2> WithIssuers(X509Certificate2 certificate)
    {
        var all = Authorities().SelectMany(a => a.Certificates).ToList();
        var chain = new List<X509Certificate2> { certificate };
        while (chain.Count < MaxRecipientChain && !CaCertificates.IsSelfIssued(chain[^1]))
        {
            if (IssuerIndex.FingerprintOfIssuer(chain[^1], all) is not { } fingerprint
                || all.Find(der => string.Equals(Convert.ToHexString(SHA256.HashData(der)), fingerprint, StringComparison.OrdinalIgnoreCase)) is not { } issuer)
            {
                break;
            }

            chain.Add(X509CertificateLoader.LoadCertificate(issuer));
        }

        return chain;
    }

    /// <summary>What a row of the Certificates list is used by: the policy that shows it, then the recipients.</summary>
    private List<string> LocalUsedBy(LocalCertificate local, RecipientUses? recipients)
    {
        var usedBy = new List<string>(2);
        if (Policies.ShowingCertificate(local.Stored.Id) is { } showing)
        {
            usedBy.Add(PolicyKinds.Name(showing.Kind));
        }

        if (recipients?.Text(local.RowId) is { } recipient)
        {
            usedBy.Add(recipient);
        }

        return usedBy;
    }

    /// <summary>The ids of a CA's rows in the Certificates list: its own certificate, every certificate of the Axis device ID CAs.</summary>
    private static IEnumerable<string> RowIdsOf(CertificateAuthorityInfo authority) =>
        (authority.Kind == CertificateAuthorityKind.AxisDeviceId ? authority.Certificates : authority.Certificates.Take(1))
        .Select(der => AuthorityRowPrefix + Convert.ToHexString(SHA256.HashData(der)));

    /// <summary>The Used by entries of a CA's recipients, for the Certificate authorities card.</summary>
    private static IEnumerable<string> RecipientsOf(CertificateAuthorityInfo authority, RecipientUses? recipients) =>
        recipients is null ? [] : RowIdsOf(authority).Select(recipients.Text).OfType<string>();

    /// <summary>"EST server (OADM CA), until 2035-10-25"; a self-signed one names itself once: "OADM Root CA, until 2045-10-25".</summary>
    private static string ChoiceText(string name, string? issuer, DateTime notAfterUtc) =>
        string.IsNullOrEmpty(issuer) || string.Equals(name, issuer, StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"{name}, until {notAfterUtc:yyyy-MM-dd}")
            : PolicyCertificateChoice.Text(name, issuer, notAfterUtc);
}

/// <summary>The recipient names per certificate id of one request.</summary>
internal sealed class RecipientUses(IReadOnlyDictionary<string, IReadOnlyList<string>> uses)
{
    public static readonly RecipientUses None = new(new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>The first recipient that uses the certificate; null when none does.</summary>
    public string? First(string id) => uses.TryGetValue(id, out var names) && names.Count > 0 ? names[0] : null;

    /// <summary>"Recipient: Plant broker, Line 2"; null when none uses the certificate.</summary>
    public string? Text(string id) => uses.TryGetValue(id, out var names) && names.Count > 0 ? CertificateRules.RecipientsUsing(names) : null;
}

/// <summary>PKI's <see cref="IRecipientCertificates"/>, offered through the plugin services while PKI runs.</summary>
internal sealed class PkiRecipientCertificates(PkiService service) : IRecipientCertificates
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ActionChoice>> ClientCertificatesAsync(CancellationToken ct) => Task.FromResult(service.RecipientClientCertificates());

    /// <inheritdoc />
    public Task<IReadOnlyList<ActionChoice>> TrustedCertificatesAsync(CancellationToken ct) => Task.FromResult(service.RecipientTrustedCertificates());

    /// <inheritdoc />
    public Task<IReadOnlyList<X509Certificate2>?> LoadAsync(string id, CancellationToken ct) => Task.FromResult(service.LoadRecipientCertificate(id));
}
