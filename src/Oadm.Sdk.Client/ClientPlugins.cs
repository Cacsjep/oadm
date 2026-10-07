using Avalonia.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client;

/// <summary>Client part of a task plugin that needs user input before running.</summary>
public interface ITaskPluginDialog
{
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
}

/// <summary>UI page of a core plugin, shown in the navigation rail.</summary>
public interface ICorePluginPage
{
    string PluginId { get; }
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
}
