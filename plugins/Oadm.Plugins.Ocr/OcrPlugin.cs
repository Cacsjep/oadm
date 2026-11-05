using System.Collections.Concurrent;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr;

/// <summary>
/// Core plugin "Text recognition" (docs/spec/ocr.md): reads text in the cameras' pictures and hands it to the event rules.
/// An add-on (docs/spec/add-ons.md), off by default (plugin.json). Read only for devices. The saved readers
/// (<see cref="ReaderStore"/>, the only thing it stores) are at work in <see cref="ReaderRuntime"/>; each reader publishes
/// the events "Text read" (<see cref="TextReadEvents"/>) and "Reader problem" (<see cref="ReaderProblemEvents"/>), one type of
/// each per reader, so the types change with the readers, and the action "Read text" (<see cref="ReadTextAction"/>) reads one
/// picture of a reader now. Page backend methods:
/// <see cref="OcrMethods"/> (Api/OcrPlugin.Methods.cs).
/// </summary>
public sealed partial class OcrPlugin : ICorePlugin, IDynamicEventSource, IActionSource, IAsyncDisposable
{
    private readonly ReaderParts _parts;
    private ICorePluginContext? _ctx;
    private ReaderStore? _store;
    private ReaderRuntime? _runtime;
    private ReaderProblemReporter? _problems;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, CameraOptions Options)> _cameraOptions = new();
    private volatile bool _useGpu = true;

    /// <summary>Starts ONNX Runtime on a model of the package, on the processor: what a reader needs before its first picture.</summary>
    public string? SelfCheck(string pluginDirectory)
    {
        var model = Path.Combine(Recognition.ModelFiles.Folder(pluginDirectory), Recognition.ModelFiles.DetectionMobile);
        if (!File.Exists(model))
        {
            return "The model " + Path.GetFileName(model) + " is missing.";
        }

        using var session = Recognition.SessionFactory.Create(model, Recognition.ExecutionTarget.Cpu, threads: 1);
        return null;
    }

    /// <summary>The plugin as the host loads it: the real recognition, matcher, camera pictures and cut-outs (<see cref="ReaderParts.Real"/>).</summary>
    public OcrPlugin()
        : this(ReaderParts.Real())
    {
    }

    /// <summary>The plugin with its parts (tests).</summary>
    internal OcrPlugin(ReaderParts parts)
    {
        _parts = parts ?? throw new ArgumentNullException(nameof(parts));
        ActionPlugins = [new ReadTextAction(this)];
    }

    public event EventHandler? EventTypesChanged;

    public string Id => OcrInfo.PluginId;

    public string DisplayName => OcrInfo.DisplayName;

    public string? IconKey => OcrInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Vision;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>"Read text".</summary>
    public IReadOnlyList<IActionPlugin> ActionPlugins { get; }

    /// <summary>The events "Text read", "No text read" and "Reader problem" of every reader and "Any reader problem"; none while the plugin is stopped.</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes => _store is null
        ? []
        : [.. AllReaders.Select(TextReadEvents.TypeOf), .. AllReaders.Select(TextReadEvents.NoTextTypeOf), .. AllReaders.Select(ReaderProblemEvents.TypeOf), ReaderProblemEvents.AnyType];

    /// <summary>Every reader, whoever owns it (the action, the event types); none while the plugin is stopped.</summary>
    internal IReadOnlyList<Reader> AllReaders => _store?.All ?? [];

    /// <summary>The readers at work (tests).</summary>
    internal ReaderRuntime? Runtime => _runtime;

    /// <summary>The reader of <paramref name="id"/>, whoever owns it; null when there is none.</summary>
    internal Reader? FindReader(string? id) => _store?.Find(id);

    /// <summary>Reads one picture of <paramref name="reader"/> now and publishes the event of every area (<see cref="ReaderRuntime.ReadOnceAsync"/>).</summary>
    internal Task<ReaderReading> ReadOnceAsync(Reader reader, CancellationToken ct) =>
        (_runtime ?? throw new InvalidOperationException(ReaderRules.NotRunning)).ReadOnceAsync(reader, ct);

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var records = ctx.Records ?? throw new InvalidOperationException("This OADM server cannot keep text readers. Update OADM.");
        var store = new ReaderStore(records, ctx.Logger);
        await store.LoadAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _store = store;
        _useGpu = await ctx.Settings.GetAsync(UseGpuSetting, ct).ConfigureAwait(false) is not { } stored
            || !bool.TryParse(stored.Trim(), out var useGpu) || useGpu;
        _cameraOptions.Clear();
        var runtime = new ReaderRuntime(ctx, _parts, ctx.Logger, () => store.All);
        _runtime = runtime;
        _problems = ctx.EventPublisher is { } publisher ? new ReaderProblemReporter(publisher) : null;
        runtime.ProblemsChanged += (_, _) =>
        {
            PublishReaders([.. AllReaders.Select(r => r.OwnerScopeKey)]);
            ReportProblems();
        };
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnScopeChanged;
        }

        EventTypesChanged?.Invoke(this, EventArgs.Empty);

        // The graphics card's test never holds up the start: the readers wait for it, the page says Starting.
        runtime.StartEngine(_useGpu, () =>
        {
            if (ReferenceEquals(_runtime, runtime))
            {
                PublishReaders([.. AllReaders.Select(r => r.OwnerScopeKey)]);
                ReportProblems();
            }
        });
        await runtime.SyncAsync().ConfigureAwait(false);
        ReportProblems();
    }

    /// <summary>Tells the event rules of the readers' problems; the types of a saved reader become ready with it.</summary>
    private void ReportProblems()
    {
        if (_runtime is { } runtime)
        {
            _problems?.Report(AllReaders, runtime.ProblemOf);
        }
    }

    /// <summary>Stops every reader's loop (also when the plugin is turned off); the readers stay stored.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_ctx?.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnScopeChanged;
        }

        var runtime = _runtime;
        var store = _store;
        _runtime = null;
        _problems = null;
        _store = null;
        _ctx = null;
        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }

        store?.Dispose();
    }

    /// <summary>Nothing changes the server's configuration beyond the plugin's own readers: no method is for administrators only.</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    /// <summary>A save counts the watching readers per site over every reader's camera (a limit of the site), showing no device data.</summary>
    public string? SystemAccessReason(string method) => method is OcrMethods.SaveReader
        ? "counts the watching readers per site (a limit of the site node), shows no device data"
        : null;

    /// <summary>"Use GPU" is server-wide: only users with all devices (the host refuses the others first).</summary>
    public bool RequiresAllDevices(string method) => method is OcrMethods.SetUseGpu;

    public bool IsAudited(string method) => method is OcrMethods.SaveReader or OcrMethods.DeleteReader or OcrMethods.ReadNow or OcrMethods.SetUseGpu;

    /// <summary>The page reads only (Viewer on); changing readers is a right of its own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, OcrInfo.ViewPermissionName, "See the text readers and what they read.") { ViewerDefault = true },
        new(OcrInfo.ChangePermission, OcrInfo.ChangePermissionName, "Create, change, delete and run text readers."),
    ];

    /// <summary>New, Delete, Save, Read now and Use GPU: "change"; the rest (the page's preview too): "show".</summary>
    public string? RequiredPermission(string method) =>
        method is OcrMethods.SaveReader or OcrMethods.DeleteReader or OcrMethods.ReadNow or OcrMethods.SetUseGpu ? OcrInfo.ChangePermission : PermissionKeys.Show;

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(ReaderRules.NotRunning)), method, payloadJson, ct);

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>A group's rules changed or the group was deleted: its readers follow its devices at once (fail closed).</summary>
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
                PublishReaders(scopeKey);
            },
            "following a device group's change");
    }
}
