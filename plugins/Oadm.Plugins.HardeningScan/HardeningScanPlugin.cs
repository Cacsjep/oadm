using Oadm.Plugins.HardeningScan.Scanning;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.HardeningScan;

/// <summary>
/// Core plugin "Hardening scan": one read-only scan of every managed device against the AXIS OS Hardening Guide, in the
/// guide's two levels Basic and Extended, shown on its page as one column per check. Read-only for devices (param.cgi
/// list, VAPIX get / list methods, config/rest GETs, the SOAP Get of the web server settings). Every method is for operators
/// (nothing changes server configuration) and no scan is audited (like the snapshot report).
/// Page backend methods: <see cref="HardeningMethods"/>. Device groups (SDK 0.4.0): every method works on the user's view,
/// a scan is a job of its user, one scan at a time per device scope.
/// </summary>
public sealed class HardeningScanPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly HardeningScanOptions _options;
    private HardeningScanService? _service;
    private ICorePluginContext? _ctx;

    public HardeningScanPlugin()
        : this(new HardeningScanOptions())
    {
    }

    public HardeningScanPlugin(HardeningScanOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => HardeningScanPluginInfo.PluginId;

    public string DisplayName => HardeningScanPluginInfo.DisplayName;

    public string? IconKey => HardeningScanPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Security;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>The running service (tests).</summary>
    internal HardeningScanService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new HardeningScanService(ctx.Devices, ctx.Vapix, ctx.Settings, ctx.Events, ctx.Logger, _options);
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

    /// <summary>Read-only scans: every method is for operators.</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    /// <summary>No audit entry for a scan.</summary>
    public bool IsAudited(string method) => false;

    /// <summary>Starting a scan contacts every device: a right of its own; seeing results is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new("scan", "Run scans", "Start and stop a scan of the devices (it only reads them)."),
    ];

    public string? RequiredPermission(string method) => method is HardeningMethods.StartScan or HardeningMethods.CancelScan ? "scan" : PermissionKeys.Show;

    /// <summary>A user's call: the devices of the user's view; scans are the user's jobs.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException("The hardening scan plugin is not running.");
        var jobs = _ctx?.Jobs ?? throw new InvalidOperationException("This OADM server cannot run scans.");
        return method switch
        {
            HardeningMethods.GetState => HardeningJson.Serialize(await service.GetStateAsync(caller, ct).ConfigureAwait(false)),
            HardeningMethods.StartScan => HardeningJson.Serialize(await service.StartAsync(caller, jobs, HardeningJson.Deserialize<StartScanRequest>(payloadJson), ct).ConfigureAwait(false)),
            HardeningMethods.CancelScan => HardeningJson.Serialize(service.Cancel(caller, jobs, HardeningJson.Deserialize<CancelScanRequest>(payloadJson).JobId)),
            HardeningMethods.GetDetail => HardeningJson.Serialize(await service.GetDetailAsync(caller, HardeningJson.Deserialize<DetailRequest>(payloadJson), ct).ConfigureAwait(false)),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>In-process callers (tests): the plugin's own devices (the context refuses them in a group user's call).</summary>
    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The hardening scan plugin is not running.");
        switch (method)
        {
            case HardeningMethods.GetState:
                return HardeningJson.Serialize(await service.GetStateAsync(ct).ConfigureAwait(false));
            case HardeningMethods.StartScan:
                return HardeningJson.Serialize(await service.StartAsync(HardeningJson.Deserialize<StartScanRequest>(payloadJson), ct).ConfigureAwait(false));
            case HardeningMethods.CancelScan:
                var status = service.Cancel(HardeningJson.Deserialize<CancelScanRequest>(payloadJson).JobId);
                return status is null ? null : HardeningJson.Serialize(status);
            case HardeningMethods.GetDetail:
                return HardeningJson.Serialize(service.GetDetail(HardeningJson.Deserialize<DetailRequest>(payloadJson)));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
