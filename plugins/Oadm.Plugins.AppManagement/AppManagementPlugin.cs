using Oadm.Plugins.AppManagement.Reading;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.AppManagement;

/// <summary>
/// Core plugin "Application management" (docs/spec/app-management.md), rail page Applications: the applications (ACAP) of
/// the chosen devices, read only on demand and kept per login session in memory, and the Applications plugin's actions for
/// many rows at once. The page changes nothing itself: Install, Install licenses, Start, Stop and Remove are <c>oadm.acap</c> tasks started through
/// the caller's task runner (Install and Install licenses from the page's own dialogs). Off by default
/// (plugin.json). Page backend methods: <see cref="AppManagementMethods"/>.
/// </summary>
public sealed class AppManagementPlugin : ICorePlugin, IAsyncDisposable
{
    private const string NotRunning = "The Application management plugin is not running.";
    private readonly AppManagementOptions _options;
    private AppReadService? _service;
    private ICorePluginContext? _ctx;

    public AppManagementPlugin()
        : this(new AppManagementOptions())
    {
    }

    public AppManagementPlugin(AppManagementOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => AppManagementInfo.PluginId;

    public string DisplayName => AppManagementInfo.DisplayName;

    public string? IconKey => AppManagementInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Maintenance;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>The running service (tests).</summary>
    internal AppReadService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _service = new AppReadService(ctx, _options);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Nothing changes the server's configuration: every method is for operators, none is audited (the tasks are).</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    public bool IsAudited(string method) => false;

    /// <summary>Seeing the applications reads every chosen device: a right of its own (Administrator and Operator).</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(AppManagementInfo.ViewPermission, "View applications", "See the applications of the chosen devices. Changes need the rights of Applications (ACAP)."),
    ];

    /// <summary>Every method needs "view"; the actions also need the Applications plugin's task rights (checked by the run).</summary>
    public string? RequiredPermission(string method) => AppManagementInfo.ViewPermission;

    /// <summary>A user's call: the chosen devices must be the caller's; the rows belong to the caller's login session.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException(NotRunning);
        var jobs = _ctx?.Jobs ?? throw new InvalidOperationException("This OADM server cannot read applications.");
        switch (method)
        {
            case AppManagementMethods.Read:
                return AppManagementJson.Serialize(await service.ReadAsync(caller, jobs, AppManagementJson.Deserialize<ReadRequest>(payloadJson), ct).ConfigureAwait(false));
            case AppManagementMethods.GetState:
                return AppManagementJson.Serialize(service.GetState(caller));
            case AppManagementMethods.GetRows:
                return AppManagementJson.Serialize(await service.GetRowsAsync(caller, AppManagementJson.Deserialize<RowsRequest>(payloadJson), ct).ConfigureAwait(false));
            case AppManagementMethods.Run:
                return AppManagementJson.Serialize(await service.RunAsync(caller, jobs, AppManagementJson.Deserialize<RunRequest>(payloadJson), ct).ConfigureAwait(false));
            case AppManagementMethods.GetLog:
                return AppManagementJson.Serialize(await service.GetLogAsync(caller, AppManagementJson.Deserialize<LogRequest>(payloadJson), ct).ConfigureAwait(false));
            case AppManagementMethods.GetSettings:
                return AppManagementJson.Serialize(await service.GetSettingsAsync(caller, AppManagementJson.Deserialize<SettingsRequest>(payloadJson), ct).ConfigureAwait(false));
            case AppManagementMethods.Follow:
                await service.FollowAsync(caller, jobs, AppManagementJson.Deserialize<FollowRequest>(payloadJson), ct).ConfigureAwait(false);
                return "{}";
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(NotRunning)), method, payloadJson, ct);

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
