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
        return _api.RemoveDevicesAsync(deviceIds.Select(id => id.ToString()).ToList(), ct);
    }

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
