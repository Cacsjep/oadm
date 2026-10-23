using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>
/// The Live View page: the saved views and the cameras (devices with video, grouped by tag) on the left, the open views
/// as tabs on the right. A view is a 20 x 20 grid of cameras; New view and Edit put a tab in edit mode, where cameras are
/// dragged in from the left, moved, resized and removed, then saved for every user. The pictures are the host's live
/// video component, one per camera.
/// </summary>
public sealed partial class LiveViewPageViewModel : ObservableObject, IDisposable
{
    private static readonly LiveVideoPlace TilePlace = new() { ControlsOpen = false, FullscreenControlsOpen = true };

    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _life = new();
    private Dictionary<Guid, IDeviceInfo> _devices = [];
    private bool _mayWatch;

    public LiveViewPageViewModel(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _ctx.DevicesChanged += OnDevicesChanged;
        _ctx.PermissionsChanged += OnPermissionsChanged;
        _devices = ctx.Devices.ToDictionary(d => d.Id);
        _mayWatch = MayWatch;
        Tree.Rebuild(ctx.Devices, SearchText);
        _ = LoadAndWatchAsync(_life.Token);
    }

    /// <summary>Cameras (devices with video) grouped by tag.</summary>
    public DeviceTreeModel Tree { get; } = new() { Filter = d => d.HasVideo };

    public RangeObservableCollection<ViewEntry> Views { get; } = [];

    public ObservableCollection<ViewTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The left card is narrow, the views as icons only (like the navigation rail); the grid gets the room.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelToggleText))]
    public partial bool IsPanelCollapsed { get; set; }

    /// <summary>The bottom toggle of the left card, like the navigation rail's.</summary>
    public string PanelToggleText => IsPanelCollapsed ? "Expand" : "Collapse";

    [RelayCommand]
    private void TogglePanel() => IsPanelCollapsed = !IsPanelCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs), nameof(ShowNoTabs), nameof(IsEditing), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(CloseAllTabsCommand), nameof(SaveViewCommand), nameof(CancelEditCommand))]
    public partial ViewTab? ActiveTab { get; set; }

    /// <summary>Reading the views failed (below the list of views).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoViews))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>"Change views": New view, Edit, Delete view.</summary>
    public bool MayManage => _ctx.Has(PermissionKeys.Of(LiveViewPluginInfo.PluginId, LiveViewPluginInfo.ManageKey));

    /// <summary>"Live view": the pictures.</summary>
    public bool MayWatch => _ctx.Has(HostPermissionKeys.DevicesLiveView);

    /// <summary>The host offers the live video component.</summary>
    public bool HasLiveVideo => _ctx.LiveVideo is not null;

    /// <summary>Why the tiles show no picture: the role or an old client; empty when they can.</summary>
    public string PictureProblem => !HasLiveVideo ? LiveViewTexts.NotAvailable : !MayWatch ? LiveViewTexts.NotAllowed : string.Empty;

    public bool HasPictureProblem => PictureProblem.Length > 0;

    public bool HasTabs => ActiveTab is not null;

    public bool ShowNoTabs => !HasTabs;

    public bool ShowNoViews => IsLoaded && Views.Count == 0 && !HasLoadError;

    public string NoViewsText => MayManage ? LiveViewTexts.NoViews : LiveViewTexts.NoViewsReadOnly;

    public bool HasCameras => Tree.UsableCount > 0;

    public bool IsEditing => ActiveTab?.IsEditing == true;

    public bool CanEdit => MayManage && ActiveTab is { IsEditing: false, IsSingleCamera: false };

    partial void OnSearchTextChanged(string value) => Tree.Rebuild(_ctx.Devices, value);

    partial void OnActiveTabChanged(ViewTab? oldValue, ViewTab? newValue)
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

        MarkViews();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewTab.IsEditing) or nameof(ViewTab.IsSaving) or nameof(ViewTab.Name) or nameof(ViewTab.HasErrors))
        {
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(CanEdit));
            SaveViewCommand.NotifyCanExecuteChanged();
            CancelEditCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A new view: a tab in edit mode named "View N".</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private void NewView()
    {
        var names = Views.Select(v => v.Name).Concat(Tabs.Select(t => t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (names.Contains(string.Create(CultureInfo.InvariantCulture, $"View {n}")))
        {
            n++;
        }

        var tab = new ViewTab(this, null, string.Create(CultureInfo.InvariantCulture, $"View {n}")) { IsEditing = true };
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>A click on a saved view: its tab to the front, or a new tab.</summary>
    [RelayCommand]
    private void OpenView(ViewEntry? entry)
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

        var tab = new ViewTab(this, entry.View, entry.Name);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>
    /// A click on a camera in the tree: while editing it goes to the first free place of the view, otherwise it opens
    /// alone in its own tab (or that tab comes to the front).
    /// </summary>
    public void ClickCamera(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ActiveTab is { IsEditing: true } editing)
        {
            editing.AddToFirstFree(device);
            return;
        }

        if (_life.IsCancellationRequested)
        {
            return;
        }

        if (Tabs.FirstOrDefault(t => t.SingleCamera == device.Id) is { } open)
        {
            ActiveTab = open;
            return;
        }

        var tab = new ViewTab(this, device);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>The pen of a view's row: its tab to the front in edit mode.</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private void EditView(ViewEntry? entry)
    {
        if (entry is null || !MayManage)
        {
            return;
        }

        OpenView(entry);
        if (ActiveTab is { IsEditing: false } tab && tab.Id == entry.Id)
        {
            tab.Error = null;
            tab.Notice = null;
            tab.IsEditing = true;
        }
    }

    private bool CanSave() => ActiveTab is { IsEditing: true, IsSaving: false, HasErrors: false };

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveViewAsync()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        tab.ShowAllErrors();
        if (tab.HasErrors || ViewRules.TilesProblem(tab.TileList()) is { } problem && SetError(tab, problem))
        {
            return;
        }

        tab.IsSaving = true;
        tab.Error = null;
        tab.Notice = null;
        try
        {
            var json = await _ctx.InvokeAsync("save", JsonSerializer.Serialize(tab.ToRequest(), LiveViewPluginInfo.Json), _life.Token).ConfigureAwait(true);
            var saved = JsonSerializer.Deserialize<SavedView>(json ?? "null", LiveViewPluginInfo.Json)
                ?? throw new InvalidOperationException("The server sent no view.");
            tab.IsEditing = false;
            tab.ApplySaved(saved);
            Upsert(saved);
            if (saved.Dropped > 0)
            {
                tab.Notice = ViewRules.DroppedText(saved.Dropped);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Any failure of the server call is shown below the view's toolbar.
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

    private static bool SetError(ViewTab tab, string problem)
    {
        tab.Error = problem;
        return true;
    }

    private bool CanCancel() => ActiveTab is { IsEditing: true, IsSaving: false };

    /// <summary>Cancel: a new view closes, a saved one goes back to what is stored.</summary>
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

    /// <summary>The red bin of a view's row: asks, deletes it for every user and closes its tab.</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private async Task DeleteViewAsync(ViewEntry? entry)
    {
        if (entry is null || !MayManage
            || !await _ctx.ConfirmAsync("Delete view", $"Delete the view {entry.Name} for every user?", "Delete view").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync("delete", JsonSerializer.Serialize(new DeleteViewRequest(entry.Id), LiveViewPluginInfo.Json), _life.Token).ConfigureAwait(true);
            if (Tabs.FirstOrDefault(t => t.Id == entry.Id) is { } tab)
            {
                RemoveTab(tab);
            }

            Views.RemoveAll(v => v.Id == entry.Id);
            OnPropertyChanged(nameof(ShowNoViews));
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Any failure of the server call is shown below the list of views.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LoadError = LiveViewTexts.DeleteFailed(ServerErrors.Message(ex));
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
    internal async Task<bool> CloseTabAsync(ViewTab tab)
    {
        if (tab.IsEditing && HasChanges(tab)
            && !await _ctx.ConfirmAsync("Close view", $"Discard the changes of {tab.Title}?", "Discard").ConfigureAwait(true))
        {
            return false;
        }

        RemoveTab(tab);
        return true;
    }

    private static bool HasChanges(ViewTab tab) =>
        tab.Saved is null || tab.Name.Trim() != tab.Saved.Name || !tab.TileList().SequenceEqual(tab.Saved.Tiles);

    private void RemoveTab(ViewTab tab)
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
        MarkViews();
    }

    /// <summary>The name is free among the stored views and the other open new views (client side; the server checks again).</summary>
    internal string? NameTakenProblem(ViewTab tab, string name)
    {
        var trimmed = name.Trim();
        var taken = Views.Any(v => v.Id != tab.Id && string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            || Tabs.Any(t => t != tab && t.Saved is null && !t.IsSingleCamera && string.Equals(t.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        return taken ? ViewRules.NameTaken(trimmed) : null;
    }

    /// <summary>A camera of a tab with its picture (when the host and the role allow it).</summary>
    internal TileViewModel CreateTile(ViewTab tab, ViewTile tile, IDeviceInfo? device = null)
    {
        device ??= _devices.GetValueOrDefault(tile.DeviceId);
        var model = new TileViewModel(tab, tile, device);
        if (MayWatch && _ctx.LiveVideo is { } access)
        {
            var video = access.Create(TilePlace);
            model.Attach(video);
            if (device is not null)
            {
                video.Start(device.Id, tile.Camera);
            }
        }

        return model;
    }

    private async Task LoadAndWatchAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var json = await _ctx.InvokeAsync("list", null, ct).ConfigureAwait(true);
                Apply(JsonSerializer.Deserialize<ViewList>(json ?? "null", LiveViewPluginInfo.Json));
                LoadError = null;
                IsLoaded = true;
                delay = TimeSpan.FromSeconds(1);
                await foreach (var item in _ctx.WatchEventsAsync(ct).ConfigureAwait(true))
                {
                    if (item.Topic == LiveViewPluginInfo.ViewsTopic && item.PayloadJson is { } payload)
                    {
                        Apply(JsonSerializer.Deserialize<ViewList>(payload, LiveViewPluginInfo.Json));
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
                LoadError = LiveViewTexts.LoadFailed(ServerErrors.Message(ex));
                IsLoaded = true;
            }

            // The watch ended (older host: no events) or failed: read again after a while.
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

    /// <summary>The server's list: the entries, and open tabs that are not edited follow it (deleted ones close).</summary>
    internal void Apply(ViewList? list)
    {
        if (list is null)
        {
            return;
        }

        var byId = list.Views.ToDictionary(v => v.Id);
        var existing = Views.ToDictionary(v => v.Id);
        Views.ReplaceAll(list.Views.Select(v =>
        {
            if (existing.TryGetValue(v.Id, out var entry))
            {
                entry.View = v;
                return entry;
            }

            return new ViewEntry(v);
        }));

        foreach (var tab in Tabs.Where(t => t.Saved is not null).ToList())
        {
            if (byId.TryGetValue(tab.Saved!.Id, out var view))
            {
                if (view.Version != tab.Saved.Version && !tab.IsEditing)
                {
                    tab.ApplySaved(view);
                }
            }
            else if (!tab.IsEditing)
            {
                RemoveTab(tab); // deleted by another user
            }
        }

        MarkViews();
        OnPropertyChanged(nameof(ShowNoViews));
    }

    private void Upsert(SavedView saved)
    {
        if (Views.FirstOrDefault(v => v.Id == saved.Id) is { } entry)
        {
            entry.View = saved;
        }
        else
        {
            Views.ReplaceAll(Views.Append(new ViewEntry(saved)).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        MarkViews();
        OnPropertyChanged(nameof(ShowNoViews));
    }

    private void MarkViews()
    {
        var active = ActiveTab?.Id;
        foreach (var entry in Views)
        {
            entry.IsActive = active is not null && entry.Id == active;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _devices = _ctx.Devices.ToDictionary(d => d.Id);
        Tree.Rebuild(_ctx.Devices, SearchText);
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
        OnPropertyChanged(nameof(HasPictureProblem));
        OnPropertyChanged(nameof(NoViewsText));
        OnPropertyChanged(nameof(CanEdit));
        NewViewCommand.NotifyCanExecuteChanged();
        EditViewCommand.NotifyCanExecuteChanged();
        DeleteViewCommand.NotifyCanExecuteChanged();

        // The pictures follow the "Live view" right: the tiles get or lose their videos.
        if (_mayWatch == MayWatch)
        {
            return;
        }

        _mayWatch = MayWatch;
        foreach (var tab in Tabs)
        {
            foreach (var tile in tab.Tiles)
            {
                tile.Dispose();
                if (MayWatch && _ctx.LiveVideo is { } access)
                {
                    var video = access.Create(TilePlace);
                    tile.Attach(video);
                    if (tile.Device is { } device)
                    {
                        video.Start(device.Id, tile.Camera);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _ctx.DevicesChanged -= OnDevicesChanged;
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        _life.Cancel();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        _life.Dispose();
    }
}
