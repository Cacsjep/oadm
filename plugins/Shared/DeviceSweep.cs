using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Shared;

/// <summary>Timing and limits of <see cref="DeviceSweep{TRow}"/>; tests shorten them.</summary>
internal sealed class DeviceSweepOptions
{
    /// <summary>Devices read at the same time (24; 200 cameras = about 9 rounds).</summary>
    public int Parallelism { get; init; } = 24;

    /// <summary>Time for the reads of one device.</summary>
    public TimeSpan DeviceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Changed rows and the progress are pushed at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The state of a sweep taken under one lock: progress and every row (unsorted).</summary>
internal sealed record SweepSnapshot<TRow>(bool Running, int Done, int Total, DateTimeOffset? FinishedUtc, IReadOnlyList<TRow> Rows);

/// <summary>
/// One table row per device, read only on request (no background polling, no open connection): <see cref="Start"/> reads
/// every device the plugin wants (<c>include</c>) once, <see cref="DeviceSweepOptions.Parallelism"/> at a time, each within
/// <see cref="DeviceSweepOptions.DeviceTimeout"/>. The plugin's <c>read</c> turns a device (and its previous row) into
/// the new row, or null when the device leaves the table. Changed rows, removed devices and the progress are handed to the
/// plugin's publish callbacks every <see cref="DeviceSweepOptions.PublishInterval"/> (the page events), one batch at a time
/// and in order. A read that started earlier never replaces the row of a read that started later (a sweep read and
/// <see cref="ReadOneAsync"/> after a task). Shared by the table pages (Image Health Dashboard, Edge storage) as linked source.
/// </summary>
internal sealed partial class DeviceSweep<TRow> : IAsyncDisposable
    where TRow : class
{
    private readonly ICorePluginContext _ctx;
    private readonly DeviceSweepOptions _options;
    private readonly Func<IDeviceInfo, bool> _include;
    private readonly Func<IDeviceInfo, TRow?, CancellationToken, Task<TRow?>> _read;
    private readonly Func<TRow, string> _address;
    private readonly Action<IReadOnlyList<TRow>, IReadOnlyList<Guid>> _publishRows;
    private readonly Action _publishState;
    private readonly Lock _sync = new();
    private readonly Lock _publishing = new();
    private readonly Dictionary<Guid, TRow> _rows = [];
    private readonly Dictionary<Guid, long> _rowRead = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly HashSet<Guid> _dirtyRemoved = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _publishLoop;
    private Task _sweep = Task.CompletedTask;
    private long _reads;
    private bool _running;
    private HashSet<Guid>? _current;
    private readonly HashSet<Guid> _pending = [];
    private bool _pendingAll;
    private int _done;
    private int _total;
    private DateTimeOffset? _finishedUtc;
    private bool _stateDirty;

    public DeviceSweep(
        ICorePluginContext ctx,
        DeviceSweepOptions options,
        Func<IDeviceInfo, bool> include,
        Func<IDeviceInfo, TRow?, CancellationToken, Task<TRow?>> read,
        Func<TRow, string> address,
        Action<IReadOnlyList<TRow>, IReadOnlyList<Guid>> publishRows,
        Action publishState)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _include = include;
        _read = read;
        _address = address;
        _publishRows = publishRows;
        _publishState = publishState;
        var token = _stopping.Token;
        _publishLoop = Task.Run(() => PublishLoopAsync(token), CancellationToken.None);
    }

    /// <summary>Called after every sweep that read all its devices (the plugin's summary log).</summary>
    public Action? Finished { get; set; }

    /// <summary>Whether a sweep runs.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    /// <summary>The running sweep (tests wait for it).</summary>
    public Task Current
    {
        get
        {
            lock (_sync)
            {
                return _sweep;
            }
        }
    }

    /// <summary>Progress and every row in one consistent state (rows unsorted; <see cref="Sorted"/> sorts them).</summary>
    public SweepSnapshot<TRow> Snapshot()
    {
        lock (_sync)
        {
            return new SweepSnapshot<TRow>(_running, _done, _total, _finishedUtc, [.. _rows.Values]);
        }
    }

    /// <summary>Rows sorted by address (IPv4 numerically).</summary>
    public IReadOnlyList<TRow> Sorted(IEnumerable<TRow> rows) => [.. rows.OrderBy(_address, AddressComparer.Instance)];

    /// <summary>Every row, sorted by address.</summary>
    public IReadOnlyList<TRow> Rows => Sorted(Snapshot().Rows);

    /// <summary>The rows matching a condition (tests).</summary>
    public int Count(Func<TRow, bool> predicate) => Snapshot().Rows.Count(predicate);

    /// <summary>
    /// Starts a sweep; <paramref name="only"/> limits it to these devices (null = all). While one runs, the devices it does
    /// not cover are read right after it (pages share the sweep and may choose other devices), never dropped.
    /// </summary>
    public void Start(IReadOnlyCollection<Guid>? only = null)
    {
        lock (_sync)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            if (_running)
            {
                if (_current is null)
                {
                    return; // the running sweep reads every device
                }

                if (only is null)
                {
                    _pendingAll = true;
                }
                else
                {
                    _pending.UnionWith(only.Where(id => !_current.Contains(id)));
                }

                return;
            }

            StartLocked(only);
        }
    }

    private void StartLocked(IReadOnlyCollection<Guid>? only)
    {
        _running = true;
        _current = only is null ? null : [.. only];
        _done = 0;
        _stateDirty = true;
        var token = _stopping.Token;
        _sweep = Task.Run(() => SweepAsync(only, token), CancellationToken.None);
    }

    /// <summary>Reads one device now, outside a sweep (e.g. after a task changed it).</summary>
    public async Task ReadOneAsync(Guid deviceId, CancellationToken ct)
    {
        if (await _ctx.Devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { } device && _include(device))
        {
            await ReadIntoRowAsync(device, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(Current, _publishLoop).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Stopping never fails: the sweep logs its own errors.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stopping.Dispose();
    }

    /// <summary>Pushes the rows changed since the last call and the progress when it changed, one batch at a time (tests call it directly).</summary>
    public void Publish()
    {
        lock (_publishing)
        {
            List<TRow> rows;
            List<Guid> removed;
            bool state;
            lock (_sync)
            {
                rows = [.. _dirty.Where(_rows.ContainsKey).Select(id => _rows[id])];
                _dirty.Clear();
                removed = [.. _dirtyRemoved];
                _dirtyRemoved.Clear();
                state = _stateDirty;
                _stateDirty = false;
            }

            if (rows.Count > 0 || removed.Count > 0)
            {
                _publishRows(rows, removed);
            }

            if (state)
            {
                _publishState();
            }
        }
    }

    /// <summary>
    /// The usual text when a device cannot be read, for the plugin's read: a cancellation inside a read is the device
    /// timeout (a stopping plugin drops its rows anyway), the network error, or the exception's message.
    /// </summary>
    public string Failure(Exception ex) => ex switch
    {
        OperationCanceledException => DeviceMessages.Timeout(_options.DeviceTimeout),
        HttpRequestException { InnerException: SocketException socket } => DeviceMessages.Unreachable(socket.Message),
        HttpRequestException http => DeviceMessages.Unreachable(http.Message),
        _ => ex.Message,
    };

    private async Task SweepAsync(IReadOnlyCollection<Guid>? only, CancellationToken ct)
    {
        var completed = false;
        try
        {
            var devices = (await _ctx.Devices.ListAsync(ct).ConfigureAwait(false)).Where(_include).ToList();
            var ids = devices.Select(d => d.Id).ToHashSet();
            lock (_sync)
            {
                foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    Remove(gone); // removed from OADM or no longer included
                }

                if (only is not null)
                {
                    devices = [.. devices.Where(d => only.Contains(d.Id))];
                }

                _total = devices.Count;
                _stateDirty = true;
            }

            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct },
                async (device, token) =>
                {
                    await ReadIntoRowAsync(device, token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _done++;
                        _stateDirty = true;
                    }
                }).ConfigureAwait(false);

            lock (_sync)
            {
                _finishedUtc = _options.Time.GetUtcNow();
            }

            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A failed sweep must not stop the plugin; the page can read again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSweepFailed(_ctx.Logger, ex);
        }
        finally
        {
            lock (_sync)
            {
                _running = false;
                _stateDirty = true;
                if (!_stopping.IsCancellationRequested && (_pendingAll || _pending.Count > 0))
                {
                    // Devices other pages asked for while this sweep ran: read them now (still "reading" for the pages).
                    IReadOnlyCollection<Guid>? next = _pendingAll ? null : [.. _pending];
                    _pendingAll = false;
                    _pending.Clear();
                    StartLocked(next);
                }
            }

            Publish();
        }

        if (completed)
        {
            Finished?.Invoke();
        }
    }

    private async Task ReadIntoRowAsync(IDeviceInfo device, CancellationToken ct)
    {
        TRow? previous;
        long read;
        lock (_sync)
        {
            previous = _rows.GetValueOrDefault(device.Id);
            read = ++_reads;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.DeviceTimeout);
        var row = await _read(device, previous, timeout.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_rowRead.TryGetValue(device.Id, out var newer) && newer > read)
            {
                return; // a read that started later already set this row
            }

            _rowRead[device.Id] = read;
            if (row is null)
            {
                if (_rows.ContainsKey(device.Id))
                {
                    Remove(device.Id);
                }
            }
            else
            {
                _rows[device.Id] = row;
                _dirty.Add(device.Id);
                _dirtyRemoved.Remove(device.Id);
            }

            _stateDirty = true;
        }
    }

    private void Remove(Guid id)
    {
        _rows.Remove(id);
        _dirty.Remove(id);
        _dirtyRemoved.Add(id);
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PublishInterval, _options.Time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Publish();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the devices of the page failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

/// <summary>Sorts IPv4 addresses numerically, everything else after them by text.</summary>
internal sealed class AddressComparer : IComparer<string>
{
    public static readonly AddressComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Key(x);
        var b = Key(y);
        var byNumber = a.CompareTo(b);
        return byNumber != 0 ? byNumber : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static long Key(string? address)
    {
        var host = address ?? string.Empty;
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && host.IndexOf(':', colon + 1) < 0)
        {
            host = host[..colon];
        }

        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        }

        return long.MaxValue;
    }
}
