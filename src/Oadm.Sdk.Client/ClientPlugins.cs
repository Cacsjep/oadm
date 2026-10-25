using Avalonia.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Licensing;

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
    /// Whether the user may reach every device (SDK 0.4.0); false for a user limited to a device group: hide or disable
    /// what only users with all devices may do (the server refuses it anyway). Default (older hosts): true.
    /// </summary>
    bool HasAllDevices => true;

    /// <summary>
    /// Every device of the system, e.g. to take settings from a device that is not selected ("Use configuration from:
    /// Device"). Default (older hosts): empty, the dialog then offers its selected devices only.
    /// </summary>
    IReadOnlyList<IDeviceInfo> AllDevices => [];

    /// <summary>
    /// True when the dialog may start its task itself with <see cref="RunTaskAsync"/> and stay open while it runs. Default
    /// (older hosts): false, the dialog then returns its payload from <c>ShowAsync</c> and closes.
    /// </summary>
    bool CanRunTasks => false;

    /// <summary>
    /// Starts the dialog's task plugin now on <paramref name="deviceIds"/> (one task per device, like a run from the
    /// menu; the tasks also show in the tasks pane) and follows them: <paramref name="progress"/> gets every task's state
    /// whenever one changes; the result is their states once all ended. Cancelling <paramref name="ct"/> stops following
    /// only, the tasks go on. The server checks the permissions; a refusal throws its RpcException. A dialog that ran its
    /// task returns null from <c>ShowAsync</c>, so nothing runs twice. Default (older hosts): NotSupportedException.
    /// </summary>
    Task<IReadOnlyList<TaskRunState>> RunTaskAsync(IReadOnlyList<Guid> deviceIds, string? payloadJson, IProgress<IReadOnlyList<TaskRunState>>? progress, CancellationToken ct) =>
        throw new NotSupportedException("This OADM cannot run tasks from a dialog.");
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

    /// <summary>
    /// The id of the core plugin whose page shows this page as a tab (SDK 0.4.0), e.g. the 802.1X server inside PKI; null
    /// (the default): a rail entry of its own. The host creates the view as for a rail page (with this plugin's own context)
    /// and hands it to that page (<see cref="ICorePluginClientContext.Tabs"/>); <c>OpenAsync("plugin:" + PluginId)</c> opens
    /// that page on this tab. While that page is not listed for the user, this page is a rail entry as before.
    /// </summary>
    string? TabOf => null;
}

/// <summary>
/// The page of another core plugin shown as a tab of this page (SDK 0.4.0, <see cref="ICorePluginPage.TabOf"/>): its
/// plugin id, its title and its view. The page puts <see cref="View"/> into its tab content; one parent at a time.
/// </summary>
/// <param name="PluginId">The tab's core plugin ("oadm.radius").</param>
/// <param name="Title">The tab's title (the page's <see cref="ICorePluginPage.Title"/>).</param>
/// <param name="View">The tab's content.</param>
public sealed record CorePluginTab(string PluginId, string Title, Control View);

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

    /// <summary>
    /// Opens a host page (<see cref="HostPages"/>), e.g. Devices to watch the tasks pane, or another plugin's page
    /// ("plugin:" + its id) while that plugin is on.
    /// </summary>
    Task OpenAsync(string hostPage) => Task.CompletedTask;

    /// <summary>
    /// The pages of other core plugins shown as tabs of this page (SDK 0.4.0, <see cref="ICorePluginPage.TabOf"/>), in the
    /// server's order, only those the user's role may show; see <see cref="TabsChanged"/>. Default (older hosts): none.
    /// </summary>
    IReadOnlyList<CorePluginTab> Tabs => [];

    /// <summary>Raised on the UI thread after <see cref="Tabs"/> changed (a role change, a plugin turned on or off).</summary>
    event EventHandler? TabsChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The host asks the page to show a tab (SDK 0.4.0): the plugin id of one of <see cref="Tabs"/>, or this page's own id
    /// for its own first tab (<c>OpenAsync("plugin:" + id)</c> of that page). Default (older hosts): never raised.
    /// </summary>
    event EventHandler<string>? OpenTabRequested
    {
        add { }
        remove { }
    }

    /// <summary>Whether the plugin package <paramref name="packageId"/> is turned on (Plugins page); null when the host cannot tell.</summary>
    bool? IsPluginOn(string packageId) => null;

    /// <summary>
    /// The host asks the page to open a device; the argument is the device's id. Web View uses it: the address link and
    /// "Open web interface" of a device at a site open the device there (the user's computer usually cannot reach the
    /// site). Default (older hosts): never raised.
    /// </summary>
    event EventHandler<string>? OpenDeviceRequested
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The license of the plugin package <paramref name="packageId"/> as the server checked it (status, reason, what it
    /// allows), e.g. for "No license" on the page; read it again when the page is shown. Null when the host cannot tell.
    /// The server checks again on every call.
    /// </summary>
    PluginLicenseInfo? LicenseOf(string packageId) => null;

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

    /// <summary>
    /// Whether the user may reach every device (SDK 0.4.0); false for a user limited to a device group: hide or disable
    /// what only users with all devices may do (the server refuses it anyway). Changes come with PermissionsChanged.
    /// Default (older hosts): true.
    /// </summary>
    bool HasAllDevices => true;

    /// <summary>Raised on the UI thread after the user's role or its permissions changed (ask <see cref="Has"/> again).</summary>
    event EventHandler? PermissionsChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Whether the user is an administrator (SDK 0.4.0), e.g. to offer Start 30-day trial (<see cref="StartProTrialAsync"/>);
    /// others see who can. Changes come with PermissionsChanged. Default (older hosts): false.
    /// </summary>
    bool IsAdministrator => false;

    /// <summary>
    /// Starts the server's 30-day OADM PRO trial (SDK 0.4.0; administrators, once per server, docs/spec/licensing.md) and
    /// returns the new state; the host refreshes its plugin list and Settings card, the server raises <c>IProLicense.Changed</c>
    /// for the server parts. Offer it only while <see cref="Oadm.Sdk.Licensing.ProLicenseInfo.TrialAvailable"/>. Throws with
    /// the server's reason ("The trial was already used on this server."). Default (older hosts): NotSupportedException.
    /// </summary>
    Task<Oadm.Sdk.Licensing.ProLicenseInfo> StartProTrialAsync(CancellationToken ct) =>
        Task.FromException<Oadm.Sdk.Licensing.ProLicenseInfo>(new NotSupportedException("This version of OADM cannot start the trial here."));

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
