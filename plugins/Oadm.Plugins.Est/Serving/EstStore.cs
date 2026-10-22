using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The plugin settings of the EST server: <c>config</c>, <c>enrollments</c> (newest first, <see cref="EstConfig.EnrollmentsKept"/>),
/// <c>certificates</c> (what EST issued; ended more than 30 days ago dropped) and <c>revoked</c> (device and profile pairs
/// that get nothing until the camera is set up again). Kept in memory; changes are written at most every
/// <see cref="FlushDelay"/> and on dispose. Thread-safe.
/// </summary>
public sealed class EstStore : IAsyncDisposable
{
    public const string ConfigKey = "config";
    public const string EnrollmentsKey = "enrollments";
    public const string CertificatesKey = "certificates";
    public const string RevokedKey = "revoked";
    public const string SitesKey = "sites";

    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(30);

    private readonly IPluginSettings _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly LinkedList<Enrollment> _enrollments = new();
    private readonly Dictionary<string, IssuedCertificate> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<RevokedPair> _revoked = [];
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
        var revoked = Read<List<RevokedPair>>(await _settings.GetAsync(RevokedKey, ct).ConfigureAwait(false)) ?? [];
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
            foreach (var certificate in certificates)
            {
                _certificates[certificate.SerialNumber] = certificate;
            }

            _revoked.Clear();
            _revoked.UnionWith(revoked);
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
        lock (_gate)
        {
            _certificates[certificate.SerialNumber] = certificate;
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

    /// <summary>The certificates, newest first, whose device or profile contains <paramref name="search"/>; one page.</summary>
    public CertificateList Certificates(string? search, int offset, int pageSize)
    {
        List<IssuedCertificate> matching;
        lock (_gate)
        {
            matching = [.. _certificates.Values.Where(c => string.IsNullOrWhiteSpace(search)
                || c.Device.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || c.Profile.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))];
        }

        matching.Sort((a, b) => b.IssuedUtc.CompareTo(a.IssuedUtc));
        return new CertificateList([.. matching.Skip(Math.Max(0, offset)).Take(pageSize)], matching.Count);
    }

    /// <summary>Revokes certificates: the device and profile get nothing more until the camera is set up again. Returns how many.</summary>
    public int Revoke(IEnumerable<string> serialNumbers)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var count = 0;
        lock (_gate)
        {
            foreach (var serial in serialNumbers)
            {
                if (_certificates.TryGetValue(serial, out var certificate) && !certificate.Revoked)
                {
                    _certificates[serial] = certificate with { RevokedUtc = now };
                    _revoked.Add(new RevokedPair(certificate.DeviceId, certificate.Profile.ToLowerInvariant()));
                    count++;
                }
            }
        }

        if (count > 0)
        {
            MarkDirty();
        }

        return count;
    }

    public bool IsRevoked(Guid deviceId, string profile)
    {
        lock (_gate)
        {
            return _revoked.Contains(new RevokedPair(deviceId, profile.ToLowerInvariant()));
        }
    }

    /// <summary>The camera was set up again: its profiles may enroll again.</summary>
    public void ClearRevoked(Guid deviceId)
    {
        lock (_gate)
        {
            if (_revoked.RemoveWhere(p => p.DeviceId == deviceId) == 0)
            {
                return;
            }
        }

        MarkDirty();
    }

    /// <summary>Writes pending changes now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Enrollment> enrollments;
            List<IssuedCertificate> certificates;
            List<RevokedPair> revoked;
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

                enrollments = [.. _enrollments];
                certificates = [.. _certificates.Values];
                revoked = [.. _revoked];
                sites = _sites.ToDictionary(s => s.Key, s => s.Value.ToList());
            }

            try
            {
                await _settings.SetAsync(EnrollmentsKey, EstJson.Serialize(enrollments), ct).ConfigureAwait(false);
                await _settings.SetAsync(CertificatesKey, EstJson.Serialize(certificates), ct).ConfigureAwait(false);
                await _settings.SetAsync(RevokedKey, EstJson.Serialize(revoked), ct).ConfigureAwait(false);
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
