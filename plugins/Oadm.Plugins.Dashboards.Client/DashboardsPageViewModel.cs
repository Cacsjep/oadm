using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// The Dashboards page: the saved dashboards, the cameras and the events with values on the left, the open dashboards as
/// tabs on the right. New dashboard and Edit put a tab in edit mode: events and cameras are dragged onto the grid (or
/// clicked), widgets moved, resized and removed, and the selected widget's panel right of the grid sets what it shows.
/// The values come from the server part, which pushes the changes of the widgets in the open tabs only.
/// </summary>
public sealed partial class DashboardsPageViewModel : ObservableObject, IDisposable
{
    private static readonly LiveVideoPlace TilePlace = new() { ControlsOpen = false, FullscreenControlsOpen = true };

    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _watchGate = new(1, 1);
    private readonly string _watchId = Guid.NewGuid().ToString("N");
    private Dictionary<Guid, IDeviceInfo> _devices = [];
    private Dictionary<string, CatalogEvent> _catalog = new(StringComparer.Ordinal);
    private Dictionary<ValueKey, ValueSnapshot> _values = [];
    private HashSet<ValueKey> _sentKeys = [];
    private bool _mayWatch;
    private bool _watchPending;
    private bool _disposed;

    public DashboardsPageViewModel(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _ctx.DevicesChanged += OnDevicesChanged;
        _ctx.PermissionsChanged += OnPermissionsChanged;
        _devices = ctx.Devices.ToDictionary(d => d.Id);
        _mayWatch = MayWatch;
        Tree.Rebuild(ctx.Devices, CameraSearch);
        _ = LoadAndWatchAsync(_life.Token);
        _ = RenewWatchAsync(_life.Token);
    }

    /// <summary>Opens the device picker for one device (set by the view, which knows the window).</summary>
    public Func<IReadOnlyList<IDeviceInfo>, Guid?, Task<Guid?>>? PickDevice { get; set; }

    /// <summary>Cameras (devices with video) grouped by tag.</summary>
    public DeviceTreeModel Tree { get; } = new() { Filter = d => d.HasVideo };

    public RangeObservableCollection<DashboardEntry> Dashboards { get; } = [];

    /// <summary>The events with values, grouped (headings), filtered by <see cref="EventSearch"/>.</summary>
    public RangeObservableCollection<EventListItem> Events { get; } = [];

    public ObservableCollection<DashboardTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial string CameraSearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventSearch { get; set; } = string.Empty;

    /// <summary>The left card is narrow, the dashboards as icons only (like the navigation rail).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelToggleText))]
    public partial bool IsPanelCollapsed { get; set; }

    public string PanelToggleText => IsPanelCollapsed ? "Expand" : "Collapse";

    [RelayCommand]
    private void TogglePanel() => IsPanelCollapsed = !IsPanelCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs), nameof(ShowEmptyState), nameof(IsEditing), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(CloseAllTabsCommand), nameof(SaveDashboardCommand), nameof(CancelEditCommand))]
    public partial DashboardTab? ActiveTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoDashboards), nameof(EmptyText), nameof(ShowEmptyState))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>"Change dashboards": New dashboard, Edit, Delete.</summary>
    public bool MayManage => _ctx.Has(PermissionKeys.Of(DashboardsPluginInfo.PluginId, DashboardsPluginInfo.ManageKey));

    /// <summary>"Live view": the pictures of video widgets.</summary>
    public bool MayWatch => _ctx.Has(HostPermissionKeys.DevicesLiveView);

    /// <summary>Why video widgets show no picture: the role or an old client; empty when they can.</summary>
    public string PictureProblem => _ctx.LiveVideo is null ? DashboardTexts.NotAvailable : !MayWatch ? DashboardTexts.NotAllowed : string.Empty;

    public bool HasTabs => ActiveTab is not null;

    public bool ShowNoDashboards => IsLoaded && Dashboards.Count == 0 && !HasLoadError;

    public string NoDashboardsText => MayManage ? DashboardTexts.NoDashboards : DashboardTexts.NoDashboardsReadOnly;

    public bool HasCameras => Tree.UsableCount > 0;

    public bool HasEvents => Events.Count > 0;

    public bool IsEditing => ActiveTab?.IsEditing == true;

    public bool CanEdit => MayManage && ActiveTab is { IsEditing: false };

    /// <summary>Device triggers is turned off (the Plugins page): dashboards have little to show.</summary>
    public bool TriggersOff => _ctx.IsPluginOn(DashboardsPluginInfo.DeviceTriggersId) == false;

    /// <summary>No device trigger yet (Device triggers is on, or the host cannot tell).</summary>
    public bool NoTriggers => !TriggersOff && !_catalog.Values.Any(e => e.SourceId == DashboardsPluginInfo.DeviceTriggersId && e.Available);

    /// <summary>The right card without an open tab: what to do first.</summary>
    public string EmptyText => TriggersOff ? DashboardTexts.TriggersOff
        : NoTriggers ? DashboardTexts.NoTriggers
        : Dashboards.Count == 0 ? NoDashboardsText
        : DashboardTexts.NoTabs;

    public bool ShowEmptyState => !HasTabs && IsLoaded;

    /// <summary>"Open Device triggers" below the empty state while there is no trigger yet.</summary>
    public bool ShowOpenTriggers => !TriggersOff && NoTriggers;

    partial void OnCameraSearchChanged(string value) => Tree.Rebuild(_ctx.Devices, value);

    partial void OnEventSearchChanged(string value) => RebuildEvents();

    partial void OnActiveTabChanged(DashboardTab? oldValue, DashboardTab? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
        }

        MarkDashboards();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardTab.IsEditing) or nameof(DashboardTab.IsSaving) or nameof(DashboardTab.Name)
            or nameof(DashboardTab.HasErrors) or nameof(DashboardTab.Editor))
        {
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(CanEdit));
            SaveDashboardCommand.NotifyCanExecuteChanged();
            CancelEditCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private Task OpenTriggersAsync() => _ctx.OpenAsync("plugin:" + DashboardsPluginInfo.DeviceTriggersId);

    /// <summary>A new dashboard: a tab in edit mode named "Dashboard N".</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private void NewDashboard()
    {
        var names = Dashboards.Select(d => d.Name).Concat(Tabs.Select(t => t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (names.Contains(DashboardTab.DefaultName(n)))
        {
            n++;
        }

        var tab = new DashboardTab(this, null, DashboardTab.DefaultName(n)) { IsEditing = true };
        Tabs.Add(tab);
        ActiveTab = tab;
        WatchChanged();
    }

    /// <summary>A click on a saved dashboard: its tab to the front, or a new tab.</summary>
    [RelayCommand]
    private void OpenDashboard(DashboardEntry? entry)
    {
        if (entry is null || _life.IsCancellationRequested)
        {
            return;
        }

        if (Tabs.FirstOrDefault(t => t.Id == entry.Id) is { } open)
        {
            ActiveTab = open;
            return;
        }

        var tab = new DashboardTab(this, entry.Dashboard, entry.Name);
        Tabs.Add(tab);
        ActiveTab = tab;
        WatchChanged();
    }

    /// <summary>The pen of a dashboard's row: its tab to the front in edit mode.</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private void EditDashboard(DashboardEntry? entry)
    {
        if (entry is null || !MayManage)
        {
            return;
        }

        OpenDashboard(entry);
        if (ActiveTab is { IsEditing: false } tab && tab.Id == entry.Id)
        {
            tab.Error = null;
            tab.IsEditing = true;
        }
    }

    /// <summary>A click on an event: while editing a widget for it in the first free place.</summary>
    public void ClickEvent(CatalogEvent catalogEvent)
    {
        ArgumentNullException.ThrowIfNull(catalogEvent);
        if (ActiveTab is { IsEditing: true } tab)
        {
            tab.AddEvent(catalogEvent);
        }
    }

    /// <summary>A click on a camera: while editing a live video widget for it in the first free place.</summary>
    public void ClickCamera(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ActiveTab is { IsEditing: true } tab)
        {
            tab.AddCamera(device);
        }
    }

    private bool CanSave() => ActiveTab is { IsEditing: true, IsSaving: false } tab && !tab.HasAnyErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveDashboardAsync()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        tab.ShowAllErrors();
        if (tab.HasAnyErrors || Problem(tab) is { } problem && SetError(tab, problem))
        {
            return;
        }

        tab.IsSaving = true;
        tab.Error = null;
        try
        {
            var json = await _ctx.InvokeAsync(DashboardsMethods.Save, JsonSerializer.Serialize(tab.ToRequest(), DashboardsPluginInfo.Json), _life.Token).ConfigureAwait(true);
            var saved = JsonSerializer.Deserialize<Dashboard>(json ?? "null", DashboardsPluginInfo.Json)
                ?? throw new InvalidOperationException("The server sent no dashboard.");
            tab.EndEditing();
            tab.ApplySaved(saved);
            Upsert(saved);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Any failure of the server call is shown below the toolbar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            tab.Error = ServerErrors.Message(ex);
        }
        finally
        {
            tab.IsSaving = false;
        }
    }

    /// <summary>The page's own checks before a save: the rules, and a device for every widget of a device event.</summary>
    private string? Problem(DashboardTab tab) =>
        DashboardRules.WidgetsProblem(tab.WidgetList())
        ?? (tab.Widgets.Any(w => w.Widget.IsValue && w.Widget.DeviceId is null && FindEvent(w.Widget.TypeId)?.DeviceScope == true) ? DashboardRules.NoDevice : null);

    private static bool SetError(DashboardTab tab, string problem)
    {
        tab.Error = problem;
        return true;
    }

    private bool CanCancel() => ActiveTab is { IsEditing: true, IsSaving: false };

    /// <summary>Cancel: a new dashboard closes, a saved one goes back to what is stored.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelEdit()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        if (tab.Saved is null)
        {
            RemoveTab(tab);
            return;
        }

        tab.Revert();
    }

    /// <summary>The red bin of a dashboard's row: asks, deletes it for every user and closes its tab.</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private async Task DeleteDashboardAsync(DashboardEntry? entry)
    {
        if (entry is null || !MayManage
            || !await _ctx.ConfirmAsync("Delete dashboard", $"Delete the dashboard {entry.Name} for every user?", "Delete dashboard").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(DashboardsMethods.Delete, JsonSerializer.Serialize(new DeleteDashboardRequest(entry.Id), DashboardsPluginInfo.Json), _life.Token).ConfigureAwait(true);
            if (Tabs.FirstOrDefault(t => t.Id == entry.Id) is { } tab)
            {
                RemoveTab(tab);
            }

            Dashboards.RemoveAll(d => d.Id == entry.Id);
            ListChanged();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Any failure of the server call is shown below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LoadError = DashboardTexts.DeleteFailed(ServerErrors.Message(ex));
        }
    }

    [RelayCommand(CanExecute = nameof(HasTabs))]
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!await CloseTabAsync(tab).ConfigureAwait(true))
            {
                return;
            }
        }
    }

    /// <summary>Closes a tab; one with unsaved changes asks first. False when the user kept it.</summary>
    internal async Task<bool> CloseTabAsync(DashboardTab tab)
    {
        if (tab.IsEditing && HasChanges(tab)
            && !await _ctx.ConfirmAsync("Close dashboard", $"Discard the changes of {tab.Title}?", "Discard").ConfigureAwait(true))
        {
            return false;
        }

        RemoveTab(tab);
        return true;
    }

    private static bool HasChanges(DashboardTab tab) =>
        tab.Saved is null || tab.Name.Trim() != tab.Saved.Name || !tab.WidgetList().SequenceEqual(tab.Saved.Widgets, WidgetComparer.Instance);

    private void RemoveTab(DashboardTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        tab.Dispose();
        MarkDashboards();
        WatchChanged();
    }

    /// <summary>The name is free among the stored dashboards and the other new ones (client side; the server checks again).</summary>
    internal string? NameTakenProblem(DashboardTab tab, string name)
    {
        var trimmed = name.Trim();
        var taken = Dashboards.Any(d => d.Id != tab.Id && string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            || Tabs.Any(t => t != tab && t.Saved is null && string.Equals(t.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        return taken ? DashboardRules.NameTaken(trimmed) : null;
    }

    /// <summary>A widget of a tab: a value widget with its last known value, a video widget with its picture.</summary>
    internal WidgetViewModel CreateWidget(DashboardTab tab, Widget widget, IDeviceInfo? device = null)
    {
        device ??= widget.DeviceId is { } id ? _devices.GetValueOrDefault(id) : null;
        var model = new WidgetViewModel(tab, widget, device);
        if (widget.Kind == WidgetKind.Video)
        {
            AttachVideo(model);
        }
        else if (model.Key is { } key && _values.TryGetValue(key, out var value))
        {
            model.SetValue(value);
        }

        return model;
    }

    private void AttachVideo(WidgetViewModel model)
    {
        if (MayWatch && _ctx.LiveVideo is { } access)
        {
            var video = access.Create(TilePlace);
            model.Attach(video);
            if (model.Device is { } device)
            {
                video.Start(device.Id, model.Widget.Camera);
            }
        }
    }

    internal WidgetEditorViewModel CreateEditor(DashboardTab tab, WidgetViewModel widget) => new(this, widget);

    /// <summary>The panel changed a widget: Save follows its errors.</summary>
    internal void EditorChanged() => SaveDashboardCommand.NotifyCanExecuteChanged();

    internal CatalogEvent? FindEvent(string? typeId) => typeId is not null ? _catalog.GetValueOrDefault(typeId) : null;

    internal IDeviceInfo? DeviceOf(Guid id) => _devices.GetValueOrDefault(id);

    /// <summary>The device of a value widget: every managed device; the one chosen now is checked.</summary>
    internal async Task<Guid?> PickDeviceAsync(WidgetViewModel widget) =>
        PickDevice is { } pick ? await pick(_ctx.Devices, widget.Widget.DeviceId).ConfigureAwait(true) : null;

    // ------------------------------------------------------------------ server

    private async Task LoadAndWatchAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var events = _ctx.WatchEventsAsync(ct); // subscribe first: nothing between the reads and the watch is lost
                Apply(JsonSerializer.Deserialize<DashboardList>(await _ctx.InvokeAsync(DashboardsMethods.List, null, ct).ConfigureAwait(true) ?? "null", DashboardsPluginInfo.Json));
                ApplyCatalog(JsonSerializer.Deserialize<CatalogReply>(await _ctx.InvokeAsync(DashboardsMethods.Catalog, null, ct).ConfigureAwait(true) ?? "null", DashboardsPluginInfo.Json));
                LoadError = null;
                IsLoaded = true;
                delay = TimeSpan.FromSeconds(1);
                _sentKeys = []; // a reconnected server knows no watch of this page
                WatchChanged();
                await foreach (var item in events.ConfigureAwait(true))
                {
                    if (item.PayloadJson is not { } payload)
                    {
                        continue;
                    }

                    switch (item.Topic)
                    {
                        case DashboardsMethods.DashboardsTopic:
                            Apply(JsonSerializer.Deserialize<DashboardList>(payload, DashboardsPluginInfo.Json));
                            break;
                        case DashboardsMethods.CatalogTopic:
                            ApplyCatalog(JsonSerializer.Deserialize<CatalogReply>(payload, DashboardsPluginInfo.Json));
                            break;
                        case DashboardsMethods.ValuesTopic:
                            ApplyValues(JsonSerializer.Deserialize<ValuesReply>(payload, DashboardsPluginInfo.Json));
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed read or a dropped watch is retried; the reason shows below the list.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LoadError = DashboardTexts.LoadFailed(ServerErrors.Message(ex));
                IsLoaded = true;
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    /// <summary>Renews the watch every 10 s, so the server keeps sending while the page lives.</summary>
    private async Task RenewWatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DashboardsPluginInfo.WatchRenewal, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SendWatchAsync(force: true).ConfigureAwait(true);
        }
    }

    /// <summary>The widgets of the open tabs changed: the server is told the new keys (once per burst of changes).</summary>
    internal void WatchChanged()
    {
        if (_watchPending || _life.IsCancellationRequested)
        {
            return;
        }

        _watchPending = true;
        _ = SendWatchAsync(force: false);
    }

    private async Task SendWatchAsync(bool force)
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _watchGate.WaitAsync(_life.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            _watchPending = false;
            var keys = Tabs.SelectMany(t => t.Widgets).Select(w => w.Key).OfType<ValueKey>().ToHashSet();
            if (!force && keys.SetEquals(_sentKeys))
            {
                return;
            }

            var json = await _ctx.InvokeAsync(DashboardsMethods.Watch, JsonSerializer.Serialize(new WatchRequest(_watchId, [.. keys]), DashboardsPluginInfo.Json), _life.Token).ConfigureAwait(true);
            _sentKeys = keys;
            ApplyValues(JsonSerializer.Deserialize<ValuesReply>(json ?? "null", DashboardsPluginInfo.Json));
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The next renewal tries again; the widgets keep their last values meanwhile.
        catch (Exception)
#pragma warning restore CA1031
        {
            _sentKeys = [];
        }
        finally
        {
            _watchGate.Release();
        }
    }

    /// <summary>The server's values: every widget of every open tab that shows one of them.</summary>
    internal void ApplyValues(ValuesReply? reply)
    {
        if (reply is null || reply.Values.Count == 0)
        {
            return;
        }

        var changed = new Dictionary<ValueKey, ValueSnapshot>();
        foreach (var value in reply.Values)
        {
            var key = new ValueKey(value.TypeId, value.DeviceId);
            _values[key] = value;
            changed[key] = value;
        }

        foreach (var widget in Tabs.SelectMany(t => t.Widgets))
        {
            if (widget.Key is { } key && changed.TryGetValue(key, out var value))
            {
                widget.SetValue(value);
            }
        }

        // Only the values of open tabs are kept.
        if (_values.Count > DashboardsPluginInfo.MaxWidgets * 20)
        {
            var open = Tabs.SelectMany(t => t.Widgets).Select(w => w.Key).OfType<ValueKey>().ToHashSet();
            _values = _values.Where(v => open.Contains(v.Key)).ToDictionary();
        }
    }

    /// <summary>The server's list: the entries, and open tabs that are not edited follow it (deleted ones close).</summary>
    internal void Apply(DashboardList? list)
    {
        if (list is null)
        {
            return;
        }

        var byId = list.Dashboards.ToDictionary(d => d.Id);
        var existing = Dashboards.ToDictionary(d => d.Id);
        Dashboards.ReplaceAll(list.Dashboards.Select(d =>
        {
            if (existing.TryGetValue(d.Id, out var entry))
            {
                entry.Dashboard = d;
                return entry;
            }

            return new DashboardEntry(d);
        }));

        foreach (var tab in Tabs.Where(t => t.Saved is not null).ToList())
        {
            if (byId.TryGetValue(tab.Saved!.Id, out var dashboard))
            {
                if (dashboard.Version != tab.Saved.Version && !tab.IsEditing)
                {
                    tab.ApplySaved(dashboard);
                }
            }
            else if (!tab.IsEditing)
            {
                RemoveTab(tab); // deleted by another user
            }
        }

        MarkDashboards();
        ListChanged();
    }

    /// <summary>The server's events and their fields: the Events list, the open panel and the empty state follow.</summary>
    internal void ApplyCatalog(CatalogReply? reply)
    {
        if (reply is null)
        {
            return;
        }

        _catalog = reply.Events.ToDictionary(e => e.TypeId, StringComparer.Ordinal);
        RebuildEvents();
        ActiveTab?.Editor?.RefreshCatalog();
        OnPropertyChanged(nameof(TriggersOff));
        OnPropertyChanged(nameof(NoTriggers));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowOpenTriggers));
    }

    private void RebuildEvents()
    {
        var search = EventSearch.Trim();
        var items = new List<EventListItem>();
        string? group = null;
        foreach (var catalogEvent in _catalog.Values.Where(e => e.Available)
            .OrderBy(e => e.SourceId == DashboardsPluginInfo.DeviceTriggersId ? 0 : 1)
            .ThenBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (search.Length > 0 && !catalogEvent.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                && !catalogEvent.Fields.Any(f => f.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            if (catalogEvent.Group != group)
            {
                group = catalogEvent.Group;
                items.Add(EventListItem.Group(group));
            }

            items.Add(EventListItem.For(catalogEvent));
        }

        Events.ReplaceAll(items);
        OnPropertyChanged(nameof(HasEvents));
    }

    private void Upsert(Dashboard saved)
    {
        if (Dashboards.FirstOrDefault(d => d.Id == saved.Id) is { } entry)
        {
            entry.Dashboard = saved;
        }
        else
        {
            Dashboards.ReplaceAll(Dashboards.Append(new DashboardEntry(saved)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        MarkDashboards();
        ListChanged();
    }

    private void ListChanged()
    {
        OnPropertyChanged(nameof(ShowNoDashboards));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void MarkDashboards()
    {
        var active = ActiveTab?.Id;
        foreach (var entry in Dashboards)
        {
            entry.IsActive = active is not null && entry.Id == active;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _devices = _ctx.Devices.ToDictionary(d => d.Id);
        Tree.Rebuild(_ctx.Devices, CameraSearch);
        OnPropertyChanged(nameof(HasCameras));
        foreach (var tab in Tabs)
        {
            tab.RefreshDevices(_devices);
        }
    }

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        if (!MayManage)
        {
            foreach (var tab in Tabs.Where(t => t.IsEditing).ToList())
            {
                if (tab.Saved is null)
                {
                    RemoveTab(tab);
                }
                else
                {
                    tab.Revert();
                }
            }
        }

        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(MayWatch));
        OnPropertyChanged(nameof(PictureProblem));
        OnPropertyChanged(nameof(NoDashboardsText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanEdit));
        NewDashboardCommand.NotifyCanExecuteChanged();
        EditDashboardCommand.NotifyCanExecuteChanged();
        DeleteDashboardCommand.NotifyCanExecuteChanged();

        // The pictures follow the "Live view" right: video widgets get or lose their videos.
        if (_mayWatch == MayWatch)
        {
            return;
        }

        _mayWatch = MayWatch;
        foreach (var widget in Tabs.SelectMany(t => t.Widgets).Where(w => w.Widget.Kind == WidgetKind.Video))
        {
            widget.Dispose();
            AttachVideo(widget);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ctx.DevicesChanged -= OnDevicesChanged;
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        {
            // The server forgets the watch at once (else after 30 s).
            _ = _ctx.InvokeAsync(DashboardsMethods.Unwatch, JsonSerializer.Serialize(new UnwatchRequest(_watchId), DashboardsPluginInfo.Json), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }

        _life.Cancel();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        _life.Dispose();
    }

    /// <summary>Widgets compared by their settings (records with a list inside compare the list by reference).</summary>
    private sealed class WidgetComparer : IEqualityComparer<Widget>
    {
        public static readonly WidgetComparer Instance = new();

        private static readonly IReadOnlyList<Threshold> None = [];

        public bool Equals(Widget? x, Widget? y) =>
            x is not null && y is not null && (x with { Thresholds = None }) == (y with { Thresholds = None }) && x.Thresholds.SequenceEqual(y.Thresholds);

        public int GetHashCode(Widget obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
