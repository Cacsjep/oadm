using Avalonia.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client;

/// <summary>Client part of a task plugin that needs user input before running.</summary>
public interface ITaskPluginDialog
{
    /// <summary>The id of the task plugin on the server that this dialog belongs to.</summary>
    string PluginId { get; }
    /// <summary>Returns the payload JSON sent to the server, or null when the user cancelled.</summary>
    Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner);
}

/// <summary>Server access for a task plugin dialog. Build dialogs from the shared OADM controls and theme.</summary>
public interface ITaskDialogContext
{
    /// <summary>Calls the plugin's server-side <c>ITaskPluginQuery.QueryAsync</c> for one device (read-only).</summary>
    Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// Uploads a local file to the server (256 KB chunks); put the returned id into the task payload.
    /// <paramref name="progress"/> receives the fraction sent, from 0.0 to 1.0 (not a percentage).
    /// </summary>
    Task<Oadm.Sdk.Plugins.UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>
    /// Whether the user's role allows <paramref name="permission"/>, e.g. "task:oadm.users:remove": the dialog hides or
    /// disables the choices the role lacks (the server refuses the run anyway). Default (older hosts): true.
    /// </summary>
    bool Has(string permission) => true;

    /// <summary>
    /// Every device of the system, e.g. to take settings from a device that is not selected ("Use configuration from:
    /// Device"). Default (older hosts): empty, the dialog then offers its selected devices only.
    /// </summary>
    IReadOnlyList<IDeviceInfo> AllDevices => [];
}

/// <summary>UI page of a core plugin, shown in the navigation rail.</summary>
public interface ICorePluginPage
{
    /// <summary>The id of the core plugin on the server that this page belongs to.</summary>
    string PluginId { get; }
    /// <summary>The page title and the name of its rail entry.</summary>
    string Title { get; }

    /// <summary>Creates the page content once per connection, on the UI thread; the host shows it in a card below the title.</summary>
    Control CreateView(ICorePluginClientContext ctx);

    /// <summary>
    /// True when the view lays out its own cards (<c>Border.card</c>, e.g. several panels side by side); the host then
    /// shows it below the page title without its own card. Default false.
    /// </summary>
    bool HasOwnCards => false;

    /// <summary>
    /// True when the page starts tasks (e.g. a rollout) and the technician should watch them without
    /// switching pages: the host shows the shared tasks pane (splitter, persisted height) below the
    /// page, exactly like on the Devices page. Default false.
    /// </summary>
    bool ShowTasksPane => false;
}

/// <summary>
/// What a core plugin page can see and do. Members are called and raise their events on the UI thread.
/// Everything except <see cref="InvokeAsync"/> has a default so older hosts keep working.
/// </summary>
public interface ICorePluginClientContext
{
    /// <summary>
    /// Calls ICorePlugin.InvokeAsync on the server. Errors arrive as <c>Grpc.Core.RpcException</c>
    /// (Status.Detail is the message).
    /// </summary>
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// Live events the server part publishes (<c>ICorePluginContext.Events</c>, gRPC PluginService.Watch), from now
    /// until <paramref name="ct"/> is cancelled; items arrive on the caller's synchronization context. The sequence ends
    /// or throws <c>RpcException</c> when the connection drops: re-read the state with <see cref="InvokeAsync"/> and
    /// watch again after a short delay. Default (older hosts, fake mode): an empty sequence, so the page polls.
    /// </summary>
    IAsyncEnumerable<Oadm.Sdk.Plugins.PluginEvent> WatchEventsAsync(CancellationToken ct) => AsyncEnumerable.Empty<Oadm.Sdk.Plugins.PluginEvent>();

    /// <summary>
    /// A file the server part sends (<c>ICorePluginDownloads</c>, gRPC PluginService.Download): the suggested name, the
    /// length when known and the bytes as a stream. Read <see cref="Oadm.Sdk.Plugins.PluginDownload.Content"/> with your own
    /// token and dispose the download (that ends the call). Errors arrive as <c>RpcException</c> like
    /// <see cref="InvokeAsync"/>. Default (older hosts): <see cref="NotSupportedException"/>.
    /// </summary>
    Task<Oadm.Sdk.Plugins.PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("This host has no plugin downloads.");

    /// <summary>All managed devices (client mirror of the device table), see <see cref="DevicesChanged"/>.</summary>
    IReadOnlyList<IDeviceInfo> Devices => [];

    /// <summary>Devices selected in the grid of the Devices page, in selection order.</summary>
    IReadOnlyList<IDeviceInfo> SelectedDevices => [];

    /// <summary>A device was added, removed or changed.</summary>
    event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    /// <summary>"user@machine" of this client, the owner shown for tasks the page starts on the server.</summary>
    string OwnerName => Environment.UserName + "@" + Environment.MachineName;

    /// <summary>The host's message dialog.</summary>
    Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    /// <summary>The host's confirmation dialog; true when confirmed. Without a host dialog nothing is confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);

    /// <summary>Opens a host page (<see cref="HostPages"/>), e.g. Devices to watch the tasks pane.</summary>
    Task OpenAsync(string hostPage) => Task.CompletedTask;

    /// <summary>Main window, owner for the plugin's own dialogs and file pickers.</summary>
    Window? Owner => null;

    /// <summary>
    /// The web interface of managed devices for an embedded browser, relayed by the server with the stored login (Web
    /// View plugin). Null when the host has none (older hosts).
    /// </summary>
    IDeviceWebAccess? DeviceWeb => null;

    /// <summary>
    /// The host's live video component for the page (Live View plugin): the server streams the camera with the stored login.
    /// Null when the host has none (older hosts). The role needs "Live view" (<c>devices.liveView</c>) to see a picture.
    /// </summary>
    ILiveVideoAccess? LiveVideo => null;

    /// <summary>
    /// Whether the user's role allows <paramref name="permission"/>, e.g. "oadm.dhcp-server:manage": the page hides or
    /// disables what the role lacks (the server refuses the call anyway). Default (older hosts): true.
    /// </summary>
    bool Has(string permission) => true;

    /// <summary>Raised on the UI thread after the user's role or its permissions changed (ask <see cref="Has"/> again).</summary>
    event EventHandler? PermissionsChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// A short text this plugin keeps on this computer (the client settings file), e.g. a page's last device choice; null
    /// when none. Not shared with other computers or users of them. Default (older hosts): nothing is kept.
    /// </summary>
    string? GetLocalSetting(string key) => null;

    /// <summary>Keeps <paramref name="value"/> (at most 64 KB) under <paramref name="key"/> on this computer; null removes it.</summary>
    void SetLocalSetting(string key, string? value)
    {
    }
}
