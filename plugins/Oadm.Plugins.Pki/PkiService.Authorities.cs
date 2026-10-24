using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The list of CAs (docs/spec/pki.md "Certificate authorities"): the OADM CA, the previous ones, the Axis device ID and
/// imported CAs; which services use which (<see cref="SetUsedBy"/>, in memory); Import CA and Remove of imported CAs.
/// </summary>
public sealed partial class PkiService
{
    private const string PreviousPrefix = "oadm-previous:";
    private const string ImportedPrefix = "imported:";
    private const string AxisDeviceIdName = "Axis device ID";
    private const string ResourcePrefix = "Oadm.Plugins.Shared.DeviceId.";

    private static readonly Lazy<List<byte[]>> AxisCertificates = new(LoadAxisCertificates);

    private readonly Lock _usedByGate = new();
    private readonly Dictionary<string, (string Name, HashSet<string> Ids)> _usedBy = new(StringComparer.Ordinal);
    private volatile List<StoredImportedCa> _imported = [];
    private AuthorityCache? _authorities;

    /// <summary>The authority id of a previous OADM CA.</summary>
    public static string PreviousAuthorityId(string sha256) => PreviousPrefix + sha256;

    /// <summary>The authority id of an imported CA.</summary>
    public static string ImportedAuthorityId(string sha256) => ImportedPrefix + sha256;

    /// <summary>
    /// Every CA PKI holds, in this order: the OADM CA (when one exists), the previous OADM CAs (newest first, not ended),
    /// the Axis device ID, the imported CAs (oldest first). Built once per change of the lists (a reference check), so a
    /// service may ask for every authentication. The arrays are shared: read only.
    /// </summary>
    public IReadOnlyList<CertificateAuthorityInfo> Authorities()
    {
        var ca = _ca;
        var previous = _previous;
        var imported = _imported;
        var now = Now.UtcDateTime;
        if (Volatile.Read(ref _authorities) is { } cached && ReferenceEquals(cached.Ca, ca) && ReferenceEquals(cached.Previous, previous)
            && ReferenceEquals(cached.Imported, imported) && cached.NextEndUtc > now)
        {
            return cached.List;
        }

        var list = new List<CertificateAuthorityInfo>();
        if (ca is not null)
        {
            list.Add(new CertificateAuthorityInfo(CertificateAuthorityInfo.OadmId, CaCertificates.CommonName(ca.Certificate), CertificateAuthorityKind.Oadm,
                [ca.Certificate.RawData, .. ca.Chain.Select(c => c.RawData)], ca.Certificate.NotAfter.ToUniversalTime(), ca.HasKey));
        }

        // A previous CA that ended leaves the list at once (the services' selections of it pass nothing any more).
        var nextEnd = DateTime.MaxValue;
        foreach (var chain in PreviousChains())
        {
            using var certificate = X509CertificateLoader.LoadCertificate(chain[0]);
            var end = certificate.NotAfter.ToUniversalTime();
            if (end > now)
            {
                nextEnd = end < nextEnd ? end : nextEnd;
                list.Add(new CertificateAuthorityInfo(PreviousAuthorityId(CaCertificates.Fingerprint(certificate)), CaCertificates.CommonName(certificate),
                    CertificateAuthorityKind.PreviousOadm, chain, end, false));
            }
        }

        if (AxisCertificates.Value is { Count: > 0 } axis)
        {
            list.Add(new CertificateAuthorityInfo(CertificateAuthorityInfo.AxisDeviceIdId, AxisDeviceIdName, CertificateAuthorityKind.AxisDeviceId, axis, LatestEnd(axis), false));
        }

        foreach (var entry in imported)
        {
            try
            {
                var certificates = CaCertificates.ReadPemCertificates(entry.CertificatePem);
                if (certificates.Count == 0)
                {
                    continue;
                }

                using var certificate = certificates[0];
                list.Add(new CertificateAuthorityInfo(ImportedAuthorityId(entry.Id), CaCertificates.CommonName(certificate), CertificateAuthorityKind.Imported,
                    [certificate.RawData], certificate.NotAfter.ToUniversalTime(), false));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // an unreadable entry is no CA
            }
        }

        Volatile.Write(ref _authorities, new AuthorityCache(ca, previous, imported, nextEnd, list));
        return list;
    }

    /// <summary>
    /// Which CAs <paramref name="serviceId"/> uses now (shown as "Used by" with <paramref name="serviceName"/>); an empty
    /// list clears it. Kept in memory: each service tells it again after a start. The page gets a new state when it changed.
    /// </summary>
    public void SetUsedBy(string serviceId, string serviceName, IReadOnlyCollection<string> authorityIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(authorityIds);
        var ids = new HashSet<string>(authorityIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(serviceName) ? serviceId : serviceName.Trim();
        lock (_usedByGate)
        {
            var known = _usedBy.TryGetValue(serviceId, out var current);
            if (ids.Count == 0)
            {
                if (!known)
                {
                    return;
                }

                _usedBy.Remove(serviceId);
            }
            else
            {
                if (known && current.Name == name && current.Ids.SetEquals(ids))
                {
                    return;
                }

                _usedBy[serviceId] = (name, ids);
            }
        }

        _ = PublishAsync(); // best effort, never throws
    }

    /// <summary>The services that use <paramref name="authorityId"/>, in name order.</summary>
    public IReadOnlyList<string> UsedBy(string authorityId)
    {
        lock (_usedByGate)
        {
            return [.. _usedBy.Values.Where(u => u.Ids.Contains(authorityId)).Select(u => u.Name).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Checks and stores a CA certificate without key (PEM or DER); field errors under <see cref="PkiFields.AuthorityFile"/>.</summary>
    public async Task<PkiReply> ImportAuthorityAsync(ImportAuthorityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryDecode(request.FileBase64, out var bytes) || bytes.Length == 0)
        {
            return Field("The file cannot be read.");
        }

        if (bytes.Length > PkiPluginInfo.MaxImportBytes)
        {
            return Field("The file is larger than 1 MB.");
        }

        if (CaCertificates.LooksLikePem(bytes) && Encoding.ASCII.GetString(bytes).Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return Field("The file contains a private key. Choose the certificate only.");
        }

        X509Certificate2? certificate;
        try
        {
            certificate = CaCertificates.ReadSingleCertificate(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            certificate = null;
        }

        if (certificate is null)
        {
            return Field("The file is not a certificate (.crt or .cer, PEM or DER).");
        }

        string name;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using (certificate)
            {
                if (!CaCertificates.IsCa(certificate, out var problem))
                {
                    return Field(problem ?? "This certificate is not a CA certificate (it cannot issue certificates).");
                }

                if (certificate.NotAfter.ToUniversalTime() <= Now.UtcDateTime)
                {
                    return Field("This certificate has expired.");
                }

                var id = CaCertificates.Fingerprint(certificate);
                if (Authorities().Any(a => a.Certificates.Any(der => Convert.ToHexString(SHA256.HashData(der)) == id)))
                {
                    return Field("This CA is already in the list.");
                }

                if (_imported.Count >= PkiPluginInfo.MaxImportedCas)
                {
                    return Field($"At most {PkiPluginInfo.MaxImportedCas} CAs can be imported. Remove one first.");
                }

                name = CaCertificates.CommonName(certificate);
                var list = new List<StoredImportedCa>(_imported) { new() { Id = id, CertificatePem = CaCertificates.ToPem(certificate), ImportedUtc = Now.UtcDateTime } };
                await _store.SaveImportedAsync(list, ct).ConfigureAwait(false);
                _imported = list;
            }
        }
        finally
        {
            _gate.Release();
        }

        LogAuthorityImported(name);
        CaChanged?.Invoke(this, EventArgs.Empty);
        await PublishAsync().ConfigureAwait(false);
        return new PkiReply { Ok = true, State = await GetStateAsync(ct).ConfigureAwait(false) };

        static PkiReply Field(string message) => new() { Errors = One(PkiFields.AuthorityFile, message) };
    }

    /// <summary>Removes an imported CA; refused while a policy names it ("Used by the policy Contoso. Change or delete the policy first.") or a service uses it.</summary>
    public async Task<PkiReply> RemoveAuthorityAsync(RemoveAuthorityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id ?? string.Empty;
        if (!id.StartsWith(ImportedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await ErrorAsync("Only imported CAs can be removed here.", ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Policies.UsingCa(id, CertificateAuthorityKind.Imported) is { Count: > 0 } naming)
            {
                return await ErrorAsync(PolicyTexts.UsedByPolicy(naming[0].Name), ct).ConfigureAwait(false);
            }

            if (UsedByText(id) is { } used)
            {
                return await ErrorAsync(used, ct).ConfigureAwait(false);
            }

            var sha = id[ImportedPrefix.Length..];
            var list = _imported.Where(i => !string.Equals(i.Id, sha, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == _imported.Count)
            {
                return await ErrorAsync("This CA is no longer in the list.", ct).ConfigureAwait(false);
            }

            await _store.SaveImportedAsync(list, ct).ConfigureAwait(false);
            _imported = list;
        }
        finally
        {
            _gate.Release();
        }

        LogAuthorityRemoved(id);
        CaChanged?.Invoke(this, EventArgs.Empty);
        await PublishAsync().ConfigureAwait(false);
        return new PkiReply { Ok = true, State = await GetStateAsync(ct).ConfigureAwait(false) };
    }

    /// <summary>"Used by the 802.1X server. Remove it there first." (several: "the 802.1X server and the EST server"); null when unused.</summary>
    internal string? UsedByText(string authorityId)
    {
        var names = UsedBy(authorityId);
        return names.Count == 0 ? null : "Used by the " + string.Join(" and the ", names) + ". Remove it there first.";
    }

    private async Task LoadImportedAsync(CancellationToken ct) => _imported = await _store.LoadImportedAsync(ct).ConfigureAwait(false);

    /// <summary>The rows of the page's Certificate authorities card.</summary>
    private List<AuthorityRow> AuthorityRows() =>
    [
        .. Authorities().Select(a => new AuthorityRow
        {
            Id = a.Id,
            Name = a.Name,
            Kind = a.Kind,
            NotAfterUtc = a.NotAfterUtc,
            Fingerprint = a.Certificates.Count > 0 ? CaCertificates.ColonFingerprint(Convert.ToHexString(SHA256.HashData(a.Certificates[0]))) : string.Empty,
            UsedBy = [.. Policies.UsingCa(a.Id, a.Kind).Select(PolicyTexts.UsedBy), .. UsedBy(a.Id)],
            CertificatesPem = [.. a.Certificates.Select(der => new string(PemEncoding.Write("CERTIFICATE", der)) + "\n")],
        }),
    ];

    private static DateTime LatestEnd(List<byte[]> certificates)
    {
        var end = DateTime.MinValue;
        foreach (var der in certificates)
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            var notAfter = certificate.NotAfter.ToUniversalTime();
            if (notAfter > end)
            {
                end = notAfter;
            }
        }

        return end;
    }

    /// <summary>The Axis device ID roots and intermediates embedded in the plugin (<c>plugins/Shared/DeviceId</c>), roots first.</summary>
    private static List<byte[]> LoadAxisCertificates()
    {
        var roots = new List<byte[]>();
        var intermediates = new List<byte[]>();
        var assembly = typeof(PkiService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            foreach (var certificate in CaCertificates.ReadPemCertificates(reader.ReadToEnd()))
            {
                using (certificate)
                {
                    (CaCertificates.IsSelfIssued(certificate) ? roots : intermediates).Add(certificate.RawData);
                }
            }
        }

        return [.. roots, .. intermediates];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: CA certificate {Name} imported into the list of CAs")]
    private partial void LogAuthorityImported(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: imported CA {Id} removed from the list of CAs")]
    private partial void LogAuthorityRemoved(string id);

    private sealed record AuthorityCache(CaMaterial? Ca, IReadOnlyList<StoredPreviousCa> Previous, IReadOnlyList<StoredImportedCa> Imported, DateTime NextEndUtc,
        IReadOnlyList<CertificateAuthorityInfo> List);
}
