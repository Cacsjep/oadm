using Oadm.Plugins.LoadTest.Checking;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest;

/// <summary>
/// Core plugin "Load test" (docs/spec/load-test.md): a load test of the chosen cameras and the network
/// between them and the server. Batch after batch, more and more streams of the highest quality stream at once, then all
/// of them in low light; the server measures packet loss, frame loss, late frames, rate and jitter per stream and second.
/// The only write is the exposure value during Low light, always written back (<see cref="ExposureKeeper"/>). One check at
/// a time per server; the last <see cref="LoadTestInfo.MaxRuns"/> runs are kept. Off by default (plugin.json).
/// Page backend methods: <see cref="LoadTestMethods"/>.
/// </summary>
public sealed class LoadTestPlugin : ICorePlugin, IAsyncDisposable
{
    private const string NotRunning = "The Load test plugin is not running.";
    private readonly LoadTestOptions _options;
    private CheckService? _service;
    private ICorePluginContext? _ctx;

    public LoadTestPlugin()
        : this(new LoadTestOptions())
    {
    }

    public LoadTestPlugin(LoadTestOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => LoadTestInfo.PluginId;

    public string DisplayName => LoadTestInfo.DisplayName;

    public string? IconKey => LoadTestInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>The running service (tests).</summary>
    internal CheckService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new CheckService(ctx, _options);
        await service.LoadAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _service = service;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Nothing changes the server's configuration: every method is for operators, none is audited.</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    public bool IsAudited(string method) => false;

    /// <summary>Starting a check streams from the cameras and changes their exposure for a while: a right of its own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(LoadTestInfo.RunPermission, "Run load tests", "Start, cancel and delete checks. A check changes the exposure of the cameras for a few minutes."),
    ];

    /// <summary>Plan, the runs and their graphs: "show" (Viewer on this Monitoring page); Start, Cancel, Delete: "run".</summary>
    public string? RequiredPermission(string method) =>
        method is LoadTestMethods.Start or LoadTestMethods.Cancel or LoadTestMethods.DeleteRun ? LoadTestInfo.RunPermission : PermissionKeys.Show;

    /// <summary>A user's call: the chosen cameras must be the caller's; runs of the caller's device scope.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException(NotRunning);
        return method switch
        {
            LoadTestMethods.Plan => LoadTestJson.Serialize(await service.PlanAsync(caller, LoadTestJson.Deserialize<PlanRequest>(payloadJson), ct).ConfigureAwait(false)),
            LoadTestMethods.Start => LoadTestJson.Serialize(await service.StartAsync(caller, LoadTestJson.Deserialize<StartRequest>(payloadJson), ct).ConfigureAwait(false)),
            LoadTestMethods.Cancel => await service.CancelAsync(caller).ConfigureAwait(false) is { } cancelled ? LoadTestJson.Serialize(cancelled) : null,
            LoadTestMethods.ListRuns => LoadTestJson.Serialize(service.ListRuns(caller)),
            LoadTestMethods.GetRun => LoadTestJson.Serialize(await service.GetRunAsync(caller, LoadTestJson.Deserialize<RunRequest>(payloadJson), ct).ConfigureAwait(false)),
            LoadTestMethods.GetStream => LoadTestJson.Serialize(await service.GetStreamAsync(caller, LoadTestJson.Deserialize<StreamRequest>(payloadJson), ct).ConfigureAwait(false)),
            LoadTestMethods.DeleteRun => await DeleteAsync(service, caller, payloadJson, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(NotRunning)), method, payloadJson, ct);

    private static async Task<string?> DeleteAsync(CheckService service, ICallContext caller, string? payloadJson, CancellationToken ct)
    {
        await service.DeleteRunAsync(caller, LoadTestJson.Deserialize<RunRequest>(payloadJson), ct).ConfigureAwait(false);
        return "{}";
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
