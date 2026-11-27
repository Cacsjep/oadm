using Microsoft.Extensions.Logging;

namespace Oadm.Plugins.LicensePlates.Log;

/// <summary>
/// The log in its folder (the setting): opens the store when a folder is chosen, follows a changed folder (a new folder
/// starts its own log; the old one keeps its files), stops storing on a full disk or an unreachable folder and tries the
/// folder again every minute, and runs the cleanup every hour. Without a folder nothing is stored.
/// </summary>
internal sealed class PlateLog(TimeProvider time, ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlateLogStore? _store;
    private string? _folder;
    private string? _openProblem;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private LicensePlateSettings _settings = new();

    /// <summary>Raised (without a caller) when <see cref="Problem"/> or the store changed.</summary>
    public event Action? Changed;

    /// <summary>The open store; null without a folder or while it cannot be opened.</summary>
    public PlateLogStore? Store => Volatile.Read(ref _store);

    public bool HasFolder => _folder is not null;

    /// <summary>Why reads are not stored; null while they are.</summary>
    public string? Problem => _folder is null ? LicensePlateTexts.NoFolder : _openProblem ?? Store?.Problem;

    /// <summary>Applies the settings: opens the store of a new folder, the picture setting.</summary>
    public async Task ApplyAsync(LicensePlateSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _settings = settings;
            var folder = string.IsNullOrWhiteSpace(settings.Folder) ? null : Path.GetFullPath(settings.Folder);
            if (!string.Equals(folder, _folder, StringComparison.Ordinal))
            {
                await CloseLockedAsync().ConfigureAwait(false);
                _folder = folder;
                if (folder is not null)
                {
                    await OpenLockedAsync(ct).ConfigureAwait(false);
                }
            }

            if (Store is { } store)
            {
                store.KeepPictures = settings.KeepPictures;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Every minute: a store that stopped is opened again; once an hour the cleanup runs.</summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_folder is null)
            {
                return;
            }

            if (_openProblem is not null || Store?.Problem is not null)
            {
                await CloseLockedAsync().ConfigureAwait(false);
                await OpenLockedAsync(ct).ConfigureAwait(false);
                changed = true;
            }

            var now = time.GetUtcNow();
            if (Store is { } store && now - _lastCleanup >= LicensePlateLimits.CleanupInterval)
            {
                _lastCleanup = now;
                await store.CleanupAsync(_settings.KeepDays, (long)_settings.MaxGigabytes * 1024 * 1024 * 1024).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task OpenLockedAsync(CancellationToken ct)
    {
        try
        {
            var store = await PlateLogStore.OpenAsync(_folder!, time, logger, ct).ConfigureAwait(false);
            store.KeepPictures = _settings.KeepPictures;
            store.ProblemChanged += () => Changed?.Invoke();
            _openProblem = null;
            Volatile.Write(ref _store, store);
        }
#pragma warning disable CA1031 // A folder that cannot be used stops storing; MaintainAsync tries again.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _openProblem = PlateLogStore.Describe(ex);
        }
    }

    private async Task CloseLockedAsync()
    {
        var store = Interlocked.Exchange(ref _store, null);
        _openProblem = null;
        if (store is not null)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseLockedAsync().ConfigureAwait(false);
            _folder = null;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }
}
