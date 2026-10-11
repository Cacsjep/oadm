using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

namespace Oadm.Client.EventRules;

/// <summary>An event type in the condition picker: "Device status: Device unreachable".</summary>
public sealed record EventTypeOption(EventTypeEntry Entry)
{
    public string Id => Entry.Id;

    public bool Stateful => Entry.Stateful;

    public bool DeviceScope => Entry.DeviceScope;

    public string Text => Entry.Available ? $"{Entry.Group}: {Entry.DisplayName}" : $"{Entry.Group}: {Entry.DisplayName} (turned off)";

    public override string ToString() => Text;
}

/// <summary>An action in the action picker.</summary>
public sealed record ActionOption(ActionEntry Entry)
{
    public string Id => Entry.Id;

    public string Text => Entry.Available ? Entry.DisplayName : $"{Entry.DisplayName} ({Entry.SourceName} is turned off)";

    public override string ToString() => Text;
}

/// <summary>A choice of a mode, day or list.</summary>
public sealed record ChoiceOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Which devices a condition or an action is about (user decision 2026-10-11: no mode drop-down): <see cref="ChooseDevicesCommand"/>
/// opens the shared <see cref="DevicePickerWindow"/> with the device of the event (when offered), All devices, or chosen
/// devices and tags together. The choice shows as one line ("Tag Building A and 3 devices: 10.0.0.48, ...").
/// </summary>
public sealed partial class DeviceSelectorViewModel : ObservableObject
{
    private const int NamedDevices = 3;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly Func<DevicePickerViewModel, Task<bool>>? _showPicker;

    public DeviceSelectorViewModel(
        IReadOnlyList<IDeviceInfo> devices,
        DevicePickerOptions options,
        DeviceChoice fallback,
        DeviceSelector? initial,
        Func<DevicePickerViewModel, Task<bool>>? showPicker = null)
    {
        _devices = devices;
        Options = options;
        _showPicker = showPicker;
        Choice = initial is null ? fallback : FromSelector(initial);
    }

    public event EventHandler? Changed;

    public DevicePickerOptions Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenText), nameof(ButtonText), nameof(IsEventDevice), nameof(Problem))]
    public partial DeviceChoice Choice { get; private set; }

    public bool IsEventDevice => Choice.Kind == DeviceChoiceKind.EventDevice;

    /// <summary>The chosen devices one by one (tests, summaries).</summary>
    public IReadOnlyCollection<Guid> ChosenIds => Choice.DeviceIds;

    /// <summary>"All devices", "The device of the event", "No devices chosen", "Tag Building A and 3 devices: 10.0.0.48, ...".</summary>
    public string ChosenText => Choice.Kind switch
    {
        DeviceChoiceKind.All => "All devices",
        DeviceChoiceKind.EventDevice => Options.EventDeviceText ?? "The device of the event",
        _ => DescribeChosen(),
    };

    /// <summary>
    /// The button (user decision 2026-10-11): "Choose devices" while nothing is chosen, else "Selected devices: All",
    /// "Selected devices: Tag Building A and 3 devices". The full choice with addresses is its tooltip.
    /// </summary>
    public string ButtonText => Choice.IsEmpty ? "Choose devices" : "Selected devices: " + ShortText();

    private string ShortText() => Choice.Kind switch
    {
        DeviceChoiceKind.All => "All",
        DeviceChoiceKind.EventDevice => Options.EventDeviceText ?? "The device of the event",
        _ => DeviceSelectorTexts.Brief(Choice.DeviceIds.Count, Choice.Tags, Choice.DeviceIds.Count == 1 ? _devices.FirstOrDefault(d => d.Id == Choice.DeviceIds[0])?.Address : null),
    };

    /// <summary>"Choose at least one device or tag." while nothing is chosen.</summary>
    public string? Problem => Choice.IsEmpty ? "Choose at least one device or tag." : null;

    /// <summary>Replaces the choice (the picker's OK, tests).</summary>
    public void SetChoice(DeviceChoice choice)
    {
        Choice = choice;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the chosen devices (tests).</summary>
    public void SetChosen(IEnumerable<Guid> ids) => SetChoice(DeviceChoice.Devices(ids));

    /// <summary>"Choose devices": the shared device picker with the current choice; OK takes the new one.</summary>
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (_showPicker is null)
        {
            return;
        }

        var picker = new DevicePickerViewModel(_devices, Choice, Options);
        if (await _showPicker(picker).ConfigureAwait(true))
        {
            SetChoice(picker.Choice);
        }
    }

    /// <summary>Short text for the overview.</summary>
    public string Summary() => ChosenText;

    public DeviceSelector ToSelector() => Choice.Kind switch
    {
        DeviceChoiceKind.All => new DeviceSelector { Mode = DeviceSelectorMode.All },
        DeviceChoiceKind.EventDevice => new DeviceSelector { Mode = DeviceSelectorMode.EventDevice },
        _ => new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [.. Choice.DeviceIds], Tags = [.. Choice.Tags] },
    };

    private static DeviceChoice FromSelector(DeviceSelector selector) => selector.Mode switch
    {
        DeviceSelectorMode.All => DeviceChoice.AllDevices,
        DeviceSelectorMode.EventDevice => DeviceChoice.EventDevice,
        _ => DeviceChoice.Devices(selector.DeviceIds, selector.Tags),
    };

    private string DescribeChosen()
    {
        var ids = Choice.DeviceIds.ToHashSet();
        var tags = Choice.Tags.Count == 0 ? null : (Choice.Tags.Count == 1 ? "Tag " : "Tags ") + string.Join(", ", Choice.Tags);
        string? devices = null;
        if (ids.Count > 0)
        {
            var known = _devices.Where(d => ids.Contains(d.Id)).Take(NamedDevices).ToList();
            var more = ids.Count - known.Count;
            devices = ids.Count == 1 && known.Count == 1
                ? Describe(known[0])
                : string.Create(CultureInfo.CurrentCulture, $"{ids.Count:N0} {(ids.Count == 1 ? "device" : "devices")}")
                    + (known.Count > 0 ? ": " + string.Join(", ", known.Select(d => d.Address)) : string.Empty)
                    + (more > 0 && known.Count > 0 ? string.Create(CultureInfo.CurrentCulture, $" +{more:N0} more") : string.Empty);
        }

        return tags is not null && devices is not null ? $"{tags} and {(ids.Count == 1 ? devices : char.ToLowerInvariant(devices[0]) + devices[1..])}"
            : tags ?? devices ?? "No devices chosen";
    }

    private static string Describe(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} · {device.Model.Replace("AXIS ", string.Empty, StringComparison.Ordinal)}";
}

/// <summary>One line of the rule overview: "When", "Input ports: Digital input · All devices".</summary>
public sealed record OverviewLine(string Label, string Text);

/// <summary>A day toggle of a schedule.</summary>
public sealed partial class DayToggle(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Text { get; } = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>An optional filter on an event value ("port" = "1").</summary>
public sealed partial class FilterFieldViewModel(EventFieldInfo field) : ObservableObject
{
    public string Name { get; } = field.Name;

    public string Label { get; } = field.DisplayName;

    [ObservableProperty] public partial string Value { get; set; } = string.Empty;
}

/// <summary>
/// One setting of an action, drawn from its declaration (<see cref="ActionFieldEntry"/>): text, number, check box, list,
/// list filled by the action, or password. The error shows below the field once it was edited or Save was tried.
/// </summary>
public sealed partial class ActionFieldViewModel : ObservableObject
{
    public ActionFieldViewModel(ActionFieldEntry entry)
    {
        Entry = entry;
        Field = ToSdk(entry);
        foreach (var choice in entry.Choices)
        {
            Choices.Add(new ChoiceOption<string>(choice.Value, choice.Label));
        }

        if (!entry.Required && entry.Kind is ActionFieldKind.ActionFieldChoice or ActionFieldKind.ActionFieldDynamicChoice)
        {
            Choices.Insert(0, new ChoiceOption<string>(string.Empty, "None"));
        }
    }

    public event EventHandler? Changed;

    public ActionFieldEntry Entry { get; }

    /// <summary>The SDK declaration for the shared checks (<see cref="ActionSettings.Check"/>).</summary>
    public ActionField Field { get; }

    public string Key => Entry.Key;

    public string Label => Entry.Label;

    public string? Hint => PasswordStored ? "Stored. Leave empty to keep it." : Entry.Hint.Length == 0 ? null : Entry.Hint;

    public bool IsText => Entry.Kind == ActionFieldKind.ActionFieldText;

    public bool IsMultiline => Entry.Kind == ActionFieldKind.ActionFieldMultiline;

    public bool IsNumber => Entry.Kind == ActionFieldKind.ActionFieldNumber;

    public bool IsBool => Entry.Kind == ActionFieldKind.ActionFieldBool;

    public bool IsChoice => Entry.Kind is ActionFieldKind.ActionFieldChoice or ActionFieldKind.ActionFieldDynamicChoice;

    public bool IsPassword => Entry.Kind == ActionFieldKind.ActionFieldPassword;

    public ObservableCollection<ChoiceOption<string>> Choices { get; } = [];

    [ObservableProperty] public partial string Text { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    [ObservableProperty] public partial ChoiceOption<string>? SelectedChoice { get; set; }

    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial bool ShowError { get; set; }

    /// <summary>The error below the field (only once edited or after a save attempt).</summary>
    public string? Error => ShowError ? Problem : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool PasswordStored { get; set; }

    /// <summary>The value as text for the settings JSON ("" = empty).</summary>
    public string Value => IsBool ? (IsChecked ? "true" : "false") : IsChoice ? SelectedChoice?.Value ?? string.Empty : Text;

    public void SetValue(string? value)
    {
        if (IsBool)
        {
            IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        else if (IsChoice)
        {
            SelectChoice(value ?? string.Empty);
        }
        else
        {
            Text = value ?? string.Empty;
        }
    }

    /// <summary>Selects a choice; a value that is not (yet) in the list is kept as "(not found)".</summary>
    public void SelectChoice(string value)
    {
        if (value.Length > 0 && !Choices.Any(c => c.Value == value))
        {
            Choices.Add(new ChoiceOption<string>(value, value + " (not found)"));
        }

        SelectedChoice = Choices.FirstOrDefault(c => c.Value == value) ?? (Entry.Required ? null : Choices.FirstOrDefault());
    }

    /// <summary>Fills a list from the action (saved commands); the current choice stays selected.</summary>
    public void SetChoices(IEnumerable<ActionChoiceEntry> choices)
    {
        var current = SelectedChoice?.Value ?? string.Empty;
        Choices.Clear();
        if (!Entry.Required)
        {
            Choices.Add(new ChoiceOption<string>(string.Empty, "None"));
        }

        foreach (var choice in choices)
        {
            Choices.Add(new ChoiceOption<string>(choice.Value, choice.Label));
        }

        SelectChoice(current);
    }

    partial void OnTextChanged(string value) => Edited();

    partial void OnIsCheckedChanged(bool value) => Edited();

    partial void OnSelectedChoiceChanged(ChoiceOption<string>? value) => Edited();

    private void Edited()
    {
        ShowError = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ActionField ToSdk(ActionFieldEntry entry) => new(entry.Key, entry.Label, (ActionFieldType)(int)entry.Kind)
    {
        Required = entry.Required,
        Default = entry.HasDefaultValue ? entry.DefaultValue : null,
        Min = entry.Min,
        Max = entry.Max == 0 ? int.MaxValue : entry.Max,
        MaxLength = entry.MaxLength == 0 ? 1000 : entry.MaxLength,
        OffEdge = entry.OffEdge,
        VisibleWhenKey = entry.VisibleWhenKey.Length == 0 ? null : entry.VisibleWhenKey,
        VisibleWhenValue = entry.VisibleWhenValue.Length == 0 ? null : entry.VisibleWhenValue,
        Choices = [.. entry.Choices.Select(c => new ActionChoice(c.Value, c.Label))],
    };
}
