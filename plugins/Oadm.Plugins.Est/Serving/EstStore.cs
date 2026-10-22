using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The plugin settings of the EST server: <c>config</c>, <c>enrollments</c> (newest first, <see cref="EstConfig.EnrollmentsKept"/>),
/// <c>certificates</c> (per device and profile the newest certificate EST issued and the one before it, which a camera
/// whose answer got lost renews with; ended more than 30 days ago or of a device OADM no longer manages dropped: at most
/// 2 x devices x profiles) and <c>sites</c>. Kept in memory; changes
/// are written at most every
/// <see cref="FlushDelay"/> and on dispose. Thread-safe.
/// </summary>
public sealed class EstStore : IAsyncDisposable
{
    public const string ConfigKey = "config";
    public const string EnrollmentsKey = "enrollments";
    public const string CertificatesKey = "certificates";
    public const string SitesKey = "sites";

    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(30);

    private readonly IPluginSettings _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly LinkedList<Enrollment> _enrollments = new();
    private readonly Dictionary<string, IssuedCertificate> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DeviceProfile, string> _newest = []; // device and profile -> serial of its newest certificate
    private readonly Dictionary<DeviceProfile, string> _previous = []; // ... and of the one before it
    private readonly Dictionary<Guid, SortedSet<string>> _sites = [];
    private EstConfig _config = new();
    private bool _dirty;
    private bool _flushPending;
    private ITimer? _flushTimer;

    public EstStore(IPluginSettings settings, TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

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
        var certificates = Read<List<IssuedCertificate>>(await _settings.GetAsync(CertificatesKey, ct).ConfigureAwait(false)) ?? [];
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
            foreach (var certificate in certificates.OrderBy(c => c.IssuedUtc)) // 4.1.0-rc.2 kept every certificate
            {
                _certificates[certificate.SerialNumber] = certificate;
                Index(certificate);
            }

            _sites.Clear();
            foreach (var (site, addresses) in sites)
            {
                _sites[site] = new SortedSet<string>(addresses, StringComparer.Ordinal);
            }
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
        lock (_gate)
        {
            _certificates[certificate.SerialNumber] = certificate;
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
                _certificates.Remove(certificate.SerialNumber); // older than the newest: never kept
                return;
            }

            if (_previous.TryGetValue(key, out var before) && before != certificate.SerialNumber)
            {
                _certificates.Remove(before);
            }

            _previous[key] = serial;
        }

        _newest[key] = certificate.SerialNumber;
    }

    /// <summary>The newest certificate of every device and profile. Under the lock.</summary>
    private IEnumerable<IssuedCertificate> Newest() =>
        _newest.Values.Select(serial => _certificates.GetValueOrDefault(serial)).OfType<IssuedCertificate>();

    /// <summary>The certificates of a device OADM no longer manages are forgotten.</summary>
    public void ForgetDevice(Guid deviceId)
    {
        lock (_gate)
        {
            var gone = _newest.Concat(_previous).Where(n => n.Key.DeviceId == deviceId).ToList();
            if (gone.Count == 0)
            {
                return;
            }

            foreach (var (key, serial) in gone)
            {
                _newest.Remove(key);
                _previous.Remove(key);
                _certificates.Remove(serial);
            }
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

    /// <summary>No renewal is expected any more for the device's newest certificates (enrollment removed, device removed).</summary>
    public void StopRenewals(Guid deviceId, DateTime nowUtc)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var (key, serial) in _newest)
            {
                if (key.DeviceId == deviceId && _certificates.TryGetValue(serial, out var certificate) && certificate.RenewalStoppedUtc is null)
                {
                    _certificates[serial] = certificate with { RenewalStoppedUtc = nowUtc };
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
            List<IssuedCertificate> certificates;
            Dictionary<Guid, List<string>> sites;
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
                    _certificates.Remove(ended);
                }

                foreach (var gone in _newest.Where(n => !_certificates.ContainsKey(n.Value)).Select(n => n.Key).ToList())
                {
                    _newest.Remove(gone);
                }

                foreach (var gone in _previous.Where(n => !_certificates.ContainsKey(n.Value)).Select(n => n.Key).ToList())
                {
                    _previous.Remove(gone);
                }

                enrollments = [.. _enrollments];
                certificates = [.. _certificates.Values];
                sites = _sites.ToDictionary(s => s.Key, s => s.Value.ToList());
            }

            try
            {
                await _settings.SetAsync(EnrollmentsKey, EstJson.Serialize(enrollments), ct).ConfigureAwait(false);
                await _settings.SetAsync(CertificatesKey, EstJson.Serialize(certificates), ct).ConfigureAwait(false);
                await _settings.SetAsync(SitesKey, EstJson.Serialize(sites), ct).ConfigureAwait(false);
            }
            catch
            {
                MarkDirty(); // written again with the next flush
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
    }

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
