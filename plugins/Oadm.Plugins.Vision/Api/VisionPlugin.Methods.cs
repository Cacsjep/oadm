using System.Collections.Concurrent;

using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision;

/// <summary>
/// The page backend (<see cref="VisionMethods"/>). Device groups (docs/spec/device-scope.md): every pipeline method works on
/// the pipelines the caller's device scope owns (users with all devices: every pipeline); another scope's pipeline answers
/// like an unknown id; a save takes only a camera of the caller's and the owner's devices; a preview needs its camera in the
/// caller's devices. Models are server-wide: their changes are for users with all devices. The work on the cameras runs
/// without the call (<see cref="PipelineRuntime"/>).
/// </summary>
public sealed partial class VisionPlugin
{
    /// <summary>The plugin setting "Use GPU" (JSON true or false; on when missing).</summary>
    internal const string UseGpuSetting = "useGpu";

    /// <summary>How long the video sources and sizes of a camera are kept.</summary>
    internal static readonly TimeSpan CameraOptionsLifetime = TimeSpan.FromMinutes(10);

    internal const int MaxCachedCameras = 200;

    /// <summary>What the audit entries of the calls name (read after the call ended, never a file's content).</summary>
    private readonly ConcurrentDictionary<string, string> _auditNames = new(StringComparer.Ordinal);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ctx = _ctx ?? throw new InvalidOperationException(PipelineRules.NotRunning);
        var store = _store ?? throw new InvalidOperationException(PipelineRules.NotRunning);
        var runtime = _runtime ?? throw new InvalidOperationException(PipelineRules.NotRunning);
        var models = _models ?? throw new InvalidOperationException(PipelineRules.NotRunning);
        var owner = OwnerScope.Of(caller.DeviceScopeKey);
        switch (method)
        {
            case VisionMethods.ListPipelines:
            {
                var visible = store.Visible(caller);
                var seen = new HashSet<Guid>();
                foreach (var id in visible.Select(p => p.DeviceId).Distinct())
                {
                    if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is not null)
                    {
                        seen.Add(id);
                    }
                }

                return VisionJson.Serialize(ListOf(visible, runtime, seen.Contains));
            }

            case VisionMethods.GetPipeline:
                return VisionJson.Serialize(Visible(store, caller, payloadJson) with { OwnerScopeKey = null });

            case VisionMethods.SavePipeline:
            {
                var incoming = VisionJson.Deserialize<SavePipelineRequest>(payloadJson).Pipeline ?? throw new ArgumentException(PluginJson.EmptyRequest);
                var eventDevices = EventDevicesOf(incoming);
                if (eventDevices.Count > 0)
                {
                    await CallDevices.RequireAsync(caller, eventDevices, ct).ConfigureAwait(false); // a Device event node names only the caller's devices
                }

                Pipeline saved;
                Pipeline? before;
                try
                {
                    (saved, before) = await store.SaveAsync(ctx, caller, incoming, runtime.MaxWatching, p => CheckWithEvents(runtime, caller, p), ctx.System.Devices.FindAsync, ct).ConfigureAwait(false);
                }
                catch (PipelineFieldException ex)
                {
                    return VisionJson.Serialize(new SavePipelineReply { Errors = ex.Errors, GraphErrors = ex.GraphErrors });
                }

                _auditNames[AuditKey(method, saved.Id)] = saved.Name;
                _auditNames[AuditKey(method, incoming.Id)] = saved.Name;
                EventTypesChanged?.Invoke(this, EventArgs.Empty);
                await runtime.SyncAsync().ConfigureAwait(false);
                ReportProblems();
                PublishPipelines(saved.OwnerScopeKey, before?.OwnerScopeKey);
                PublishModels(); // its models' "Used by"
                return VisionJson.Serialize(new SavePipelineReply { Pipeline = saved with { OwnerScopeKey = null } });
            }

            case VisionMethods.CopyPipeline:
            {
                var request = VisionJson.Deserialize<CopyPipelineRequest>(payloadJson);
                var original = store.FindVisible(caller, request.PipelineId) ?? throw new ArgumentException(PipelineRules.Gone);
                var reply = await CopyAsync(ctx, caller, store, runtime, original, request.DeviceIds ?? [], ct).ConfigureAwait(false);
                _auditNames[AuditKey(method, original.Id)] = original.Name + "\u001f" + reply.Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return VisionJson.Serialize(reply);
            }

            case VisionMethods.DeletePipeline:
            {
                var id =VisionJson.Deserialize<PipelineRequest>(payloadJson).PipelineId;
                if (await store.DeleteAsync(caller, id, ct).ConfigureAwait(false) is { } deleted)
                {
                    _auditNames[AuditKey(method, deleted.Id)] = deleted.Name;
                    await runtime.SyncAsync().ConfigureAwait(false);
                    EventTypesChanged?.Invoke(this, EventArgs.Empty);
                    ReportProblems();
                    PublishPipelines(deleted.OwnerScopeKey);
                    PublishModels();
                }

                return "{}";
            }

            case VisionMethods.CheckGraph:
            {
                var pipeline = VisionJson.Deserialize<CheckGraphRequest>(payloadJson).Pipeline ?? throw new ArgumentException(PluginJson.EmptyRequest);
                return VisionJson.Serialize(CheckWithEvents(runtime, caller, PipelineRules.Stored(pipeline)));
            }

            case VisionMethods.StartPreview:
            {
                var request = VisionJson.Deserialize<PreviewRequest>(payloadJson);
                var draft = request.Pipeline ?? throw new ArgumentException(PluginJson.EmptyRequest);
                if (!string.IsNullOrEmpty(draft.Id) && store.FindVisible(caller, draft.Id) is null)
                {
                    throw new ArgumentException(PipelineRules.Gone);
                }

                if (draft.DeviceId != Guid.Empty)
                {
                    await CallDevices.RequireAsync(caller, [draft.DeviceId], ct).ConfigureAwait(false);
                }

                if (!MaySeePictures(caller))
                {
                    // Live view taken away while paused: the frame goes (fail closed).
                    await runtime.ResumePreviewAsync(request.PreviewId, owner, caller.UserName).ConfigureAwait(false);
                }

                return VisionJson.Serialize(await runtime.StartPreviewAsync(request.PreviewId, draft, owner, caller.UserName).ConfigureAwait(false));
            }

            case VisionMethods.StopPreview:
                await runtime.StopPreviewAsync(VisionJson.Deserialize<PreviewRequest>(payloadJson).PreviewId, owner, caller.UserName).ConfigureAwait(false);
                return "{}";

            case VisionMethods.PausePreview:
            {
                // The held picture is a piece of the camera's video: only for users who may see it, the camera in their devices.
                if (!MaySeePictures(caller))
                {
                    throw new DeviceAccessRefusedException(PipelineRules.PauseNeedsLiveView);
                }

                var previewId = VisionJson.Deserialize<PreviewRequest>(payloadJson).PreviewId;
                if (runtime.PreviewDraftOf(previewId, owner, caller.UserName) is not { } draft)
                {
                    return VisionJson.Serialize(new PauseReply());
                }

                await CallDevices.RequireAsync(caller, [draft.DeviceId], ct).ConfigureAwait(false);
                return VisionJson.Serialize(await runtime.PausePreviewAsync(previewId, owner, caller.UserName).ConfigureAwait(false));
            }

            case VisionMethods.ResumePreview:
                return VisionJson.Serialize(await runtime.ResumePreviewAsync(VisionJson.Deserialize<PreviewRequest>(payloadJson).PreviewId, owner, caller.UserName).ConfigureAwait(false));

            case VisionMethods.ListModels:
                return VisionJson.Serialize(ModelsOf(models, store.Visible(caller)));

            case VisionMethods.BeginUpload:
            {
                RequireAllDevices(caller);
                var request = VisionJson.Deserialize<BeginUploadRequest>(payloadJson);
                var uploadId = await models.BeginUploadAsync(request.FileName, request.Size, request.Labels, ct).ConfigureAwait(false);
                _auditNames[AuditKey(VisionMethods.FinishUpload, uploadId)] = Path.GetFileNameWithoutExtension(request.FileName ?? string.Empty);
                return VisionJson.Serialize(new BeginUploadReply { UploadId = uploadId });
            }

            case VisionMethods.UploadChunk:
            {
                RequireAllDevices(caller);
                var request = VisionJson.Deserialize<UploadChunkRequest>(payloadJson);
                await models.AppendAsync(request.UploadId, request.Offset, request.Data ?? [], ct).ConfigureAwait(false);
                return "{}";
            }

            case VisionMethods.FinishUpload:
            {
                RequireAllDevices(caller);
                var uploadId = VisionJson.Deserialize<UploadRequest>(payloadJson).UploadId;
                var check = await models.FinishUploadAsync(uploadId, ct).ConfigureAwait(false);
                var key = AuditKey(method, uploadId);
                _auditNames[key] = check.Model is { } model ? model.Name : RefusedPrefix + _auditNames.GetValueOrDefault(key, string.Empty);
                PublishModels();
                return VisionJson.Serialize(new FinishUploadReply { Model = check.Model, Refusal = check.Refusal });
            }

            case VisionMethods.CancelUpload:
                RequireAllDevices(caller);
                await models.CancelUploadAsync(VisionJson.Deserialize<UploadRequest>(payloadJson).UploadId, ct).ConfigureAwait(false);
                return "{}";

            case VisionMethods.UpdateModel:
            {
                RequireAllDevices(caller);
                var changed = VisionJson.Deserialize<UpdateModelRequest>(payloadJson).Model ?? throw new ArgumentException(PluginJson.EmptyRequest);
                var updated = await models.UpdateAsync(changed, ct).ConfigureAwait(false);
                _auditNames[AuditKey(method, updated.Id)] = updated.Name;
                runtime.Inference.Forget(updated.Id); // its sessions follow the new fit, scaling or size
                PublishModels();
                return VisionJson.Serialize(updated);
            }

            case VisionMethods.DeleteModel:
            {
                RequireAllDevices(caller);
                var id = VisionJson.Deserialize<ModelRequest>(payloadJson).ModelId;
                if (models.Find(id) is { } model)
                {
                    _auditNames[AuditKey(method, id)] = model.Name;
                    await models.DeleteAsync(id, UsedBy(store.All, id), ct).ConfigureAwait(false);
                    runtime.Inference.Forget(id);
                    PublishModels();
                }

                return "{}";
            }

            case VisionMethods.ListClassLists:
                return VisionJson.Serialize(ClassListsOf(models));

            case VisionMethods.SaveClassList:
            {
                RequireAllDevices(caller);
                var incoming = VisionJson.Deserialize<SaveClassListRequest>(payloadJson).ClassList ?? throw new ArgumentException(PluginJson.EmptyRequest);
                ClassList saved;
                try
                {
                    saved = await models.SaveClassListAsync(incoming, ct).ConfigureAwait(false);
                }
                catch (Models.ClassListFieldException ex)
                {
                    return VisionJson.Serialize(new SaveClassListReply { Errors = new Dictionary<string, string> { [ex.Field] = ex.Message } });
                }

                _auditNames[AuditKey(method, saved.Id)] = saved.Name;
                _auditNames[AuditKey(method, incoming.Id)] = saved.Name;
                PublishModels(); // the models on the list took its names
                return VisionJson.Serialize(new SaveClassListReply { ClassList = saved });
            }

            case VisionMethods.DeleteClassList:
            {
                RequireAllDevices(caller);
                var id = VisionJson.Deserialize<ClassListRequest>(payloadJson).ClassListId;
                if (models.ClassLists.FirstOrDefault(l => l.Id == id) is { } list)
                {
                    _auditNames[AuditKey(method, id)] = list.Name;
                }

                await models.DeleteClassListAsync(id, ct).ConfigureAwait(false);
                PublishModels();
                return "{}";
            }

            case VisionMethods.GetEngine:
                return VisionJson.Serialize(EngineOf(runtime));

            case VisionMethods.SetUseGpu:
            {
                RequireAllDevices(caller);
                var useGpu = VisionJson.Deserialize<UseGpuRequest>(payloadJson).UseGpu;
                await ctx.Settings.SetAsync(UseGpuSetting, useGpu ? "true" : "false", ct).ConfigureAwait(false);
                _useGpu = useGpu;
                await runtime.SetUseGpuAsync(useGpu).ConfigureAwait(false);
                PublishPipelines([.. store.All.Select(p => p.OwnerScopeKey)]);
                return VisionJson.Serialize(EngineOf(runtime));
            }

            case VisionMethods.GetCameraOptions:
                return VisionJson.Serialize(await CameraOptionsAsync(caller, VisionJson.Deserialize<CameraOptionsRequest>(payloadJson).DeviceId, ct).ConfigureAwait(false));

            case VisionMethods.ListEventTypes:
                return VisionJson.Serialize(await EventTypesAsync(caller, ct).ConfigureAwait(false));

            case VisionMethods.GetStats:
                return VisionJson.Serialize(runtime.GetStats(owner));

            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    private const string RefusedPrefix = "\u001f";

    /// <summary>"Saved vision pipeline Gate 1", "Uploaded vision model Gate people", "Turned Use GPU off": names only, never a file's content.</summary>
    public string? AuditDetail(string method, string? payloadJson)
    {
        string? Named(string? id) => id is null ? null : _auditNames.GetValueOrDefault(AuditKey(method, id));
        switch (method)
        {
            case VisionMethods.SavePipeline:
            {
                var pipeline = VisionJson.Deserialize<SavePipelineRequest>(payloadJson).Pipeline;
                var name = (pipeline is { Id.Length: > 0 } ? Named(pipeline.Id) : null) ?? pipeline?.Name?.Trim();
                return string.IsNullOrEmpty(name) ? "Saved a vision pipeline" : "Saved vision pipeline " + name;
            }

            case VisionMethods.DeletePipeline:
                return Named(VisionJson.Deserialize<PipelineRequest>(payloadJson).PipelineId) is { } deleted ? "Deleted vision pipeline " + deleted : "Deleted a vision pipeline";
            case VisionMethods.CopyPipeline:
            {
                var parts = Named(VisionJson.Deserialize<CopyPipelineRequest>(payloadJson).PipelineId)?.Split('\u001f');
                if (parts is not [var name, var count])
                {
                    return "Copied a vision pipeline";
                }

                return count == "1" ? $"Copied vision pipeline {name} to 1 camera" : $"Copied vision pipeline {name} to {count} cameras";
            }

            case VisionMethods.SetUseGpu:
                return VisionJson.Deserialize<UseGpuRequest>(payloadJson).UseGpu ? "Turned Use GPU on for Vision Center" : "Turned Use GPU off for Vision Center";
            case VisionMethods.FinishUpload:
            {
                var name = Named(VisionJson.Deserialize<UploadRequest>(payloadJson).UploadId) ?? string.Empty;
                return name.StartsWith(RefusedPrefix, StringComparison.Ordinal)
                    ? "Refused vision model " + name[RefusedPrefix.Length..]
                    : "Uploaded vision model " + name;
            }

            case VisionMethods.UpdateModel:
                return Named(VisionJson.Deserialize<UpdateModelRequest>(payloadJson).Model?.Id) is { } changed ? "Changed vision model " + changed : "Changed a vision model";
            case VisionMethods.DeleteModel:
                return Named(VisionJson.Deserialize<ModelRequest>(payloadJson).ModelId) is { } model ? "Deleted vision model " + model : "Deleted a vision model";
            case VisionMethods.SaveClassList:
            {
                var list = VisionJson.Deserialize<SaveClassListRequest>(payloadJson).ClassList;
                var name = (list is { Id.Length: > 0 } ? Named(list.Id) : null) ?? list?.Name?.Trim();
                return string.IsNullOrEmpty(name) ? "Saved a class list" : "Saved class list " + name;
            }

            case VisionMethods.DeleteClassList:
                return Named(VisionJson.Deserialize<ClassListRequest>(payloadJson).ClassListId) is { } deletedList ? "Deleted class list " + deletedList : "Deleted a class list";
            default:
                return null;
        }
    }

    private static string AuditKey(string method, string id) => method + "\u001f" + id;

    private static void RequireAllDevices(ICallContext caller)
    {
        if (!caller.HasAllDevices)
        {
            throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices); fail closed
        }
    }

    /// <summary>The pipeline of the request if the caller sees it; else <see cref="PipelineRules.Gone"/>, the same for an unknown id.</summary>
    private static Pipeline Visible(PipelineStore store, ICallContext caller, string? payloadJson) =>
        store.FindVisible(caller, VisionJson.Deserialize<PipelineRequest>(payloadJson).PipelineId) ?? throw new ArgumentException(PipelineRules.Gone);

    /// <summary>The names of the pipelines among <paramref name="pipelines"/> whose nodes use the model <paramref name="modelId"/>.</summary>
    private static IReadOnlyList<string> UsedBy(IEnumerable<Pipeline> pipelines, string? modelId) =>
        [.. pipelines.Where(p => PipelineRules.ModelsOf(p).Contains(modelId)).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The models with the pipelines among the caller's that use them (another group's names never show).</summary>
    private static ModelList ModelsOf(IModelLibrary models, IReadOnlyList<Pipeline> visible)
    {
        var all = models.All;
        var usedBy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var model in all)
        {
            if (UsedBy(visible, model.Id) is { Count: > 0 } names)
            {
                usedBy[model.Id] = names;
            }
        }

        return new ModelList { Models = [.. all.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)], UsedBy = usedBy };
    }

    /// <summary>The class lists with the models that use each (models are server-wide: every user sees their names).</summary>
    private static ClassListList ClassListsOf(IModelLibrary models)
    {
        var lists = models.ClassLists;
        var usedBy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            if (models.ModelsUsing(list.Id) is { Count: > 0 } names)
            {
                usedBy[list.Id] = names;
            }
        }

        return new ClassListList { Lists = lists, UsedBy = usedBy };
    }

    private EngineInfo EngineOf(PipelineRuntime runtime)
    {
        var inference = runtime.Inference;
        return new EngineInfo
        {
            RunsOn = inference.RunsOn is { Length: > 0 } runsOn ? runsOn : PipelineRules.CpuText(Environment.ProcessorCount),
            // With the setting off the card is not tried: the box stays usable, turning it on finds out.
            GpuAvailable = inference.GpuAvailable || !_useGpu,
            UseGpu = _useGpu,
            GpuStarting = runtime.Starting,
            FpsChoices = VisionLimits.FpsChoices,
        };
    }

    /// <summary>
    /// The video sources and picture sizes (largest first) of a camera in the caller's devices, read from the camera through
    /// the caller's device access and kept for <see cref="CameraOptionsLifetime"/>.
    /// </summary>
    private async Task<CameraOptions> CameraOptionsAsync(ICallContext caller, Guid deviceId, CancellationToken ct)
    {
        var camera = (await CallDevices.RequireAsync(caller, [deviceId], ct).ConfigureAwait(false))[0];
        var now = (_parts?.Time ?? TimeProvider.System).GetUtcNow();
        if (_cameraOptions.TryGetValue(deviceId, out var kept) && now - kept.At < CameraOptionsLifetime)
        {
            return kept.Options;
        }

        if (DeviceMessages.Refusal(camera) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var client = await caller.Vapix.CreateAsync(deviceId, ct).ConfigureAwait(false);
        var sources = await client.GetVideoSourcesAsync(ct).ConfigureAwait(false);
        var options = new CameraOptions
        {
            VideoSources = sources.Count > 0 ? [.. sources.Select(s => s.Camera).Distinct().Order()] : [1],
            Resolutions = [.. sources
                .SelectMany(s => s.Resolutions)
                .Where(r => r.Width is >= PipelineRules.MinSide and <= PipelineRules.MaxSide && r.Height is >= PipelineRules.MinSide and <= PipelineRules.MaxSide)
                .Distinct()
                .OrderByDescending(r => (long)r.Width * r.Height)
                .ThenByDescending(r => r.Width)
                .Select(r => new Resolution(r.Width, r.Height))],
        };
        if (_cameraOptions.Count >= MaxCachedCameras)
        {
            foreach (var old in _cameraOptions.OrderBy(o => o.Value.At).Take(_cameraOptions.Count - MaxCachedCameras + 1).ToList())
            {
                _cameraOptions.TryRemove(old.Key, out _);
            }
        }

        _cameraOptions[deviceId] = (now, options);
        return options;
    }

    /// <summary>The list; <paramref name="seesCamera"/> tells whether the watchers see a pipeline's camera (else its live line and problem stay blank).</summary>
    private static PipelineList ListOf(IEnumerable<Pipeline> pipelines, PipelineRuntime runtime, Func<Guid, bool> seesCamera) => new()
    {
        Pipelines = [.. pipelines.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => RowOf(p, runtime, seesCamera(p.DeviceId)))],
        MaxWatching = runtime.MaxWatching,
    };

    private static PipelineRow RowOf(Pipeline pipeline, PipelineRuntime runtime, bool seesCamera)
    {
        var problem = seesCamera ? runtime.ProblemOf(pipeline.Id) : PipelineRules.Off;
        var (reads, slow) = seesCamera ? runtime.RateOf(pipeline) : (null, false);
        return new PipelineRow
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            DeviceId = pipeline.DeviceId,
            Mode = pipeline.Mode,
            Fps = pipeline.Fps,
            Reads = reads,
            Slow = slow,
            Problem = problem,
            Last = seesCamera ? runtime.LiveOf(pipeline) : string.Empty,
            Starting = problem == PipelineRules.Starting,
        };
    }

    /// <summary>Object cut-outs and the held picture are pieces of the camera's picture: only with the host's Live view.</summary>
    private static bool MaySeePictures(ICallContext caller) => caller.Has(HostPermissionKeys.DevicesLiveView);

    /// <summary>The page's model list after a change (models are server-wide; "Used by" per audience).</summary>
    private void PublishModels()
    {
        if (_ctx?.Events is not { } events || _store is not { } store || _models is not { } models)
        {
            return;
        }

        var all = store.All;
        events.Publish(VisionMethods.ModelsTopic, VisionJson.Serialize(ModelsOf(models, all)), PluginEventAudience.AllDevices);
        foreach (var group in all.Select(p => OwnerScope.Of(p.OwnerScopeKey)).Where(o => o != ICallContext.AllDevicesScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mine = all.Where(p => string.Equals(OwnerScope.Of(p.OwnerScopeKey), group, StringComparison.OrdinalIgnoreCase)).ToList();
            events.Publish(VisionMethods.ModelsTopic, VisionJson.Serialize(ModelsOf(models, mine)), PluginEventAudience.DeviceScope(group));
        }
    }

    /// <summary>
    /// Tells the open pages the pipeline list after a change: users with all devices every pipeline, the users of each group
    /// among <paramref name="owners"/> their group's pipelines.
    /// </summary>
    private void PublishPipelines(params string?[] owners)
    {
        if (_ctx?.Events is not { } events || _store is not { } store || _runtime is not { } runtime)
        {
            return;
        }

        var all = store.All;
        events.Publish(VisionMethods.PipelinesTopic, VisionJson.Serialize(ListOf(all, runtime, _ => true)), PluginEventAudience.AllDevices);
        foreach (var group in owners.Select(OwnerScope.Of).Where(o => o != ICallContext.AllDevicesScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mine = all.Where(p => string.Equals(OwnerScope.Of(p.OwnerScopeKey), group, StringComparison.OrdinalIgnoreCase));
            var scopes = _ctx?.DeviceScopes;
            events.Publish(
                VisionMethods.PipelinesTopic,
                VisionJson.Serialize(ListOf(mine, runtime, id => scopes is not null && scopes.Contains(group, id))),
                PluginEventAudience.DeviceScope(group));
        }
    }

    /// <summary>The devices the Device event nodes of <paramref name="pipeline"/> name.</summary>
    private static List<Guid> EventDevicesOf(Pipeline pipeline) =>
        [.. (pipeline.Nodes ?? []).Where(n => n?.Kind == NodeKinds.DeviceEvent && n.Settings is not null)
            .Select(n => EventStateBook.ChoiceOf(n).Device).OfType<Guid>().Distinct()];

    /// <summary>
    /// The graph check with the rule of the Device event node: never one of Vision Center's own events (no loop), and for a
    /// user with a device group only an event of their devices.
    /// </summary>
    private GraphCheck CheckWithEvents(PipelineRuntime runtime, ICallContext caller, Pipeline pipeline)
    {
        var check = runtime.CheckOf(pipeline);
        var errors = new Dictionary<string, string>(check.NodeErrors, StringComparer.Ordinal);
        HashSet<string>? allowed = null;
        foreach (var node in pipeline.Nodes.Where(n => n.Kind == NodeKinds.DeviceEvent))
        {
            if (EventStateBook.ChoiceOf(node).Type is not { } type)
            {
                continue;
            }

            allowed ??= caller.HasAllDevices ? null : [.. ChoicesOf(caller, devices: null).Select(c => c.Id)];
            if (EventStateBook.IsOwn(type) || (allowed is not null && !allowed.Contains(type)))
            {
                errors[node.Id] = EventStateBook.OwnEventRefused;
            }
        }

        return errors.Count == check.NodeErrors.Count ? check : check with { NodeErrors = errors };
    }

    /// <summary>The on and off events a Device event node of <paramref name="caller"/> may follow.</summary>
    private async Task<EventTypeList> EventTypesAsync(ICallContext caller, CancellationToken ct)
    {
        HashSet<Guid>? devices = null;
        if (!caller.HasAllDevices)
        {
            devices = [.. (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id)];
        }

        return new EventTypeList { Types = ChoicesOf(caller, devices) };
    }

    /// <summary>
    /// The stateful types of the event rules other than Vision Center's own. A user with a device group gets only events per
    /// device that nobody else owns and that one of their devices can send (<paramref name="devices"/>; null: not checked).
    /// </summary>
    private List<EventTypeChoice> ChoicesOf(ICallContext caller, HashSet<Guid>? devices)
    {
        var source = _ctx is { } ctx ? _parts?.EventSubscriptions(ctx) : null;
        var choices = new List<EventTypeChoice>();
        foreach (var type in source?.Types ?? [])
        {
            var info = type.Info;
            if (info.Kind != Oadm.Sdk.Events.EventKind.Stateful || EventStateBook.IsOwn(info.Id))
            {
                continue;
            }

            if (!caller.HasAllDevices
                && (info.Scope != Oadm.Sdk.Events.EventScope.Device
                    || (info.OwnerScopeKey is { } owner && !string.Equals(owner, caller.DeviceScopeKey, StringComparison.OrdinalIgnoreCase))
                    || (devices is not null && info.DeviceIds is { } ids && !ids.Any(devices.Contains))))
            {
                continue;
            }

            choices.Add(new EventTypeChoice
            {
                Id = info.Id,
                Name = info.DisplayName,
                Path = info.Folders,
                PerDevice = info.Scope == Oadm.Sdk.Events.EventScope.Device,
                Available = type.Available,
            });
        }

        return choices;
    }

    /// <summary>
    /// The copies of <paramref name="original"/> on <paramref name="deviceIds"/> (docs/spec/vision-center.md "Copy to cameras"):
    /// refused per camera (its own camera, a camera the caller does not see, a failed check, the pipeline limit); each
    /// camera's options read once, a few at a time; all copies stored in one batch, then one sync and one change of the
    /// event types and the list.
    /// </summary>
    private async Task<CopyPipelineReply> CopyAsync(ICorePluginContext ctx, ICallContext caller, PipelineStore store, PipelineRuntime runtime, Pipeline original, IReadOnlyList<Guid> deviceIds, CancellationToken ct)
    {
        var ids = deviceIds.Distinct().ToList();
        if (ids.Count > PipelineRules.MaxCopyCameras)
        {
            throw new ArgumentException(PipelineRules.TooManyCopyCameras);
        }

        var refused = new Dictionary<Guid, string>();
        var cameras = new List<Oadm.Sdk.Devices.IDeviceInfo>();
        foreach (var id in ids)
        {
            if (id == original.DeviceId)
            {
                refused[id] = PipelineRules.OwnCamera;
            }
            else if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is { } camera)
            {
                cameras.Add(camera);
            }
            else
            {
                refused[id] = PipelineRules.CameraNotAvailable; // another group's camera answers like an unknown one
            }
        }

        var options = new CameraOptions?[cameras.Count];
        using (var gate = new SemaphoreSlim(PipelineCopies.OptionsAtOnce))
        {
            await Task.WhenAll(cameras.Select(async (camera, i) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    options[i] = await CameraOptionsAsync(caller, camera.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    options[i] = null; // the camera's defaults
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        var copies = cameras.Select((camera, i) => PipelineCopies.CopyOf(original, camera, options[i])).ToList();
        var (created, notStored) = await store.AddCopiesAsync(ctx, caller, copies, p => CheckWithEvents(runtime, caller, p), ct).ConfigureAwait(false);
        foreach (var (id, error) in notStored)
        {
            refused[id] = error;
        }

        if (created.Count > 0)
        {
            EventTypesChanged?.Invoke(this, EventArgs.Empty);
            await runtime.SyncAsync().ConfigureAwait(false);
            ReportProblems();
            PublishPipelines(created[0].OwnerScopeKey);
            PublishModels();
        }

        // In the order of the cameras asked for.
        var order = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        return new CopyPipelineReply
        {
            Created = [.. created.OrderBy(p => order[p.DeviceId]).Select(p => p with { OwnerScopeKey = null })],
            Refused = refused,
        };
    }
}
