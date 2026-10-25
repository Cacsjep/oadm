using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Shared;

/// <summary>Timing and limits of <see cref="DeviceSweep{TRow}"/>; tests shorten them.</summary>
internal sealed class DeviceSweepOptions
{
    private readonly Lazy<SemaphoreSlim> _gate;

    public DeviceSweepOptions() => _gate = new(() => new SemaphoreSlim(Math.Max(1, Parallelism)));

    /// <summary>
    /// Devices read at the same time (24; 200 cameras = about 9 rounds), by every sweep made with these options together:
    /// a plugin keeps one options instance, so the sweeps of all device scopes share the limit (<see cref="Gate"/>).
    /// </summary>
    public int Parallelism { get; init; } = 24;

    /// <summary>The reads in flight of every sweep made with these options (<see cref="Parallelism"/> at most).</summary>
    public SemaphoreSlim Gate => _gate.Value;

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
/// The sweep is background work: it never carries the caller of the call that started it (its reads run detached), and it
/// reads the devices of its repository (the plugin's devices, or one device scope's: <see cref="DeviceSweeps{TRow}"/>).
/// </summary>
internal sealed partial class DeviceSweep<TRow> : IAsyncDisposable
    where TRow : class
{
    private readonly ICorePluginContext _ctx;
    private readonly IDeviceRepository _devices;
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
        : this(ctx, (ctx ?? throw new ArgumentNullException(nameof(ctx))).Devices, options, include, read, address, publishRows, publishState)
    {
    }

    /// <summary>A sweep of the devices of <paramref name="devices"/> (e.g. one device scope's, <c>ctx.DeviceScopes.Devices(key)</c>).</summary>
    public DeviceSweep(
        ICorePluginContext ctx,
        IDeviceRepository devices,
        DeviceSweepOptions options,
        Func<IDeviceInfo, bool> include,
        Func<IDeviceInfo, TRow?, CancellationToken, Task<TRow?>> read,
        Func<TRow, string> address,
        Action<IReadOnlyList<TRow>, IReadOnlyList<Guid>> publishRows,
        Action publishState)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _include = include;
        _read = read;
        _address = address;
        _publishRows = publishRows;
        _publishState = publishState;
        var token = _stopping.Token;
        _publishLoop = Detached.Run(() => PublishLoopAsync(token));
    }

    /// <summary>Called after every sweep that read all its devices (the plugin's summary log).</summary>
    public Action? Finished { get; set; }

    /// <summary>
    /// Whether a device is in the sweep's scope now (<see cref="IDeviceScopes.Contains(string, Guid)"/>, set by
    /// <see cref="DeviceSweeps{TRow}"/>); null: every row counts (hosts without groups).
    /// </summary>
    public Func<Guid, bool>? InScope { get; set; }

    /// <summary>The key of the device scope <see cref="InScope"/> checks (set with it by <see cref="DeviceSweeps{TRow}"/>).</summary>
    public string? ScopeKey { get; set; }

    /// <summary>Whether the table has a row of the device (one lookup, e.g. after a task on it).</summary>
    public bool HasRow(Guid deviceId)
    {
        lock (_sync)
        {
            return _rows.ContainsKey(deviceId);
        }
    }

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

    /// <summary>
    /// Progress and the rows of devices in the sweep's scope now (<see cref="InScope"/>): rows of devices that left it are
    /// dropped first, like <see cref="SnapshotAsync"/>, without a request. For what goes to the scope's users in the background
    /// (the published state and its counts), between a device leaving the group and the prune.
    /// </summary>
    public SweepSnapshot<TRow> ScopedSnapshot()
    {
        var inScope = InScope;
        lock (_sync)
        {
            if (inScope is not null)
            {
                foreach (var gone in _rows.Keys.Where(id => !inScope(id)).ToList())
                {
                    Remove(gone);
                    _stateDirty = true;
                }
            }

            return new SweepSnapshot<TRow>(_running, _done, _total, _finishedUtc, [.. _rows.Values]);
        }
    }

    /// <summary>
    /// Progress and the rows <paramref name="caller"/> may see: rows of devices that left the caller's devices (a tag or site
    /// change moved them out of the group, or they were removed) are dropped from the table first, and their removal goes out
    /// with the next batch, so such a device shows nothing even before the group's rows are pruned. The caller's devices are
    /// the sweep's scope (<see cref="DeviceSweeps{TRow}.For(ICallContext)"/>).
    /// </summary>
    public async Task<SweepSnapshot<TRow>> SnapshotAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (InScope is not null && string.Equals(ScopeKey, caller.DeviceScopeKey, StringComparison.OrdinalIgnoreCase))
        {
            // The sweep's scope is the caller's devices: a few hash lookups per row instead of listing every device (the
            // pages read the state about once a second while a sweep runs).
            return ScopedSnapshot();
        }

        HashSet<Guid> ids;
        try
        {
            ids = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        }
#pragma warning disable CA1031 // Fail closed: when the caller's devices cannot be listed, the progress shows without rows.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogListFailed(_ctx.Logger, ex);
            lock (_sync)
            {
                return new SweepSnapshot<TRow>(_running, _done, _total, _finishedUtc, []);
            }
        }

        lock (_sync)
        {
            foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                Remove(gone);
                _stateDirty = true;
            }

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
        _sweep = Detached.Run(() => SweepAsync(only, token));
    }

    /// <summary>
    /// Reads one device now, outside a sweep (e.g. after a task changed it); detached like the sweep. A device that is no
    /// longer in the sweep's devices (it left the group) or no longer included loses its row instead.
    /// </summary>
    public Task ReadOneAsync(Guid deviceId, CancellationToken ct) => Detached.Run(async () =>
    {
        if (await _devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { } device && _include(device) && Covers(device.Id))
        {
            await ReadIntoRowAsync(device, ct).ConfigureAwait(false);
            return;
        }

        DropRow(deviceId);
    });

    /// <summary>
    /// Drops the rows of devices that are no longer in the repository or no longer included (a group's rules changed), without
    /// reading any device; detached like the sweep.
    /// </summary>
    public Task PruneAsync(CancellationToken ct) => Detached.Run(async () =>
    {
        var ids = (await _devices.ListAsync(ct).ConfigureAwait(false)).Where(_include).Select(d => d.Id).ToHashSet();
        lock (_sync)
        {
            foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                Remove(gone);
            }

            _stateDirty = true;
        }

        Publish();
    });

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
            var inScope = InScope;
            lock (_sync)
            {
                if (inScope is not null)
                {
                    // A device that left the scope since its row was read goes out as removed, never as a row.
                    foreach (var gone in _dirty.Where(id => _rows.ContainsKey(id) && !inScope(id)).ToList())
                    {
                        Remove(gone);
                        _stateDirty = true;
                    }
                }

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
            var devices = (await _devices.ListAsync(ct).ConfigureAwait(false)).Where(_include).ToList();
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
                    if (Covers(device.Id))
                    {
                        await ReadIntoRowAsync(device, token).ConfigureAwait(false);
                    }
                    else
                    {
                        DropRow(device.Id); // it left the scope during the sweep: never read, never stored
                    }

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

        // One limit for the plugin: the sweeps of every device scope share the options' gate.
        var gate = _options.Gate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        TRow? row;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.DeviceTimeout);
            row = await _read(device, previous, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        lock (_sync)
        {
            if (_rowRead.TryGetValue(device.Id, out var newer) && newer > read)
            {
                return; // a read that started later already set this row
            }

            _rowRead[device.Id] = read;
            if (row is null || !Covers(device.Id))
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

    /// <summary>Whether the device is in the sweep's scope now (always without <see cref="InScope"/>).</summary>
    private bool Covers(Guid id) => InScope is not { } inScope || inScope(id);

    private void DropRow(Guid id)
    {
        lock (_sync)
        {
            if (_rows.ContainsKey(id))
            {
                Remove(id);
                _stateDirty = true;
            }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing the devices of the page failed: the table is shown without rows")]
    private static partial void LogListFailed(ILogger logger, Exception ex);
}

/// <summary>
/// One <see cref="DeviceSweep{TRow}"/> per device scope (SDK 0.4.0, docs/spec/device-scope.md, Plugins: sweep state is kept
/// per group, not per plugin): a user's call works on the sweep of its <see cref="ICallContext.DeviceScopeKey"/>, which
/// reads only that scope's devices (<see cref="IDeviceScopes.Devices"/>; on hosts without groups every device). The
/// callbacks get the scope key: publish rows and progress with <see cref="PluginEventAudience.DeviceScope"/>, so only the
/// users of that scope receive them. A deleted group's sweep is dropped, a changed group's rows are pruned to its devices.
/// </summary>
internal sealed class DeviceSweeps<TRow> : IAsyncDisposable
    where TRow : class
{
    private readonly ICorePluginContext _ctx;
    private readonly Func<string, IDeviceRepository, DeviceSweep<TRow>> _create;
    private readonly Dictionary<string, DeviceSweep<TRow>> _sweeps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private bool _disposed;

    /// <param name="ctx">The plugin's context.</param>
    /// <param name="create">Makes the sweep of one scope over its devices (the key goes into the publish callbacks).</param>
    public DeviceSweeps(ICorePluginContext ctx, Func<string, IDeviceRepository, DeviceSweep<TRow>> create)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnScopeChanged;
        }
    }

    /// <summary>The same as <see cref="DeviceSweeps{TRow}(ICorePluginContext, Func{string, IDeviceRepository, DeviceSweep{TRow}})"/> from the usual parts.</summary>
    public DeviceSweeps(
        ICorePluginContext ctx,
        DeviceSweepOptions options,
        Func<IDeviceInfo, bool> include,
        Func<IDeviceInfo, TRow?, CancellationToken, Task<TRow?>> read,
        Func<TRow, string> address,
        Action<string, IReadOnlyList<TRow>, IReadOnlyList<Guid>> publishRows,
        Action<string> publishState)
        : this(ctx, (key, devices) => new DeviceSweep<TRow>(ctx, devices, options, include, read, address, (rows, removed) => publishRows(key, rows, removed), () => publishState(key)))
    {
    }

    /// <summary>The scope keys that have a sweep (tests).</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_sync)
            {
                return [.. _sweeps.Keys];
            }
        }
    }

    /// <summary>The sweep of the caller's devices.</summary>
    public DeviceSweep<TRow>? For(ICallContext call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return For(call.DeviceScopeKey);
    }

    /// <summary>
    /// The sweep of <paramref name="scopeKey"/>, made on first use; null for a scope that does not exist (a deleted group:
    /// its users have no devices) and after <see cref="DisposeAsync"/>.
    /// </summary>
    public DeviceSweep<TRow>? For(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var scopes = _ctx.DeviceScopes;
        if (scopes is null ? scopeKey != ICallContext.AllDevicesScopeKey : !scopes.Exists(scopeKey))
        {
            return null;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            if (!_sweeps.TryGetValue(scopeKey, out var sweep))
            {
                // Older hosts have no groups: the plugin's devices (the sweep runs detached, as background work).
                var devices = scopes?.Devices(scopeKey) ?? _ctx.Devices;
                sweep = _create(scopeKey, devices);
                if (scopes is not null)
                {
                    var key = scopeKey;
                    sweep.InScope = id => scopes.Contains(key, id);
                    sweep.ScopeKey = key;
                }

                _sweeps[scopeKey] = sweep;
            }

            return sweep;
        }
    }

    /// <summary>The sweep of a scope if it has one (no new sweep).</summary>
    public DeviceSweep<TRow>? Existing(string scopeKey)
    {
        lock (_sync)
        {
            return _sweeps.GetValueOrDefault(scopeKey);
        }
    }

    /// <summary>Every sweep (e.g. to read a device again after a task in each scope that has a row of it).</summary>
    public IReadOnlyList<DeviceSweep<TRow>> All
    {
        get
        {
            lock (_sync)
            {
                return [.. _sweeps.Values];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnScopeChanged;
        }

        List<DeviceSweep<TRow>> sweeps;
        lock (_sync)
        {
            _disposed = true;
            sweeps = [.. _sweeps.Values];
            _sweeps.Clear();
        }

        foreach (var sweep in sweeps)
        {
            await sweep.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A group changed or was deleted: prune its rows to its devices, or drop its sweep.</summary>
    private void OnScopeChanged(object? sender, string scopeKey)
    {
        DeviceSweep<TRow>? sweep;
        var exists = _ctx.DeviceScopes?.Exists(scopeKey) ?? true;
        lock (_sync)
        {
            if (!_sweeps.TryGetValue(scopeKey, out sweep))
            {
                return;
            }

            if (!exists)
            {
                _sweeps.Remove(scopeKey);
            }
        }

        _ = exists ? sweep.PruneAsync(CancellationToken.None) : Detached.Run(async () => await sweep.DisposeAsync().ConfigureAwait(false));
    }
}

/// <summary>
/// Background work of a sweep started from a user's call: it runs without the call's context (the host refuses the plugin's
/// full device access in the call of a user with a device group, and work started from the call keeps the call).
/// </summary>
internal static class Detached
{
    public static Task Run(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }
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
