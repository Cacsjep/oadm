using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr;

/// <summary>
/// The page backend (<see cref="OcrMethods"/>). Device groups (docs/spec/device-scope.md): every method works on the
/// readers the caller's device scope owns (users with all devices: every reader); another scope's reader answers like an
/// unknown id; a save takes only a camera of the caller's and the owner's devices; a preview and the last reading need the
/// reader's camera in the caller's devices. The work on the cameras runs without the call (<see cref="ReaderRuntime"/>).
/// </summary>
public sealed partial class OcrPlugin
{
    /// <summary>A user's call.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ctx = _ctx ?? throw new InvalidOperationException(NotRunning);
        var store = _store ?? throw new InvalidOperationException(NotRunning);
        var runtime = _runtime ?? throw new InvalidOperationException(NotRunning);
        switch (method)
        {
            case OcrMethods.ListReaders:
                return OcrJson.Serialize(ListOf(store.Visible(caller), runtime));
            case OcrMethods.GetReader:
                return OcrJson.Serialize(Visible(store, caller, payloadJson) with { OwnerScopeKey = null });
            case OcrMethods.SaveReader:
            {
                var incoming = OcrJson.Deserialize<SaveReaderRequest>(payloadJson).Reader ?? throw new ArgumentException("Missing payload.", nameof(payloadJson));
                Reader saved;
                Reader? before;
                try
                {
                    (saved, before) = await store.SaveAsync(ctx, caller, incoming, runtime.OnGpu, ctx.System.Devices.FindAsync, ct).ConfigureAwait(false);
                }
                catch (ReaderFieldException ex) when (ex.Errors.Count > 0)
                {
                    return OcrJson.Serialize(new SaveReaderReply { Errors = ex.Errors });
                }

                if (before is not null && !SamePicture(before, saved))
                {
                    runtime.Forget(saved.Id);
                }

                await runtime.SyncAsync(store.All).ConfigureAwait(false);
                EventTypesChanged?.Invoke(this, EventArgs.Empty);
                PublishReaders(saved.OwnerScopeKey, before?.OwnerScopeKey);
                return OcrJson.Serialize(new SaveReaderReply { Reader = saved with { OwnerScopeKey = null } });
            }

            case OcrMethods.DeleteReader:
            {
                var id = OcrJson.Deserialize<ReaderRequest>(payloadJson).ReaderId;
                if (await store.DeleteAsync(caller, id, ct).ConfigureAwait(false) is { } deleted)
                {
                    await runtime.SyncAsync(store.All).ConfigureAwait(false);
                    runtime.Remove(deleted.Id);
                    EventTypesChanged?.Invoke(this, EventArgs.Empty);
                    PublishReaders(deleted.OwnerScopeKey);
                }

                return "{}";
            }

            case OcrMethods.StartPreview:
            {
                var request = OcrJson.Deserialize<PreviewRequest>(payloadJson);
                var draft = ReaderRules.PreviewOf(request.Reader ?? throw new ArgumentException("Missing payload.", nameof(payloadJson)), runtime.OnGpu);
                if (draft.Id.Length > 0 && store.FindVisible(caller, draft.Id) is null)
                {
                    throw new ArgumentException(ReaderRules.Gone);
                }

                await CallDevices.RequireAsync(caller, [draft.DeviceId], ct).ConfigureAwait(false);
                return OcrJson.Serialize(await runtime.StartPreviewAsync(request.PreviewId, draft, OwnerScope.Of(caller.DeviceScopeKey)).ConfigureAwait(false));
            }

            case OcrMethods.StopPreview:
                await runtime.StopPreviewAsync(OcrJson.Deserialize<PreviewRequest>(payloadJson).PreviewId, OwnerScope.Of(caller.DeviceScopeKey)).ConfigureAwait(false);
                return "{}";

            case OcrMethods.GetPreview:
            {
                if (runtime.PreviewOf(OcrJson.Deserialize<PreviewRequest>(payloadJson).PreviewId, OwnerScope.Of(caller.DeviceScopeKey)) is not { } preview)
                {
                    return OcrJson.Serialize(new ReadingReply());
                }

                await CallDevices.RequireAsync(caller, [preview.Reader.DeviceId], ct).ConfigureAwait(false);
                return OcrJson.Serialize(preview.Last is { } last ? ReplyOf(last) with { Jpeg = null } : new ReadingReply());
            }

            case OcrMethods.GetReading:
            {
                var reader = Visible(store, caller, payloadJson);
                await CallDevices.RequireAsync(caller, [reader.DeviceId], ct).ConfigureAwait(false);
                return OcrJson.Serialize(runtime.Last(reader.Id) is { } last ? ReplyOf(last) : new ReadingReply());
            }

            case OcrMethods.GetEngine:
                return OcrJson.Serialize(EngineOf(runtime));

            case OcrMethods.SetUseGpu:
            {
                if (!caller.HasAllDevices)
                {
                    throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices); fail closed
                }

                var useGpu = OcrJson.Deserialize<UseGpuRequest>(payloadJson).UseGpu;
                await ctx.Settings.SetAsync(UseGpuSetting, useGpu ? "true" : "false", ct).ConfigureAwait(false);
                _useGpu = useGpu;
                runtime.Engine?.SetUseGpu(useGpu);

                // The limits follow what reads now (watching readers, frame rates).
                await runtime.SyncAsync(store.All).ConfigureAwait(false);
                PublishReaders([.. store.All.Select(r => r.OwnerScopeKey)]);
                return OcrJson.Serialize(EngineOf(runtime));
            }

            case OcrMethods.GetCameraOptions:
                return OcrJson.Serialize(await CameraOptionsAsync(caller, OcrJson.Deserialize<CameraOptionsRequest>(payloadJson).DeviceId, ct).ConfigureAwait(false));

            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>The plugin setting "Use the graphics card" (JSON true or false; on when missing).</summary>
    internal const string UseGpuSetting = "useGpu";

    /// <summary>How long the video sources and sizes of a camera are kept (the page reads them on every open).</summary>
    internal static readonly TimeSpan CameraOptionsLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Cameras whose options are kept at most; beyond, the oldest go.</summary>
    internal const int MaxCachedCameras = 200;

    /// <summary>"Runs on", the graphics card and the setting, and the frame rates offered now.</summary>
    private EngineInfo EngineOf(ReaderRuntime runtime)
    {
        var engine = runtime.Engine;
        return new EngineInfo
        {
            RunsOn = engine?.RunsOn is { Length: > 0 } runsOn ? runsOn : ReaderRules.CpuText(Environment.ProcessorCount),
            GpuAvailable = engine?.GpuAvailable ?? false,
            UseGpu = _useGpu,
            FpsChoices = ReaderRules.FpsChoices(runtime.OnGpu),
        };
    }

    /// <summary>
    /// The video sources and picture sizes (largest first, every source's together) of a camera in the caller's devices,
    /// read only from the camera's parameters through the caller's device access and kept for
    /// <see cref="CameraOptionsLifetime"/>. A camera OADM cannot contact refuses with the device's text.
    /// </summary>
    private async Task<CameraOptions> CameraOptionsAsync(ICallContext caller, Guid deviceId, CancellationToken ct)
    {
        var camera = (await CallDevices.RequireAsync(caller, [deviceId], ct).ConfigureAwait(false))[0];
        var now = _parts.Time.GetUtcNow();
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
            VideoSources = [.. sources.Select(s => new CameraSource { Number = s.Camera, Name = s.Name })],
            Resolutions = [.. sources
                .SelectMany(s => s.Resolutions)
                .Where(r => r.Width is >= ReaderRules.MinSide and <= ReaderRules.MaxSide && r.Height is >= ReaderRules.MinSide and <= ReaderRules.MaxSide)
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

    /// <summary>The reader of the request if the caller sees it; else <see cref="ReaderRules.Gone"/>, the same for an unknown id.</summary>
    private static Reader Visible(ReaderStore store, ICallContext caller, string? payloadJson) =>
        store.FindVisible(caller, OcrJson.Deserialize<ReaderRequest>(payloadJson).ReaderId) ?? throw new ArgumentException(ReaderRules.Gone);

    private static ReadingReply ReplyOf(ReaderReading reading) =>
        new() { Reading = reading.Reading, Jpeg = reading.Jpeg.ToArray(), Details = reading.Details };

    private static ReaderList ListOf(IEnumerable<Reader> readers, ReaderRuntime runtime) => new()
    {
        Readers = [.. readers.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => SummaryOf(r, runtime))],
        FpsChoices = ReaderRules.FpsChoices(runtime.OnGpu),
        MaxWatching = ReaderRules.MaxWatching(runtime.OnGpu),
    };

    private static ReaderSummary SummaryOf(Reader reader, ReaderRuntime runtime)
    {
        var last = runtime.Last(reader.Id)?.Reading;
        return new ReaderSummary
        {
            Id = reader.Id,
            Name = reader.Name,
            DeviceId = reader.DeviceId,
            Mode = reader.Mode,
            Fps = reader.Fps,
            LastText = last is { Areas.Count: > 0 } ? last.Areas[0].Text : string.Empty,
            LastTime = last?.Time,
            Problem = runtime.ProblemOf(reader.Id),
        };
    }

    /// <summary>Whether the last reading still fits: the same camera, source, resolution and areas.</summary>
    private static bool SamePicture(Reader before, Reader now) =>
        OcrJson.Serialize(before with { Name = string.Empty, Mode = ReaderMode.OnAction, Fps = 0 })
        == OcrJson.Serialize(now with { Name = string.Empty, Mode = ReaderMode.OnAction, Fps = 0 });

    /// <summary>
    /// Tells the open pages the reader list after a change: users with all devices every reader, the users of each group
    /// among <paramref name="owners"/> their group's readers.
    /// </summary>
    private void PublishReaders(params string?[] owners)
    {
        if (_ctx?.Events is not { } events || _store is not { } store || _runtime is not { } runtime)
        {
            return;
        }

        var all = store.All;
        events.Publish(OcrMethods.ReadersTopic, OcrJson.Serialize(ListOf(all, runtime)), PluginEventAudience.AllDevices);
        foreach (var group in owners.Select(OwnerScope.Of).Where(o => o != ICallContext.AllDevicesScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mine = all.Where(r => string.Equals(OwnerScope.Of(r.OwnerScopeKey), group, StringComparison.OrdinalIgnoreCase));
            events.Publish(OcrMethods.ReadersTopic, OcrJson.Serialize(ListOf(mine, runtime)), PluginEventAudience.DeviceScope(group));
        }
    }
}
