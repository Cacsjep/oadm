using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The log of the 802.1X server (docs/spec/radius-server.md "Log"): every ended session, newest first, at most "Log entries
/// kept". In memory for the page (search is one pass) and in the plugin records, one row each (collection <c>log</c>, key =
/// the entry's id); the client certificates once each by fingerprint (<c>logCertificates</c>), read one at a time for View
/// certificate and deleted with their last row. Only new and removed rows are written, batched every
/// <see cref="FlushDelay"/>. Thread-safe.
/// </summary>
public sealed partial class RadiusLog : IAsyncDisposable
{
    public const string LogCollection = "log";
    public const string CertificatesCollection = "logCertificates";

    private readonly IPluginRecords _records;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly LinkedList<Entry> _entries = new(); // newest first
    private readonly Dictionary<long, LinkedListNode<Entry>> _byId = [];
    private readonly Dictionary<string, int> _certificateUses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _rowChanges = new(StringComparer.Ordinal); // id -> row JSON, null = delete
    private readonly Dictionary<string, string?> _certificateChanges = new(StringComparer.Ordinal); // fingerprint -> record, null = delete
    private bool _clear;
    private long _generation; // + 1 with every Delete all: a write that failed before it never brings its rows back
    private long _nextId = 1;
    private int _kept = RadiusLimits.DefaultLogKept;
    private ITimer? _flushTimer;
    private bool _flushPending;
    private bool _stopped;
    private readonly ILogger _logger;

    public RadiusLog(IPluginRecords? records, TimeProvider? time = null, ILogger? logger = null)
    {
        _records = records ?? new MemoryPluginRecords();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Reads the kept rows; rows beyond <paramref name="kept"/>, unreadable ones and certificates no row points to (left by a
    /// stop between two writes) are deleted with the next flush.
    /// </summary>
    public async Task LoadAsync(int kept, CancellationToken ct)
    {
        var rows = await _records.ListAsync(LogCollection, ct).ConfigureAwait(false);
        var certificates = await _records.ListAsync(CertificatesCollection, ct).ConfigureAwait(false);
        var loaded = new List<(string Key, StoredRow Row)>(rows.Count);
        var drop = new List<string>();
        foreach (var (key, json) in rows)
        {
            if (RadiusStore.Read<StoredRow>(json) is { } row && row.Row.Id > 0)
            {
                loaded.Add((key, row));
            }
            else
            {
                drop.Add(key);
            }
        }

        lock (_gate)
        {
            _kept = kept;
            _entries.Clear();
            _byId.Clear();
            _certificateUses.Clear();
            foreach (var (_, row) in loaded.OrderByDescending(r => r.Row.Row.Id))
            {
                _nextId = Math.Max(_nextId, row.Row.Id + 1);
                var node = _entries.AddLast(new Entry(row.Row, row.Fingerprint, Searchable(row.Row)));
                _byId[row.Row.Id] = node;
                if (row.Fingerprint is { } fingerprint)
                {
                    _certificateUses[fingerprint] = _certificateUses.GetValueOrDefault(fingerprint) + 1;
                }
            }

            foreach (var key in drop)
            {
                _rowChanges[key] = null;
            }

            Trim();
            foreach (var fingerprint in certificates.Keys)
            {
                if (!_certificateUses.ContainsKey(fingerprint))
                {
                    _certificateChanges[fingerprint] = null;
                }
            }
        }

        if (drop.Count > 0 || Count < loaded.Count || HasChanges())
        {
            MarkDirty();
        }
    }

    private bool HasChanges()
    {
        lock (_gate)
        {
            return _rowChanges.Count > 0 || _certificateChanges.Count > 0;
        }
    }

    /// <summary>"Log entries kept" changed: the oldest rows beyond it go.</summary>
    public void SetKept(int kept)
    {
        lock (_gate)
        {
            _kept = kept;
            Trim();
        }

        MarkDirty();
    }

    /// <summary>Adds an entry (its id is set here) with the client certificate (DER) of the session; returns the stored row.</summary>
    public LogRow Add(LogRow row, byte[]? certificate)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? fingerprint = certificate is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData(certificate)) : null;
        LogRow stored;
        lock (_gate)
        {
            stored = row with { Id = _nextId++, HasCertificate = fingerprint is not null };
            var node = _entries.AddFirst(new Entry(stored, fingerprint, Searchable(stored)));
            _byId[stored.Id] = node;
            _rowChanges[KeyOf(stored.Id)] = RadiusJson.Serialize(new StoredRow(stored, fingerprint));
            if (fingerprint is not null)
            {
                var uses = _certificateUses.GetValueOrDefault(fingerprint);
                _certificateUses[fingerprint] = uses + 1;
                if (uses == 0)
                {
                    _certificateChanges[fingerprint] = RadiusJson.Serialize(new StoredCertificate(Convert.ToBase64String(certificate!)));
                }
            }

            Trim();
        }

        MarkDirty();
        return stored;
    }

    /// <summary>
    /// One page of the rows <paramref name="include"/> keeps (null: every row), newest first, matching
    /// <paramref name="search"/> in any column (case-insensitive), the time also as the page shows it in
    /// <paramref name="timeZone"/>. One pass over the rows.
    /// </summary>
    public LogPage Page(string? search, int offset, int limit, Func<Guid?, bool>? include, TimeZoneInfo? timeZone = null)
    {
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var local = text is not null && timeZone is not null && !timeZone.Equals(TimeZoneInfo.Utc) && text.Any(char.IsAsciiDigit) ? timeZone : null;
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, RadiusLimits.MaxLogPage);
        var rows = new List<LogRow>(Math.Min(limit, 64));
        var total = 0;
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if ((include is not null && !include(entry.Row.DeviceId))
                    || (text is not null && !entry.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                        && (local is null || !LocalTime(entry.Row.TimeUtc, local).Contains(text, StringComparison.Ordinal))))
                {
                    continue;
                }

                if (total >= offset && rows.Count < limit)
                {
                    rows.Add(entry.Row);
                }

                total++;
            }
        }

        return new LogPage(total, rows);
    }

    /// <summary>The serial numbers of the client certificates in the kept rows (one pass).</summary>
    public IReadOnlySet<string> CertificateSerials()
    {
        lock (_gate)
        {
            var serials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _entries)
            {
                if (entry.Row.CertificateSerial is { Length: > 0 } serial)
                {
                    serials.Add(serial);
                }
            }

            return serials;
        }
    }

    /// <summary>The row of <paramref name="id"/>, null when it is no longer kept.</summary>
    public LogRow? Find(long id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var node) ? node.Value.Row : null;
        }
    }

    /// <summary>The client certificate (DER) of row <paramref name="id"/>; null when the row or its certificate is gone.</summary>
    public async Task<byte[]?> CertificateAsync(long id, CancellationToken ct)
    {
        string? fingerprint;
        string? pending;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var node) || node.Value.Fingerprint is null)
            {
                return null;
            }

            fingerprint = node.Value.Fingerprint;
            _certificateChanges.TryGetValue(fingerprint, out pending);
        }

        var json = pending ?? await _records.GetAsync(CertificatesCollection, fingerprint, ct).ConfigureAwait(false);
        return RadiusStore.Read<StoredCertificate>(json) is { } stored ? Convert.FromBase64String(stored.Der) : null;
    }

    /// <summary>Delete all.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _byId.Clear();
            _certificateUses.Clear();
            _rowChanges.Clear();
            _certificateChanges.Clear();
            _clear = true;
            _generation++;
        }

        MarkDirty();
    }

    /// <summary>Writes pending changes now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool clear;
            long generation;
            List<KeyValuePair<string, string?>> rows;
            List<KeyValuePair<string, string?>> certificates;
            lock (_gate)
            {
                _flushPending = false;
                generation = _generation;
                clear = _clear;
                _clear = false;
                rows = [.. _rowChanges];
                _rowChanges.Clear();
                certificates = [.. _certificateChanges];
                _certificateChanges.Clear();
            }

            if (!clear && rows.Count == 0 && certificates.Count == 0)
            {
                return;
            }

            try
            {
                if (clear)
                {
                    await _records.ClearAsync(LogCollection, ct).ConfigureAwait(false);
                    await _records.ClearAsync(CertificatesCollection, ct).ConfigureAwait(false);
                    clear = false;
                }

                // Certificates first: a row never points to a certificate that is not written yet.
                await _records.SetManyAsync(CertificatesCollection, certificates, ct).ConfigureAwait(false);
                certificates = [];
                await _records.SetManyAsync(LogCollection, rows, ct).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _clear |= clear;
                    if (_generation == generation)
                    {
                        // Written again with the next flush; after a Delete all meanwhile they are gone with everything else.
                        foreach (var (key, value) in rows)
                        {
                            _rowChanges.TryAdd(key, value); // a newer change of the same key wins
                        }

                        foreach (var (key, value) in certificates)
                        {
                            _certificateChanges.TryAdd(key, value);
                        }
                    }
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
        lock (_gate)
        {
            _stopped = true;
        }

        if (_flushTimer is not null)
        {
            await _flushTimer.DisposeAsync().ConfigureAwait(false);
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _writeGate.Dispose();
    }

    /// <summary>Drops the oldest rows beyond the limit. Under the lock.</summary>
    private void Trim()
    {
        while (_entries.Count > _kept)
        {
            var oldest = _entries.Last!.Value;
            _entries.RemoveLast();
            _byId.Remove(oldest.Row.Id);
            var key = KeyOf(oldest.Row.Id);
            if (_rowChanges.TryGetValue(key, out var pending) && pending is not null)
            {
                _rowChanges.Remove(key); // never written: nothing to delete
            }
            else
            {
                _rowChanges[key] = null;
            }

            if (oldest.Fingerprint is { } fingerprint)
            {
                var uses = _certificateUses.GetValueOrDefault(fingerprint) - 1;
                if (uses > 0)
                {
                    _certificateUses[fingerprint] = uses;
                }
                else
                {
                    _certificateUses.Remove(fingerprint);
                    _certificateChanges[fingerprint] = null;
                }
            }
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            if (_stopped || _flushPending)
            {
                return; // stopped: DisposeAsync writes the last time; pending: a steady stream is written every FlushDelay
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
            // stopped meanwhile: DisposeAsync wrote it
        }
#pragma warning disable CA1031 // A failed write (database locked, disk full) is logged and tried again with the next flush.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlushFailed(ex);
        }
    }

    private static string KeyOf(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The time as the page shows it ("yyyy-MM-dd HH:mm:ss" in the page's time zone).</summary>
    private static string LocalTime(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Every column of a row in one text for the search.</summary>
    private static string Searchable(LogRow row) => string.Join('\u001f',
        row.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), row.SwitchName, row.Port, row.Mac, row.Identity, row.DeviceName,
        row.IpAddress, row.Result == LogResults.Authenticated ? "Authenticated" : "Failed", row.Reason, row.Warning,
        row.Vlan?.ToString(CultureInfo.InvariantCulture), row.CertificateSerial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "802.1X server: saving the log failed; tried again in a few seconds")]
    private partial void LogFlushFailed(Exception ex);

    private sealed record Entry(LogRow Row, string? Fingerprint, string Text);

    /// <summary>A row of the records: the page's row and the fingerprint of its certificate.</summary>
    private sealed record StoredRow(LogRow Row, string? Fingerprint);

    private sealed record StoredCertificate(string Der);
}
