using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Certificates;

/// <summary>A certificate created, signed or imported here: one row of the plugin records <c>localCertificates</c> (by id).</summary>
public sealed record StoredLocalCertificate
{
    public string Id { get; init; } = string.Empty;

    /// <summary><see cref="ListedCertificateKind.Created"/>, <see cref="ListedCertificateKind.SignedRequest"/> or <see cref="ListedCertificateKind.Imported"/>.</summary>
    public ListedCertificateKind Kind { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>One of <see cref="ListedCertificateUses"/>.</summary>
    public string Purpose { get; init; } = ListedCertificateUses.Other;

    /// <summary>The certificate, DER, base64.</summary>
    public string Der { get; init; } = string.Empty;

    /// <summary>The private key (PKCS#8, base64) encrypted with the master key, purpose <c>pki:certificate:&lt;id&gt;</c>; null without a key.</summary>
    public string? KeyProtected { get; init; }

    /// <summary>The SHA-256 of the CA certificate that issued it, when PKI knows that CA.</summary>
    public string? IssuerFingerprint { get; init; }

    /// <summary>The issuing policy of Create and Sign request.</summary>
    public Guid? PolicyId { get; init; }

    public DateTime AddedUtc { get; init; }

    /// <summary>Who created, signed or imported it.</summary>
    public string? AddedBy { get; init; }
}

/// <summary>A listed certificate read once: its stored row and what the list shows of it.</summary>
public sealed record LocalCertificate(StoredLocalCertificate Stored, byte[] Der, string Serial, string Fingerprint, DateTime NotBeforeUtc, DateTime NotAfterUtc, string IssuerName)
{
    /// <summary>The key, "RSA 2048", "ECDSA P-256".</summary>
    public string? KeyText { get; init; }

    public bool HasKey => Stored.KeyProtected is not null;

    public string RowId => LocalCertificates.RowPrefix + Stored.Id;

    /// <summary>Reads the certificate of a stored row; null when it is damaged.</summary>
    public static LocalCertificate? Read(StoredLocalCertificate stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        try
        {
            var der = Convert.FromBase64String(stored.Der);
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return new LocalCertificate(stored, der, CertificateSerials.Normalize(certificate.SerialNumber), CaCertificates.Fingerprint(certificate),
                certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), IssuerCommonName(certificate))
            {
                KeyText = CaCertificates.KeyType(certificate),
            };
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The issuer's common name (else the whole issuer name).</summary>
    public static string IssuerCommonName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return string.IsNullOrWhiteSpace(name) ? certificate.Issuer : name;
    }
}

/// <summary>The certificate of an OADM server, in memory (the servers keep their own keys).</summary>
/// <param name="Server">"802.1X server" or "EST server".</param>
/// <param name="IssuerFingerprint">The SHA-256 of the CA that issued it.</param>
public sealed record ServerCertificate(string Slot, string Server, string Name, byte[] Der, string Serial, string Fingerprint, DateTime NotBeforeUtc, DateTime NotAfterUtc, string IssuerName,
    string? IssuerFingerprint, DateTime SeenUtc)
{
    public string RowId => LocalCertificates.ServerPrefix + Slot;

    /// <summary>The key, "ECDSA P-256".</summary>
    public string? KeyText { get; init; }
}

/// <summary>
/// The certificates created, signed or imported on the PKI page (docs/spec/pki.md "Certificates"), kept in the plugin
/// records <c>localCertificates</c> (so they are part of the server backup), their keys encrypted with the master key like
/// the CA key; and the certificates of the 802.1X and EST servers, in memory only (the servers make them and keep their keys).
/// At most <see cref="CertificateRules.MaxLocalCertificates"/>, read into memory at the start.
/// </summary>
public sealed partial class LocalCertificates(IPluginRecords records, ISecretProtector? secrets, TimeProvider time, ILogger logger) : IDisposable
{
    public const string Collection = "localCertificates";
    public const string RowPrefix = "local:";
    public const string ServerPrefix = "server:";
    private const string KeyPurposePrefix = "pki:certificate:";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _serversGate = new();
    private readonly Dictionary<string, ServerCertificate> _servers = new(StringComparer.Ordinal);
    private volatile List<LocalCertificate> _list = [];
    private volatile HashSet<string> _serials = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<ServerCertificate> _serverList = [];

    /// <summary>Raised after a row was added or deleted (and after a server certificate changed).</summary>
    public event EventHandler? Changed;

    /// <summary>The rows, newest first (an immutable snapshot).</summary>
    public IReadOnlyList<LocalCertificate> List => _list;

    /// <summary>The servers' certificates, in name order (an immutable snapshot).</summary>
    public IReadOnlyList<ServerCertificate> Servers => _serverList;

    /// <summary>Keys can be kept (the server has a master key).</summary>
    public bool CanKeepKeys => secrets is not null;

    public async Task LoadAsync(CancellationToken ct)
    {
        var stored = await records.ListAsync(Collection, ct).ConfigureAwait(false);
        var list = new List<LocalCertificate>();
        foreach (var json in stored.Values)
        {
            if (PkiJson.TryDeserialize<StoredLocalCertificate>(json) is { Id.Length: > 0 } row && LocalCertificate.Read(row) is { } read)
            {
                list.Add(read);
            }
            else
            {
                LogUnreadable();
            }
        }

        SetList([.. list.OrderByDescending(l => l.Stored.AddedUtc).ThenBy(l => l.Stored.Id, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A certificate created, signed or imported here has this serial number (any notation): never a device's certificate,
    /// so EST and the 802.1X server refuse it (fail closed). A hash lookup in memory.
    /// </summary>
    public bool HasSerial(string serialNumber) =>
        !string.IsNullOrWhiteSpace(serialNumber) && _serials.Contains(CertificateSerials.Normalize(serialNumber));

    private void SetList(List<LocalCertificate> list)
    {
        _serials = new HashSet<string>(list.Select(l => l.Serial), StringComparer.Ordinal);
        _list = list;
    }

    public LocalCertificate? Find(string id) => _list.FirstOrDefault(l => string.Equals(l.Stored.Id, id, StringComparison.Ordinal));

    public ServerCertificate? FindServer(string slot)
    {
        lock (_serversGate)
        {
            return _servers.GetValueOrDefault(slot);
        }
    }

    /// <summary>
    /// Adds a row; <paramref name="pkcs8"/> is encrypted with the master key first (refused without one). Refused when
    /// <see cref="CertificateRules.MaxLocalCertificates"/> rows exist or the certificate is listed already.
    /// </summary>
    /// <returns>The row, or the refusal.</returns>
    public async Task<(LocalCertificate? Row, string? Error)> AddAsync(StoredLocalCertificate row, byte[]? pkcs8, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (pkcs8 is not null && secrets is null)
        {
            return (null, CertificateRules.NoKeyStore);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_list.Count >= CertificateRules.MaxLocalCertificates)
            {
                return (null, CertificateRules.TooMany);
            }

            var stored = pkcs8 is { } key ? row with { KeyProtected = secrets!.Protect(Convert.ToBase64String(key), KeyPurposePrefix + row.Id) } : row;
            if (LocalCertificate.Read(stored) is not { } read)
            {
                return (null, "The certificate cannot be read.");
            }

            if (_list.Any(l => l.Fingerprint == read.Fingerprint))
            {
                return (null, CertificateRules.AlreadyListed);
            }

            await records.SetManyAsync(Collection, [new(stored.Id, PkiJson.Serialize(stored))], ct).ConfigureAwait(false);
            SetList([read, .. _list]);
            Changed?.Invoke(this, EventArgs.Empty);
            return (read, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes rows by id (one transaction); returns how many were listed.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        int removed;
        try
        {
            var gone = new HashSet<string>(ids, StringComparer.Ordinal);
            var kept = _list.Where(l => !gone.Contains(l.Stored.Id)).ToList();
            removed = _list.Count - kept.Count;
            if (removed == 0)
            {
                return 0;
            }

            await records.SetManyAsync(Collection, [.. _list.Where(l => gone.Contains(l.Stored.Id)).Select(l => new KeyValuePair<string, string?>(l.Stored.Id, null))], ct)
                .ConfigureAwait(false);
            SetList(kept);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>The private key of a row (PKCS#8); null without one or when the master key cannot read it. The caller clears it.</summary>
    public byte[]? KeyOf(LocalCertificate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Stored.KeyProtected is not { } protectedKey || secrets is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(secrets.Unprotect(protectedKey, KeyPurposePrefix + row.Stored.Id));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            LogKeyUnreadable(row.Stored.Id);
            return null;
        }
    }

    /// <summary>
    /// A server made a new certificate (or read its stored one at its start): it replaces the one of the same server and
    /// name. Server certificates that ended more than 30 days ago leave (<see cref="IssuedRegistry.KeptAfterEnd"/>).
    /// </summary>
    public void NoteServer(string source, byte[] der, string? issuerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(der);
        ServerCertificate entry;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            var server = ServerCertificateSources.NameOf(source);
            var name = CaCertificates.CommonName(certificate);
            entry = new ServerCertificate(server + "|" + name, server, name, der, CertificateSerials.Normalize(certificate.SerialNumber), CaCertificates.Fingerprint(certificate),
                certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), LocalCertificate.IssuerCommonName(certificate), issuerFingerprint,
                time.GetUtcNow().UtcDateTime)
            {
                KeyText = CaCertificates.KeyType(certificate),
            };
        }
        catch (CryptographicException)
        {
            return;
        }

        lock (_serversGate)
        {
            if (_servers.TryGetValue(entry.Slot, out var known) && known.Fingerprint == entry.Fingerprint)
            {
                return;
            }

            _servers[entry.Slot] = entry;
            var limit = time.GetUtcNow().UtcDateTime - IssuedRegistry.KeptAfterEnd;
            foreach (var old in _servers.Values.Where(s => s.NotAfterUtc < limit).ToList())
            {
                _servers.Remove(old.Slot);
            }

            _serverList = [.. _servers.Values.OrderBy(s => s.Server, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes server certificates from the list (Delete of ended ones); returns how many were listed.</summary>
    public int DeleteServers(IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        int removed;
        lock (_serversGate)
        {
            removed = slots.Count(_servers.Remove);
            if (removed > 0)
            {
                _serverList = [.. _servers.Values.OrderBy(s => s.Server, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: a stored certificate of the Certificates list cannot be read and is not shown")]
    private partial void LogUnreadable();

    [LoggerMessage(Level = LogLevel.Error, Message = "PKI: the key of the certificate {Id} cannot be decrypted (other master key)")]
    private partial void LogKeyUnreadable(string id);
}
