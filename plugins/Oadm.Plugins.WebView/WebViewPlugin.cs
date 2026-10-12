using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.WebView;

/// <summary>
/// Core plugin "Web View": a rail page that opens the web interface of the managed devices in tabs. The server part
/// only puts the page in the rail; the requests of the page go through the host's DeviceWebService, which adds the
/// stored login on the server (passwords never reach the client) and writes "Opened web interface" to the audit log.
/// </summary>
public sealed class WebViewPlugin : ICorePlugin
{
    public string Id => WebViewPluginInfo.PluginId;

    public string DisplayName => WebViewPluginInfo.DisplayName;

    public string? IconKey => WebViewPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Utilities;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
}
