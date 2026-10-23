using System.Collections.ObjectModel;

using Avalonia.Controls;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Devices.Toolbar;

/// <summary>
/// <see cref="IToolbarContext"/> of the Devices page: devices from the <see cref="DeviceStore"/>, the
/// grid selection, task plugins from the <see cref="TaskPluginCatalog"/>, server access through
/// <see cref="IOadmApi"/>, dialogs through <see cref="IDialogService"/>. Host pages are opened by the
/// page view model (<paramref name="openPage"/>).
/// </summary>
public sealed class ToolbarContext : IToolbarContext
{
    /// <summary>Plugin id used for uploads that do not belong to a task plugin.</summary>
    private const string ToolbarUploadId = "oadm.toolbar";

    private readonly DeviceStore _store;
    private readonly ObservableCollection<DeviceRowViewModel> _selection;
    private readonly TaskPluginCatalog _catalog;
    private readonly TaskPluginRunner _runner;
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly Func<string, Task> _openPage;
    private readonly Action _showTasks;
    private Shell.UserSession? _session;

    public ToolbarContext(
        DeviceStore store,
        ObservableCollection<DeviceRowViewModel> selection,
        TaskPluginCatalog catalog,
        TaskPluginRunner runner,
        IOadmApi api,
        IDialogService dialogs,
        Func<string, Task> openPage,
        Action showTasks)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        _store = store;
        _selection = selection;
        _catalog = catalog;
        _runner = runner;
        _api = api;
        _dialogs = dialogs;
        _openPage = openPage;
        _showTasks = showTasks;
        selection.CollectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        store.Changed += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        catalog.Changed += (_, _) => TaskPluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the user's role or its permissions changed.</summary>
    public event EventHandler? PermissionsChanged;

    /// <summary>The logged-in user whose permissions <see cref="Has"/> answers (set by the Devices page).</summary>
    public Shell.UserSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.PermissionsChanged -= OnPermissionsChanged;
            }

            _session = value;
            if (_session is not null)
            {
                _session.PermissionsChanged += OnPermissionsChanged;
            }
        }
    }

    public bool Has(string permission) => _session?.Has(permission) ?? true;

    /// <summary>The user may reach every device (false for a user with a device group).</summary>
    public bool HasAllDevices => _session?.HasAllDevices ?? true;

    /// <summary>Why the user cannot add devices (a device group without a site), or null (<see cref="Shell.UserSession.AddDevicesRefusal"/>).</summary>
    public string? AddDevicesRefusal => _session?.AddDevicesRefusal;

    private void OnPermissionsChanged(object? sender, EventArgs e) => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<IDeviceInfo> SelectedDevices => [.. _selection];

    public IReadOnlyList<IDeviceInfo> Devices => [.. _store.Devices];

    public event EventHandler? SelectionChanged;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<ToolbarTaskPlugin> TaskPlugins =>
        _catalog.Plugins.Select(p => new ToolbarTaskPlugin(p.Id, Oadm.Sdk.Plugins.TaskPluginNames.Normalize(p.DisplayName), string.IsNullOrEmpty(p.IconKey) ? null : p.IconKey, p.ShowInToolbar, p.RequiresDialog)).ToList();

    public event EventHandler? TaskPluginsChanged;

    public Window? Owner => _dialogs.Owner;

    public bool CanRunTask(string pluginId)
    {
        TaskPluginInfo? plugin = Find(pluginId);
        return plugin is not null && TaskPluginCatalog.RunnableFor([plugin], _selection.Select(d => d.Id).ToList()).Any();
    }

    public string? CannotRunTaskReason(string pluginId)
    {
        TaskPluginInfo? plugin = Find(pluginId);
        return plugin is null ? null : TaskPluginCatalog.NotRunnableReason(plugin, _selection.Select(d => d.Id).ToList());
    }

    public async Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct)
    {
        TaskPluginInfo? plugin = Find(pluginId);
        if (plugin is null || _selection.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string>? taskIds = await _runner.RunAsync(plugin, [.. _selection], ct).ConfigureAwait(true);
        if (taskIds is { Count: > 0 })
        {
            _showTasks();
        }

        return taskIds;
    }

    public Task OpenAsync(string hostPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPage);
        return _openPage(hostPage);
    }

    public Task RemoveDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var ids = deviceIds.Select(id => id.ToString()).ToList();
        return Work is { } work
            ? work.RunAsync(RemovingText(ids.Count), () => _api.RemoveDevicesAsync(ids, ct))
            : _api.RemoveDevicesAsync(ids, ct);
    }

    /// <summary>The progress row of the Devices page; null: none.</summary>
    public Oadm.Client.Infrastructure.WorkIndicator? Work { get; set; }

    /// <summary>"Removing 30,000 devices".</summary>
    public static string RemovingText(int devices) =>
        devices == 1 ? "Removing the device" : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Removing {devices:N0} devices");

    public Task RefreshDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        return deviceIds.Count == 0 ? Task.CompletedTask : _api.RefreshDevicesAsync(deviceIds.Select(id => id.ToString()).ToList(), ct);
    }

    public Task ShowMessageAsync(string title, string message) => _dialogs.ShowMessageAsync(title, message);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => _dialogs.ConfirmAsync(title, message, confirmText);

    public Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct) =>
        new TaskDialogContext(_api, pluginId).QueryAsync(deviceId, method, payloadJson, ct);

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) =>
        new TaskDialogContext(_api, ToolbarUploadId).UploadAsync(localPath, progress, ct);

    public Task<string?> InvokePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return _api.InvokeCorePluginAsync(pluginId, method, payloadJson, ct);
    }

    private TaskPluginInfo? Find(string pluginId) =>
        _catalog.Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}
