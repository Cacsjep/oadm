using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The list of every device certificate the OADM CA issued and the revocation list (docs/spec/radius-server.md
/// "Certificates and revocation"). Both live in memory, indexed by serial number and by device, and in the plugin records:
/// <c>issued</c> (one row per certificate, no DER), <c>issuedDer</c> (the certificate, read one at a time) and <c>revoked</c>.
/// Tasks of thousands of devices add in parallel, so new certificates are written at most every <see cref="FlushDelay"/>
/// (and on stop), only the changed rows; a revocation is written before <see cref="RevokeAsync"/> returns. Kept: a
/// certificate until a year after it ended (<see cref="KeptAfterEnd"/>), a revocation while its certificate could still be
/// valid. <see cref="IsRevoked"/> is one lookup in an immutable set that a change replaces.
/// </summary>
public sealed partial class IssuedRegistry : IAsyncDisposable
{
    public const string IssuedCollection = "issued";
    public const string DerCollection = "issuedDer";
    public const string RevokedCollection = "revoked";

    /// <summary>Rows per write: one transaction each.</summary>
    private const int WriteBatch = 2000;

    /// <summary>A certificate leaves the list this long after it ended.</summary>
    public static readonly TimeSpan KeptAfterEnd = TimeSpan.FromDays(365);

    private static readonly IReadOnlyDictionary<string, CertificateRevocation> NoRevocations = new Dictionary<string, CertificateRevocation>(StringComparer.Ordinal);

    private readonly PkiStore _store;
    private readonly IPluginRecords _records;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    // Everything below under _gate. Keys: CertificateSerials.Normalize.
    private readonly Dictionary<string, IssuedCertificate> _bySerial = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<string>> _byDevice = [];
    private readonly Dictionary<(Guid Device, string Purpose), IssuedCertificate> _newest = [];
    private readonly Dictionary<string, CertificateRevocation> _revoked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyIssued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _pendingDer = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyRevoked = new(StringComparer.Ordinal);
    private List<string> _ordered = []; // keys; the entries are in _bySerial (an entry can be replaced in place)
    private bool _orderDirty;
    private bool _flushScheduled;
    private bool _loaded;
    private bool _disposed;
    private ITimer? _pruneTimer;

    // Read without the gate.
    private volatile IReadOnlyDictionary<string, CertificateRevocation> _revokedView = NoRevocations;

    public IssuedRegistry(PkiStore store, TimeProvider time, IPluginRecords? records = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _records = records ?? new MemoryPluginRecords();
        _logger = logger ?? NullLogger.Instance;
    }

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often old certificates and revocations are dropped (also at the start).</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The end of validity kept for a revoked serial the list does not hold (a certificate of the OADM CA from before the list
    /// that no read found): the latest end any OADM CA allows. Null or unset: kept until the list is cleared.
    /// </summary>
    public Func<DateTime?>? UnknownNotAfter { get; set; }

    /// <summary>Raised (outside the lock, on the caller's thread) after certificates were added, revoked or dropped.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after the revocation list changed (revoked, or ended revocations dropped).</summary>
    public event EventHandler? RevocationChanged;

    /// <summary>How many certificates the list holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bySerial.Count;
            }
        }
    }

    /// <summary>
    /// Reads the records (once): the certificates, the revocations, and the plugin setting <c>issued</c> of OADM before 4.2,
    /// which moves into the records and is deleted. Drops what is too old. Further calls do nothing.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var issued = await _records.ListAsync(IssuedCollection, ct).ConfigureAwait(false);
            var revoked = await _records.ListAsync(RevokedCollection, ct).ConfigureAwait(false);
            var legacy = await _store.LoadIssuedAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var json in issued.Values)
                {
                    if (PkiJson.TryDeserialize<IssuedCertificate>(json) is { SerialNumber.Length: > 0 } entry)
                    {
                        Index(entry);
                    }
                }

                foreach (var json in revoked.Values)
                {
                    if (PkiJson.TryDeserialize<CertificateRevocation>(json) is { SerialNumber.Length: > 0 } revocation)
                    {
                        _revoked[CertificateSerials.Normalize(revocation.SerialNumber)] = revocation;
                    }
                }

                foreach (var entry in legacy.Where(e => e.SerialNumber.Length > 0))
                {
                    var key = CertificateSerials.Normalize(entry.SerialNumber);
                    if (!_bySerial.ContainsKey(key))
                    {
                        Index(entry with { NoCopy = true }); // OADM 4.1 kept no DER: the next read of the device fills it
                        _dirtyIssued.Add(key);
                    }
                }

                _orderDirty = true;
                _revokedView = new Dictionary<string, CertificateRevocation>(_revoked, StringComparer.Ordinal);
                _loaded = true;
            }

            if (legacy.Count > 0)
            {
                await WriteAsync(ct).ConfigureAwait(false);
                await _store.DeleteIssuedSettingAsync(ct).ConfigureAwait(false);
                LogMoved(legacy.Count);
            }
        }
        finally
        {
            _flushGate.Release();
        }

        await PruneAsync(ct).ConfigureAwait(false);
        _pruneTimer = _time.CreateTimer(_ => _ = PruneQuietlyAsync(), null, PruneInterval, PruneInterval);
    }

    // ------------------------------------------------------------------ the list

    /// <summary>All certificates (a copy).</summary>
    public IReadOnlyList<IssuedCertificate> Snapshot()
    {
        lock (_gate)
        {
            return [.. _bySerial.Values];
        }
    }

    /// <summary>All certificates (a copy); kept for the callers of the settings registry.</summary>
    public Task<IReadOnlyList<IssuedCertificate>> SnapshotAsync(CancellationToken ct) => Task.FromResult(Snapshot());

    /// <summary>The certificates of one device (an index: no pass over the list).</summary>
    public IReadOnlyList<IssuedCertificate> ForDevice(Guid deviceId)
    {
        lock (_gate)
        {
            return _byDevice.TryGetValue(deviceId, out var serials) ? [.. serials.Select(s => _bySerial[s])] : [];
        }
    }

    public Task<IReadOnlyList<IssuedCertificate>> ForDeviceAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(ForDevice(deviceId));

    /// <summary>The certificate with <paramref name="serialNumber"/> (any notation); null when the list does not hold it.</summary>
    public IssuedCertificate? Find(string serialNumber)
    {
        lock (_gate)
        {
            return _bySerial.GetValueOrDefault(CertificateSerials.Normalize(serialNumber));
        }
    }

    /// <summary>Adds a certificate the OADM CA issued (with its DER when known); a serial already listed keeps its entry.</summary>
    public Task AddAsync(IssuedCertificate entry, CancellationToken ct) => AddAsync(entry, null, ct);

    /// <inheritdoc cref="AddAsync(IssuedCertificate, CancellationToken)"/>
    public Task AddAsync(IssuedCertificate entry, byte[]? der, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SerialNumber.Length == 0)
        {
            throw new ArgumentException("A certificate without a serial number.", nameof(entry));
        }

        var added = false;
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(entry.SerialNumber);
            if (!_bySerial.ContainsKey(key))
            {
                Index(entry);
                _dirtyIssued.Add(key);
                added = true;
            }

            if (der is not null && (added || !_pendingDer.ContainsKey(key)))
            {
                _pendingDer[key] = der;
            }

            ScheduleFlush();
        }

        if (added)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    /// <summary>The certificate (DER) with <paramref name="serialNumber"/>: waiting for its write, or one record read.</summary>
    public async Task<byte[]?> GetDerAsync(string serialNumber, CancellationToken ct)
    {
        var key = CertificateSerials.Normalize(serialNumber);
        lock (_gate)
        {
            if (!_bySerial.ContainsKey(key))
            {
                return null;
            }

            if (_pendingDer.TryGetValue(key, out var pending))
            {
                return pending;
            }
        }

        var json = await _records.GetAsync(DerCollection, key, ct).ConfigureAwait(false);
        try
        {
            return json is null || JsonNode.Parse(json)?["der"]?.GetValue<string>() is not { } base64 ? null : Convert.FromBase64String(base64);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The certificates of the OADM CAs found in a device's certificate list (OADM before the list existed, or an answer that
    /// got lost): each one signed by one of <paramref name="cas"/> and naming this device (<c>axis-&lt;serial&gt;.local</c>)
    /// is added once. Use: IEEE 802.1X for a client certificate, else HTTPS; source EST for the EST profile names
    /// (<c>oadm_</c>, <c>oadm2_</c>), else Task. Returns how many were added.
    /// </summary>
    public async Task<int> ImportFoundAsync(IDeviceInfo device, IEnumerable<(string Alias, string Pem)> certificates, IReadOnlyList<X509Certificate2> cas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(cas);
        var added = 0;
        if (cas.Count == 0)
        {
            return 0;
        }

        foreach (var (alias, pem) in certificates)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = X509Certificate2.CreateFromPem(pem);
            }
            catch (CryptographicException)
            {
                continue;
            }

            using (certificate)
            {
                var listed = Find(certificate.SerialNumber);
                if ((listed is not null && !listed.NoCopy) || SignedBy(certificate, cas) is not { } ca)
                {
                    continue;
                }

                if (listed is not null)
                {
                    FillCopy(listed, certificate); // an entry of OADM 4.1: its certificate copy from the device
                    continue;
                }

                var names = AxisSerials(certificate);
                if (names.Count != 1 || !string.Equals(names[0], device.Serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // names another camera (a copied certificate) or none: not listed under this device
                }

                var client = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                    .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == DeviceCertificateIssuer.ClientAuthOid));
                var notBefore = certificate.NotBefore.ToUniversalTime();
                await AddAsync(new IssuedCertificate
                {
                    SerialNumber = certificate.SerialNumber.ToUpperInvariant(),
                    DeviceId = device.Id,
                    DeviceSerial = names[0],
                    Purpose = client ? CertificatePurpose.Dot1x : CertificatePurpose.Https,
                    CaId = CaCertificates.Fingerprint(ca),
                    NotBeforeUtc = notBefore,
                    NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
                    IssuedUtc = notBefore,
                    Alias = alias,
                    Source = alias.StartsWith("oadm_", StringComparison.Ordinal) || alias.StartsWith("oadm2_", StringComparison.Ordinal) ? IssuedCertificateSources.Est : null,
                }, certificate.RawData, ct).ConfigureAwait(false);
                added++;
            }
        }

        return added;
    }

    /// <summary>The serials of the <c>axis-&lt;12 hex&gt;.local</c> names in a certificate (upper case, distinct).</summary>
    public static IReadOnlyList<string> AxisSerials(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        return [.. (san?.EnumerateDnsNames() ?? [])
            .Select(name => name.Length == 23 && name.StartsWith("axis-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                && name.AsSpan(5, 12).ToString().All(Uri.IsHexDigit) ? name.Substring(5, 12).ToUpperInvariant() : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A PKI task deleted the certificate from the device: it stays listed (a copy could still be in use) and shows as Replaced.
    /// Unknown serials are ignored.
    /// </summary>
    public void MarkRemovedFromDevice(string serialNumber)
    {
        var changed = false;
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(serialNumber);
            if (_bySerial.TryGetValue(key, out var entry) && entry.RemovedFromDeviceUtc is null)
            {
                ReplaceLocked(key, entry with { RemovedFromDeviceUtc = Now });
                changed = true;
                ScheduleFlush();
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stores the certificate copy (and the start and device serial it lacked) of an entry moved from OADM 4.1.</summary>
    private void FillCopy(IssuedCertificate listed, X509Certificate2 certificate)
    {
        var names = AxisSerials(certificate);
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(listed.SerialNumber);
            if (!_bySerial.TryGetValue(key, out var entry) || !entry.NoCopy)
            {
                return;
            }

            ReplaceLocked(key, entry with
            {
                NoCopy = false,
                NotBeforeUtc = entry.NotBeforeUtc ?? certificate.NotBefore.ToUniversalTime(),
                DeviceSerial = entry.DeviceSerial ?? (names.Count == 1 ? names[0] : null),
            });
            _pendingDer[key] = certificate.RawData;
            ScheduleFlush();
        }
    }

    // ------------------------------------------------------------------ revocation

    /// <summary>True when the certificate is on the revocation list (any notation of the serial). Lock-free.</summary>
    public bool IsRevoked(string serialNumber) => _revokedView.ContainsKey(CertificateSerials.Normalize(serialNumber));

    /// <summary>The revocation of the certificate; null when it is not revoked.</summary>
    public CertificateRevocation? FindRevocation(string serialNumber) => _revokedView.GetValueOrDefault(CertificateSerials.Normalize(serialNumber));

    /// <summary>The revocation list, newest first.</summary>
    public IReadOnlyList<CertificateRevocation> Revocations => [.. _revokedView.Values.OrderByDescending(r => r.RevokedUtc).ThenBy(r => r.SerialNumber, StringComparer.Ordinal)];

    /// <summary>
    /// Revokes certificates that are still valid (no undo, a revocation is never changed) and writes the list before it returns.
    /// A serial the list does not hold is revoked too (a certificate of the OADM CA from before the list that no read found),
    /// kept until <see cref="UnknownNotAfter"/>. Listed ones that ended, and text that is no serial number (not hex, longer
    /// than 20 bytes), are counted as not found.
    /// </summary>
    public async Task<RevokeResult> RevokeAsync(IEnumerable<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serialNumbers);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var now = Now;
        var unknownUntil = UnknownNotAfter?.Invoke() is { } end && end > now ? end : DateTime.MaxValue;
        int revoked = 0, already = 0, missing = 0;
        lock (_gate)
        {
            foreach (var key in serialNumbers.Select(CertificateSerials.Normalize).Distinct(StringComparer.Ordinal))
            {
                if (_revoked.ContainsKey(key))
                {
                    already++;
                }
                else if (_bySerial.TryGetValue(key, out var entry))
                {
                    if (entry.NotAfterUtc <= now)
                    {
                        missing++;
                    }
                    else
                    {
                        _revoked[key] = new CertificateRevocation(key, now, reason, userName) { DeviceId = entry.DeviceId, NotAfterUtc = entry.NotAfterUtc };
                        _dirtyRevoked.Add(key);
                        revoked++;
                    }
                }
                else if (IsSerialNumber(key))
                {
                    _revoked[key] = new CertificateRevocation(key, now, reason, userName) { NotAfterUtc = unknownUntil };
                    _dirtyRevoked.Add(key);
                    revoked++;
                }
                else
                {
                    missing++;
                }
            }

            if (revoked > 0)
            {
                _revokedView = new Dictionary<string, CertificateRevocation>(_revoked, StringComparer.Ordinal);
            }
        }

        if (revoked > 0)
        {
            await FlushAsync(ct).ConfigureAwait(false); // a revocation is never left only in memory
            var reasonText = CertificateRevocation.ReasonText(reason);
            LogRevoked(revoked, reasonText, userName);
            RevocationChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return new RevokeResult(revoked, already, missing);
    }

    /// <summary>The serials of a device's certificates that are still valid and not revoked (Revoke all of this device).</summary>
    public IReadOnlyList<string> RevocableOf(Guid deviceId)
    {
        var now = Now;
        lock (_gate)
        {
            return _byDevice.TryGetValue(deviceId, out var serials)
                ? [.. serials.Where(s => !_revoked.ContainsKey(s) && _bySerial[s].NotAfterUtc > now)]
                : [];
        }
    }

    // ------------------------------------------------------------------ the page

    /// <summary>The state of a certificate now (revoked, expired, replaced, expires soon, valid; in that order).</summary>
    public IssuedCertificateState StateOf(IssuedCertificate entry, DateTime nowUtc, int warningDays)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            return StateLocked(CertificateSerials.Normalize(entry.SerialNumber), entry, nowUtc, warningDays);
        }
    }

    /// <summary>
    /// One page of the list, newest first: filters, search, and only the devices of <paramref name="devices"/> when
    /// <paramref name="onlyListedDevices"/> (a user limited to a group), else every certificate (a device that is gone:
    /// empty device). One pass over the list.
    /// </summary>
    public IssuedCertificatePage Query(IssuedCertificateQuery query, IReadOnlyDictionary<Guid, IDeviceInfo> devices, bool onlyListedDevices, int warningDays)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(devices);
        var now = Now;
        var limit = Math.Clamp(query.Limit, 1, IssuedCertificateQuery.MaxLimit);
        var offset = Math.Max(0, query.Offset);
        var search = query.Search?.Trim() ?? string.Empty;
        var searchHex = search.Length == 0 ? string.Empty : CertificateSerials.Normalize(search);
        var rows = new List<IssuedCertificateInfo>(limit);
        var total = 0;
        lock (_gate)
        {
            if (_orderDirty)
            {
                _ordered.Sort((a, b) => Compare(_bySerial[a], _bySerial[b]));
                _orderDirty = false;
            }

            for (var i = _ordered.Count - 1; i >= 0; i--)
            {
                var key = _ordered[i];
                var entry = _bySerial[key];
                var device = devices.GetValueOrDefault(entry.DeviceId);
                if ((onlyListedDevices && device is null)
                    || (query.DeviceIds is { } ids && !ids.Contains(entry.DeviceId))
                    || (query.Purpose is { } purpose && !string.Equals(entry.Purpose, purpose, StringComparison.Ordinal))
                    || (query.Source is { } source && !string.Equals(SourceOf(entry), source, StringComparison.Ordinal)))
                {
                    continue;
                }

                var state = StateLocked(key, entry, now, warningDays);
                if (query.State is { } wanted && wanted != state)
                {
                    continue;
                }

                var deviceSerial = entry.DeviceSerial ?? device?.Serial ?? string.Empty;
                if (search.Length > 0
                    && !(device is not null && (device.Address.Contains(search, StringComparison.OrdinalIgnoreCase) || (device.HostName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                    && !deviceSerial.Contains(searchHex, StringComparison.OrdinalIgnoreCase)
                    && !key.Contains(searchHex, StringComparison.Ordinal))
                {
                    continue;
                }

                if (total++ < offset || rows.Count >= limit)
                {
                    continue;
                }

                rows.Add(new IssuedCertificateInfo
                {
                    SerialNumber = key,
                    DeviceId = entry.DeviceId,
                    DeviceSerial = deviceSerial,
                    Device = device?.Address ?? string.Empty,
                    DeviceManaged = device is not null,
                    Purpose = entry.Purpose,
                    Source = SourceOf(entry),
                    NotBeforeUtc = entry.NotBeforeUtc ?? entry.IssuedUtc,
                    NotAfterUtc = entry.NotAfterUtc,
                    IssuedUtc = entry.IssuedUtc,
                    State = state,
                    Revocation = _revoked.GetValueOrDefault(key),
                });
            }
        }

        return new IssuedCertificatePage(rows, total);
    }

    /// <summary>Task (PKI's own) or EST.</summary>
    public static string SourceOf(IssuedCertificate entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Equals(entry.Source, IssuedCertificateSources.Est, StringComparison.OrdinalIgnoreCase) ? IssuedCertificateSources.Est : IssuedCertificateSources.Task;
    }

    // ------------------------------------------------------------------ keeping and writing

    /// <summary>Drops certificates that ended more than <see cref="KeptAfterEnd"/> ago and revocations of ended certificates.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var now = Now;
        var limit = now - KeptAfterEnd;
        bool changed, revocations;
        lock (_gate)
        {
            var old = _bySerial.Where(p => p.Value.NotAfterUtc < limit).Select(p => p.Key).ToList();
            foreach (var key in old)
            {
                Unindex(key);
                _dirtyIssued.Add(key);
                _pendingDer[key] = null; // delete the DER too
            }

            if (old.Count > 0)
            {
                var gone = old.ToHashSet(StringComparer.Ordinal);
                _ordered = [.. _ordered.Where(k => !gone.Contains(k))];
            }

            var ended = _revoked.Where(p => p.Value.NotAfterUtc <= now).Select(p => p.Key).ToList();
            foreach (var key in ended)
            {
                _revoked.Remove(key);
                _dirtyRevoked.Add(key);
            }

            revocations = ended.Count > 0;
            changed = old.Count > 0 || revocations;
            if (revocations)
            {
                _revokedView = new Dictionary<string, CertificateRevocation>(_revoked, StringComparer.Ordinal);
            }
        }

        if (!changed)
        {
            return;
        }

        await FlushAsync(ct).ConfigureAwait(false);
        if (revocations)
        {
            RevocationChanged?.Invoke(this, EventArgs.Empty);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending changes now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pruneTimer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _flushGate.Dispose();
    }

    /// <summary>Writes the changed rows (inside the flush gate). A failed write keeps them for the next flush.</summary>
    private async Task WriteAsync(CancellationToken ct)
    {
        List<KeyValuePair<string, string?>> issued, der, revoked;
        List<KeyValuePair<string, byte[]?>> derTaken;
        lock (_gate)
        {
            issued = [.. _dirtyIssued.Select(k => new KeyValuePair<string, string?>(k, _bySerial.TryGetValue(k, out var e) ? PkiJson.Serialize(e) : null))];
            derTaken = [.. _pendingDer];
            revoked = [.. _dirtyRevoked.Select(k => new KeyValuePair<string, string?>(k, _revoked.TryGetValue(k, out var r) ? PkiJson.Serialize(r) : null))];
            _dirtyIssued.Clear();
            _dirtyRevoked.Clear();
            _flushScheduled = false;
        }

        der = [.. derTaken.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value is null ? null : new JsonObject { ["der"] = Convert.ToBase64String(p.Value) }.ToJsonString()))];
        if (issued.Count == 0 && der.Count == 0 && revoked.Count == 0)
        {
            return;
        }

        try
        {
            await WriteBatchesAsync(RevokedCollection, revoked, ct).ConfigureAwait(false);
            await WriteBatchesAsync(IssuedCollection, issued, ct).ConfigureAwait(false);
            await WriteBatchesAsync(DerCollection, der, ct).ConfigureAwait(false);
            lock (_gate)
            {
                // Written: read from the records from now on. One set again while writing stays pending.
                foreach (var (key, value) in derTaken)
                {
                    if (_pendingDer.TryGetValue(key, out var now) && ReferenceEquals(now, value))
                    {
                        _pendingDer.Remove(key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                foreach (var (key, _) in issued)
                {
                    _dirtyIssued.Add(key);
                }

                foreach (var (key, _) in revoked)
                {
                    _dirtyRevoked.Add(key);
                }

                if (ex is not OperationCanceledException)
                {
                    ScheduleFlush();
                }
            }

            if (ex is not OperationCanceledException)
            {
                LogWriteFailed(ex);
            }

            throw;
        }
    }

    private async Task WriteBatchesAsync(string collection, List<KeyValuePair<string, string?>> rows, CancellationToken ct)
    {
        for (var i = 0; i < rows.Count; i += WriteBatch)
        {
            await _records.SetManyAsync(collection, rows.GetRange(i, Math.Min(WriteBatch, rows.Count - i)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>One write after <see cref="FlushDelay"/> for every change in between (call inside the gate).</summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled || _disposed)
        {
            return;
        }

        _flushScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FlushDelay, _time, CancellationToken.None).ConfigureAwait(false);
                if (!_disposed)
                {
                    await FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // stopped meanwhile: DisposeAsync wrote the changes
            }
#pragma warning disable CA1031 // Logged in WriteAsync; the next change or the stop writes again.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }, CancellationToken.None);
    }

    private async Task PruneQuietlyAsync()
    {
        try
        {
            if (!_disposed)
            {
                await PruneAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // stopped
        }
#pragma warning disable CA1031 // Tried again at the next interval; logged in WriteAsync.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private IssuedCertificateState StateLocked(string key, IssuedCertificate entry, DateTime nowUtc, int warningDays)
    {
        if (_revoked.ContainsKey(key))
        {
            return IssuedCertificateState.Revoked;
        }

        if (entry.NotAfterUtc <= nowUtc)
        {
            return IssuedCertificateState.Expired;
        }

        if (entry.RemovedFromDeviceUtc is not null
            || (_newest.TryGetValue((entry.DeviceId, entry.Purpose ?? string.Empty), out var newest)
                && !string.Equals(CertificateSerials.Normalize(newest.SerialNumber), key, StringComparison.Ordinal)))
        {
            return IssuedCertificateState.Replaced; // a newer one exists, or a task deleted it from the device
        }

        return entry.NotAfterUtc <= nowUtc.AddDays(warningDays) ? IssuedCertificateState.ExpiresSoon : IssuedCertificateState.Valid;
    }

    /// <summary>Adds to the indexes (inside the gate; not to the ordered list).</summary>
    private void Index(IssuedCertificate entry)
    {
        var key = CertificateSerials.Normalize(entry.SerialNumber);
        _bySerial[key] = entry;
        if (!_byDevice.TryGetValue(entry.DeviceId, out var serials))
        {
            _byDevice[entry.DeviceId] = serials = [];
        }

        serials.Add(key);
        var newestKey = (entry.DeviceId, entry.Purpose ?? string.Empty);
        if (!_newest.TryGetValue(newestKey, out var known) || Compare(known, entry) <= 0)
        {
            _newest[newestKey] = entry;
        }

        _ordered.Add(key);
        _orderDirty |= _ordered.Count > 1 && Compare(_bySerial[_ordered[^2]], entry) > 0;
    }

    /// <summary>Replaces an entry in place (same serial, device, use and issue time; inside the gate) and marks it for writing.</summary>
    private void ReplaceLocked(string key, IssuedCertificate updated)
    {
        var old = _bySerial[key];
        _bySerial[key] = updated;
        var newestKey = (old.DeviceId, old.Purpose ?? string.Empty);
        if (_newest.TryGetValue(newestKey, out var newest) && ReferenceEquals(newest, old))
        {
            _newest[newestKey] = updated;
        }

        _dirtyIssued.Add(key);
    }

    /// <summary>A normalized serial number: hex digits, at most 20 bytes (RFC 5280 4.1.2.2).</summary>
    private static bool IsSerialNumber(string key) => key.Length is > 0 and <= 40 && key.All(Uri.IsHexDigit);

    private void Unindex(string key)
    {
        if (!_bySerial.Remove(key, out var entry))
        {
            return;
        }

        if (_byDevice.TryGetValue(entry.DeviceId, out var serials))
        {
            serials.Remove(key);
            if (serials.Count == 0)
            {
                _byDevice.Remove(entry.DeviceId);
            }
        }

        var newestKey = (entry.DeviceId, entry.Purpose ?? string.Empty);
        if (_newest.TryGetValue(newestKey, out var newest) && ReferenceEquals(newest, entry))
        {
            _newest.Remove(newestKey);
            foreach (var other in serials ?? [])
            {
                var candidate = _bySerial[other];
                if (string.Equals(candidate.Purpose ?? string.Empty, newestKey.Item2, StringComparison.Ordinal)
                    && (!_newest.TryGetValue(newestKey, out var best) || Compare(best, candidate) <= 0))
                {
                    _newest[newestKey] = candidate;
                }
            }
        }
    }

    /// <summary>Older first: by the time it was issued, then by serial (a stable order for the pages).</summary>
    private static int Compare(IssuedCertificate a, IssuedCertificate b)
    {
        var byTime = a.IssuedUtc.CompareTo(b.IssuedUtc);
        return byTime != 0 ? byTime : string.CompareOrdinal(a.SerialNumber, b.SerialNumber);
    }

    /// <summary>The CA of <paramref name="cas"/> that signed <paramref name="certificate"/> (name and signature), else null.</summary>
    private static X509Certificate2? SignedBy(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> cas)
    {
        foreach (var ca in cas)
        {
            if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(ca.SubjectName.RawData))
            {
                continue;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.AllowUnknownCertificateAuthority;
            try
            {
                // Element 1 must be this CA: the signature is checked by the chain build.
                if (chain.Build(certificate) && chain.ChainElements.Count >= 2 && chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData))
                {
                    return ca;
                }
            }
            catch (CryptographicException)
            {
                // not readable: not ours
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
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: moved {Count} issued certificates from the settings to the records")]
    private partial void LogMoved(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: revoked {Count} certificates ({Reason}) for {User}")]
    private partial void LogRevoked(int count, string reason, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: writing the issued certificates failed; tried again with the next change")]
    private partial void LogWriteFailed(Exception ex);
}
