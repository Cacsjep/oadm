using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The data of the EST server. Plugin settings: <c>config</c>, <c>enrollments</c> (newest first,
/// <see cref="EstConfig.EnrollmentsKept"/>) and <c>sites</c>. Plugin records, collection <c>certificates</c>, one row per
/// certificate by serial number: per device and profile the newest certificate EST issued and the one before it (a camera
/// whose answer got lost renews with it); ended more than 30 days ago or of a device OADM no longer manages dropped: at
/// most 2 x devices x profiles. Only changed certificates are written. Kept in memory; changes are written at most every
/// <see cref="FlushDelay"/> and on dispose. Thread-safe.
/// </summary>
public sealed partial class EstStore : IAsyncDisposable
{
    public const string ConfigKey = "config";
    public const string EnrollmentsKey = "enrollments";
    /// <summary>The records collection of the certificates (4.1.0-rc.2: a settings value under the same name).</summary>
    public const string CertificatesKey = "certificates";
    public const string SitesKey = "sites";

    /// <summary>4.1.0-rc.2 only (revoked device and profile pairs): deleted when loaded.</summary>
    public const string LegacyRevokedKey = "revoked";

    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(30);

    private readonly IPluginSettings _settings;
    private readonly IPluginRecords _records;
    private readonly HashSet<string> _changedCertificates = new(StringComparer.OrdinalIgnoreCase); // serials to write or delete
    private bool _dropLegacyCertificates;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly LinkedList<Enrollment> _enrollments = new();
    private readonly Dictionary<string, IssuedCertificate> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DeviceProfile, string> _newest = []; // device and profile -> serial of its newest certificate
    private readonly Dictionary<DeviceProfile, string> _previous = []; // ... and of the one before it
    private readonly Dictionary<Guid, HashSet<string>> _profilesOf = []; // device -> its profiles in _newest (O(1) per device)
    private readonly Dictionary<Guid, SortedSet<string>> _sites = [];
    private EstConfig _config = new();
    private bool _dirty;
    private bool _flushPending;
    private ITimer? _flushTimer;

    /// <param name="records">The plugin's records; null keeps the certificates in memory only (tests).</param>
    public EstStore(IPluginSettings settings, IPluginRecords? records = null, TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _records = records ?? new MemoryPluginRecords();
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Where a write that failed is reported (it is tried again with the next flush).</summary>
    public ILogger Logger { get => _logger; init => _logger = value ?? NullLogger.Instance; }

    private readonly ILogger _logger = NullLogger.Instance;

    public EstConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var config = Read<EstConfig>(await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false)) ?? new EstConfig();
        var enrollments = Read<List<Enrollment>>(await _settings.GetAsync(EnrollmentsKey, ct).ConfigureAwait(false)) ?? [];
        var certificates = (await _records.ListAsync(CertificatesKey, ct).ConfigureAwait(false)).Values
            .Select(Read<IssuedCertificate>).OfType<IssuedCertificate>().ToList();
        var legacy = await _settings.GetAsync(CertificatesKey, ct).ConfigureAwait(false);
        if (legacy is not null)
        {
            // 4.1.0-rc.2: one settings value, moved to the records. A move cut short (the server stopped before the old
            // value was deleted): the records already hold the newer state of those certificates.
            var moved = certificates.Select(c => c.SerialNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
            certificates.AddRange(ReadLegacy(legacy).Where(c => !moved.Contains(c.SerialNumber)));
        }

        legacy ??= await _settings.GetAsync(LegacyRevokedKey, ct).ConfigureAwait(false);
        var sites = Read<Dictionary<Guid, List<string>>>(await _settings.GetAsync(SitesKey, ct).ConfigureAwait(false)) ?? [];
        lock (_gate)
        {
            _config = config;
            _enrollments.Clear();
            foreach (var enrollment in enrollments.Take(config.EnrollmentsKept))
            {
                _enrollments.AddLast(enrollment);
            }

            _certificates.Clear();
            _newest.Clear();
            _previous.Clear();
            _changedCertificates.Clear();
            _profilesOf.Clear();
            foreach (var certificate in certificates.OrderBy(c => c.IssuedUtc)) // 4.1.0-rc.2 kept every certificate
            {
                _certificates[certificate.SerialNumber] = certificate;
                Index(certificate);
            }

            if (legacy is not null)
            {
                _changedCertificates.UnionWith(_certificates.Keys);
                _dropLegacyCertificates = true;
            }

            _sites.Clear();
            foreach (var (site, addresses) in sites)
            {
                _sites[site] = new SortedSet<string>(addresses, StringComparer.Ordinal);
            }
        }

        if (legacy is not null)
        {
            MarkDirty(); // the move to the records is written with the next flush
        }
    }

    /// <summary>
    /// The site node's address a camera at <paramref name="siteId"/> was given as EST server (the set-up task): it goes
    /// into the site's server certificate. Returns true when it is new.
    /// </summary>
    public bool AddSiteAddress(Guid siteId, string address)
    {
        lock (_gate)
        {
            if (!_sites.TryGetValue(siteId, out var addresses))
            {
                _sites[siteId] = addresses = new SortedSet<string>(StringComparer.Ordinal);
            }

            if (!addresses.Add(address))
            {
                return false;
            }
        }

        MarkDirty();
        return true;
    }

    /// <summary>The node addresses of a site the cameras were given; empty when no camera there is set up.</summary>
    public IReadOnlyList<string> SiteAddresses(Guid siteId)
    {
        lock (_gate)
        {
            return _sites.TryGetValue(siteId, out var addresses) ? [.. addresses] : [];
        }
    }

    /// <summary>The sites with set-up cameras.</summary>
    public IReadOnlyList<Guid> Sites()
    {
        lock (_gate)
        {
            return [.. _sites.Keys];
        }
    }

    /// <summary>A removed site: its addresses are forgotten.</summary>
    public void RemoveSite(Guid siteId)
    {
        lock (_gate)
        {
            if (!_sites.Remove(siteId))
            {
                return;
            }
        }

        MarkDirty();
    }

    /// <summary>Stores new settings at once.</summary>
    public async Task SaveConfigAsync(EstConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _settings.SetAsync(ConfigKey, EstJson.Serialize(config), ct).ConfigureAwait(false);
        lock (_gate)
        {
            _config = config;
            while (_enrollments.Count > config.EnrollmentsKept)
            {
                _enrollments.RemoveLast();
            }
        }

        MarkDirty();
    }

    /// <summary>Deletes every enrollment (Delete all on the page).</summary>
    public void ClearEnrollments()
    {
        lock (_gate)
        {
            _enrollments.Clear();
        }

        MarkDirty();
    }

    public void AddEnrollment(Enrollment enrollment)
    {
        lock (_gate)
        {
            _enrollments.AddFirst(enrollment);
            while (_enrollments.Count > _config.EnrollmentsKept)
            {
                _enrollments.RemoveLast();
            }
        }

        MarkDirty();
    }

    public IReadOnlyList<Enrollment> Enrollments()
    {
        lock (_gate)
        {
            return [.. _enrollments];
        }
    }

    public void AddCertificate(IssuedCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        certificate = certificate with { SerialNumber = certificate.SerialNumber.ToUpperInvariant() }; // one record key per certificate
        lock (_gate)
        {
            _certificates[certificate.SerialNumber] = certificate;
            _changedCertificates.Add(certificate.SerialNumber);
            Index(certificate);
        }

        MarkDirty();
    }

    /// <summary>
    /// Keeps per device and profile the newest certificate and the one before it: a renewal or a new set-up pushes out the
    /// older ones. Under the lock, after adding it.
    /// </summary>
    private void Index(IssuedCertificate certificate)
    {
        var key = new DeviceProfile(certificate.DeviceId, certificate.Profile.ToLowerInvariant());
        if (_newest.TryGetValue(key, out var serial) && serial != certificate.SerialNumber && _certificates.TryGetValue(serial, out var newest))
        {
            if (newest.IssuedUtc > certificate.IssuedUtc)
            {
                Drop(certificate.SerialNumber); // older than the newest: never kept
                return;
            }

            if (_previous.TryGetValue(key, out var before) && before != certificate.SerialNumber)
            {
                Drop(before);
            }

            _previous[key] = serial;
        }

        _newest[key] = certificate.SerialNumber;
        if (!_profilesOf.TryGetValue(key.DeviceId, out var profiles))
        {
            _profilesOf[key.DeviceId] = profiles = new HashSet<string>(StringComparer.Ordinal);
        }

        profiles.Add(key.Profile);
    }

    /// <summary>The keys of a device's profiles. Under the lock.</summary>
    private IEnumerable<DeviceProfile> KeysOf(Guid deviceId) =>
        _profilesOf.TryGetValue(deviceId, out var profiles) ? profiles.Select(p => new DeviceProfile(deviceId, p)) : [];

    /// <summary>
    /// 4.1.0-rc.2 kept revoked certificates (<c>revokedUtc</c>; Revoke is gone): they count as no longer renewed, never
    /// as Not renewed.
    /// </summary>
    private static IEnumerable<IssuedCertificate> ReadLegacy(string json)
    {
        System.Text.Json.Nodes.JsonArray? list;
        try
        {
            list = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        foreach (var node in list ?? [])
        {
            if (Read<IssuedCertificate>(node?.ToJsonString()) is not { } certificate)
            {
                continue;
            }

            var revoked = node?["revokedUtc"]?.GetValue<DateTime?>();
            yield return revoked is { } at && certificate.RenewalStoppedUtc is null ? certificate with { RenewalStoppedUtc = at } : certificate;
        }
    }

    /// <summary>Removes a certificate and remembers to delete its record. Under the lock.</summary>
    private void Drop(string serialNumber)
    {
        _certificates.Remove(serialNumber);
        _changedCertificates.Add(serialNumber);
    }

    /// <summary>The newest certificate of every device and profile. Under the lock.</summary>
    private IEnumerable<IssuedCertificate> Newest() =>
        _newest.Values.Select(serial => _certificates.GetValueOrDefault(serial)).OfType<IssuedCertificate>();

    /// <summary>The certificates of a device OADM no longer manages are forgotten.</summary>
    public void ForgetDevice(Guid deviceId)
    {
        lock (_gate)
        {
            var keys = KeysOf(deviceId).ToList();
            if (keys.Count == 0)
            {
                return;
            }

            foreach (var key in keys)
            {
                if (_newest.Remove(key, out var newest))
                {
                    Drop(newest);
                }

                if (_previous.Remove(key, out var previous))
                {
                    Drop(previous);
                }
            }

            _profilesOf.Remove(deviceId);
        }

        MarkDirty();
    }

    /// <summary>
    /// The certificates the camera should have renewed by now and not yet flagged (renewal not stopped): Axis cameras renew
    /// halfway through the lifetime and try again daily, so half the lifetime plus <paramref name="grace"/> (a tenth of the
    /// lifetime for short ones).
    /// </summary>
    public IReadOnlyList<IssuedCertificate> DueForRenewal(DateTime nowUtc, TimeSpan grace)
    {
        lock (_gate)
        {
            return [.. Newest().Where(c => c is { NotRenewedUtc: null, RenewalStoppedUtc: null } && nowUtc >= RenewalDue(c, grace))];
        }
    }

    /// <summary>When the certificate counts as not renewed (see <see cref="DueForRenewal"/>).</summary>
    public static DateTime RenewalDue(IssuedCertificate certificate, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var lifetime = certificate.NotAfterUtc - certificate.IssuedUtc;
        return certificate.IssuedUtc + (lifetime / 2) + TimeSpan.FromTicks(Math.Min(grace.Ticks, lifetime.Ticks / 10));
    }

    /// <summary>The camera did not renew the certificate in time.</summary>
    public void MarkNotRenewed(string serialNumber, DateTime nowUtc) => Change(serialNumber, c => c with { NotRenewedUtc = nowUtc });

    /// <summary>
    /// No renewal is expected any more for the device's newest certificates (its enrollment profile was removed); null
    /// <paramref name="profile"/> = every profile.
    /// </summary>
    public void StopRenewals(Guid deviceId, DateTime nowUtc, string? profile = null)
    {
        var changed = false;
        lock (_gate)
        {
            var keys = profile is null ? KeysOf(deviceId) : [new DeviceProfile(deviceId, profile.ToLowerInvariant())];
            foreach (var key in keys)
            {
                if (_newest.TryGetValue(key, out var serial)
                    && _certificates.TryGetValue(serial, out var certificate) && certificate.RenewalStoppedUtc is null)
                {
                    _certificates[serial] = certificate with { RenewalStoppedUtc = nowUtc };
                    _changedCertificates.Add(serial);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            MarkDirty();
        }
    }

    private void Change(string serialNumber, Func<IssuedCertificate, IssuedCertificate> change)
    {
        lock (_gate)
        {
            if (!_certificates.TryGetValue(serialNumber, out var certificate))
            {
                return;
            }

            _certificates[serialNumber] = change(certificate);
            _changedCertificates.Add(serialNumber);
        }

        MarkDirty();
    }

    public IssuedCertificate? FindCertificate(string serialNumber)
    {
        lock (_gate)
        {
            return _certificates.GetValueOrDefault(serialNumber);
        }
    }

    public int CertificateCount
    {
        get
        {
            lock (_gate)
            {
                return _certificates.Count;
            }
        }
    }

    /// <summary>The newest certificate of every device and profile, newest first.</summary>
    public IReadOnlyList<IssuedCertificate> Current()
    {
        lock (_gate)
        {
            return [.. Newest().OrderByDescending(c => c.IssuedUtc)];
        }
    }

    /// <summary>The certificates the cameras did not renew in time, the ones that end first first; at most <paramref name="max"/>.</summary>
    public CertificateList NotRenewed(int max)
    {
        List<IssuedCertificate> matching;
        lock (_gate)
        {
            matching = [.. Newest().Where(c => c.NotRenewed)];
        }

        matching.Sort((a, b) => a.NotAfterUtc.CompareTo(b.NotAfterUtc));
        return new CertificateList([.. matching.Take(max)], matching.Count);
    }

    /// <summary>Writes pending changes now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Enrollment> enrollments;
            List<KeyValuePair<string, string?>> certificates;
            Dictionary<Guid, List<string>> sites;
            bool dropLegacy;
            lock (_gate)
            {
                _flushPending = false;
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                var cutoff = _time.GetUtcNow().UtcDateTime - KeepEnded;
                foreach (var ended in _certificates.Values.Where(c => c.NotAfterUtc < cutoff).Select(c => c.SerialNumber).ToList())
                {
                    Drop(ended);
                }

                foreach (var gone in _newest.Where(n => !_certificates.ContainsKey(n.Value)).Select(n => n.Key).ToList())
                {
                    _newest.Remove(gone);
                    if (_profilesOf.TryGetValue(gone.DeviceId, out var profiles) && profiles.Remove(gone.Profile) && profiles.Count == 0)
                    {
                        _profilesOf.Remove(gone.DeviceId);
                    }
                }

                foreach (var gone in _previous.Where(n => !_certificates.ContainsKey(n.Value)).Select(n => n.Key).ToList())
                {
                    _previous.Remove(gone);
                }

                enrollments = [.. _enrollments];
                certificates = [.. _changedCertificates.Select(serial => new KeyValuePair<string, string?>(
                    serial, _certificates.TryGetValue(serial, out var certificate) ? EstJson.Serialize(certificate) : null))];
                _changedCertificates.Clear();
                sites = _sites.ToDictionary(s => s.Key, s => s.Value.ToList());
                dropLegacy = _dropLegacyCertificates;
            }

            try
            {
                await _settings.SetAsync(EnrollmentsKey, EstJson.Serialize(enrollments), ct).ConfigureAwait(false);
                await _records.SetManyAsync(CertificatesKey, certificates, ct).ConfigureAwait(false);
                await _settings.SetAsync(SitesKey, EstJson.Serialize(sites), ct).ConfigureAwait(false);
                if (dropLegacy)
                {
                    await _settings.SetAsync(CertificatesKey, null, ct).ConfigureAwait(false);
                    await _settings.SetAsync(LegacyRevokedKey, null, ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _dropLegacyCertificates = false;
                    }
                }
            }
            catch
            {
                lock (_gate)
                {
                    _changedCertificates.UnionWith(certificates.Select(c => c.Key)); // written again with the next flush
                }

                MarkDirty();
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer?.Dispose();
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_flushPending)
            {
                return; // a steady stream of changes is still written every FlushDelay
            }

            _flushPending = true;
            _flushTimer ??= _time.CreateTimer(_ => _ = FlushFromTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushFromTimerAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // disposed meanwhile: DisposeAsync wrote it
        }
#pragma warning disable CA1031 // A failed write (database locked, disk full) is logged and tried again with the next flush.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlushFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "EST: saving the enrollments and certificates failed; tried again in a few seconds")]
    private partial void LogFlushFailed(Exception ex);

    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, EstJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
