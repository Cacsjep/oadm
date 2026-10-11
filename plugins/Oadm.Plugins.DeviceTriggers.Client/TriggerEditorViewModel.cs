using System.Globalization;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
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

    /// <summary>In the trigger: chosen, through a tag or through All devices.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    /// <summary>"Chosen", "Tag Building A", "All devices".</summary>
    [ObservableProperty] public partial string IncludedBy { get; internal set; } = string.Empty;

    /// <summary>Came in through a tag or All devices: without the event it is left out, it does not block Save.</summary>
    [ObservableProperty] public partial bool IsImplicit { get; internal set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool IsOk { get; private set; }

    [ObservableProperty] public partial bool IsAccent { get; private set; }

    [ObservableProperty] public partial bool IsError { get; private set; }

    [ObservableProperty] public partial bool IsWarning { get; private set; }

    /// <summary>Its event list is being read now.</summary>
    [ObservableProperty] public partial bool IsReading { get; internal set; }

    internal void SetStatus(string text, bool ok = false, bool accent = false, bool error = false, bool warning = false)
    {
        StatusText = text;
        IsOk = ok;
        IsAccent = accent;
        IsError = error;
        IsWarning = warning;
    }

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>An event the chosen devices offer, for the event select.</summary>
public sealed record TriggerEventOption(DeviceEvent Event)
{
    public string Text => Event.Title;

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
/// The trigger dialog: name, turned on, the devices (only those that can send events this
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
    private readonly Dictionary<Guid, IReadOnlyList<string>> _tagsOf;
    private readonly Dictionary<string, DeviceEvent> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>?> _offered = []; // null = read failed
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<Guid> _explicit = [];
    private IReadOnlyList<string> _tags = [];
    private bool _allDevices;
    private int _chosen;
    private int _reads;
    private bool _silent;

    public TriggerEditorViewModel(ICorePluginClientContext ctx, TriggerDefinition? trigger, IReadOnlyList<string> otherNames)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _initial = trigger;
        _otherNames = otherNames ?? [];
        Name = trigger?.Name ?? string.Empty;
        Enabled = trigger?.Enabled ?? true;
        _explicit.UnionWith(trigger?.DeviceIds ?? []);
        _tags = trigger?.Tags ?? [];
        _allDevices = trigger?.AllDevices ?? false;
        _devices = [.. ctx.Devices
            .Select(d => new TriggerDeviceChoice(d, OnDeviceChecked))
            .OrderBy(d => d.Supported ? 0 : 1)
            .ThenBy(d => AddressKey(d.Address), StringComparer.Ordinal)];
        _byId = _devices.ToDictionary(d => d.Id);
        _tagsOf = ctx.Devices.ToDictionary(d => d.Id, d => d.Tags);
        ApplyChoice();
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

    /// <summary>The devices of the trigger (chosen with Add devices), in address order.</summary>
    public RangeObservableCollection<TriggerDeviceChoice> VisibleDevices { get; } = [];

    public bool HasNoDevices => VisibleDevices.Count == 0;

    /// <summary>Opens the shared device picker (the window sets it; tests replace it). True = OK.</summary>
    public Func<DevicePickerViewModel, Task<bool>>? ShowPicker { get; set; }

    [ObservableProperty] public partial string DevicesSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanSave), nameof(DevicesError), nameof(EventError))]
    public partial bool IsReading { get; private set; }

    /// <summary>The progress row shows while several devices are read at once (editing a trigger).</summary>
    [ObservableProperty] public partial bool ShowReadProgress { get; private set; }

    [ObservableProperty] public partial double ReadProgress { get; private set; }

    [ObservableProperty] public partial string ReadText { get; private set; } = string.Empty;

    public RangeObservableCollection<TriggerEventOption> EventOptions { get; } = [];

    /// <summary>The event select: <see cref="EventOptions"/> grouped like the event list of the device itself.</summary>
    public RangeObservableCollection<GroupedChoice> EventChoices { get; } = [];

    [ObservableProperty] public partial GroupedChoice? SelectedEventChoice { get; set; }

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
            if (TriggerValidation.DevicesProblem(_explicit.Count, _tags.Count, _allDevices) is { } count)
            {
                return count;
            }

            // Only chosen devices hold Save back: a device of a tag or of all devices without the event is left out anyway.
            var chosen = Chosen().Where(d => !d.IsImplicit).ToList();
            if (chosen.Any(d => d.IsReading))
            {
                return WaitText;
            }

            // Devices that could not be read (offline, login refused) stay: OADM connects once they answer.
            // Only a device that was read and lacks the event cannot stay.
            if (SelectedEvent?.Event is { } e)
            {
                var missing = chosen.Where(d => _offered.GetValueOrDefault(d.Id) is { } topics && !topics.Contains(e.Topic)).Select(d => d.Address).ToList();
                if (missing.Count > 0)
                {
                    return $"Remove the devices that do not offer this event: {Names(missing)}.";
                }
            }

            return null;
        }
    }

    private const string WaitText = "Wait until the events are read.";

    /// <summary>Save waits for the devices of a tag or of all devices only until the chosen event is known.</summary>
    private string? EventProblem => DevicesProblem is not null || SelectedEvent is not null ? null
        : IsReading ? WaitText
        : EventOptions.Count == 0 ? "No events yet: none of the devices could be read." : "Choose an event.";

    /// <summary>Editing a trigger reads the event lists of its devices at once (live, never cached).</summary>
    public Task StartAsync() => IsNew ? Task.CompletedTask : ReadDevicesAsync([.. Chosen().Where(d => d.Supported)]);

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    /// <summary>
    /// "Choose devices": the shared device picker with the devices of the trigger chosen (unchoose to remove); devices that
    /// cannot send events this way are greyed with the reason. OK applies the choice in one step and reads the event lists
    /// of the added devices at once (the server reads 16 at a time).
    /// </summary>
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (ShowPicker is not { } show)
        {
            return;
        }

        var initial = _allDevices ? DeviceChoice.AllDevices : DeviceChoice.Devices(_explicit, _tags);
        var picker = new DevicePickerViewModel(
            _ctx.Devices,
            initial,
            new DevicePickerOptions { AllowAll = true, AllowTags = true },
            d => _byId.TryGetValue(d.Id, out var choice) && !choice.Supported ? DeviceTriggersPluginInfo.NotSupportedReason : null);
        if (!await show(picker).ConfigureAwait(true))
        {
            return;
        }

        var result = picker.Choice;
        _allDevices = result.Kind == DeviceChoiceKind.All;
        _explicit.Clear();
        _explicit.UnionWith(result.DeviceIds);
        _tags = result.Tags;
        ApplyChoice();
        ShowErrors = true;
        DevicesServerError = null;
        RefreshEvents();
        await ReadDevicesAsync([.. Chosen().Where(d => d.Supported && !d.IsReading && !_offered.ContainsKey(d.Id))]).ConfigureAwait(true);
    }

    /// <summary>
    /// The chosen devices first, then those of a tag or of all devices (possibly thousands): Save does not wait for the
    /// second read once the event is known.
    /// </summary>
    private async Task ReadDevicesAsync(IReadOnlyList<TriggerDeviceChoice> devices)
    {
        await ReadAsync([.. devices.Where(d => !d.IsImplicit).Select(d => d.Id)]).ConfigureAwait(true);
        await ReadAsync([.. devices.Where(d => d.IsImplicit).Select(d => d.Id)]).ConfigureAwait(true);
    }

    partial void OnSelectedEventChoiceChanged(GroupedChoice? value)
    {
        if (value?.Value is TriggerEventOption option && !ReferenceEquals(option, SelectedEvent))
        {
            SelectedEvent = option;
        }
    }

    partial void OnSelectedEventChanged(TriggerEventOption? value)
    {
        var choice = value is null ? null : EventChoices.FirstOrDefault(c => ReferenceEquals(c.Value, value));
        if (!ReferenceEquals(choice, SelectedEventChoice))
        {
            SelectedEventChoice = choice;
        }

        var previous = Filters.ToDictionary(f => f.Item.Name, f => f.Selected.Value);
        var initial = _initial?.Topic == value?.Event.Topic ? _initial?.Filters.ToDictionary(f => f.Name, f => f.Value) : null;
        Filters.ReplaceAll(value?.Event.Sources.Select(s => new FilterRowViewModel(s, initial?.GetValueOrDefault(s.Name) ?? previous.GetValueOrDefault(s.Name))).ToList() ?? []);
        OnPropertyChanged(nameof(HasFilters));
        UpdateStatuses();
    }

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
            DeviceIds = [.. _explicit],
            Tags = [.. _tags],
            AllDevices = _allDevices,
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
        VisibleDevices.ReplaceAll([.. Chosen()]);
        OnPropertyChanged(nameof(HasNoDevices));
        ShowErrors = true;
        DevicesServerError = null;
        UpdateSummary();
        RefreshEvents();
    }

    /// <summary>
    /// The devices in the trigger from the choice: the chosen ones, plus every device that can send events with one of the
    /// tags or, with All devices, every such device (one pass over all devices).
    /// </summary>
    private void ApplyChoice()
    {
        _silent = true;
        foreach (var device in _devices)
        {
            var chosen = _explicit.Contains(device.Id);
            var tag = !chosen && !_allDevices && _tags.Count > 0 && device.Supported
                ? _tags.FirstOrDefault(t => _tagsOf.GetValueOrDefault(device.Id)?.Contains(t, StringComparer.OrdinalIgnoreCase) == true)
                : null;
            var all = !chosen && _allDevices && device.Supported;
            device.IsChecked = chosen || tag is not null || all;
            device.IsImplicit = !chosen && device.IsChecked;
            device.IncludedBy = chosen ? "Chosen" : tag is not null ? "Tag " + tag : all ? "All devices" : string.Empty;
        }

        _silent = false;
        _chosen = _devices.Count(d => d.IsChecked);
        VisibleDevices.ReplaceAll([.. Chosen()]);
        OnPropertyChanged(nameof(HasNoDevices));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var parts = new List<string>();
        if (_allDevices)
        {
            parts.Add("All devices");
        }

        if (_tags.Count > 0)
        {
            parts.Add((_tags.Count == 1 ? "Tag " : "Tags ") + string.Join(", ", _tags));
        }

        if (_explicit.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_explicit.Count:N0} chosen {(_explicit.Count == 1 ? "device" : "devices")}"));
        }

        DevicesSummary = parts.Count == 0 ? "No devices yet"
            : string.Join(" and ", parts) + string.Create(CultureInfo.CurrentCulture, $": {_chosen:N0} {(_chosen == 1 ? "device" : "devices")} now");
        DevicesButtonText = parts.Count == 0 ? "Choose devices"
            : "Selected devices: " + (_allDevices ? "All" : DeviceSelectorTexts.Brief(_explicit.Count, _tags, _explicit.Count == 1 ? _byId.GetValueOrDefault(_explicit.First())?.Address : null));
    }

    /// <summary>"Choose devices", or what is chosen: "Selected devices: All", "Selected devices: Tag Gate and 2 devices".</summary>
    [ObservableProperty] public partial string DevicesButtonText { get; private set; } = "Choose devices";

    /// <summary>Live read of event lists (a device read before in this dialog is read again). Reads may overlap.</summary>
    private async Task ReadAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        _reads++;
        IsReading = true;
        ShowReadProgress = ids.Count > 1;
        ReadProgress = 0;
        ReadText = $"Reading events 0 of {ids.Count:N0}";
        foreach (var id in ids)
        {
            _byId[id].IsReading = true;
            _byId[id].SetStatus("Reading", accent: true);
        }

        try
        {
            var status = TriggerJson.Deserialize<ReadStatusReply>(await _ctx.InvokeAsync(DeviceTriggersMethods.StartRead, TriggerJson.Serialize(new StartReadRequest(ids)), _closing.Token).ConfigureAwait(true));
            if (string.IsNullOrEmpty(status.JobId))
            {
                throw new InvalidOperationException("The server did not read the events.");
            }

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
            var message = DeviceTriggersViewModel.Message(ex);
            foreach (var id in ids.Where(i => !_offered.ContainsKey(i)))
            {
                _offered[id] = null;
                _byId[id].SetStatus(NotRead(message), warning: true);
            }
        }
        finally
        {
            foreach (var id in ids)
            {
                _byId[id].IsReading = false;
            }

            _reads--;
            IsReading = _reads > 0;
            ShowReadProgress = ShowReadProgress && IsReading;
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
                choice.SetStatus(NotRead(error), warning: true);
            }
        }
    }

    private static DeviceEvent Merge(DeviceEvent a, DeviceEvent b) => a with
    {
        Sources = [.. a.Sources.Select(item => b.Sources.FirstOrDefault(o => o.Name == item.Name) is { } other
            ? item with { Values = [.. item.Values.Concat(other.Values).DistinctBy(v => v.Value).OrderBy(v => int.TryParse(v.Value, out var n) ? n : int.MaxValue).ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)] }
            : item)],
    };

    /// <summary>
    /// Every event any device of the trigger offers (O(devices x events)); keeps the choice. The event of a trigger being
    /// edited stays even when none of its devices can be read now (offline): it is shown from the stored trigger.
    /// </summary>
    private void RefreshEvents()
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in Chosen())
        {
            if (_offered.GetValueOrDefault(device.Id) is { } topics)
            {
                shown.UnionWith(topics);
            }
        }

        var wanted = SelectedEvent?.Event.Topic ?? _initial?.Topic;
        if (wanted is not null && _initial is { } initial && initial.Topic == wanted && !_events.ContainsKey(wanted) && _chosen > 0)
        {
            _events[wanted] = new DeviceEvent(
                initial.Topic,
                initial.EventName,
                initial.Stateful,
                initial.StateItem,
                [.. initial.Filters.Select(f => new EventItem(f.Name, null, [new EventValue(f.Value, null)]))],
                []);
        }

        if (wanted is not null && _events.ContainsKey(wanted) && (shown.Contains(wanted) || _initial?.Topic == wanted))
        {
            shown.Add(wanted);
        }

        var options = shown.Where(_events.ContainsKey).Select(t => new TriggerEventOption(_events[t])).OrderBy(o => o.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
        EventOptions.ReplaceAll(options);
        EventChoices.ReplaceAll(GroupedChoice.Build(options, o => o.Event.Group, o => o.Text));
        SelectedEventChoice = SelectedEvent is null ? null : EventChoices.FirstOrDefault(c => c.Value is TriggerEventOption o && o.Event.Topic == SelectedEvent.Event.Topic);
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
                if (choice.IsImplicit)
                {
                    choice.SetStatus("Does not offer this event (left out)"); // a device of a tag or all devices
                }
                else
                {
                    choice.SetStatus("Does not offer this event", error: true);
                }
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

    /// <summary>"Not read: Unreachable - ... It stays in the trigger." (a device that cannot be read now may stay).</summary>
    private static string NotRead(string reason) => $"Not read: {reason.TrimEnd('.')}. It stays in the trigger.";

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
