using System.Globalization;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Client;

/// <summary>A device in the editor's list: check box, address, model and what OADM knows of its events.</summary>
public sealed partial class TriggerDeviceChoice : ObservableObject
{
    private readonly Action _changed;

    internal TriggerDeviceChoice(IDeviceInfo device, Action changed)
    {
        _changed = changed;
        Id = device.Id;
        Address = device.Address;
        Model = device.Model?.Replace("AXIS ", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        Supported = device.Apis.Count == 0 || device.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion);
        SearchText = Address + " " + Model;
        if (!Supported)
        {
            SetStatus(DeviceTriggersPluginInfo.NotSupportedReason, error: true);
        }
    }

    public Guid Id { get; }

    public string Address { get; }

    public string Model { get; }

    public bool Supported { get; }

    internal string SearchText { get; }

    [ObservableProperty] public partial bool IsChecked { get; set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool IsOk { get; private set; }

    [ObservableProperty] public partial bool IsAccent { get; private set; }

    [ObservableProperty] public partial bool IsError { get; private set; }

    internal void SetStatus(string text, bool ok = false, bool accent = false, bool error = false)
    {
        StatusText = text;
        IsOk = ok;
        IsAccent = accent;
        IsError = error;
    }

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>An event the chosen devices offer, for the event select.</summary>
public sealed record TriggerEventOption(DeviceEvent Event)
{
    public string Text => Event.Name;

    public override string ToString() => Text;
}

/// <summary>A value of a source item, or "Any".</summary>
public sealed record FilterOption(string? Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One source item of the chosen event: Any or one of the values the devices list.</summary>
public sealed partial class FilterRowViewModel : ObservableObject
{
    internal FilterRowViewModel(EventItem item, string? selected)
    {
        Item = item;
        Options = [new FilterOption(null, "Any"), .. item.Values.Select(v => new FilterOption(v.Value, string.IsNullOrWhiteSpace(v.DisplayName) ? v.Value : $"{v.DisplayName} ({v.Value})"))];
        Selected = Options.FirstOrDefault(o => o.Value == selected) ?? Options[0];
    }

    internal EventItem Item { get; }

    public string Label => Item.Label;

    public IReadOnlyList<FilterOption> Options { get; }

    [ObservableProperty] public partial FilterOption Selected { get; set; }
}

/// <summary>
/// The trigger dialog (user decisions 2026-10-10): name, turned on, the devices (only those that can send events this
/// way), **Read events** (live from the chosen devices, every time; a device whose list cannot be read stays with the
/// reason), the event all chosen devices offer, and per source item Any or one value. Save is disabled with the first
/// problem as its tooltip; the server checks again.
/// </summary>
public sealed partial class TriggerEditorViewModel : ValidatingViewModel, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TriggerDefinition? _initial;
    private readonly IReadOnlyList<string> _otherNames;
    private readonly List<TriggerDeviceChoice> _devices;
    private readonly Dictionary<Guid, TriggerDeviceChoice> _byId;
    private readonly Dictionary<string, DeviceEvent> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>?> _offered = []; // null = read failed
    private readonly CancellationTokenSource _closing = new();
    private int _chosen;
    private bool _silent;

    public TriggerEditorViewModel(ICorePluginClientContext ctx, TriggerDefinition? trigger, IReadOnlyList<string> otherNames)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _initial = trigger;
        _otherNames = otherNames ?? [];
        Name = trigger?.Name ?? string.Empty;
        Enabled = trigger?.Enabled ?? true;
        var chosen = trigger?.DeviceIds.ToHashSet() ?? [];
        _devices = [.. ctx.Devices
            .Select(d => new TriggerDeviceChoice(d, OnDeviceChecked))
            .OrderBy(d => chosen.Contains(d.Id) ? 0 : 1)
            .ThenBy(d => d.Supported ? 0 : 1)
            .ThenBy(d => AddressKey(d.Address), StringComparer.Ordinal)];
        _byId = _devices.ToDictionary(d => d.Id);
        _silent = true;
        foreach (var device in _devices.Where(d => chosen.Contains(d.Id)))
        {
            device.IsChecked = true;
            _chosen++;
        }

        _silent = false;
        VisibleDevices.ReplaceAll(_devices);
        Validation.Rule(nameof(Name), () => TriggerValidation.NameProblem(Name, _otherNames));
        Validation.Validate();
        UpdateSummary();
    }

    public event EventHandler<bool>? CloseRequested;

    public static TimeSpan ReadPollInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    public bool IsNew => _initial is null;

    public string Title => IsNew ? "New trigger" : "Edit trigger";

    public Window? Owner => _ctx.Owner;

    /// <summary>The trigger as saved (after Save).</summary>
    public TriggerDefinition? Saved { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanSave))]
    public partial string Name { get; set; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    public RangeObservableCollection<TriggerDeviceChoice> VisibleDevices { get; } = [];

    [ObservableProperty] public partial string DeviceSearch { get; set; } = string.Empty;

    [ObservableProperty] public partial string DevicesSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanSave), nameof(DevicesError), nameof(EventError))]
    [NotifyCanExecuteChangedFor(nameof(ReadEventsCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty] public partial double ReadProgress { get; private set; }

    [ObservableProperty] public partial string ReadText { get; private set; } = string.Empty;

    public RangeObservableCollection<TriggerEventOption> EventOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanSave), nameof(EventKindText), nameof(EventError), nameof(DevicesError), nameof(HasFilters))]
    public partial TriggerEventOption? SelectedEvent { get; set; }

    public RangeObservableCollection<FilterRowViewModel> Filters { get; } = [];

    public bool HasFilters => Filters.Count > 0;

    public string? EventKindText => SelectedEvent?.Event is not { } e ? null
        : e.Stateful ? "Turns on and off. Rules can use it as \"When\" and \"And\"." : "Happens once. Rules can use it as \"When\" only.";

    /// <summary>A refusal of the server for a field, shown below it until the next change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesError), nameof(Problem), nameof(CanSave))]
    public partial string? DevicesServerError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    public partial string? ServerError { get; private set; }

    public bool HasServerError => !string.IsNullOrEmpty(ServerError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesError), nameof(EventError))]
    public partial bool ShowErrors { get; private set; }

    /// <summary>Shown below the device list once the user changed something or tried to save.</summary>
    public string? DevicesError => ShowErrors ? DevicesServerError ?? DevicesProblem : null;

    public string? EventError => ShowErrors ? EventProblem : null;

    public bool CanSave => Problem is null && !IsSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    /// <summary>Why Save is disabled (its tooltip).</summary>
    public string? Problem => TriggerValidation.NameProblem(Name, _otherNames) ?? DevicesServerError ?? DevicesProblem ?? EventProblem;

    private string? DevicesProblem
    {
        get
        {
            if (TriggerValidation.DevicesProblem(_chosen) is { } count)
            {
                return count;
            }

            if (IsReading)
            {
                return "Wait until the events are read.";
            }

            var chosen = Chosen().ToList();
            var unread = chosen.Count(d => !_offered.ContainsKey(d.Id));
            if (unread > 0)
            {
                return unread == 1 ? "Read the events of the chosen device first." : $"Read the events of the {unread} chosen devices first.";
            }

            var failed = chosen.Where(d => _offered[d.Id] is null).Select(d => d.Address).ToList();
            if (failed.Count > 0)
            {
                return $"Remove the devices whose events could not be read: {Names(failed)}.";
            }

            if (SelectedEvent?.Event is { } e)
            {
                var missing = chosen.Where(d => !_offered[d.Id]!.Contains(e.Topic)).Select(d => d.Address).ToList();
                if (missing.Count > 0)
                {
                    return $"Remove the devices that do not offer this event: {Names(missing)}.";
                }
            }

            return null;
        }
    }

    private string? EventProblem => IsReading || DevicesProblem is not null ? null
        : SelectedEvent is not null ? null
        : EventOptions.Count == 0 ? "The chosen devices have no event in common." : "Choose an event.";

    /// <summary>Reads the event lists of the chosen devices; editing a trigger reads its devices at once.</summary>
    public Task StartAsync() => IsNew ? Task.CompletedTask : ReadAsync();

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    partial void OnDeviceSearchChanged(string value)
    {
        var text = value.Trim();
        VisibleDevices.ReplaceAll(text.Length == 0 ? _devices : _devices.Where(d => d.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    partial void OnSelectedEventChanged(TriggerEventOption? value)
    {
        var previous = Filters.ToDictionary(f => f.Item.Name, f => f.Selected.Value);
        var initial = _initial?.Topic == value?.Event.Topic ? _initial?.Filters.ToDictionary(f => f.Name, f => f.Value) : null;
        Filters.ReplaceAll(value?.Event.Sources.Select(s => new FilterRowViewModel(s, initial?.GetValueOrDefault(s.Name) ?? previous.GetValueOrDefault(s.Name))).ToList() ?? []);
        OnPropertyChanged(nameof(HasFilters));
        UpdateStatuses();
    }

    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task ReadEventsAsync() => ReadAsync();

    private bool CanRead() => !IsReading;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand]
    private async Task SaveAsync()
    {
        ShowErrors = true;
        Validation.ShowAll();
        if (Problem is not null || SelectedEvent?.Event is not { } e)
        {
            return;
        }

        var filters = Filters.Where(f => f.Selected.Value is not null).ToList();
        var trigger = new TriggerDefinition
        {
            Id = _initial?.Id ?? Guid.Empty,
            Name = Name.Trim(),
            Enabled = Enabled,
            Topic = e.Topic,
            EventName = e.Name,
            Stateful = e.Stateful,
            StateItem = e.StateItem,
            Filters = [.. filters.Select(f => new TriggerFilter(f.Item.Name, f.Selected.Value!))],
            FilterText = string.Join(", ", filters.Select(f => $"{f.Label}: {f.Selected.Text}")),
            Values = [.. e.Sources.Concat(e.Data).Select(i => i.Name).Distinct(StringComparer.Ordinal)],
            DeviceIds = [.. Chosen().Select(d => d.Id)],
        };
        IsSaving = true;
        ServerError = null;
        try
        {
            var reply = TriggerJson.Deserialize<SaveReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.Save, TriggerJson.Serialize(new SaveRequest(trigger)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Trigger is { } saved)
            {
                Saved = saved;
                CloseRequested?.Invoke(this, true);
                return;
            }

            if (reply.Errors is { } errors)
            {
                Validation.SetServerError(nameof(Name), errors.GetValueOrDefault(TriggerValidation.NameField));
                DevicesServerError = errors.GetValueOrDefault(TriggerValidation.DevicesField);
                ServerError = errors.GetValueOrDefault(TriggerValidation.EventField);
            }

            ServerError ??= reply.Error;
        }
#pragma warning disable CA1031 // Shown below the cards.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ServerError = DeviceTriggersViewModel.Message(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private IEnumerable<TriggerDeviceChoice> Chosen() => _devices.Where(d => d.IsChecked);

    private void OnDeviceChecked()
    {
        if (_silent)
        {
            return;
        }

        _chosen = _devices.Count(d => d.IsChecked);
        ShowErrors = true;
        DevicesServerError = null;
        UpdateSummary();
        RefreshEvents();
    }

    private void UpdateSummary() =>
        DevicesSummary = _chosen == 0 ? "None chosen" : string.Create(CultureInfo.CurrentCulture, $"{_chosen:N0} {(_chosen == 1 ? "device" : "devices")} chosen");

    /// <summary>Live read of the chosen devices' event lists (devices read before in this dialog are read again).</summary>
    private async Task ReadAsync()
    {
        var ids = Chosen().Where(d => d.Supported).Select(d => d.Id).ToList();
        if (ids.Count == 0)
        {
            ShowErrors = true;
            return;
        }

        IsReading = true;
        ReadProgress = 0;
        ReadText = $"Reading events 0 of {ids.Count:N0}";
        foreach (var id in ids)
        {
            _byId[id].SetStatus("Reading", accent: true);
        }

        try
        {
            var status = TriggerJson.Deserialize<ReadStatusReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.StartRead, TriggerJson.Serialize(new StartReadRequest(ids)), _closing.Token).ConfigureAwait(true));
            while (!status.Finished)
            {
                await Task.Delay(ReadPollInterval, _closing.Token).ConfigureAwait(true);
                status = TriggerJson.Deserialize<ReadStatusReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.ReadStatus, TriggerJson.Serialize(new ReadStatusRequest(status.JobId)), _closing.Token).ConfigureAwait(true));
                ReadProgress = status.Total == 0 ? 0 : 100.0 * status.Done / status.Total;
                ReadText = $"Reading events {status.Done:N0} of {status.Total:N0}";
            }

            Apply(status);
            ReadText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // Shown below the device list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReadText = string.Empty;
            foreach (var id in ids.Where(i => !_offered.ContainsKey(i)))
            {
                _byId[id].SetStatus(string.Empty);
            }

            ServerError = DeviceTriggersViewModel.Message(ex);
        }
        finally
        {
            IsReading = false;
            ShowErrors = true;
            RefreshEvents();
        }
    }

    private void Apply(ReadStatusReply status)
    {
        foreach (var e in status.Events)
        {
            _events[e.Topic] = _events.TryGetValue(e.Topic, out var known) ? Merge(known, e) : e;
        }

        foreach (var device in status.Devices)
        {
            _offered[device.DeviceId] = device.Error is null ? [.. device.Events.Select(i => status.Events[i].Topic)] : null;
            if (_byId.TryGetValue(device.DeviceId, out var choice) && device.Error is { } error)
            {
                choice.SetStatus(error, error: true);
            }
        }
    }

    private static DeviceEvent Merge(DeviceEvent a, DeviceEvent b) => a with
    {
        Sources = [.. a.Sources.Select(item => b.Sources.FirstOrDefault(o => o.Name == item.Name) is { } other
            ? item with { Values = [.. item.Values.Concat(other.Values).DistinctBy(v => v.Value).OrderBy(v => int.TryParse(v.Value, out var n) ? n : int.MaxValue).ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)] }
            : item)],
    };

    /// <summary>The events every chosen, read device offers (O(devices x events)); keeps the choice when it stays possible.</summary>
    private void RefreshEvents()
    {
        var read = Chosen().Where(d => _offered.TryGetValue(d.Id, out var topics) && topics is not null).Select(d => _offered[d.Id]!).ToList();
        HashSet<string>? common = null;
        foreach (var topics in read)
        {
            if (common is null)
            {
                common = [.. topics];
            }
            else
            {
                common.IntersectWith(topics);
            }
        }

        // The chosen event stays even when a device lacks it: that device shows "Does not offer this event" and must go.
        var wanted = SelectedEvent?.Event.Topic ?? _initial?.Topic;
        var shown = new HashSet<string>(common ?? [], StringComparer.Ordinal);
        if (wanted is not null && _events.ContainsKey(wanted) && read.Any(t => t.Contains(wanted)))
        {
            shown.Add(wanted);
        }

        var options = shown.Where(_events.ContainsKey).Select(t => new TriggerEventOption(_events[t])).OrderBy(o => o.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
        EventOptions.ReplaceAll(options);
        var keep = options.FirstOrDefault(o => o.Event.Topic == wanted);
        if (!ReferenceEquals(keep, SelectedEvent))
        {
            SelectedEvent = keep;
        }

        UpdateStatuses();
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DevicesError));
        OnPropertyChanged(nameof(EventError));
    }

    /// <summary>Each read device: its event count, or that it lacks the chosen event.</summary>
    private void UpdateStatuses()
    {
        var topic = SelectedEvent?.Event.Topic;
        foreach (var (id, topics) in _offered)
        {
            if (topics is null || !_byId.TryGetValue(id, out var choice))
            {
                continue;
            }

            if (topic is not null && !topics.Contains(topic))
            {
                choice.SetStatus("Does not offer this event", error: true);
            }
            else
            {
                choice.SetStatus(topics.Count == 1 ? "1 event" : $"{topics.Count} events", ok: true);
            }
        }

        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DevicesError));
    }

    private static string Names(List<string> addresses) =>
        string.Join(", ", addresses.Take(3)) + (addresses.Count > 3 ? $" and {addresses.Count - 3} more" : string.Empty);

    private static string AddressKey(string address)
    {
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}
