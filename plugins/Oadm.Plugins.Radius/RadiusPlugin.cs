using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius;

/// <summary>
/// Core plugin "802.1X server", an OADM PRO plugin (docs/spec/radius-server.md): OADM answers the switches as their RADIUS
/// server and authenticates the cameras on the switch ports with EAP-TLS. The protocol layer is in <c>Protocol/</c>
/// (no sockets, no host); this class is the plugin's entry point and starts nothing yet.
/// </summary>
public sealed class RadiusPlugin : ICorePlugin
{
    /// <summary>The plugin id, equal to the id in plugin.json.</summary>
    public const string PluginId = "oadm.radius";

    public string Id => PluginId;

    public string DisplayName => "802.1X server";

    public string? IconKey => "lock";

    public CorePluginGroup Group => CorePluginGroup.Security;

    /// <summary>The page comes with the client part.</summary>
    public bool HasPage => false;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>The page backend. So far one read: <see cref="GetStateMethod"/> (server-wide, no device data).</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        method == GetStateMethod
            ? Task.FromResult<string?>("""{"running":false}""")
            : throw new ArgumentException($"Unknown method '{method}'.", nameof(method));

    /// <summary>The server's state for the page (the server part fills it).</summary>
    public const string GetStateMethod = "getState";
}
