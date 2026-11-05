using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
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
/// Which devices a condition or an action is about (no mode drop-down): <see cref="ChooseDevicesCommand"/>
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
    /// The button: "Choose devices" while nothing is chosen, else "Selected devices: All",
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

/// <summary>
/// A value of the event a check can compare, with its kind: "Temperature (number)". A stored check keeps its value also
/// when the event no longer offers it for checks: <see cref="Declared"/> false (the event lacks it) or
/// <see cref="Checkable"/> false (a value only, allowed for an older equals filter left unchanged).
/// </summary>
public sealed record CheckFieldOption(string Name, string DisplayName, EventValueKind Kind, bool Checkable = true, bool Declared = true)
{
    public string Text => DisplayName + Kind switch
    {
        EventValueKind.Number => " (number)",
        EventValueKind.YesNo => " (yes or no)",
        _ => " (text)",
    };

    /// <summary>The checkable fields of an event (<c>no_filter</c> false).</summary>
    public static IReadOnlyList<CheckFieldOption> Of(EventTypeEntry? type) =>
        type is null ? [] : [.. type.Fields.Where(f => !f.NoFilter).Select(f => new CheckFieldOption(f.Name, f.DisplayName, KindOf(f.Kind)))];

    /// <summary>The value of a stored check the event does not offer for checks: a value only, or one it lacks.</summary>
    public static CheckFieldOption Stored(string name, EventTypeEntry? type) =>
        type?.Fields.FirstOrDefault(f => f.Name == name) is { } field
            ? new CheckFieldOption(field.Name, field.DisplayName, KindOf(field.Kind), Checkable: !field.NoFilter)
            : new CheckFieldOption(name, name, EventValueKind.Text, Checkable: false, Declared: false);

    public static EventValueKind KindOf(EventFieldKind kind) => kind switch
    {
        EventFieldKind.EventFieldNumber => EventValueKind.Number,
        EventFieldKind.EventFieldYesNo => EventValueKind.YesNo,
        _ => EventValueKind.Text,
    };

    public override string ToString() => Text;
}

/// <summary>A comparison of a check: "equals", "&gt;", "is yes".</summary>
public sealed record CheckOperatorOption(RuleCheckOperator Op, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// One check of an event condition: Field (a value of the event), Operator (by the field's kind) and Value (hidden for
/// yes / no). Its errors show below its inputs (<see cref="ValidatingViewModel"/>). A stored check is never dropped: one on
/// a value the event no longer offers, or one beyond <see cref="RuleChecks.MaxPerCondition"/>, shows its error below the
/// Field at once and blocks the step until it is removed or fixed. An older equals filter (<see cref="RuleCheck.IsLegacy"/>)
/// left unchanged is saved as it was and checked as leniently as the server does.
/// </summary>
public sealed partial class CheckViewModel : ValidatingViewModel
{
    private readonly Action<CheckViewModel> _remove;
    private readonly Action _changed;
    private readonly RuleCheck? _legacy;
    private readonly (string? Field, RuleCheckOperator? Op, string Value) _initialState;
    private bool _tooMany;

    internal CheckViewModel(IReadOnlyList<CheckFieldOption> fields, RuleCheck? initial, Action<CheckViewModel> remove, Action changed, EventTypeEntry? type = null)
    {
        _remove = remove;
        _changed = changed;
        if (initial is not null && !fields.Any(f => f.Name == initial.Field))
        {
            fields = [.. fields, CheckFieldOption.Stored(initial.Field, type)];
        }

        Fields = fields;
        SelectedField = initial is null ? (fields.Count > 0 ? fields[0] : null) : fields.First(f => f.Name == initial.Field);
        var wrongOperator = false;
        if (initial is not null)
        {
            var op = initial.Op;
            if (SelectedField?.Kind == EventValueKind.YesNo && op is RuleCheckOperator.Eq or RuleCheckOperator.Ne)
            {
                // An older equals filter on a yes / no value ("true"): "is yes" / "is no".
                op = RuleChecks.IsYes(initial.Value) == (op == RuleCheckOperator.Eq) ? RuleCheckOperator.Yes : RuleCheckOperator.No;
            }

            // A stored operator that no longer fits the value's kind is not replaced by another one: "Choose how to compare.".
            SelectedOperator = Operators.FirstOrDefault(o => o.Op == op);
            wrongOperator = SelectedOperator is null;
            Value = initial.Value;
            _legacy = initial.IsLegacy ? initial.Clone() : null;
        }

        _initialState = State;
        Validation
            .Rule(nameof(SelectedField), () => FieldProblem)
            .Rule(nameof(SelectedOperator), () => SelectedOperator is null && !IsUnchangedLegacy ? "Choose how to compare." : null)
            .Rule(nameof(Value), () => SelectedField is null || SelectedOperator is null || IsUnchangedLegacy ? null
                : RuleChecks.ValueProblem(SelectedOperator.Op, SelectedField.Kind, Value));
        Validation.Validate();
        if (SelectedField is { Declared: false } or { Checkable: false } || wrongOperator)
        {
            // A stored check that no longer fits says so when the dialog opens.
            Validation.ShowAll(nameof(SelectedField), nameof(SelectedOperator));
        }
    }

    /// <summary>The checkable values of the condition's event (and the stored value of this check).</summary>
    [ObservableProperty] public partial IReadOnlyList<CheckFieldOption> Fields { get; private set; }

    [ObservableProperty]
    public partial CheckFieldOption? SelectedField { get; set; }

    /// <summary>The comparisons of the chosen field's kind.</summary>
    [ObservableProperty] public partial IReadOnlyList<CheckOperatorOption> Operators { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue))]
    public partial CheckOperatorOption? SelectedOperator { get; set; }

    [ObservableProperty] public partial string Value { get; set; } = string.Empty;

    /// <summary>False for "is yes" / "is no": no value box.</summary>
    public bool NeedsValue => SelectedOperator is null || RuleChecks.NeedsValue(SelectedOperator.Op);

    /// <summary>The first problem of the check (shown or not), for the step's Next tooltip.</summary>
    public string? Problem => Validation.FirstError;

    /// <summary>An older equals filter as it was stored: saved back unchanged.</summary>
    public bool IsUnchangedLegacy => _legacy is not null && State == _initialState;

    private (string? Field, RuleCheckOperator? Op, string Value) State => (SelectedField?.Name, SelectedOperator?.Op, (Value ?? string.Empty).Trim());

    private string? FieldProblem
    {
        get
        {
            if (SelectedField is null)
            {
                return "Choose a value of the event.";
            }

            if (!SelectedField.Declared || (!SelectedField.Checkable && !IsUnchangedLegacy))
            {
                return RuleChecks.NoSuchValueText;
            }

            return _tooMany ? RuleChecks.TooManyText : null;
        }
    }

    /// <summary>Next or Save was tried: every error of the check shows.</summary>
    public void ShowAllErrors() => Validation.ShowAll();

    /// <summary>The condition has more checks than allowed and this one is beyond the limit (<see cref="RuleChecks.TooMany"/>).</summary>
    internal void SetTooMany(bool value)
    {
        if (_tooMany == value)
        {
            return;
        }

        _tooMany = value;
        if (value)
        {
            Validation.ShowAll(nameof(SelectedField));
        }
        else
        {
            Validation.Validate();
        }
    }

    /// <summary>
    /// The event of the condition changed: the check keeps its field when the new event has it (true), else it goes.
    /// </summary>
    internal bool SetFields(IReadOnlyList<CheckFieldOption> fields)
    {
        var field = fields.FirstOrDefault(f => f.Name == SelectedField?.Name);
        if (field is null)
        {
            return false;
        }

        var op = SelectedOperator?.Op;
        Fields = fields;
        SelectedField = field;
        if (op is { } kept && Operators.FirstOrDefault(o => o.Op == kept) is { } same)
        {
            SelectedOperator = same;
        }

        return true;
    }

    /// <summary>The check as edited; an older equals filter left unchanged as it was stored (<see cref="RuleCheck.IsLegacy"/>).</summary>
    public RuleCheck ToCheck() => IsUnchangedLegacy ? _legacy!.Clone() : Edited();

    /// <summary>"Temperature &gt; 40", "Door is yes" (as the row shows it).</summary>
    public string Summary() => SelectedField is null
        ? string.Empty
        : RuleChecks.Describe(Edited(), SelectedField.DisplayName, SelectedField.Kind);

    private RuleCheck Edited() => new()
    {
        Field = SelectedField?.Name ?? string.Empty,
        Op = SelectedOperator?.Op ?? RuleCheckOperator.Eq,
        Value = NeedsValue ? (Value ?? string.Empty).Trim() : string.Empty,
    };

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnSelectedFieldChanged(CheckFieldOption? value)
    {
        var kind = value?.Kind ?? EventValueKind.Text;
        var op = SelectedOperator?.Op;
        Operators = [.. RuleChecks.OperatorsFor(kind).Select(o => new CheckOperatorOption(o, RuleChecks.Label(o, kind)))];
        SelectedOperator = Operators.FirstOrDefault(o => o.Op == op) ?? (Operators.Count > 0 ? Operators[0] : null);
        _changed?.Invoke();
    }

    // Any edit can turn an older filter into a check: the condition counts its checks again (the limit).
    partial void OnSelectedOperatorChanged(CheckOperatorOption? value) => _changed?.Invoke();

    partial void OnValueChanged(string value) => _changed?.Invoke();

    /// <summary>After the rules ran (not before): the dialog's Next and its tooltip follow the check's errors.</summary>
    protected override void OnValidationChanged() => _changed();
}

/// <summary>
/// One setting of an action, drawn from its declaration (<see cref="ActionFieldEntry"/>): text, number, check box, list,
/// list filled by the action, or password. The error shows below the field once it was edited or Save was tried.
/// </summary>
public sealed partial class ActionFieldViewModel : ObservableObject
{
    /// <summary>The hint of a certificate select with nothing to choose.</summary>
    public const string NoCertificatesHint = "Import or create one on the PKI page.";

    /// <summary>
    /// A stored certificate the list does not hold. The list cannot say why (it ended, a server shows it, PKI is stopped or it is
    /// gone), so the text claims none of them; a session's own failure stays <see cref="RecipientSessionTexts.CertificateGone"/>.
    /// </summary>
    public const string CertificateUnusableText = "This certificate cannot be used now. Choose another one.";

    private string? _missingChoice; // a stored certificate the list does not hold (yet): kept until the user chooses
    private bool _hasCertificateList;
    private bool _fillingCertificates;

    public ActionFieldViewModel(ActionFieldEntry entry)
    {
        Entry = entry;
        Field = ToSdk(entry);
        foreach (var choice in entry.Choices)
        {
            Choices.Add(new ChoiceOption<string>(choice.Value, choice.Label));
        }

        if (!entry.Required && IsChoice)
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

    public string? Hint => PasswordStored ? "Stored. Leave empty to keep it."
        : IsCertificate && _hasCertificateList && !Choices.Any(c => c.Value.Length > 0) ? NoCertificatesHint
        : Entry.Hint.Length == 0 ? null : Entry.Hint;

    public bool IsText => Entry.Kind == ActionFieldKind.ActionFieldText;

    public bool IsMultiline => Entry.Kind == ActionFieldKind.ActionFieldMultiline;

    public bool IsNumber => Entry.Kind == ActionFieldKind.ActionFieldNumber;

    public bool IsBool => Entry.Kind == ActionFieldKind.ActionFieldBool;

    public bool IsChoice => IsCertificate || Entry.Kind is ActionFieldKind.ActionFieldChoice or ActionFieldKind.ActionFieldDynamicChoice or ActionFieldKind.ActionFieldRecipient;

    /// <summary>A select of the certificates of PKI (<see cref="SetCertificates"/>).</summary>
    public bool IsCertificate => Entry.Kind is ActionFieldKind.ActionFieldClientCertificate or ActionFieldKind.ActionFieldTrustedCertificate;

    /// <summary>Which list of <see cref="RecipientCertificateList"/> fills it.</summary>
    public bool IsClientCertificate => Entry.Kind == ActionFieldKind.ActionFieldClientCertificate;

    /// <summary>A select of the recipients of its types with New beside it (<see cref="NewRecipientCommand"/>).</summary>
    public bool IsRecipient => Entry.Kind == ActionFieldKind.ActionFieldRecipient;

    /// <summary>What an empty select says.</summary>
    public string? ChoicePlaceholder => IsRecipient ? "Choose a recipient" : IsCertificate ? "Choose a certificate" : null;

    public bool IsPassword => Entry.Kind == ActionFieldKind.ActionFieldPassword;

    /// <summary>A body with a format (JSON, XML, HTML, text) in the code editor.</summary>
    public bool IsCode => Entry.Kind == ActionFieldKind.ActionFieldCode;

    /// <summary>Rows of name and value (headers, query parameters).</summary>
    public bool IsKeyValues => Entry.Kind == ActionFieldKind.ActionFieldKeyValues;

    /// <summary>A file (a configuration file of a compliance check), chosen with <see cref="ChooseFileCommand"/>.</summary>
    public bool IsFile => Entry.Kind == ActionFieldKind.ActionFieldFile;

    /// <summary>A list read from one device the user picks (<see cref="ReadFromDeviceCommand"/>).</summary>
    public bool ReadsFromDevice => IsChoice && Entry.ChoicesFromDevice;

    /// <summary>Opens the file picker for a <see cref="IsFile"/> field; set by the dialog.</summary>
    public Func<ActionFieldViewModel, Task>? FilePicker { get; set; }

    /// <summary>Opens the recipient dialog and selects the new recipient; set by the dialog.</summary>
    public Func<ActionFieldViewModel, Task>? RecipientCreator { get; set; }

    /// <summary>Picks a device and reads the choices from it; set by the dialog.</summary>
    public Func<ActionFieldViewModel, Task>? DeviceReader { get; set; }

    /// <summary>The chosen file's name ("base-config.cfg"); null without one.</summary>
    [ObservableProperty] public partial string? FileName { get; private set; }

    /// <summary>"12 KB" of the chosen file.</summary>
    [ObservableProperty] public partial string? FileDetails { get; private set; }

    /// <summary>The chosen file's content in base64.</summary>
    public string? FileData { get; private set; }

    /// <summary>"Read from 10.0.0.48" after the list was read; null before.</summary>
    [ObservableProperty] public partial string? ReadFromText { get; set; }

    /// <summary>Sets the chosen file (the dialog's picker).</summary>
    public void SetFile(string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        FileName = name;
        FileData = Convert.ToBase64String(content);
        FileDetails = SizeText(content.Length);
        Edited();
    }

    /// <summary>The file setting as stored: {"name", "data"}; null without a file.</summary>
    public JsonObject? FileJson() => FileData is null ? null : new JsonObject { ["name"] = FileName ?? string.Empty, ["data"] = FileData };

    private static string SizeText(long bytes) => bytes < 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} bytes")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:N0} KB");

    [RelayCommand]
    private Task ChooseFileAsync() => FilePicker is { } pick ? pick(this) : Task.CompletedTask;

    [RelayCommand]
    private Task NewRecipientAsync() => RecipientCreator is { } create ? create(this) : Task.CompletedTask;

    [RelayCommand]
    private Task ReadFromDeviceAsync() => DeviceReader is { } read ? read(this) : Task.CompletedTask;

    /// <summary>How the code editor colors the body; follows the field <see cref="ActionField.LanguageKey"/>.</summary>
    [ObservableProperty] public partial CodeLanguage Language { get; set; } = CodeLanguage.Plain;

    /// <summary>The rows of a <see cref="IsKeyValues"/> field.</summary>
    public ObservableCollection<KeyValueEntry> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

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

    /// <summary>A list filled by the action that could not be read ("The list could not be read: ..."); kept as the
    /// field's problem by every re-check until the list loads.</summary>
    public string? LoadError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool PasswordStored { get; set; }

    /// <summary>The value as text for the settings JSON ("" = empty).</summary>
    public string Value => IsBool ? (IsChecked ? "true" : "false") : IsChoice ? SelectedChoice?.Value ?? _missingChoice ?? string.Empty
        : IsKeyValues ? RowsJson().ToJsonString() : IsFile ? FileJson()?.ToJsonString() ?? string.Empty : Text;

    /// <summary>The rows as the settings store them: [{"name", "value"}], rows that are completely empty left out.</summary>
    public JsonArray RowsJson() =>
        [.. Rows.Where(r => r.Name.Trim().Length > 0 || r.Value.Length > 0).Select(r => (JsonNode)new JsonObject { ["name"] = r.Name.Trim(), ["value"] = r.Value })];

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(NewRow(string.Empty, string.Empty));
        OnPropertyChanged(nameof(HasRows));
        Edited();
    }

    [RelayCommand]
    private void RemoveRow(KeyValueEntry? row)
    {
        if (row is not null && Rows.Remove(row))
        {
            OnPropertyChanged(nameof(HasRows));
            Edited();
        }
    }

    private KeyValueEntry NewRow(string name, string value)
    {
        var row = new KeyValueEntry { Name = name, Value = value };
        row.PropertyChanged += (_, _) => Edited();
        return row;
    }

    /// <summary>The code editor's coloring for "json", "xml", "html" (as XML), anything else plain.</summary>
    internal static CodeLanguage LanguageOf(string? language) => language?.ToLowerInvariant() switch
    {
        "json" => CodeLanguage.Json,
        "xml" or "html" => CodeLanguage.Xml,
        _ => CodeLanguage.Plain,
    };

    public void SetValue(string? value)
    {
        if (IsFile)
        {
            try
            {
                if (!string.IsNullOrEmpty(value) && JsonNode.Parse(value) is JsonObject file && file["data"]?.GetValue<string>() is { } data)
                {
                    FileName = file["name"]?.GetValue<string>() ?? string.Empty;
                    FileData = data;
                    FileDetails = SizeText(data.Length * 3L / 4);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // A broken stored value starts without a file.
            }
        }
        else if (IsKeyValues)
        {
            Rows.Clear();
            try
            {
                if (!string.IsNullOrEmpty(value) && JsonNode.Parse(value) is JsonArray rows)
                {
                    foreach (var row in rows.OfType<JsonObject>())
                    {
                        Rows.Add(NewRow(row["name"]?.GetValue<string>() ?? string.Empty, row["value"]?.GetValue<string>() ?? string.Empty));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // A broken stored value starts empty; the user sees no rows.
            }

            OnPropertyChanged(nameof(HasRows));
        }
        else if (IsBool)
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

    /// <summary>
    /// Selects a choice; a value that is not (yet) in the list is kept as "(not found)", except a recipient that is gone:
    /// the select stays empty with its error.
    /// </summary>
    public void SelectChoice(string value)
    {
        if (IsCertificate && value.Length > 0 && !Choices.Any(c => c.Value == value))
        {
            _missingChoice = value;
            SelectedChoice = null;
            return;
        }

        if (value.Length > 0 && !IsRecipient && !Choices.Any(c => c.Value == value))
        {
            Choices.Add(new ChoiceOption<string>(value, value + " (not found)"));
        }

        SelectedChoice = Choices.FirstOrDefault(c => c.Value == value) ?? (Entry.Required ? null : Choices.FirstOrDefault());
    }

    /// <summary>Fills a list from the action (saved commands); the current choice stays selected.</summary>
    public void SetChoices(IEnumerable<ActionChoiceEntry> choices)
    {
        var current = Value;
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

    /// <summary>
    /// Fills a certificate select from PKI, not as an edit; the current choice stays. A stored certificate the list no longer
    /// holds leaves the select empty with <see cref="CertificateUnusableText"/> below it until the user chooses.
    /// </summary>
    public void SetCertificates(IEnumerable<RecipientCertificateChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var current = Value;
        _fillingCertificates = true;
        try
        {
            SetChoices(choices.Select(c => new ActionChoiceEntry { Value = c.Id, Label = c.Text }));
        }
        finally
        {
            _fillingCertificates = false;
        }

        _hasCertificateList = true;
        OnPropertyChanged(nameof(Hint));
        if (_missingChoice is not null && Choices.Any(c => c.Value == _missingChoice))
        {
            _missingChoice = null;
        }
        else if (_missingChoice is not null)
        {
            LoadError = CertificateUnusableText;
            ShowError = true;
        }
    }

    partial void OnTextChanged(string value) => Edited();

    partial void OnIsCheckedChanged(bool value) => Edited();

    partial void OnSelectedChoiceChanged(ChoiceOption<string>? value)
    {
        if (_fillingCertificates)
        {
            return;
        }

        if (value is not null)
        {
            _missingChoice = null;
        }

        Edited();
    }

    private void Edited()
    {
        ShowError = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ActionField ToSdk(ActionFieldEntry entry) => new(entry.Key, entry.Label, (ActionFieldType)(int)entry.Kind)
    {
        Required = entry.Required,
        EmptyText = entry.EmptyText.Length == 0 ? null : entry.EmptyText,
        Format = (ActionFieldFormat)(int)entry.Format,
        Default = entry.HasDefaultValue ? entry.DefaultValue : null,
        Min = entry.Min,
        Max = entry.Max == 0 ? int.MaxValue : entry.Max,
        MaxLength = entry.MaxLength == 0 ? 1000 : entry.MaxLength,
        OffEdge = entry.OffEdge,
        VisibleWhenKey = entry.VisibleWhenKey.Length == 0 ? null : entry.VisibleWhenKey,
        VisibleWhenValue = entry.VisibleWhenValue.Length == 0 ? null : entry.VisibleWhenValue,
        AlsoVisibleWhenKey = entry.AlsoVisibleWhenKey.Length == 0 ? null : entry.AlsoVisibleWhenKey,
        AlsoVisibleWhenValue = entry.AlsoVisibleWhenValue.Length == 0 ? null : entry.AlsoVisibleWhenValue,
        Language = entry.Language.Length == 0 ? null : entry.Language,
        LanguageKey = entry.LanguageKey.Length == 0 ? null : entry.LanguageKey,
        Choices = [.. entry.Choices.Select(c => new ActionChoice(c.Value, c.Label))],
        ChoicesFromDevice = entry.ChoicesFromDevice,
        Accept = [.. entry.Accept],
        RecipientTypes = [.. entry.RecipientTypes],
    };

    /// <summary>The settings JSON of a form (an empty password keeps the stored value: left out).</summary>
    public static JsonObject ToSettings(IEnumerable<ActionFieldViewModel> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var settings = new JsonObject();
        foreach (var field in fields)
        {
            var value = field.Value;
            if (field.IsPassword && value.Length == 0)
            {
                continue; // empty: keep the stored value (StoredSecretFields)
            }

            settings[field.Key] = field.Field.Type switch
            {
                ActionFieldType.KeyValues => field.RowsJson(),
                ActionFieldType.File => field.FileJson(),
                ActionFieldType.Number when value.Length == 0 && !field.Field.Required => null,
                ActionFieldType.Number when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                ActionFieldType.Bool => JsonValue.Create(field.IsChecked),
                _ => JsonValue.Create(value),
            };
        }

        return settings;
    }

    /// <summary>Visibility, errors and code coloring of a form for its current values (the shared host checks).</summary>
    public static void Refresh(IReadOnlyList<ActionFieldViewModel> fields, bool statefulRule)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var settings = ToSettings(fields);
        var stored = fields.Where(f => f.PasswordStored).Select(f => f.Key).ToList();
        var errors = ActionSettings.Check([.. fields.Select(f => f.Field)], settings, statefulRule, stored);
        foreach (var field in fields)
        {
            field.IsVisible = ActionSettings.IsVisible(field.Field, settings, statefulRule);
            field.Problem = field.LoadError ?? errors.GetValueOrDefault(field.Key);
            if (field.IsCode)
            {
                field.Language = LanguageOf(field.Field.Language ?? (field.Field.LanguageKey is { } key ? ActionSettings.Text(settings, key) : null));
            }
        }
    }
}

/// <summary>One row of a name and value list (an HTTP header, a query parameter).</summary>
public sealed partial class KeyValueEntry : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string Value { get; set; } = string.Empty;
}

/// <summary>A placeholder in the panel of the rule dialog: "{objectId}" and what it is.</summary>
public sealed record PlaceholderItem(string Text, string Description);

/// <summary>A group of the placeholder panel: Rule, Device, the trigger's values.</summary>
public sealed record PlaceholderGroup(string Title, IReadOnlyList<PlaceholderItem> Items);
