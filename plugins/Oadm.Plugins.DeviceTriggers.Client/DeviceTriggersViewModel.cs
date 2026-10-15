using System.Globalization;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceTriggers.Client;

/// <summary>A trigger in the list, updated in place.</summary>
public sealed partial class TriggerRowViewModel : ObservableObject
{
    private readonly Action<TriggerRowViewModel, bool> _toggle;
    private bool _applying;

    internal TriggerRowViewModel(TriggerSummary summary, Action<TriggerRowViewModel, bool> toggle)
    {
        _toggle = toggle;
        Id = summary.Id;
        Apply(summary);
    }

    public Guid Id { get; }

    [ObservableProperty] public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty] public partial string EventText { get; private set; } = string.Empty;

    [ObservableProperty] public partial int DeviceCount { get; private set; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    [ObservableProperty] public partial bool Stateful { get; private set; }

    [ObservableProperty] public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool IsOk { get; private set; }

    [ObservableProperty] public partial bool IsAccent { get; private set; }

    [ObservableProperty] public partial bool IsError { get; private set; }

    [ObservableProperty] public partial string LastEventText { get; private set; } = string.Empty;

    public DateTimeOffset LastEventSortKey { get; private set; }

    internal void Apply(TriggerSummary s)
    {
        _applying = true;
        Name = s.Name;
        EventText = s.FilterText.Length == 0 ? s.EventName : $"{s.EventName} · {s.FilterText}";
        DeviceCount = s.Devices;
        Enabled = s.Enabled;
        Stateful = s.Stateful;
        _applying = false;

        var parts = new List<string>();
        if (!s.Enabled)
        {
            parts.Add("Turned off");
        }
        else
        {
            if (s.Stateful)
            {
                parts.Add(s.On == 0 ? "Off" : s.On == s.Devices && s.Devices == 1 ? "On" : $"On for {s.On} of {s.Devices}");
            }

            if (s.Lost > 0)
            {
                parts.Add($"{s.Lost} connection lost");
            }
            else if (s.Waiting > 0)
            {
                parts.Add($"{s.Waiting} connecting");
            }
            else if (!s.Stateful)
            {
                parts.Add("Ready");
            }

            if (s.NotWatched > 0)
            {
                parts.Add(DeviceTriggersPluginInfo.NotWatchedText);
            }
        }

        StateText = string.Join(" · ", parts);
        IsError = s.Enabled && (s.Lost > 0 || s.NotWatched > 0);
        IsAccent = s.Enabled && !IsError && s.Waiting > 0;
        IsOk = s.Enabled && !IsError && !IsAccent && (s.On > 0 || !s.Stateful);
        LastEventSortKey = s.LastEventUtc ?? DateTimeOffset.MinValue;
        LastEventText = s.LastEventUtc is { } last ? DeviceTriggersViewModel.TimeText(last) : string.Empty;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _toggle(this, value);
        }
    }
}

/// <summary>One device of the selected trigger.</summary>
public sealed record TriggerDeviceRow(
    string Address,
    string Model,
    string ConnectionText,
    string? Problem,
    bool IsOk,
    bool IsAccent,
    bool IsWarning,
    bool IsError,
    string StateText,
    string LastEventText,
    string AddressSortKey);

/// <summary>
/// Page "Device triggers": the triggers (live counts), New / Edit / Duplicate / Delete and the on switch (administrators;
/// the server refuses others), and the devices of the selected trigger with their connection and state. Live through the
/// plugin's "changed" events (at most every 300 ms a reload); without events (fake mode) it reloads every 2 s.
/// </summary>
public sealed partial class DeviceTriggersViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<Guid, TriggerRowViewModel> _rows = [];
    private List<TriggerRowViewModel> _all = [];
    private CancellationTokenSource? _active;
    private int _reloadQueued;

    public DeviceTriggersViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.PermissionsChanged += (_, _) => OnPropertyChanged(nameof(MayManage));
    }

    /// <summary>The role may change triggers ("oadm.device-triggers:manage"); without it the page only shows them.</summary>
    public bool MayManage => _ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Of(DeviceTriggersPluginInfo.PluginId, Oadm.Sdk.Plugins.PermissionKeys.Manage));

    /// <summary>Opens the editor; returns true when the trigger was saved (tests replace it).</summary>
    public Func<TriggerEditorViewModel, Task<bool>> ShowEditor { get; set; } = async editor =>
    {
        var window = new TriggerEditorWindow();
        window.Attach(editor);
        return editor.Owner is { } owner ? await window.ShowDialog<bool>(owner).ConfigureAwait(true) : false;
    };

    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public static TimeSpan ReloadDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public RangeObservableCollection<TriggerRowViewModel> Triggers { get; } = [];

    public RangeObservableCollection<TriggerDeviceRow> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DevicesTitle))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DuplicateCommand), nameof(DeleteCommand))]
    public partial TriggerRowViewModel? SelectedTrigger { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string DevicesSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasSelection => SelectedTrigger is not null;

    public string DevicesTitle => SelectedTrigger is { } t ? "Devices of " + t.Name : "Devices";

    public bool ShowEmpty => IsLoaded && _all.Count == 0;

    public static string EmptyText => "No triggers yet. Choose New trigger to use an event of your devices.";

    public string DevicesEmptyText => SelectedTrigger is null ? "Choose a trigger to see its devices." : string.Empty;

    /// <summary>The page is shown: load and follow the changes.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = ReloadAsync();
        _ = WatchAsync(_active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>Reads the triggers and the selected trigger's devices.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            var list = TriggerJson.Deserialize<ListReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.List, null, CancellationToken.None).ConfigureAwait(true));
            ApplyList(list.Triggers);
            await ReloadDevicesAsync().ConfigureAwait(true);
            ErrorText = null;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = Message(ex);
        }
    }

    internal static string TimeText(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture) : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    internal static string Message(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTriggerChanged(TriggerRowViewModel? value)
    {
        OnPropertyChanged(nameof(DevicesEmptyText));
        _ = ReloadDevicesAsync();
    }

    [RelayCommand]
    private async Task NewTriggerAsync() => await OpenEditorAsync(null).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedTrigger is not { } row)
        {
            return;
        }

        try
        {
            var reply = TriggerJson.Deserialize<GetReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.Get, TriggerJson.Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Trigger is { } trigger)
            {
                await OpenEditorAsync(trigger).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = Message(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DuplicateAsync()
    {
        if (SelectedTrigger is not { } row)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var reply = TriggerJson.Deserialize<SaveReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.Duplicate, TriggerJson.Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Error is { } error)
            {
                ErrorText = error;
                return;
            }

            await ReloadAsync().ConfigureAwait(true);
            SelectedTrigger = reply.Trigger is { } copy ? _rows.GetValueOrDefault(copy.Id) : SelectedTrigger;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedTrigger is not { } row)
        {
            return;
        }

        if (!await _ctx.ConfirmAsync("Delete trigger", $"Delete the trigger {row.Name}? Event rules that use it stop working.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _ctx.InvokeAsync(DeviceTriggersMethods.Delete, TriggerJson.Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true);
            SelectedTrigger = null;
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task OpenEditorAsync(TriggerDefinition? trigger)
    {
        var editor = new TriggerEditorViewModel(_ctx, trigger, [.. _all.Where(r => r.Id != trigger?.Id).Select(r => r.Name)]);
        try
        {
            if (await ShowEditor(editor).ConfigureAwait(true))
            {
                await ReloadAsync().ConfigureAwait(true);
                if (editor.Saved is { } saved && _rows.TryGetValue(saved.Id, out var row))
                {
                    SelectedTrigger = row;
                }
            }
        }
        finally
        {
            editor.Dispose();
        }
    }

    private void Toggle(TriggerRowViewModel row, bool enabled) => _ = RunAsync(async () =>
    {
        await _ctx.InvokeAsync(DeviceTriggersMethods.SetEnabled, TriggerJson.Serialize(new SetEnabledRequest(row.Id, enabled)), CancellationToken.None).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }, revert: ReloadAsync);

    private async Task RunAsync(Func<Task> action, Func<Task>? revert = null)
    {
        try
        {
            ErrorText = null;
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = Message(ex);
            if (revert is not null)
            {
                await revert().ConfigureAwait(true);
            }

            ErrorText = message;
        }
    }

    private void ApplyList(IReadOnlyList<TriggerSummary> summaries)
    {
        var seen = new HashSet<Guid>();
        var all = new List<TriggerRowViewModel>(summaries.Count);
        foreach (var summary in summaries)
        {
            seen.Add(summary.Id);
            if (_rows.TryGetValue(summary.Id, out var row))
            {
                row.Apply(summary);
            }
            else
            {
                row = new TriggerRowViewModel(summary, Toggle);
                _rows[summary.Id] = row;
            }

            all.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        var structural = all.Count != _all.Count || all.Where((r, i) => !ReferenceEquals(r, _all[i])).Any();
        _all = all;
        if (structural)
        {
            ApplyFilter();
        }

        var on = all.Count(r => r.Enabled);
        StatusText = all.Count == 0 ? string.Empty : $"{Count(all.Count, "trigger", "triggers")} · {on} turned on";
        IsLoaded = true;
        OnPropertyChanged(nameof(ShowEmpty));
        if (SelectedTrigger is { } selected && !_rows.ContainsKey(selected.Id))
        {
            SelectedTrigger = null;
        }
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var selected = SelectedTrigger;
        Triggers.ReplaceAll(text.Length == 0 ? _all : _all.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.EventText.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList());
        if (selected is not null && Triggers.Contains(selected))
        {
            SelectedTrigger = selected;
        }
    }

    private async Task ReloadDevicesAsync()
    {
        if (SelectedTrigger is not { } row)
        {
            Devices.ReplaceAll([]);
            DevicesSummary = string.Empty;
            return;
        }

        var reply = TriggerJson.Deserialize<GetReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.Get, TriggerJson.Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
        if (SelectedTrigger?.Id != row.Id)
        {
            return;
        }

        var devices = _ctx.Devices.ToDictionary(d => d.Id);
        var stateful = reply.Trigger?.Stateful ?? true;
        var rows = reply.Devices.Select(d => Row(d, devices.GetValueOrDefault(d.DeviceId), stateful)).OrderBy(r => r.AddressSortKey, StringComparer.Ordinal).ToList();
        Devices.ReplaceAll(rows);
        var connected = reply.Devices.Count(d => d.Connection == TriggerConnection.Connected);
        var on = reply.Devices.Count(d => d.IsOn);
        DevicesSummary = reply.Devices.Count == 0 ? string.Empty
            : $"{connected} of {Count(reply.Devices.Count, "device", "devices")} connected" + (stateful ? $" · {on} on" : string.Empty);
    }

    private static TriggerDeviceRow Row(TriggerDeviceState state, IDeviceInfo? device, bool stateful)
    {
        var address = device?.Address ?? "Removed device";
        var (text, ok, accent, warning, error) = state.Connection switch
        {
            TriggerConnection.Connected => ("Connected", true, false, false, false),
            TriggerConnection.Connecting => ("Connecting", false, true, false, false),
            TriggerConnection.Problem => (state.Problem ?? "Problem", false, false, true, false),
            TriggerConnection.Lost => ("Connection lost", false, false, false, true),
            _ => ("Turned off", false, false, false, false),
        };
        var last = state.LastEventUtc is { } time ? $"{TimeText(time)} · {state.LastEventText}" : string.Empty;
        return new TriggerDeviceRow(
            address,
            device?.Model?.Replace("AXIS ", string.Empty, StringComparison.Ordinal) ?? string.Empty,
            text,
            state.Connection == TriggerConnection.Lost ? state.Problem : null,
            ok,
            accent,
            warning,
            error,
            !stateful || state.Connection == TriggerConnection.Off ? string.Empty : state.IsOn ? "On" : state.Connection == TriggerConnection.Connected ? "Off" : string.Empty,
            last,
            AddressKey(address));
    }

    private static string AddressKey(string address)
    {
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }

    private static string Count(int n, string one, string many) => string.Create(CultureInfo.CurrentCulture, $"{n:N0} {(n == 1 ? one : many)}");

    private void QueueReload()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 0)
        {
            _ = ReloadLaterAsync();
        }
    }

    /// <summary>One reload for a burst of changes (on the caller's context, the UI thread).</summary>
    private async Task ReloadLaterAsync()
    {
        await Task.Delay(ReloadDelay).ConfigureAwait(true);
        Interlocked.Exchange(ref _reloadQueued, 0);
        if (_active is not null)
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var any = false;
            try
            {
                await foreach (var item in _ctx.WatchEventsAsync(ct).ConfigureAwait(true))
                {
                    any = true;
                    if (item.Topic == DeviceTriggersMethods.ChangedTopic)
                    {
                        QueueReload();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connection dropped: read again and watch again.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!any)
            {
                await ReloadAsync().ConfigureAwait(true); // no events (fake mode, older host): poll
            }
        }
    }

    /// <summary>Owner for the editor dialog.</summary>
    internal Window? Owner => _ctx.Owner;
}
