using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Collections;
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

/// <summary>One entry of a pick list (a device or a tag) with its check box.</summary>
public sealed partial class PickItem(string key, string text) : ObservableObject
{
    public string Key { get; } = key;

    public string Text { get; } = text;

    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>
/// A searchable list with check boxes (devices or tags), virtualized in the view; search, count and select-all are O(n)
/// over the items, one reset per filter.
/// </summary>
public sealed partial class PickListViewModel : ObservableObject
{
    private readonly List<PickItem> _all;
    private readonly string _noun;
    private readonly string _nounPlural;

    public PickListViewModel(IEnumerable<PickItem> items, string noun, string nounPlural)
    {
        _all = [.. items];
        _noun = noun;
        _nounPlural = nounPlural;
        foreach (var item in _all)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PickItem.IsChecked))
                {
                    UpdateCount();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        Visible.ReplaceAll(_all);
        UpdateCount();
    }

    public event EventHandler? Changed;

    public RangeObservableCollection<PickItem> Visible { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial string SummaryText { get; private set; } = string.Empty;

    public bool IsEmpty => _all.Count == 0;

    public IReadOnlyList<string> CheckedKeys => [.. _all.Where(i => i.IsChecked).Select(i => i.Key)];

    /// <summary>Checks the given keys; the checked items move to the top so a saved choice is visible at once.</summary>
    public void Check(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            item.IsChecked = set.Contains(item.Key);
        }

        var ordered = _all.OrderBy(i => i.IsChecked ? 0 : 1).ToList(); // stable
        _all.Clear();
        _all.AddRange(ordered);
        OnSearchTextChanged(SearchText);
    }

    partial void OnSearchTextChanged(string value)
    {
        var text = value.Trim();
        Visible.ReplaceAll(text.Length == 0 ? _all : _all.Where(i => i.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private void UpdateCount()
    {
        var count = _all.Count(i => i.IsChecked);
        SummaryText = count == 0 ? "None chosen" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? _noun : _nounPlural)} chosen");
    }
}

/// <summary>Which devices a condition or an action means: a mode plus a device or tag list.</summary>
public sealed partial class DeviceSelectorViewModel : ObservableObject
{
    public DeviceSelectorViewModel(IReadOnlyList<ChoiceOption<DeviceSelectorMode>> modes, PickListViewModel devices, PickListViewModel tags, DeviceSelector? initial)
    {
        Modes = modes;
        Devices = devices;
        Tags = tags;
        var mode = initial?.Mode ?? modes[0].Value;
        SelectedMode = modes.FirstOrDefault(m => m.Value == mode) ?? modes[0];
        if (initial is not null)
        {
            Devices.Check(initial.DeviceIds.Select(id => id.ToString()));
            Tags.Check(initial.Tags);
        }

        Devices.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Tags.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ChoiceOption<DeviceSelectorMode>> Modes { get; }

    public PickListViewModel Devices { get; }

    public PickListViewModel Tags { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevices), nameof(IsTags))]
    public partial ChoiceOption<DeviceSelectorMode> SelectedMode { get; set; }

    public bool IsDevices => SelectedMode.Value == DeviceSelectorMode.Devices;

    public bool IsTags => SelectedMode.Value == DeviceSelectorMode.Tags;

    /// <summary>"Choose at least one device." while the list mode has nothing chosen.</summary>
    public string? Problem => SelectedMode.Value switch
    {
        DeviceSelectorMode.Devices when Devices.CheckedKeys.Count == 0 => "Choose at least one device.",
        DeviceSelectorMode.Tags when Tags.CheckedKeys.Count == 0 => "Choose at least one tag.",
        _ => null,
    };

    partial void OnSelectedModeChanged(ChoiceOption<DeviceSelectorMode> value) => Changed?.Invoke(this, EventArgs.Empty);

    public DeviceSelector ToSelector() => new()
    {
        Mode = SelectedMode.Value,
        DeviceIds = SelectedMode.Value == DeviceSelectorMode.Devices ? [.. Devices.CheckedKeys.Select(Guid.Parse)] : [],
        Tags = SelectedMode.Value == DeviceSelectorMode.Tags ? [.. Tags.CheckedKeys] : [],
    };
}

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
