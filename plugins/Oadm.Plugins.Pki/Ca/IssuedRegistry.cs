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
/// The store of device certificates the OADM CA issued and the revocation list (docs/spec/pki.md "Current and pending",
/// docs/spec/radius-server.md "Certificates and revocation"). Per camera (by its serial; the device id for entries without
/// one) and use the store holds the current certificate, the one confirmed on the camera, and at most one pending one
/// (issued, not confirmed yet). Confirming a pending one puts the previous current one on the revocation list and removes it
/// from the store; an unused pending one goes the same way; Revoke removes the certificate from the store too. Entries of
/// OADM before 4.5 stay as they were (no conversion): the newest one of each camera and use counts as current.
/// Everything lives in memory, indexed by serial number, device and camera, and in the plugin records: <c>issued</c> (one
/// row per certificate, no DER), <c>issuedDer</c> (the certificate, read one at a time) and <c>revoked</c>. Tasks of
/// thousands of devices add in parallel, so new certificates are written at most every <see cref="FlushDelay"/> (and on
/// stop), only the changed rows; a revocation is written before <see cref="RevokeAsync"/> returns. Kept: a certificate
/// until <see cref="KeptAfterEnd"/> after it ended, a revocation while its certificate could still be valid.
/// <see cref="IsRevoked"/> is one lookup in an immutable set that a change replaces.
/// </summary>
public sealed partial class IssuedRegistry : IAsyncDisposable
{
    public const string IssuedCollection = "issued";
    public const string DerCollection = "issuedDer";
    public const string RevokedCollection = "revoked";

    /// <summary>The user of the revocations OADM makes by itself (a replaced or unused certificate).</summary>
    public const string OadmUser = "OADM";

    /// <summary>Rows per write: one transaction each.</summary>
    private const int WriteBatch = 2000;

    /// <summary>A certificate leaves the store this long after it ended (decision 2026-10-25: 30 days, before 365).</summary>
    public static readonly TimeSpan KeptAfterEnd = TimeSpan.FromDays(30);

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
    private readonly Dictionary<string, List<string>> _byCamera = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Camera, string Purpose), string> _current = [];
    private readonly Dictionary<(string Camera, string Purpose), string> _pending = [];
    private readonly Dictionary<string, CertificateRevocation> _revoked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyIssued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _pendingDer = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyRevoked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deadOrdered = new(StringComparer.Ordinal); // keys still in _ordered that left the store
    private List<string> _ordered = []; // keys; the entries are in _bySerial (an entry can be replaced in place)
    private bool _orderDirty;
    private bool _flushScheduled;
    private bool _loaded;
    private bool _disposed;
    private ITimer? _pruneTimer;
    private (IReadOnlyDictionary<Guid, IDeviceInfo> Source, Dictionary<string, IDeviceInfo> BySerial)? _devicesBySerial;

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
    /// How long an EST certificate may stay pending without its answer written (the server stopped between signing and
    /// answering): then it goes on the revocation list as unused at the next prune.
    /// </summary>
    public TimeSpan PendingTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The end of validity kept for a revoked serial the list does not hold (a certificate of the OADM CA from before the list
    /// that no read found): the latest end any OADM CA allows. Null or unset: kept until the list is cleared.
    /// </summary>
    public Func<DateTime?>? UnknownNotAfter { get; set; }

    /// <summary>Raised (outside the lock, on the caller's thread) after certificates were added, revoked or dropped.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after the revocation list changed (revoked, or ended revocations dropped).</summary>
    public event EventHandler? RevocationChanged;

    /// <summary>How many certificates the store holds.</summary>
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

    // ------------------------------------------------------------------ the store

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

    /// <summary>The certificates stored under one device id (an index: no pass over the list).</summary>
    public IReadOnlyList<IssuedCertificate> ForDevice(Guid deviceId)
    {
        lock (_gate)
        {
            return _byDevice.TryGetValue(deviceId, out var serials) ? [.. serials.Select(s => _bySerial[s])] : [];
        }
    }

    /// <summary>
    /// The certificates of one camera: those stored under its device id and those naming its serial (a camera removed and
    /// added again has a new device id). Two index lookups.
    /// </summary>
    public IReadOnlyList<IssuedCertificate> ForDevice(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            return [.. KeysOfLocked(device).Select(s => _bySerial[s])];
        }
    }

    public Task<IReadOnlyList<IssuedCertificate>> ForDeviceAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(ForDevice(deviceId));

    public Task<IReadOnlyList<IssuedCertificate>> ForDeviceAsync(IDeviceInfo device, CancellationToken ct) => Task.FromResult(ForDevice(device));

    /// <summary>The certificate with <paramref name="serialNumber"/> (any notation); null when the store does not hold it.</summary>
    public IssuedCertificate? Find(string serialNumber)
    {
        lock (_gate)
        {
            return _bySerial.GetValueOrDefault(CertificateSerials.Normalize(serialNumber));
        }
    }

    /// <summary>
    /// Adds a certificate (with its DER when known) as the store gets it: a found one (<see cref="ImportFoundAsync"/>) or one
    /// of OADM before 4.5. A pending one (<see cref="IssuedCertificate.Pending"/>) goes through <see cref="AddPendingAsync"/>.
    /// A serial already stored keeps its entry.
    /// </summary>
    public Task AddAsync(IssuedCertificate entry, CancellationToken ct) => AddAsync(entry, null, ct);

    /// <inheritdoc cref="AddAsync(IssuedCertificate, CancellationToken)"/>
    public Task AddAsync(IssuedCertificate entry, byte[]? der, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Pending)
        {
            return AddPendingAsync(entry, der, ct);
        }

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

    /// <summary>
    /// Adds a certificate the OADM CA just issued and the camera has not confirmed yet (a task before its verify step, EST
    /// before its answer). An older pending one of the same camera and use goes on the revocation list (Superseded) and
    /// leaves the store, except one a task delivered (it may be in use: kept, not current).
    /// </summary>
    public async Task AddPendingAsync(IssuedCertificate entry, byte[]? der, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SerialNumber.Length == 0)
        {
            throw new ArgumentException("A certificate without a serial number.", nameof(entry));
        }

        string? pushedOut = null;
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(entry.SerialNumber);
            if (_bySerial.ContainsKey(key))
            {
                return;
            }

            var pending = entry with { Pending = true, NotCurrent = false };
            if (_pending.TryGetValue(SlotOf(pending), out var older))
            {
                var old = _bySerial[older];
                if (old.Delivered && SourceOf(old) == IssuedCertificateSources.Task)
                {
                    ReplaceLocked(older, old with { Pending = false, Delivered = false, NotCurrent = true });
                }
                else
                {
                    pushedOut = older;
                }
            }

            Index(pending);
            _dirtyIssued.Add(key);
            if (der is not null)
            {
                _pendingDer[key] = der;
            }

            ScheduleFlush();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (pushedOut is not null)
        {
            await RetireAsync([pushedOut], RevocationReason.Superseded, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The camera has the pending certificate (a task's verify step passed, EST wrote its answer, the camera signed in to the
    /// 802.1X server with it): it becomes current; the previous current one goes on the revocation list (Superseded) and
    /// leaves the store. False when the serial is not pending.
    /// </summary>
    public async Task<bool> ConfirmAsync(string serialNumber, CancellationToken ct)
    {
        string? previous;
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(serialNumber);
            if (!_bySerial.TryGetValue(key, out var entry) || !entry.Pending)
            {
                return false;
            }

            previous = _current.GetValueOrDefault(SlotOf(entry));
            ReplaceLocked(key, entry with { Pending = false, Delivered = false, NotCurrent = false });
            if (previous == key)
            {
                previous = null;
            }

            ScheduleFlush();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (previous is not null)
        {
            await RetireAsync([previous], RevocationReason.Superseded, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The pending certificate never reached the camera (a failed install, an EST answer that could not be written, a task
    /// that found the camera using another one): it goes on the revocation list (Other) and leaves the store. One a task
    /// delivered (<see cref="IssuedCertificate.Delivered"/>) stays unless <paramref name="evenDelivered"/> (the camera reports
    /// another client certificate). False when nothing was done.
    /// </summary>
    public async Task<bool> DiscardAsync(string serialNumber, bool evenDelivered, CancellationToken ct)
    {
        string key;
        lock (_gate)
        {
            key = CertificateSerials.Normalize(serialNumber);
            if (!_bySerial.TryGetValue(key, out var entry) || !entry.Pending
                || (entry.Delivered && SourceOf(entry) == IssuedCertificateSources.Task && !evenDelivered))
            {
                return false;
            }
        }

        await RetireAsync([key], RevocationReason.Other, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The camera may use the pending certificate already (see <see cref="IssuedCertificate.Delivered"/>).</summary>
    public void MarkDelivered(string serialNumber)
    {
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(serialNumber);
            if (_bySerial.TryGetValue(key, out var entry) && entry.Pending && !entry.Delivered)
            {
                ReplaceLocked(key, entry with { Delivered = true });
                ScheduleFlush();
            }
        }
    }

    /// <summary>
    /// A certificate OADM did not issue replaced the camera's current one of <paramref name="purpose"/> (Install certificates,
    /// verified): the current one goes on the revocation list (Superseded) and leaves the store. Returns its serial, or null.
    /// </summary>
    public async Task<string?> ReplacedOnCameraAsync(IDeviceInfo device, string purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        string? current = null;
        lock (_gate)
        {
            foreach (var key in KeysOfLocked(device))
            {
                var entry = _bySerial[key];
                if (string.Equals(entry.Purpose, purpose, StringComparison.Ordinal) && IsCurrentLocked(key, entry))
                {
                    current = key;
                    break;
                }
            }
        }

        if (current is not null)
        {
            await RetireAsync([current], RevocationReason.Superseded, ct).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>The DER of a stored certificate: waiting for its write, or one record read; null when not stored.</summary>
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
    /// got lost): each one signed by one of <paramref name="cas"/>, naming this device (<c>axis-&lt;serial&gt;.local</c>) and
    /// not on the revocation list is added once. Use: <see cref="PurposeOfFound"/>; source EST for the EST profile names
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
                if (IsRevoked(certificate.SerialNumber))
                {
                    continue; // revoked (also replaced or unused): never back in the store
                }

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
                    Purpose = PurposeOfFound(alias, client),
                    CaId = CaCertificates.Fingerprint(ca),
                    NotBeforeUtc = notBefore,
                    NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
                    IssuedUtc = notBefore,
                    Alias = alias,
                    Source = EstProfileName(alias) is not null ? IssuedCertificateSources.Est : null,
                }, certificate.RawData, ct).ConfigureAwait(false);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// The use of a certificate found on a device: the one the EST profile name behind <c>oadm_</c> / <c>oadm2_</c> names
    /// ("oadm_mqtt", "oadm2_mqtt_cameras" -> MQTT; "oadm_rtsps" -> RTSPS) when it fits the certificate (MQTT and IEEE 802.1X
    /// are client certificates, HTTPS and RTSPS server certificates); else IEEE 802.1X for a client certificate, HTTPS for
    /// the others.
    /// </summary>
    public static string PurposeOfFound(string alias, bool client)
    {
        if (EstProfileName(alias) is { } profile)
        {
            var named = DeviceCertificatePurposes.All.FirstOrDefault(p =>
                profile.Equals(p, StringComparison.OrdinalIgnoreCase) || profile.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));
            if (named is not null && (named is CertificatePurpose.Dot1x or CertificatePurpose.Mqtt) == client)
            {
                return named;
            }
        }

        return client ? CertificatePurpose.Dot1x : CertificatePurpose.Https;
    }

    /// <summary>The EST profile of an alias the EST plugin gave ("oadm2_https" -> "https"); null for any other alias.</summary>
    private static string? EstProfileName(string? alias) =>
        alias is null ? null
        : alias.StartsWith("oadm2_", StringComparison.Ordinal) ? alias["oadm2_".Length..]
        : alias.StartsWith("oadm_", StringComparison.Ordinal) ? alias["oadm_".Length..]
        : null;

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
    /// A PKI task deleted the certificate from the device: it stays in the store (a copy could still be in use, Revoke can
    /// take it) but is no longer listed. Unknown serials are ignored.
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
    /// A revoked certificate leaves the store (its camera has no row until it gets a new certificate). A serial the store
    /// does not hold is revoked too (a certificate of the OADM CA from before the list that no read found, created or signed
    /// ones, server certificates), kept until <see cref="UnknownNotAfter"/>. Stored ones that ended, and text that is no
    /// serial number (not hex, longer than 20 bytes), are counted as not found.
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
                        DropLocked(key);
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

    /// <summary>
    /// The serials of a camera's certificates (by its device id and its serial) that are still valid and not revoked: Revoke
    /// all of this device and the Revoke policies, also for a camera added again.
    /// </summary>
    public IReadOnlyList<string> RevocableOf(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = Now;
        lock (_gate)
        {
            return [.. KeysOfLocked(device).Where(s => !_revoked.ContainsKey(s) && _bySerial[s].NotAfterUtc > now)];
        }
    }

    /// <summary>
    /// Puts certificates OADM no longer needs on the revocation list by the user "OADM" (written first) and removes them from
    /// the store, also when they ended already.
    /// </summary>
    private async Task RetireAsync(IReadOnlyList<string> keys, RevocationReason reason, CancellationToken ct)
    {
        await RevokeAsync(keys, reason, OadmUser, ct).ConfigureAwait(false);
        var dropped = false;
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (_bySerial.ContainsKey(key))
                {
                    DropLocked(key);
                    dropped = true;
                }
            }
        }

        if (dropped)
        {
            await FlushAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ the page

    /// <summary>The state of a certificate now (revoked, expired, expires soon, valid; in that order).</summary>
    public IssuedCertificateState StateOf(IssuedCertificate entry, DateTime nowUtc, int warningDays)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            return StateLocked(CertificateSerials.Normalize(entry.SerialNumber), entry, nowUtc, warningDays);
        }
    }

    /// <summary>True when the certificate is its camera's current one of its use (not pending, not pushed out).</summary>
    public bool IsCurrent(string serialNumber)
    {
        lock (_gate)
        {
            var key = CertificateSerials.Normalize(serialNumber);
            return _bySerial.TryGetValue(key, out var entry) && IsCurrentLocked(key, entry);
        }
    }

    /// <summary>
    /// One page of the list, newest first: filters, search, and only the devices of <paramref name="devices"/> when
    /// <paramref name="onlyListedDevices"/> (a user limited to a group), else every certificate (a device that is gone:
    /// empty device). One pass over the list.
    /// </summary>
    public IssuedCertificatePage Query(IssuedCertificateQuery query, IReadOnlyDictionary<Guid, IDeviceInfo> devices, bool onlyListedDevices, int warningDays) =>
        Query(query, devices, onlyListedDevices, _ => warningDays);

    /// <summary>
    /// The page with the warning days of each device (its Expiry warning policy). <paramref name="filter"/> leaves out more
    /// (the "Issued by" filter); <paramref name="searchMore"/> matches the search in more columns (use, issuer, valid until,
    /// state) for an entry the address, serial and device serial did not match. <paramref name="currentOnly"/> (the PKI
    /// page, decision 2026-10-25): only each managed camera's current certificate of each use, found by the camera's
    /// serial (the device id for entries without one), not deleted from the camera; false: every stored certificate.
    /// </summary>
    public IssuedCertificatePage Query(IssuedCertificateQuery query, IReadOnlyDictionary<Guid, IDeviceInfo> devices, bool onlyListedDevices, Func<Guid, int> warningDays,
        Func<IssuedCertificate, bool>? filter = null, Func<IssuedCertificate, IssuedCertificateState, bool>? searchMore = null, bool currentOnly = true)
    {
        ArgumentNullException.ThrowIfNull(warningDays);
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
            var bySerial = DevicesBySerialLocked(devices);
            if (_deadOrdered.Count > 0)
            {
                _ordered.RemoveAll(_deadOrdered.Contains);
                _deadOrdered.Clear();
            }

            if (_orderDirty)
            {
                _ordered.Sort((a, b) => Compare(_bySerial[a], _bySerial[b]));
                _orderDirty = false;
            }

            for (var i = _ordered.Count - 1; i >= 0; i--)
            {
                var key = _ordered[i];
                var entry = _bySerial[key];
                var device = entry.DeviceSerial is { Length: > 0 } cameraSerial ? bySerial.GetValueOrDefault(cameraSerial) : devices.GetValueOrDefault(entry.DeviceId);
                var listed = device is not null && IsListedLocked(key, entry, device);
                var deviceId = device?.Id ?? entry.DeviceId;
                if ((currentOnly && !listed)
                    || (onlyListedDevices && device is null)
                    || (query.DeviceIds is { } ids && !ids.Contains(deviceId))
                    || (query.Purpose is { } purpose && !string.Equals(entry.Purpose, purpose, StringComparison.Ordinal))
                    || (query.Source is { } source && !string.Equals(SourceOf(entry), source, StringComparison.Ordinal))
                    || (filter is not null && !filter(entry)))
                {
                    continue;
                }

                var state = StateLocked(key, entry, now, warningDays(deviceId));
                if (query.State is { } wanted && wanted != state)
                {
                    continue;
                }

                var deviceSerial = entry.DeviceSerial ?? device?.Serial ?? string.Empty;
                if (search.Length > 0
                    && !(device is not null && (device.Address.Contains(search, StringComparison.OrdinalIgnoreCase) || (device.HostName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                    && !deviceSerial.Contains(searchHex, StringComparison.OrdinalIgnoreCase)
                    && !key.Contains(searchHex, StringComparison.Ordinal)
                    && !(searchMore?.Invoke(entry, state) ?? false))
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
                    DeviceId = deviceId,
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
                    Kind = ListedCertificateKind.Device,
                    UsedBy = listed ? device!.Address : null,
                });
            }
        }

        return new IssuedCertificatePage(rows, total);
    }

    /// <summary>
    /// Deletes certificates that ended or are revoked from the store (Delete on the page); their revocations stay. Serials that
    /// are not stored or still valid are left. Written before it returns.
    /// </summary>
    /// <returns>How many left the store.</returns>
    public async Task<int> DeleteEndedAsync(IReadOnlyCollection<string> serialNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serialNumbers);
        var now = Now;
        var removed = 0;
        lock (_gate)
        {
            foreach (var key in serialNumbers.Select(CertificateSerials.Normalize).Distinct(StringComparer.Ordinal))
            {
                if (_bySerial.TryGetValue(key, out var entry) && (_revoked.ContainsKey(key) || entry.NotAfterUtc <= now))
                {
                    DropLocked(key);
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            await FlushAsync(ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>Task (PKI's own) or EST.</summary>
    public static string SourceOf(IssuedCertificate entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Equals(entry.Source, IssuedCertificateSources.Est, StringComparison.OrdinalIgnoreCase) ? IssuedCertificateSources.Est : IssuedCertificateSources.Task;
    }

    // ------------------------------------------------------------------ keeping and writing

    /// <summary>
    /// Drops certificates that ended more than <see cref="KeptAfterEnd"/> ago and revocations of ended certificates; EST
    /// certificates pending longer than <see cref="PendingTimeout"/> without their answer go on the revocation list.
    /// </summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var now = Now;
        var limit = now - KeptAfterEnd;
        bool changed, revocations;
        List<string> stale;
        lock (_gate)
        {
            var old = _bySerial.Where(p => p.Value.NotAfterUtc < limit).Select(p => p.Key).ToList();
            foreach (var key in old)
            {
                DropLocked(key);
            }

            stale = [.. _pending.Values.Where(k => _bySerial[k] is { Delivered: false } e && SourceOf(e) == IssuedCertificateSources.Est && e.IssuedUtc < now - PendingTimeout)];

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

        if (stale.Count > 0)
        {
            LogStalePending(stale.Count);
            await RetireAsync(stale, RevocationReason.Other, ct).ConfigureAwait(false);
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

        return entry.NotAfterUtc <= nowUtc.AddDays(warningDays) ? IssuedCertificateState.ExpiresSoon : IssuedCertificateState.Valid;
    }

    /// <summary>The camera of an entry: its serial (upper case), or its device id for entries without one.</summary>
    private static string CameraOf(IssuedCertificate entry) =>
        entry.DeviceSerial is { Length: > 0 } serial ? serial.ToUpperInvariant() : "#" + entry.DeviceId.ToString("N", CultureInfo.InvariantCulture);

    private static (string Camera, string Purpose) SlotOf(IssuedCertificate entry) => (CameraOf(entry), entry.Purpose ?? string.Empty);

    /// <summary>The camera's current one of its use (inside the gate).</summary>
    private bool IsCurrentLocked(string key, IssuedCertificate entry) =>
        !entry.Pending && _current.TryGetValue(SlotOf(entry), out var current) && current == key;

    /// <summary>
    /// A row of the current list (inside the gate): the current one, not deleted from the camera, and for an entry without
    /// a serial (OADM before 4.2) only while the camera has no newer current one under its serial.
    /// </summary>
    private bool IsListedLocked(string key, IssuedCertificate entry, IDeviceInfo device)
    {
        if (entry.RemovedFromDeviceUtc is not null || !IsCurrentLocked(key, entry))
        {
            return false;
        }

        return entry.DeviceSerial is { Length: > 0 }
            || !_current.TryGetValue((device.Serial.ToUpperInvariant(), entry.Purpose ?? string.Empty), out var other)
            || Compare(_bySerial[other], entry) <= 0;
    }

    /// <summary>The keys of a camera: by its device id and by its serial (inside the gate).</summary>
    private List<string> KeysOfLocked(IDeviceInfo device)
    {
        var byId = _byDevice.TryGetValue(device.Id, out var ids) ? ids : [];
        var bySerial = device.Serial is { Length: > 0 } serial && _byCamera.TryGetValue(serial.ToUpperInvariant(), out var keys) ? keys : [];
        return byId.Concat(bySerial).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The devices by serial (upper case), built once per device list (inside the gate).</summary>
    private Dictionary<string, IDeviceInfo> DevicesBySerialLocked(IReadOnlyDictionary<Guid, IDeviceInfo> devices)
    {
        if (_devicesBySerial is { } cached && ReferenceEquals(cached.Source, devices))
        {
            return cached.BySerial;
        }

        var bySerial = new Dictionary<string, IDeviceInfo>(devices.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices.Values)
        {
            if (device.Serial is { Length: > 0 } serial)
            {
                bySerial[serial] = device;
            }
        }

        _devicesBySerial = (devices, bySerial);
        return bySerial;
    }

    /// <summary>Adds to the indexes and the ordered list (inside the gate).</summary>
    private void Index(IssuedCertificate entry)
    {
        var key = CertificateSerials.Normalize(entry.SerialNumber);
        _bySerial[key] = entry;
        AddIndexes(key, entry);
        if (_deadOrdered.Remove(key))
        {
            return; // still in the ordered list at its place (same serial, same issue time)
        }

        _ordered.Add(key);
        _orderDirty |= _ordered.Count > 1 && (!_bySerial.TryGetValue(_ordered[^2], out var before) || Compare(before, entry) > 0); // a key that left: sort to be sure
    }

    private void AddIndexes(string key, IssuedCertificate entry)
    {
        if (!_byDevice.TryGetValue(entry.DeviceId, out var serials))
        {
            _byDevice[entry.DeviceId] = serials = [];
        }

        serials.Add(key);
        var camera = CameraOf(entry);
        if (!_byCamera.TryGetValue(camera, out var ofCamera))
        {
            _byCamera[camera] = ofCamera = [];
        }

        ofCamera.Add(key);
        var slot = (camera, entry.Purpose ?? string.Empty);
        if (entry.Pending)
        {
            if (!_pending.TryGetValue(slot, out var known) || Compare(_bySerial[known], entry) <= 0)
            {
                _pending[slot] = key;
            }
        }
        else if (!entry.NotCurrent && (!_current.TryGetValue(slot, out var current) || Compare(_bySerial[current], entry) <= 0))
        {
            _current[slot] = key;
        }
    }

    private void RemoveIndexes(string key, IssuedCertificate entry)
    {
        if (_byDevice.TryGetValue(entry.DeviceId, out var serials))
        {
            serials.Remove(key);
            if (serials.Count == 0)
            {
                _byDevice.Remove(entry.DeviceId);
            }
        }

        var camera = CameraOf(entry);
        if (_byCamera.TryGetValue(camera, out var ofCamera))
        {
            ofCamera.Remove(key);
            if (ofCamera.Count == 0)
            {
                _byCamera.Remove(camera);
            }
        }

        var slot = (camera, entry.Purpose ?? string.Empty);
        if (_current.TryGetValue(slot, out var current) && current == key)
        {
            _current.Remove(slot);
            foreach (var other in ofCamera ?? [])
            {
                var candidate = _bySerial[other];
                if (!candidate.Pending && !candidate.NotCurrent && string.Equals(candidate.Purpose ?? string.Empty, slot.Item2, StringComparison.Ordinal)
                    && (!_current.TryGetValue(slot, out var best) || Compare(_bySerial[best], candidate) <= 0))
                {
                    _current[slot] = other;
                }
            }
        }

        if (_pending.TryGetValue(slot, out var pending) && pending == key)
        {
            _pending.Remove(slot);
            foreach (var other in ofCamera ?? [])
            {
                var candidate = _bySerial[other];
                if (candidate.Pending && string.Equals(candidate.Purpose ?? string.Empty, slot.Item2, StringComparison.Ordinal)
                    && (!_pending.TryGetValue(slot, out var best) || Compare(_bySerial[best], candidate) <= 0))
                {
                    _pending[slot] = other;
                }
            }
        }
    }

    /// <summary>Replaces an entry in place (same serial and issue time; inside the gate) and marks it for writing.</summary>
    private void ReplaceLocked(string key, IssuedCertificate updated)
    {
        RemoveIndexes(key, _bySerial[key]);
        _bySerial[key] = updated;
        AddIndexes(key, updated);
        _dirtyIssued.Add(key);
    }

    /// <summary>
    /// Removes an entry from the store and the records (inside the gate). When it was its camera's current one, the older
    /// entries of that camera and use (OADM before 4.5) are no longer current either: the camera has no row until it gets
    /// a new certificate.
    /// </summary>
    private void DropLocked(string key)
    {
        if (!_bySerial.TryGetValue(key, out var entry))
        {
            return;
        }

        var wasCurrent = IsCurrentLocked(key, entry);
        RemoveIndexes(key, entry);
        _bySerial.Remove(key);
        _deadOrdered.Add(key);
        _dirtyIssued.Add(key);
        _pendingDer[key] = null; // delete the DER too
        if (!wasCurrent)
        {
            return;
        }

        var slot = SlotOf(entry);
        if (!_byCamera.TryGetValue(slot.Camera, out var ofCamera))
        {
            return;
        }

        foreach (var other in ofCamera.ToList())
        {
            var older = _bySerial[other];
            if (!older.Pending && !older.NotCurrent && string.Equals(older.Purpose ?? string.Empty, slot.Purpose, StringComparison.Ordinal))
            {
                ReplaceLocked(other, older with { NotCurrent = true });
            }
        }

        ScheduleFlush();
    }

    /// <summary>A normalized serial number: hex digits, at most 20 bytes (RFC 5280 4.1.2.2).</summary>
    private static bool IsSerialNumber(string key) => key.Length is > 0 and <= 40 && key.All(Uri.IsHexDigit);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: {Count} EST certificates were issued but their answer never written; revoked as unused")]
    private partial void LogStalePending(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: writing the issued certificates failed; tried again with the next change")]
    private partial void LogWriteFailed(Exception ex);
}
