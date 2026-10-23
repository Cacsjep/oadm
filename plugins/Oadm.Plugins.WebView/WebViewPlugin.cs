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

    /// <summary>
    /// No page methods: the tabs go through the host's DeviceWebService, which takes the device from the user's view and
    /// ends a tab's requests when its device leaves that view.
    /// </summary>
    public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
        throw new ArgumentException($"Unknown method '{method}'.", nameof(method));

    /// <summary>
    /// Opening devices (DeviceWebService) is a right of its own and off for operators by default: the relay logs in with the
    /// stored device login, so whoever opens a device can change everything on it.
    /// </summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        // The page is of no use without "open": off for operators by default as well.
        new(PermissionKeys.Show, "Show the page", "See the Web View page.") { OperatorDefault = false },
        new("open", "Open devices", "Open the web interface of devices in tabs, logged in with the stored login.") { OperatorDefault = false, FullDeviceControl = true },
    ];
}
