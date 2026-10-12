using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.WebView.Client;

/// <summary>Whether the embedded browser exists on this computer, and what to install when not.</summary>
public sealed record WebRuntimeState(bool IsAvailable, string? Problem)
{
    public static WebRuntimeState Available { get; } = new(true, null);
}

/// <summary>
/// The Web View page: the devices grouped by tag on the left (a device with several tags under each; "No tag" last),
/// browser tabs on the right, one per device. A click on a device opens its tab or brings it to the front. The page select
/// opens a preset or a custom path in the active tab, and new tabs use the page chosen last.
/// </summary>
public sealed partial class WebViewPageViewModel : ValidatingViewModel, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly HashSet<WebTab> _closed = [];
    private readonly CancellationTokenSource _life = new();
    private List<WebTreeDevice> _order = [];
    private Dictionary<string, bool> _collapsed = new(StringComparer.Ordinal);
    private bool _applyingTab;

    public WebViewPageViewModel(ICorePluginClientContext ctx, WebRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(runtime);
        _ctx = ctx;
        Runtime = runtime;
        SelectedPreset = WebPagePreset.All[0];
        Validation.Rule(nameof(CustomPath), () => !SelectedPreset.IsCustom || IsDevicePath(CustomPath.Trim()) ? null : WebViewTexts.CustomPathInvalid);
        _ctx.DevicesChanged += OnDevicesChanged;
        _ctx.PermissionsChanged += (_, _) =>
        {
            if (!MayOpen)
            {
                CloseAllTabs(); // the role lost "Open devices": open pages close too
            }

            OnPropertyChanged(nameof(MayOpen));
            OnPropertyChanged(nameof(ShowNotAllowed));
            OnPropertyChanged(nameof(ShowNoTabs));
        };
        Rebuild();
    }

    public WebRuntimeState Runtime { get; }

    public bool IsRuntimeMissing => !Runtime.IsAvailable;

    /// <summary>The role may open devices ("oadm.web-view:open": the relay acts with the stored login).</summary>
    public bool MayOpen => _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(WebViewPluginInfo.PluginId, "open"));

    /// <summary>The page says why nothing opens (the runtime message wins).</summary>
    public bool ShowNotAllowed => !MayOpen && !IsRuntimeMissing;

    internal IDeviceWebAccess? Access => _ctx.DeviceWeb;

    public static IReadOnlyList<WebPagePreset> Presets => WebPagePreset.All;

    /// <summary>The tree as rows: group headers and the devices of expanded groups.</summary>
    public RangeObservableCollection<WebTreeRow> TreeRows { get; } = [];

    public ObservableCollection<WebTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs), nameof(ShowNoTabs))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(GoBackCommand), nameof(CloseAllTabsCommand))]
    public partial WebTab? ActiveTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial WebPagePreset SelectedPreset { get; set; }

    public bool IsCustom => SelectedPreset.IsCustom;

    /// <summary>The path of Custom; used once it is valid (Enter or leaving the field opens it in the active tab).</summary>
    [ObservableProperty]
    public partial string CustomPath { get; set; } = string.Empty;

    public bool HasTabs => ActiveTab is not null;

    public bool ShowNoTabs => !HasTabs && Runtime.IsAvailable && MayOpen;

    public bool HasDevices => _ctx.Devices.Count > 0;

    /// <summary>The native browser of a tab goes back one page (the view owns the browsers).</summary>
    public event EventHandler<WebTab>? GoBackRequested;

    /// <summary>The page the next tab opens: the preset chosen last (Custom with its path).</summary>
    private string CurrentPath => SelectedPreset.IsCustom ? CustomPath.Trim() : SelectedPreset.Path;

    private bool CanUseCurrentPath => !SelectedPreset.IsCustom || IsDevicePath(CustomPath.Trim());

    /// <summary>One leading "/", no "//", "\" or spaces: the path stays on the device.</summary>
    public static bool IsDevicePath(string path) =>
        path.Length > 0 && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal) && !path.Any(c => char.IsControl(c) || c == ' ');

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnActiveTabChanged(WebTab? oldValue, WebTab? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            _applyingTab = true;
            try
            {
                if (newValue.Preset.IsCustom)
                {
                    CustomPath = newValue.Path;
                }

                SelectedPreset = newValue.Preset;
            }
            finally
            {
                _applyingTab = false;
            }
        }

        MarkTree();
    }

    partial void OnSelectedPresetChanged(WebPagePreset value)
    {
        if (_applyingTab || value.IsCustom)
        {
            return;
        }

        OpenPathInActiveTab();
    }

    /// <summary>Opens the custom path in the active tab (Enter in the field, or leaving it).</summary>
    [RelayCommand]
    private void ApplyCustomPath()
    {
        if (SelectedPreset.IsCustom && CanUseCurrentPath && ActiveTab is { } tab && (tab.Path != CurrentPath || !tab.Preset.IsCustom))
        {
            OpenPathInActiveTab();
        }
    }

    private void OpenPathInActiveTab()
    {
        if (ActiveTab is not { } tab || !CanUseCurrentPath)
        {
            return;
        }

        tab.Preset = SelectedPreset;
        tab.Path = CurrentPath;
        _ = tab.LoadAsync(tab.Device, _life.Token);
    }

    /// <summary>A click on a device: its tab to the front, or a new tab with the page chosen last.</summary>
    [RelayCommand]
    private void OpenDevice(WebTreeDevice? row)
    {
        if (row is null || !Runtime.IsAvailable || !MayOpen || _life.IsCancellationRequested)
        {
            return;
        }

        if (Tabs.FirstOrDefault(t => t.Device.Id == row.Device.Id) is { } open)
        {
            open.GroupKey = row.GroupKey;
            ActiveTab = open;
            return;
        }

        var tab = new WebTab(this, row.Device, row.GroupKey, CanUseCurrentPath ? SelectedPreset : WebPagePreset.All[0], CanUseCurrentPath ? CurrentPath : "/");
        Tabs.Add(tab);
        ActiveTab = tab;
        _ = tab.LoadAsync(row.Device, _life.Token);
    }

    [RelayCommand]
    private void ToggleGroup(WebTreeGroup? group)
    {
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        _collapsed[group.Key] = !group.IsExpanded;
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(HasTabs))]
    private void Reload()
    {
        if (ActiveTab is { } tab)
        {
            _ = tab.LoadAsync(tab.Device, _life.Token);
        }
    }

    /// <summary>The classic browser Back of the active tab.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (ActiveTab is { } tab)
        {
            GoBackRequested?.Invoke(this, tab);
        }
    }

    private bool CanGoBack() => ActiveTab is { CanGoBack: true };

    /// <summary>The active tab's history changed (the view reports it).</summary>
    internal void RefreshGoBack() => GoBackCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasTabs))]
    private void CloseAllTabs()
    {
        foreach (var tab in Tabs.ToList())
        {
            CloseTab(tab);
        }
    }

    internal void CloseTab(WebTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _closed.Add(tab);
        tab.Dispose();
        Tabs.RemoveAt(index);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        MarkTree();
    }

    internal bool IsClosed(WebTab tab) => _closed.Contains(tab);

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // Devices removed from OADM close their tabs; changed ones keep theirs with the new address in the title.
        var byId = _ctx.Devices.ToDictionary(d => d.Id);
        foreach (var tab in Tabs.ToList())
        {
            if (byId.TryGetValue(tab.Device.Id, out var device))
            {
                tab.Update(device);
            }
            else
            {
                CloseTab(tab);
            }
        }

        Rebuild();
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>Builds the tree rows in one reset: O(devices x tags).</summary>
    private void Rebuild()
    {
        var search = SearchText.Trim();
        var devices = _ctx.Devices
            .Where(d => search.Length == 0 || Matches(d, search))
            .OrderBy(d => DeviceOrder.AddressKey(d.Address), StringComparer.Ordinal)
            .ToList();
        var groups = new SortedDictionary<string, List<IDeviceInfo>>(StringComparer.CurrentCultureIgnoreCase);
        var untagged = new List<IDeviceInfo>();
        foreach (var device in devices)
        {
            if (device.Tags.Count == 0)
            {
                untagged.Add(device);
                continue;
            }

            foreach (var tag in device.Tags)
            {
                if (!groups.TryGetValue(tag, out var list))
                {
                    groups[tag] = list = [];
                }

                list.Add(device);
            }
        }

        var rows = new List<WebTreeRow>(devices.Count + groups.Count + 1);
        var order = new List<WebTreeDevice>(devices.Count);
        void Add(string key, string name, List<IDeviceInfo> members)
        {
            var collapsed = _collapsed.TryGetValue(key, out var c) && c;
            rows.Add(new WebTreeGroup(key, name) { IsExpanded = !collapsed });
            foreach (var device in members)
            {
                var row = new WebTreeDevice(device, key);
                order.Add(row);
                if (!collapsed)
                {
                    rows.Add(row);
                }
            }
        }

        foreach (var (tag, members) in groups)
        {
            Add(tag, tag, members);
        }

        if (untagged.Count > 0)
        {
            Add(string.Empty, WebViewTexts.NoTag, untagged);
        }

        _order = order;
        TreeRows.ReplaceAll(rows);
        MarkTree();
    }

    private static bool Matches(IDeviceInfo device, string search) =>
        device.Address.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (device.Model?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || device.Serial.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (device.HostName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || device.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase));

    /// <summary>Marks the rows of open devices and the active one (O(rows)).</summary>
    private void MarkTree()
    {
        var open = Tabs.Select(t => t.Device.Id).ToHashSet();
        var active = ActiveTab;
        foreach (var row in _order)
        {
            row.IsOpen = open.Contains(row.Device.Id);
            row.IsActive = active is not null && row.Device.Id == active.Device.Id && row.GroupKey == active.GroupKey;
        }
    }


    public void Dispose()
    {
        _ctx.DevicesChanged -= OnDevicesChanged;
        _life.Cancel();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
    }
}
