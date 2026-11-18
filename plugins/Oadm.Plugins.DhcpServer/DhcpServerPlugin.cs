using Oadm.Plugins.DhcpServer.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer;

/// <summary>
/// Core plugin "DHCP server" (see the plugin README): an RFC 2131 DHCP server on one IPv4 interface with
/// a dynamic range, static leases, an in-use probe before offering, a check for other DHCP servers and rate limits.
/// Settings and leases are stored server side and restored on start; the server starts disabled.
/// Page backend methods: <see cref="DhcpServerMethods"/>; live events: state and lease changes. Device groups (SDK 0.4.0):
/// the server is server-wide, so every change is for users with all devices; users with a group see the page with the
/// leases of their devices only.
/// </summary>
public sealed class DhcpServerPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly DhcpServerOptions _options;
    private volatile DhcpServerService? _service;
    private IPluginServices? _services;

    public DhcpServerPlugin()
        : this(new DhcpServerOptions())
    {
    }

    public DhcpServerPlugin(DhcpServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => DhcpServerPluginInfo.PluginId;

    public string DisplayName => DhcpServerPluginInfo.DisplayName;

    public string? IconKey => DhcpServerPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Servers;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

    /// <summary>The running service (null while stopped).</summary>
    public DhcpServerService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new DhcpServerService(_options, ctx.Settings, ctx.Events, ctx.Logger, ctx.Firewall, ctx.AutoAdd, ctx.Network?.ForSite(null), ctx.DeviceScopes);
        await service.StartAsync(ct).ConfigureAwait(false);
        _service = service;
        _services = ctx.Services;
        _services?.Offer<Sdk.Network.IDhcpServerLeases>(service.Feed); // read only, for Provisioning
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _services?.Withdraw<Sdk.Network.IDhcpServerLeases>();
        _services = null;
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reading the state is for everyone; saving, static leases and release change the server (Admin, audited).</summary>
    public UserRole RequiredRole(string method)
    {
        method = DhcpPageTools.Set.RulesMethodOf(method);
        return method == DhcpServerMethods.GetState || DhcpTools.IsTool(method) ? UserRole.Operator : UserRole.Admin;
    }

    /// <summary>The Admin methods and the runs of the lease tools are audited; a preview only reads.</summary>
    public bool IsAudited(string method) => RequiredRole(method) == UserRole.Admin && !PluginTool.IsPreviewMethod(method);

    /// <summary>
    /// The tools (docs/spec/mcp.md "Plugins"): the status and the leases (read only, for the caller's devices), and the page's
    /// lease actions with the rights of their page methods.
    /// </summary>
    public IReadOnlyList<PluginTool> Tools { get; } = [.. DhcpTools.All, .. DhcpPageTools.Set.Tools];

    /// <summary>The running service; refuses a call while the plugin is stopped.</summary>
    internal DhcpServerService RunningService => _service ?? throw new InvalidOperationException("The DHCP server plugin is not running.");

    /// <summary>The tools of this plugin are named <c>oadm_dhcp_*</c>.</summary>
    public string? ToolArea => "dhcp";

    /// <summary>Changing the server is one right ("manage", the Admin methods); the page itself is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change the DHCP server", "Turn the DHCP server on or off, change its addresses and static leases, and release leases.") { OperatorDefault = false },
    ];

    /// <summary>The DHCP server is server-wide: saving, static leases and release are for users with all devices.</summary>
    public bool RequiresAllDevices(string method)
    {
        method = DhcpPageTools.Set.RulesMethodOf(method);
        return method != DhcpServerMethods.GetState && !DhcpTools.IsTool(method);
    }

    /// <summary>A user's call: users with a device group read the state with the leases of their devices.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException("The DHCP server plugin is not running.");
        if (DhcpTools.IsTool(method))
        {
            // Read only, with the page's own rules: the leases of the caller's devices.
            var state = caller.HasAllDevices
                ? service.GetState(full: true)
                : service.GetState(Serving.DhcpGroupView.From(await caller.Devices.ListAsync(ct).ConfigureAwait(false)));
            return method == DhcpTools.Status.Method ? DhcpTools.StatusOf(state) : DhcpTools.LeasesOf(state, payloadJson);
        }

        if (DhcpPageTools.Set.Handles(method))
        {
            if (!caller.HasAllDevices)
            {
                throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices)
            }

            return await DhcpPageTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        if (caller.HasAllDevices)
        {
            return await InvokeAsync(method, payloadJson, ct).ConfigureAwait(false);
        }

        if (method != DhcpServerMethods.GetState)
        {
            throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices)
        }

        service.Viewers.Seen(caller);
        var view = Serving.DhcpGroupView.From(await caller.Devices.ListAsync(ct).ConfigureAwait(false));
        return DhcpJson.Serialize(service.GetState(view) with { Renew = service.Viewers.Renews });
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The DHCP server plugin is not running.");
        return method switch
        {
            DhcpServerMethods.GetState => DhcpJson.Serialize(service.GetState(full: true)),
            DhcpServerMethods.Save => DhcpJson.Serialize(await service.SaveAsync(DhcpJson.Deserialize<DhcpSaveRequest>(payloadJson), ct).ConfigureAwait(false)),
            DhcpServerMethods.SaveStatic => DhcpJson.Serialize(service.SaveStatic(DhcpJson.Deserialize<StaticLeaseRequest>(payloadJson))),
            DhcpServerMethods.MakeStatic => DhcpJson.Serialize(service.MakeStatic(DhcpJson.Deserialize<LeaseRequest>(payloadJson))),
            DhcpServerMethods.DeleteStatic => DhcpJson.Serialize(service.DeleteStatic(DhcpJson.Deserialize<LeaseRequest>(payloadJson))),
            DhcpServerMethods.Release => DhcpJson.Serialize(service.Release(DhcpJson.Deserialize<LeaseRequest>(payloadJson))),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
