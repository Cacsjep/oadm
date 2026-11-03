using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The saved readers (docs/spec/ocr.md "Readers"): one plugin record per reader (collection <see cref="Collection"/>), the
/// only thing the plugin stores. Each reader belongs to the device scope that made it (docs/spec/device-scope.md, Shared
/// objects): users of a group see and change only their group's readers, users with all devices every reader; an edit
/// keeps the owner. Writes run one at a time; reads take the copy in memory.
/// </summary>
internal sealed partial class ReaderStore(IPluginRecords records, ILogger logger) : IDisposable
{
    /// <summary>The records' collection.</summary>
    public const string Collection = "readers";

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _sync = new();
    private List<Reader> _readers = [];

    /// <summary>Every reader (those read at the start by name, then the new ones).</summary>
    public IReadOnlyList<Reader> All
    {
        get
        {
            lock (_sync)
            {
                return [.. _readers];
            }
        }
    }

    /// <summary>The reader of <paramref name="id"/>, whoever owns it (background work, the action); null when there is none.</summary>
    public Reader? Find(string? id)
    {
        lock (_sync)
        {
            return _readers.Find(r => r.Id == id);
        }
    }

    /// <summary>The readers <paramref name="caller"/> sees.</summary>
    public IReadOnlyList<Reader> Visible(ICallContext caller) => [.. All.Where(r => OwnerScope.Sees(caller, r.OwnerScopeKey))];

    /// <summary>The reader of <paramref name="id"/> if <paramref name="caller"/> sees it; null like an unknown id otherwise.</summary>
    public Reader? FindVisible(ICallContext caller, string? id) => Find(id) is { } reader && OwnerScope.Sees(caller, reader.OwnerScopeKey) ? reader : null;

    /// <summary>Reads the stored readers; records that cannot be read are logged and left as they are.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var stored = await records.ListAsync(Collection, ct).ConfigureAwait(false);
        var readers = new List<Reader>(stored.Count);
        foreach (var (key, json) in stored)
        {
            try
            {
                var reader = OcrJson.Deserialize<Reader>(json);
                readers.Add(reader with { Id = key });
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                LogUnreadable(logger, key, ex);
            }
        }

        lock (_sync)
        {
            _readers = [.. readers.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Creates (empty id) or changes a reader for <paramref name="caller"/>: checks the reader, the camera against the caller's
    /// and the owner's devices (<see cref="Oadm.Sdk.Devices.DevicesNotAvailableException"/>, nothing stored), the names and the
    /// limits; returns the stored reader and the one it replaced (null for a new one). <paramref name="deviceOf"/> finds any
    /// managed camera (system view) for the count of watching readers per site; it shows the caller no device data.
    /// </summary>
    public async Task<(Reader Saved, Reader? Before)> SaveAsync(
        ICorePluginContext ctx,
        ICallContext caller,
        Reader incoming,
        bool onGpu,
        Func<Guid, CancellationToken, Task<IDeviceInfo?>> deviceOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceOf);
        ArgumentNullException.ThrowIfNull(incoming);
        var reader = ReaderRules.Normalize(incoming);
        if (ReaderRules.Problem(reader, onGpu) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var isNew = string.IsNullOrEmpty(reader.Id);
            var before = isNew ? null : FindVisible(caller, reader.Id) ?? throw new ArgumentException(ReaderRules.Gone);
            var owner = OwnerScope.ForSave(ctx, caller, before?.OwnerScopeKey, isNew);
            await CallDevices.RequireAsync(caller, [reader.DeviceId], ct).ConfigureAwait(false);
            await OwnerScope.RequireDevicesAsync(ctx, caller, owner, [reader.DeviceId], ct).ConfigureAwait(false);

            var all = All;
            var others = all.Where(r => r.Id != reader.Id).ToList();
            if (others.Any(r => string.Equals(OwnerScope.Of(r.OwnerScopeKey), owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Name, reader.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(ReaderRules.NameTaken(reader.Name));
            }

            if (isNew && all.Count >= OcrLimits.MaxReaders)
            {
                throw new ArgumentException(ReaderRules.TooManyReaders);
            }

            if (reader.Mode == ReaderMode.Watching && before?.Mode != ReaderMode.Watching
                && others.Count(r => r.Mode == ReaderMode.Watching) >= ReaderRules.MaxWatching(onGpu))
            {
                throw new ArgumentException(ReaderRules.TooManyWatching(onGpu));
            }

            if (reader.Mode == ReaderMode.Watching && await deviceOf(reader.DeviceId, ct).ConfigureAwait(false) is { SiteId: { } site } camera)
            {
                // At a site every stream holds one of the node's large transfers: at most 4 watching readers per site.
                var atSite = 0;
                foreach (var other in others.Where(r => r.Mode == ReaderMode.Watching))
                {
                    if ((await deviceOf(other.DeviceId, ct).ConfigureAwait(false))?.SiteId == site)
                    {
                        atSite++;
                    }
                }

                if (atSite >= ReaderRules.MaxWatchingPerSite)
                {
                    throw new ArgumentException(ReaderRules.TooManyWatchingAtSite(camera.SiteName));
                }
            }

            var saved = reader with
            {
                Id = isNew ? Guid.NewGuid().ToString("N") : reader.Id,
                OwnerScopeKey = owner == ICallContext.AllDevicesScopeKey ? null : owner,
            };
            await records.SetManyAsync(Collection, [new(saved.Id, OcrJson.Serialize(saved with { Id = string.Empty }))], ct).ConfigureAwait(false);
            lock (_sync)
            {
                var index = _readers.FindIndex(r => r.Id == saved.Id);
                if (index >= 0)
                {
                    _readers[index] = saved;
                }
                else
                {
                    _readers.Add(saved);
                }
            }

            return (saved, before);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a reader <paramref name="caller"/> sees; returns it, or null when there was none to delete.</summary>
    public async Task<Reader?> DeleteAsync(ICallContext caller, string? id, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FindVisible(caller, id) is not { } reader)
            {
                return null;
            }

            await records.SetManyAsync(Collection, [new(reader.Id, null)], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _readers.RemoveAll(r => r.Id == reader.Id);
            }

            return reader;
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: the stored reader {Key} could not be read and is left out")]
    private static partial void LogUnreadable(ILogger logger, string key, Exception ex);
}
