using Avalonia.Controls;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Client;

/// <summary>
/// Groups of the Devices page toolbar, left to right. The host draws a separator between two
/// non-empty groups.
/// </summary>
public enum ToolbarGroup
{
    /// <summary>Adding devices (Scan, Scan IP range, Add manually).</summary>
    Add = 0,

    /// <summary>Managing the selection (Remove).</summary>
    Manage = 1,

    /// <summary>Task plugin actions (Restart, ...).</summary>
    Tasks = 2,

    /// <summary>Everything else; default for third-party plugins.</summary>
    Plugins = 3,
}

/// <summary>
/// A part of the Devices page toolbar: any Avalonia control (button, toggle, dropdown, text, ...).
/// The host creates the control once per page with <see cref="CreateControl"/> and places it by
/// <see cref="Group"/>, then <see cref="Order"/>, then <see cref="Id"/>. Build buttons from the shared
/// <c>Oadm.Sdk.Client.Controls.ToolbarButton</c> so every toolbar entry looks the same. Found like
/// the other client plugin parts: compiled into the client (built-in) or in a <c>*.Client.dll</c>
/// with a public parameterless constructor.
/// </summary>
public interface IToolbarPlugin
{
    /// <summary>Unique id, e.g. "oadm.toolbar.scan".</summary>
    string Id { get; }

    /// <summary>Position within the group, ascending.</summary>
    int Order { get; }

    ToolbarGroup Group { get; }

    /// <summary>Creates the control on the UI thread. Keep the context to react to selection changes.</summary>
    Control CreateControl(IToolbarContext ctx);
}

/// <summary>Host pages a toolbar plugin can open (<see cref="IToolbarContext.OpenAsync"/>).</summary>
public static class HostPages
{
    /// <summary>Add page in scan mode: zero-configuration (mDNS) discovery starts immediately.</summary>
    public const string AddScan = "add.scan";

    /// <summary>Add page with the IP range input.</summary>
    public const string AddIpRange = "add.range";

    /// <summary>Add page with the address input (IP or host name, optional port and scheme).</summary>
    public const string AddManually = "add.manual";

    /// <summary>
    /// Import devices: the host asks for a CSV file (the export format, or one address per line, optional
    /// "User name" and "Password" columns) and opens the add page with every address of it.
    /// </summary>
    public const string AddImport = "add.import";

    /// <summary>Export devices: the host saves the selected devices (or every device the search shows) as a CSV file.</summary>
    public const string ExportDevices = "devices.export";

    /// <summary>Navigation pages.</summary>
    public const string Devices = "devices";
    public const string Logs = "logs";
    public const string Settings = "settings";

    /// <summary>Users page (administrators only; for an operator opening it does nothing).</summary>
    public const string Users = "users";

    /// <summary>Credential list page (administrators only; for an operator opening it does nothing).</summary>
    public const string Credentials = "credentials";

    /// <summary>About page: terms of use, versions and licenses.</summary>
    public const string About = "about";
}

/// <summary>A task plugin as the toolbar sees it (server TaskService.ListTaskPlugins).</summary>
/// <param name="Id">Plugin id, e.g. "oadm.restart".</param>
/// <param name="DisplayName">Menu text.</param>
/// <param name="IconKey">Theme icon key ("restart"), resolve with <c>Controls.ToolbarButton.IconKey</c>.</param>
/// <param name="ShowInToolbar">The plugin asks for a toolbar button.</param>
/// <param name="RequiresDialog">Running it opens the plugin's dialog first.</param>
public sealed record ToolbarTaskPlugin(string Id, string DisplayName, string? IconKey, bool ShowInToolbar, bool RequiresDialog);

/// <summary>
/// What a toolbar plugin can see and do: the devices and the selection (with change events), the
/// task plugins, the host pages and dialogs, and the same server access as a task plugin dialog.
/// All members are called and raise their events on the UI thread.
/// </summary>
public interface IToolbarContext
{
    /// <summary>Selected devices of the grid, in selection order.</summary>
    IReadOnlyList<IDeviceInfo> SelectedDevices { get; }

    /// <summary>All managed devices.</summary>
    IReadOnlyList<IDeviceInfo> Devices { get; }

    /// <summary>The grid selection changed.</summary>
    event EventHandler? SelectionChanged;

    /// <summary>A device was added, removed or changed.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Task plugins of the server.</summary>
    IReadOnlyList<ToolbarTaskPlugin> TaskPlugins { get; }

    /// <summary><see cref="TaskPlugins"/> or what they can run on changed.</summary>
    event EventHandler? TaskPluginsChanged;

    /// <summary>True when the task plugin can run on every selected device (and something is selected).</summary>
    bool CanRunTask(string pluginId);

    /// <summary>
    /// Runs a task plugin on the selection like the context menu does: its dialog first when it has
    /// one, then one task per device. Returns the task ids, or null when nothing was started.
    /// </summary>
    Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct);

    /// <summary>Opens a host page, see <see cref="HostPages"/>. Completes when a dialog page closes.</summary>
    Task OpenAsync(string hostPage);

    /// <summary>Removes devices from OADM (the devices themselves are not changed). No confirmation.</summary>
    Task RemoveDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct);

    /// <summary>The host's message dialog.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>The host's confirmation dialog; true when confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Calls a task plugin's server-side <c>ITaskPluginQuery.QueryAsync</c> for one device (read-only).</summary>
    Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct);

    /// <summary>Uploads a local file to the server; <paramref name="progress"/> receives 0.0 to 1.0.</summary>
    Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>
    /// Calls a core plugin's server part (<c>ICorePlugin.InvokeAsync</c> through gRPC PluginService.Invoke), e.g. the
    /// backend of a toolbar-only core plugin (<c>ICorePlugin.HasPage</c> false). Failures are <c>Grpc.Core.RpcException</c>
    /// (Status.Detail is the message). Hosts without it throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<string?> InvokePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("This host cannot call core plugins from the toolbar.");

    /// <summary>The main window, as owner for the plugin's own dialogs; null before it is shown.</summary>
    Window? Owner { get; }
}
