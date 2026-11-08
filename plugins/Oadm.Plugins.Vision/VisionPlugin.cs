using System.Collections.Concurrent;

using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision;

/// <summary>
/// Core plugin "Vision Center" (docs/spec/vision-center.md): pipelines of nodes on the user's own ONNX models, read only for
/// devices. An add-on (docs/spec/add-ons.md), off by default (plugin.json). The saved pipelines (<see cref="PipelineStore"/>)
/// run in <see cref="PipelineRuntime"/>; each Event node publishes its events (<see cref="VisionEventTypes"/>), each pipeline
/// "Pipeline problem", so the types change with the pipelines; the actions Run pipeline and Reset counter. Page backend
/// methods: <see cref="VisionMethods"/> (Api/VisionPlugin.Methods.cs).
/// </summary>
public sealed partial class VisionPlugin : ICorePlugin, IDynamicEventSource, IActionSource, IAsyncDisposable
{
    private readonly Func<VisionParts> _makeParts;
    private string? _lastLive;
    private ICorePluginContext? _ctx;
    private VisionParts? _parts;
    private PipelineStore? _store;
    private PipelineRuntime? _runtime;
    private IModelLibrary? _models;
    private PipelineProblemReporter? _problems;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, CameraOptions Options)> _cameraOptions = new();
    private volatile bool _useGpu = true;

    /// <summary>The plugin as the host loads it: the real models, inference and camera pictures (<see cref="VisionParts.Real"/>).</summary>
    public VisionPlugin()
    {
        _makeParts = () => VisionParts.Real();
        ActionPlugins = [new RunPipelineAction(this), new ResetCounterAction(this)];
    }

    /// <summary>The plugin with its parts (tests).</summary>
    internal VisionPlugin(VisionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _makeParts = () => parts;
        ActionPlugins = [new RunPipelineAction(this), new ResetCounterAction(this)];
    }

    public event EventHandler? EventTypesChanged;

    public string Id => VisionInfo.PluginId;

    public string DisplayName => VisionInfo.DisplayName;

    public string? IconKey => VisionInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Vision;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>"Run pipeline" and "Reset counter".</summary>
    public IReadOnlyList<IActionPlugin> ActionPlugins { get; }

    /// <summary>The events of every Event node, "Pipeline problem" of every pipeline and "Any pipeline problem"; none while stopped.</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes
    {
        get
        {
            if (_store is null || _runtime is not { } runtime)
            {
                return [];
            }

            var pipelines = AllPipelines;
            return
            [
                .. pipelines.SelectMany(p => VisionEventTypes.TypesOf(p, runtime.CheckOf(p))),
                .. pipelines.Select(PipelineProblemEvents.TypeOf),
                PipelineProblemEvents.AnyType,
            ];
        }
    }

    /// <summary>Every pipeline, whoever owns it (the actions, the event types); none while stopped.</summary>
    internal IReadOnlyList<Pipeline> AllPipelines => _store?.All ?? [];

    /// <summary>The pipelines at work (tests).</summary>
    internal PipelineRuntime? Runtime => _runtime;

    internal Pipeline? FindPipeline(string? id) => _store?.Find(id);

    /// <summary>Runs one picture of <paramref name="pipeline"/> now (<see cref="PipelineRuntime.RunOnceAsync"/>).</summary>
    internal Task<FrameResult> RunOnceAsync(Pipeline pipeline, CancellationToken ct) =>
        (_runtime ?? throw new InvalidOperationException(PipelineRules.NotRunning)).RunOnceAsync(pipeline, ct);

    internal Task<bool> ResetCounterAsync(string pipelineId, string nodeId) =>
        (_runtime ?? throw new InvalidOperationException(PipelineRules.NotRunning)).ResetCounterAsync(pipelineId, nodeId);

    /// <summary>Starts ONNX Runtime on the processor and the picture library: what a pipeline needs before its first model.</summary>
    public string? SelfCheck(string pluginDirectory)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        if (!Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders().Contains("CPUExecutionProvider", StringComparer.Ordinal))
        {
            return "ONNX Runtime has no processor provider.";
        }

        using var picture = new SkiaSharp.SKBitmap(1, 1);
        return null;
    }

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var records = ctx.Records ?? throw new InvalidOperationException("This OADM server cannot keep vision pipelines. Update OADM.");
        var parts = _makeParts();
        var store = new PipelineStore(records, ctx.Logger);
        await store.LoadAsync(ct).ConfigureAwait(false);
        var models = parts.Models(ctx);
        await models.LoadAsync(ct).ConfigureAwait(false);
        var inference = parts.Inference(ctx);
        var frames = parts.Frames(ctx);
        _ctx = ctx;
        _parts = parts;
        _store = store;
        _models = models;
        _useGpu = await ctx.Settings.GetAsync(UseGpuSetting, ct).ConfigureAwait(false) is not { } stored
            || !bool.TryParse(stored.Trim(), out var useGpu) || useGpu;
        _cameraOptions.Clear();
        var runtime = new PipelineRuntime(ctx, parts, models, inference, frames, new VisionEventSink(ctx.EventPublisher), () => store.All);
        _runtime = runtime;
        _problems = ctx.EventPublisher is { } publisher ? new PipelineProblemReporter(publisher) : null;
        runtime.ProblemsChanged += (_, _) =>
        {
            PublishPipelines([.. AllPipelines.Select(p => p.OwnerScopeKey)]);
            ReportProblems();
        };
        runtime.LiveChanged += (_, _) =>
        {
            // Only a live line that changed reaches the pages (at most every LiveEvery).
            var all = AllPipelines;
            var live = string.Join('\u001e', all.Select(p => p.Id + "\u001f" + runtime.LiveOf(p)));
            if (!string.Equals(Interlocked.Exchange(ref _lastLive, live), live, StringComparison.Ordinal))
            {
                PublishPipelines([.. all.Select(p => p.OwnerScopeKey)]);
            }
        };
        models.Changed += OnModelsChanged;
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnScopeChanged;
        }

        EventTypesChanged?.Invoke(this, EventArgs.Empty);
        runtime.StartEngine(_useGpu, () =>
        {
            if (ReferenceEquals(_runtime, runtime))
            {
                PublishPipelines([.. AllPipelines.Select(p => p.OwnerScopeKey)]);
                ReportProblems();
            }
        });
        await runtime.SyncAsync().ConfigureAwait(false);
        ReportProblems();
    }

    /// <summary>Stops every pipeline (also when the plugin is turned off); the pipelines stay stored.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_ctx?.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnScopeChanged;
        }

        if (_models is { } models)
        {
            models.Changed -= OnModelsChanged;
        }

        var runtime = _runtime;
        var store = _store;
        _runtime = null;
        _problems = null;
        _store = null;
        _models = null;
        _ctx = null;
        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            await DisposeOf(runtime.Inference).ConfigureAwait(false);
        }

        store?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static async Task DisposeOf(object part)
    {
        switch (part)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>Tells the event rules of the pipelines' problems; the types of a saved pipeline become ready with it.</summary>
    private void ReportProblems()
    {
        if (_runtime is { } runtime)
        {
            _problems?.Report(AllPipelines, runtime.ProblemOf);
        }
    }

    /// <summary>A model changed (uploaded, changed, deleted, back after a restore): the checks, the event types and the loops follow.</summary>
    private void OnModelsChanged(object? sender, EventArgs e)
    {
        if (_runtime is not { } runtime)
        {
            return;
        }

        _ = runtime.QuietlyAsync(
            async () =>
            {
                EventTypesChanged?.Invoke(this, EventArgs.Empty);
                await runtime.SyncAsync().ConfigureAwait(false);
                ReportProblems();
                PublishModels();
                PublishPipelines([.. AllPipelines.Select(p => p.OwnerScopeKey)]);
            },
            "following a change of the models");
    }

    /// <summary>A group's rules changed or the group was deleted: its pipelines follow its devices at once (fail closed).</summary>
    private void OnScopeChanged(object? sender, string scopeKey)
    {
        if (_runtime is not { } runtime)
        {
            return;
        }

        _ = runtime.QuietlyAsync(
            async () =>
            {
                await runtime.SyncAsync().ConfigureAwait(false);
                ReportProblems();
                PublishPipelines(scopeKey);
            },
            "following a device group's change");
    }

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, VisionInfo.ViewPermissionName, "See the vision pipelines and what they find.") { ViewerDefault = true },
        new(VisionInfo.ChangePermission, VisionInfo.ChangePermissionName, "Create, change and delete vision pipelines."),
        new(VisionInfo.ModelsPermission, VisionInfo.ModelsPermissionName, "Upload, change and delete vision models.") { OperatorDefault = false },
    ];

    /// <summary>Nothing changes the server beyond the plugin's own pipelines and models: no method needs a role, the permissions decide.</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    /// <summary>Pipelines: "change"; models: "models"; the rest (the page's preview too): "show".</summary>
    public string? RequiredPermission(string method) => method switch
    {
        VisionMethods.SavePipeline or VisionMethods.DeletePipeline or VisionMethods.SetUseGpu => VisionInfo.ChangePermission,
        _ when IsModelMethod(method) => VisionInfo.ModelsPermission,
        _ => PermissionKeys.Show,
    };

    /// <summary>"Use GPU" and the models are server-wide: only users with all devices.</summary>
    public bool RequiresAllDevices(string method) => method is VisionMethods.SetUseGpu || IsModelMethod(method);

    /// <summary>A save counts the watching pipelines per site over every pipeline's camera, showing no device data.</summary>
    public string? SystemAccessReason(string method) => method is VisionMethods.SavePipeline
        ? "counts the watching pipelines per site (a limit of the site node), shows no device data"
        : null;

    public bool IsAudited(string method) => method is VisionMethods.SavePipeline or VisionMethods.DeletePipeline or VisionMethods.SetUseGpu
        or VisionMethods.FinishUpload or VisionMethods.UpdateModel or VisionMethods.DeleteModel;

    private static bool IsModelMethod(string method) => method is VisionMethods.BeginUpload or VisionMethods.UploadChunk or VisionMethods.FinishUpload
        or VisionMethods.CancelUpload or VisionMethods.UpdateModel or VisionMethods.DeleteModel;

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(PipelineRules.NotRunning)), method, payloadJson, ct);
}
