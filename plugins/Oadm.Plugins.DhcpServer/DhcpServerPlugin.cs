using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer;

/// <summary>
/// Core plugin "DHCP server" (see the plugin README): an RFC 2131 DHCP server on one IPv4 interface with
/// a dynamic range, static leases, an in-use probe before offering, a check for other DHCP servers and rate limits.
/// Settings and leases are stored server side and restored on start; the server starts disabled.
/// Page backend methods: <see cref="DhcpServerMethods"/>; live events: state and lease changes.
/// </summary>
public sealed class DhcpServerPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly DhcpServerOptions _options;
    private volatile DhcpServerService? _service;

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
        var service = new DhcpServerService(_options, ctx.Settings, ctx.Events, ctx.Logger, ctx.Firewall, ctx.AutoAdd, ctx.Network?.ForSite(null));
        await service.StartAsync(ct).ConfigureAwait(false);
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

    /// <summary>Reading the state is for everyone; saving, static leases and release change the server (Admin, audited).</summary>
    public UserRole RequiredRole(string method) => method == DhcpServerMethods.GetState ? UserRole.Operator : UserRole.Admin;

    /// <summary>Changing the server is one right ("manage", the Admin methods); the page itself is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change the DHCP server", "Turn the DHCP server on or off, change its addresses and static leases, and release leases.") { OperatorDefault = false },
    ];

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
