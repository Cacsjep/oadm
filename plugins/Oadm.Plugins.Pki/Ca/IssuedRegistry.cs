namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The registry of device certificates OADM issued (plugin setting <c>issued</c>). Tasks of thousands of devices add to it
/// in parallel, so changes go to memory and are written at most every <see cref="FlushDelay"/> (and on stop) instead of
/// rewriting the whole list per task. Reads return the pending state while a write is due, else the stored list.
/// Entries whose certificate ended more than 30 days ago are dropped when the registry changes.
/// </summary>
public sealed class IssuedRegistry : IAsyncDisposable
{
    private readonly PkiStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<IssuedCertificate>? _entries;
    private bool _dirty;
    private bool _disposed;

    public IssuedRegistry(PkiStore store, TimeProvider time)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>All entries (a copy).</summary>
    public async Task<IReadOnlyList<IssuedCertificate>> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_dirty || _entries is null)
            {
                _entries = await _store.LoadIssuedAsync(ct).ConfigureAwait(false);
            }

            return [.. _entries];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The entries of one device.</summary>
    public async Task<IReadOnlyList<IssuedCertificate>> ForDeviceAsync(Guid deviceId, CancellationToken ct) =>
        [.. (await SnapshotAsync(ct).ConfigureAwait(false)).Where(e => e.DeviceId == deviceId)];

    public Task AddAsync(IssuedCertificate entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ChangeAsync(list => list.Add(entry), ct);
    }

    /// <summary>Forgets the certificates with these serial numbers on the device (deleted from it).</summary>
    public Task RemoveAsync(Guid deviceId, IReadOnlyCollection<string> serialNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serialNumbers);
        return serialNumbers.Count == 0
            ? Task.CompletedTask
            : ChangeAsync(list => list.RemoveAll(e => e.DeviceId == deviceId && serialNumbers.Contains(e.SerialNumber, StringComparer.OrdinalIgnoreCase)), ct);
    }

    /// <summary>Writes pending changes now.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dirty && _entries is not null)
            {
                await _store.SaveIssuedAsync(_entries, ct).ConfigureAwait(false);
                _dirty = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task ChangeAsync(Action<List<IssuedCertificate>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_dirty || _entries is null)
            {
                _entries = await _store.LoadIssuedAsync(ct).ConfigureAwait(false);
            }

            change(_entries);
            var limit = _time.GetUtcNow().UtcDateTime.AddDays(-30);
            _entries.RemoveAll(e => e.NotAfterUtc < limit);
            if (!_dirty)
            {
                _dirty = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(FlushDelay, _time, CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (!_disposed)
                        {
                            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // stopped meanwhile: DisposeAsync wrote the changes
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
