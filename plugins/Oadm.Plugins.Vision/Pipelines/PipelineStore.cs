using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// The saved pipelines (docs/spec/vision-center.md "Pipelines"): one plugin record per pipeline (collection
/// <see cref="Collection"/>). Each belongs to the device scope that made it (docs/spec/device-scope.md, Shared objects): users
/// of a group see and change only their group's pipelines, users with all devices every pipeline; an edit keeps the owner.
/// Writes run one at a time; reads take the copy in memory.
/// </summary>
internal sealed partial class PipelineStore(IPluginRecords records, ILogger logger) : IDisposable
{
    public const string Collection = "pipelines";

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _sync = new();
    private List<Pipeline> _pipelines = [];

    /// <summary>Raised after a save or a delete changed the list.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Pipeline> All
    {
        get
        {
            lock (_sync)
            {
                return [.. _pipelines];
            }
        }
    }

    /// <summary>The pipeline of <paramref name="id"/>, whoever owns it; null when there is none.</summary>
    public Pipeline? Find(string? id)
    {
        lock (_sync)
        {
            return _pipelines.Find(p => p.Id == id);
        }
    }

    public IReadOnlyList<Pipeline> Visible(ICallContext caller) => [.. All.Where(p => OwnerScope.Sees(caller, p.OwnerScopeKey))];

    /// <summary>The pipeline of <paramref name="id"/> if <paramref name="caller"/> sees it; null like an unknown id otherwise.</summary>
    public Pipeline? FindVisible(ICallContext caller, string? id) =>
        Find(id) is { } pipeline && OwnerScope.Sees(caller, pipeline.OwnerScopeKey) ? pipeline : null;

    /// <summary>Reads the stored pipelines; records that cannot be read are logged and left as they are.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var stored = await records.ListAsync(Collection, ct).ConfigureAwait(false);
        var pipelines = new List<Pipeline>(stored.Count);
        foreach (var (key, json) in stored)
        {
            try
            {
                pipelines.Add(PipelineRules.Stored(VisionJson.Deserialize<Pipeline>(json)) with { Id = key });
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                LogUnreadable(logger, key, ex);
            }
        }

        lock (_sync)
        {
            _pipelines = Sorted(pipelines);
        }
    }

    /// <summary>
    /// Creates (empty id) or changes a pipeline for <paramref name="caller"/>: the fields, the camera against the caller's and
    /// the owner's devices (<see cref="DevicesNotAvailableException"/>), the names, the limits and the graph
    /// (<paramref name="check"/>: node errors under <see cref="PipelineFields.Node"/>); any error is a
    /// <see cref="PipelineFieldException"/> and nothing is stored. <paramref name="deviceOf"/> finds any managed camera (system
    /// view) for the count of watching pipelines per site; it shows the caller no device data.
    /// </summary>
    public async Task<(Pipeline Saved, Pipeline? Before)> SaveAsync(
        ICorePluginContext ctx,
        ICallContext caller,
        Pipeline incoming,
        int maxWatching,
        Func<Pipeline, GraphCheck> check,
        Func<Guid, CancellationToken, Task<IDeviceInfo?>> deviceOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(deviceOf);
        var pipeline = PipelineRules.Normalize(incoming);
        var errors = PipelineRules.Problems(pipeline);

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var isNew = string.IsNullOrEmpty(pipeline.Id);
            var before = isNew ? null : FindVisible(caller, pipeline.Id) ?? throw new ArgumentException(PipelineRules.Gone);
            if (before is not null && before.DeviceId != pipeline.DeviceId)
            {
                errors[PipelineFields.Camera] = PipelineRules.CameraFixed;
            }

            var all = All;
            var others = all.Where(p => p.Id != pipeline.Id).ToList();
            var owner = OwnerScope.ForSave(ctx, caller, before?.OwnerScopeKey, isNew);
            if (!errors.ContainsKey(PipelineFields.Name) && others.Any(p =>
                string.Equals(OwnerScope.Of(p.OwnerScopeKey), owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Name, pipeline.Name, StringComparison.OrdinalIgnoreCase)))
            {
                errors[PipelineFields.Name] = PipelineRules.NameTaken(pipeline.Name);
            }

            var graph = check(pipeline);
            foreach (var (nodeId, error) in graph.NodeErrors)
            {
                errors[PipelineFields.Node(nodeId)] = error;
            }

            if (errors.Count > 0 || graph.GraphErrors.Count > 0)
            {
                throw new PipelineFieldException(errors, graph.GraphErrors);
            }

            if (pipeline.DeviceId != Guid.Empty)
            {
                await CallDevices.RequireAsync(caller, [pipeline.DeviceId], ct).ConfigureAwait(false);
                await OwnerScope.RequireDevicesAsync(ctx, caller, owner, [pipeline.DeviceId], ct).ConfigureAwait(false);
            }

            if (isNew && all.Count >= VisionLimits.MaxPipelines)
            {
                throw new ArgumentException(PipelineRules.TooManyPipelines);
            }

            if (pipeline.Mode == PipelineMode.Watching && before?.Mode != PipelineMode.Watching)
            {
                var watching = others.Where(p => p.Mode == PipelineMode.Watching).ToList();
                if (watching.Count >= maxWatching)
                {
                    throw new PipelineFieldException(PipelineFields.Mode, PipelineRules.TooManyWatching(maxWatching));
                }

                if (await deviceOf(pipeline.DeviceId, ct).ConfigureAwait(false) is { SiteId: { } site } camera)
                {
                    // A site node's link carries every stream of its cameras: at most 4 watching pipelines per site.
                    var atSite = 0;
                    foreach (var other in watching)
                    {
                        if ((await deviceOf(other.DeviceId, ct).ConfigureAwait(false))?.SiteId == site)
                        {
                            atSite++;
                        }
                    }

                    if (atSite >= VisionLimits.MaxWatchingPerSite)
                    {
                        throw new PipelineFieldException(PipelineFields.Mode, PipelineRules.TooManyWatchingAtSite(camera.SiteName));
                    }
                }
            }

            var saved = pipeline with
            {
                Id = isNew ? Guid.NewGuid().ToString("N") : pipeline.Id,
                OwnerScopeKey = owner == ICallContext.AllDevicesScopeKey ? null : owner,
            };
            await records.SetManyAsync(Collection, [new(saved.Id, VisionJson.Serialize(saved with { Id = string.Empty }))], ct).ConfigureAwait(false);
            lock (_sync)
            {
                var index = _pipelines.FindIndex(p => p.Id == saved.Id);
                if (index >= 0)
                {
                    _pipelines[index] = saved;
                }
                else
                {
                    _pipelines.Add(saved);
                }

                _pipelines = Sorted(_pipelines);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return (saved, before);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a pipeline <paramref name="caller"/> sees; returns it, or null when there was none to delete.</summary>
    public async Task<Pipeline?> DeleteAsync(ICallContext caller, string? id, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FindVisible(caller, id) is not { } pipeline)
            {
                return null;
            }

            await records.SetManyAsync(Collection, [new(pipeline.Id, null)], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _pipelines.RemoveAll(p => p.Id == pipeline.Id);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return pipeline;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Stores <paramref name="copies"/> (from <see cref="PipelineCopies.CopyOf"/>) for <paramref name="caller"/>, owned by the
    /// caller's scope, in one batch: each with the checks of a save (its fields, <paramref name="check"/>, its camera in the
    /// owner's devices) and a name made unique; a copy that fails is refused with its first error, as are the copies beyond
    /// <see cref="VisionLimits.MaxPipelines"/>. The watching limits are not checked: a copy beyond them is stored and shows
    /// the limit as its problem.
    /// </summary>
    public async Task<(List<Pipeline> Created, Dictionary<Guid, string> Refused)> AddCopiesAsync(
        ICorePluginContext ctx,
        ICallContext caller,
        IReadOnlyList<Pipeline> copies,
        Func<Pipeline, GraphCheck> check,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(check);
        var owner = OwnerScope.ForSave(ctx, caller, null, isNew: true);
        var created = new List<Pipeline>();
        var refused = new Dictionary<Guid, string>();
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = All;
            var taken = all.Where(p => string.Equals(OwnerScope.Of(p.OwnerScopeKey), owner, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var incoming in copies)
            {
                var copy = PipelineRules.Normalize(incoming);
                copy = copy with { Name = PipelineCopies.Unique(copy.Name, taken) };
                var graph = check(copy);
                var error = PipelineRules.Problems(copy).Values.FirstOrDefault() ?? PipelineRules.FirstError(graph, copy);
                if (error is null)
                {
                    try
                    {
                        await OwnerScope.RequireDevicesAsync(ctx, caller, owner, [copy.DeviceId], ct).ConfigureAwait(false);
                    }
                    catch (DevicesNotAvailableException)
                    {
                        error = PipelineRules.CameraNotAvailable;
                    }
                }

                if (error is null && all.Count + created.Count >= VisionLimits.MaxPipelines)
                {
                    error = PipelineRules.CopyLimit;
                }

                if (error is not null)
                {
                    refused[copy.DeviceId] = error;
                    continue;
                }

                taken.Add(copy.Name);
                created.Add(copy with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OwnerScopeKey = owner == ICallContext.AllDevicesScopeKey ? null : owner,
                });
            }

            if (created.Count == 0)
            {
                return (created, refused);
            }

            await records.SetManyAsync(Collection, [.. created.Select(p => new KeyValuePair<string, string?>(p.Id, VisionJson.Serialize(p with { Id = string.Empty })))], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _pipelines = Sorted([.. _pipelines, .. created]);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return (created, refused);
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    private static List<Pipeline> Sorted(IEnumerable<Pipeline> pipelines) =>
        [.. pipelines.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal)];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: the stored pipeline {Key} could not be read and is left out")]
    private static partial void LogUnreadable(ILogger logger, string key, Exception ex);
}
