using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Events;

namespace Oadm.Client.EventRules;

/// <summary>
/// One condition of the rule dialog. The first is the trigger ("When"): any event, devices All / chosen / tags. Further
/// ones ("And") are events that turn on and off (also "the same device") or a schedule.
/// </summary>
public sealed partial class ConditionViewModel : ObservableObject
{
    private readonly RuleEditorViewModel _owner;

    internal ConditionViewModel(RuleEditorViewModel owner, RuleConditionKind kind, bool isTrigger, RuleCondition? initial)
    {
        _owner = owner;
        Kind = kind;
        IsTrigger = isTrigger;
        EventTypes = isTrigger ? owner.AllEventTypes : [.. owner.AllEventTypes.Where(t => t.Stateful)];
        IReadOnlyList<ChoiceOption<DeviceSelectorMode>> modes = isTrigger
            ? [new(DeviceSelectorMode.All, "All devices"), new(DeviceSelectorMode.Devices, "Chosen devices"), new(DeviceSelectorMode.Tags, "Devices with tags")]
            : [new(DeviceSelectorMode.EventDevice, "The same device"), new(DeviceSelectorMode.All, "Any device"), new(DeviceSelectorMode.Devices, "Any of the chosen devices"), new(DeviceSelectorMode.Tags, "Any device with tags")];
        Selector = new DeviceSelectorViewModel(modes, owner.NewDeviceList(), owner.NewTagList(), initial?.Devices);
        Selector.Changed += (_, _) => owner.Revalidate();
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var toggle = new DayToggle(day) { IsChecked = initial?.Schedule?.Days.Contains(day) ?? day is not (DayOfWeek.Saturday or DayOfWeek.Sunday) };
            toggle.PropertyChanged += (_, _) => owner.Revalidate();
            Days.Add(toggle);
        }

        From = initial?.Schedule?.From ?? "08:00";
        To = initial?.Schedule?.To ?? "18:00";
        SelectedEventType = EventTypes.FirstOrDefault(t => t.Id == initial?.EventTypeId);
        foreach (var filter in Filters)
        {
            filter.Value = initial?.Data.GetValueOrDefault(filter.Name) ?? string.Empty;
        }
    }

    public RuleConditionKind Kind { get; }

    public bool IsTrigger { get; }

    public bool IsEvent => Kind == RuleConditionKind.Event;

    public bool IsSchedule => Kind == RuleConditionKind.Schedule;

    public string Title => IsTrigger ? "When" : "And";

    public IReadOnlyList<EventTypeOption> EventTypes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelector), nameof(EventDescription))]
    public partial EventTypeOption? SelectedEventType { get; set; }

    public DeviceSelectorViewModel Selector { get; }

    public bool ShowSelector => SelectedEventType?.DeviceScope == true;

    public string? EventDescription => SelectedEventType?.Entry.Description is { Length: > 0 } text ? text : null;

    public ObservableCollection<FilterFieldViewModel> Filters { get; } = [];

    public ObservableCollection<DayToggle> Days { get; } = [];

    [ObservableProperty] public partial string From { get; set; }

    [ObservableProperty] public partial string To { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial bool ShowErrors { get; set; }

    /// <summary>The condition's problem, shown below it once edited or after a save attempt.</summary>
    public string? Error => ShowErrors ? Problem : null;

    public string? Problem
    {
        get
        {
            if (IsSchedule)
            {
                if (!Days.Any(d => d.IsChecked))
                {
                    return "Choose at least one day.";
                }

                return !IsTime(From) || !IsTime(To) ? "Enter the times as HH:mm, e.g. 08:00." : null;
            }

            if (SelectedEventType is null)
            {
                return "Choose an event.";
            }

            if (ShowSelector && Selector.SelectedMode.Value == DeviceSelectorMode.EventDevice && _owner.Trigger?.SelectedEventType?.DeviceScope != true)
            {
                return "\"The same device\" needs a device event as the first condition.";
            }

            return ShowSelector ? Selector.Problem : null;
        }
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveCondition(this);

    partial void OnSelectedEventTypeChanged(EventTypeOption? value)
    {
        Filters.Clear();
        foreach (var field in value?.Entry.Fields ?? [])
        {
            var filter = new FilterFieldViewModel(field);
            filter.PropertyChanged += (_, _) => _owner.Revalidate();
            Filters.Add(filter);
        }

        ShowErrors = true;
        _owner.Revalidate();
    }

    partial void OnFromChanged(string value) => _owner.Revalidate();

    partial void OnToChanged(string value) => _owner.Revalidate();

    internal void Refresh() => OnPropertyChanged(nameof(Error));

    internal RuleCondition ToCondition() => IsSchedule
        ? new RuleCondition
        {
            Kind = RuleConditionKind.Schedule,
            Schedule = new RuleSchedule { Days = [.. Days.Where(d => d.IsChecked).Select(d => d.Day)], From = From.Trim(), To = To.Trim() },
        }
        : new RuleCondition
        {
            Kind = RuleConditionKind.Event,
            EventTypeId = SelectedEventType?.Id,
            Devices = ShowSelector ? Selector.ToSelector() : new DeviceSelector { Mode = DeviceSelectorMode.All },
            Data = Filters.Where(f => f.Value.Trim().Length > 0).ToDictionary(f => f.Name, f => f.Value.Trim()),
        };

    private static bool IsTime(string text) =>
        TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>One action of the rule dialog: the action, its targets (actions on devices) and its settings.</summary>
public sealed partial class RuleActionViewModel : ObservableObject
{
    private readonly RuleEditorViewModel _owner;
    private readonly RuleAction? _initial;
    private bool _loading;

    internal RuleActionViewModel(RuleEditorViewModel owner, RuleAction? initial)
    {
        _owner = owner;
        _initial = initial;
        Key = initial?.Key ?? Guid.NewGuid();
        Targets = new DeviceSelectorViewModel(
            [new(DeviceSelectorMode.EventDevice, "The device of the event"), new(DeviceSelectorMode.Devices, "Chosen devices"), new(DeviceSelectorMode.Tags, "Devices with tags"), new(DeviceSelectorMode.All, "All devices")],
            owner.NewDeviceList(),
            owner.NewTagList(),
            initial?.Targets);
        Targets.Changed += (_, _) => owner.Revalidate();
        _loading = true;
        SelectedAction = owner.Actions.FirstOrDefault(a => a.Id == initial?.ActionId);
        _loading = false;
        BuildFields(initial);
    }

    public Guid Key { get; }

    public IReadOnlyList<ActionOption> Actions => _owner.Actions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTargets), nameof(Description))]
    public partial ActionOption? SelectedAction { get; set; }

    public DeviceSelectorViewModel Targets { get; }

    public bool ShowTargets => SelectedAction?.Entry.NeedsDevice == true;

    public string? Description => SelectedAction?.Entry.Description is { Length: > 0 } text ? text : null;

    public ObservableCollection<ActionFieldViewModel> Fields { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "Action";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial bool ShowErrors { get; set; }

    public string? Error => ShowErrors ? Problem : null;

    public string? Problem
    {
        get
        {
            if (SelectedAction is null)
            {
                return "Choose an action.";
            }

            if (ShowTargets && Targets.SelectedMode.Value == DeviceSelectorMode.EventDevice && _owner.Trigger?.SelectedEventType?.DeviceScope == false)
            {
                return "The trigger is not about a device; choose the devices.";
            }

            return ShowTargets ? Targets.Problem : null;
        }
    }

    public bool HasFieldErrors => Fields.Any(f => f.IsVisible && f.Problem is not null);

    [RelayCommand]
    private void Remove() => _owner.RemoveAction(this);

    [RelayCommand]
    private void MoveUp() => _owner.MoveAction(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.MoveAction(this, 1);

    partial void OnSelectedActionChanged(ActionOption? value)
    {
        if (_loading)
        {
            return;
        }

        ShowErrors = true;
        BuildFields(null);
        _owner.Revalidate();
    }

    /// <summary>Draws the action's settings; a new action starts with the declared defaults.</summary>
    private void BuildFields(RuleAction? initial)
    {
        Fields.Clear();
        if (SelectedAction is null)
        {
            return;
        }

        foreach (var entry in SelectedAction.Entry.Fields)
        {
            var field = new ActionFieldViewModel(entry);
            var value = initial is not null
                ? ActionSettings.Text(initial.Settings, entry.Key)
                : entry.HasDefaultValue ? entry.DefaultValue : null;
            field.SetValue(value);
            field.PasswordStored = initial?.StoredSecretFields.Contains(entry.Key) == true;
            field.ShowError = false;
            field.Changed += (_, _) => _owner.Revalidate();
            Fields.Add(field);
            if (entry.Kind == ActionFieldKind.ActionFieldDynamicChoice)
            {
                _ = _owner.LoadChoicesAsync(SelectedAction.Id, field);
            }
        }
    }

    /// <summary>Visibility and errors of the settings for the current rule (stateful or not).</summary>
    internal void RefreshFields(bool statefulRule)
    {
        var settings = Settings();
        var stored = Fields.Where(f => f.PasswordStored).Select(f => f.Key).ToList();
        var errors = ActionSettings.Check([.. Fields.Select(f => f.Field)], settings, statefulRule, stored);
        foreach (var field in Fields)
        {
            field.IsVisible = ActionSettings.IsVisible(field.Field, settings, statefulRule);
            field.Problem = errors.GetValueOrDefault(field.Key);
        }

        OnPropertyChanged(nameof(Error));
    }

    internal void ShowAllErrors()
    {
        ShowErrors = true;
        foreach (var field in Fields)
        {
            field.ShowError = true;
        }
    }

    internal JsonObject Settings()
    {
        var settings = new JsonObject();
        foreach (var field in Fields)
        {
            var value = field.Value;
            if (field.IsPassword && value.Length == 0)
            {
                continue; // empty: keep the stored value (StoredSecretFields)
            }

            settings[field.Key] = field.Field.Type switch
            {
                ActionFieldType.Number when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                ActionFieldType.Bool => JsonValue.Create(field.IsChecked),
                _ => JsonValue.Create(value),
            };
        }

        return settings;
    }

    internal RuleAction ToAction() => new()
    {
        Key = Key,
        ActionId = SelectedAction?.Id ?? string.Empty,
        Targets = ShowTargets ? Targets.ToSelector() : new DeviceSelector { Mode = DeviceSelectorMode.EventDevice },
        Settings = Settings(),
        StoredSecretFields = SelectedAction?.Id == _initial?.ActionId ? [.. Fields.Where(f => f.PasswordStored).Select(f => f.Key)] : [],
    };
}

/// <summary>
/// The rule dialog (new rule, or change an existing one; administrators only): name, conditions ("When" plus any number
/// of "And"), one to five actions with their targets and settings, and the limits. Errors stand below their field;
/// Save is disabled with the first problem as tooltip until the rule is complete.
/// </summary>
public sealed partial class RuleEditorViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IReadOnlyList<PickItem> _deviceItems;
    private readonly IReadOnlyList<string> _tagNames;
    private readonly RuleDefinition _original;
    private bool _revalidating;

    public RuleEditorViewModel(IOadmApi api, EventCatalog catalog, RuleDetail? detail, DeviceStore devices)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(devices);
        AllEventTypes = [.. catalog.EventTypes.OrderBy(t => t.Group == "Device status" ? 0 : 1).ThenBy(t => t.Group).ThenBy(t => t.DisplayName).Select(t => new EventTypeOption(t))];
        Actions = [.. catalog.Actions.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(a => new ActionOption(a))];
        _deviceItems = [.. devices.Devices.OrderBy(d => d.Address, StringComparer.Ordinal).Select(d => new PickItem(d.Id, string.IsNullOrEmpty(d.Model) ? d.Address : $"{d.Address} · {d.Model}"))];
        _tagNames = [.. devices.Tags.Tags.Select(t => t.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];
        _original = detail is null ? new RuleDefinition { Enabled = true } : RuleDefinition.FromJson(detail.DefinitionJson);
        IsNew = detail is null;

        Name = _original.Name;
        Enabled = _original.Enabled;
        Cooldown = _original.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        MaxRuns = _original.MaxRunsPerMinute.ToString(CultureInfo.InvariantCulture);
        if (_original.Conditions.Count == 0)
        {
            Conditions.Add(new ConditionViewModel(this, RuleConditionKind.Event, isTrigger: true, null));
        }
        else
        {
            for (var i = 0; i < _original.Conditions.Count; i++)
            {
                var condition = _original.Conditions[i];
                Conditions.Add(new ConditionViewModel(this, condition.Kind, i == 0, condition));
            }
        }

        foreach (var action in _original.Actions)
        {
            RuleActions.Add(new RuleActionViewModel(this, action));
        }

        if (RuleActions.Count == 0)
        {
            RuleActions.Add(new RuleActionViewModel(this, null));
        }

        Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? "Enter a name."
            : Name.Trim().Length > RuleDefinition.MaxNameLength ? string.Create(CultureInfo.InvariantCulture, $"At most {RuleDefinition.MaxNameLength} characters.") : null);
        Validation.Rule(nameof(Cooldown), () => int.TryParse(Cooldown, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 3600 ? null : "Must be 0 to 3600.");
        Validation.Rule(nameof(MaxRuns), () => int.TryParse(MaxRuns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1000 ? null : "Must be 1 to 1000.");
        Validation.Validate();
        Revalidate();
    }

    public event EventHandler<bool>? CloseRequested;

    public bool IsNew { get; }

    public string Title => IsNew ? "New event rule" : "Change event rule";

    public IReadOnlyList<EventTypeOption> AllEventTypes { get; }

    public IReadOnlyList<ActionOption> Actions { get; }

    public ObservableCollection<ConditionViewModel> Conditions { get; } = [];

    /// <summary>The actions of the rule (<c>Actions</c> is the picker list).</summary>
    public ObservableCollection<RuleActionViewModel> RuleActions { get; } = [];

    public ConditionViewModel? Trigger => Conditions.FirstOrDefault();

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial bool Enabled { get; set; }

    [ObservableProperty] public partial string Cooldown { get; set; } = string.Empty;

    [ObservableProperty] public partial string MaxRuns { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    public partial string? ServerError { get; private set; }

    public bool HasServerError => !string.IsNullOrEmpty(ServerError);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>All event conditions turn on and off: the rule is on / off and actions get a "When the rule turns off" part.</summary>
    public bool IsStateful => Trigger?.SelectedEventType?.Stateful != false;

    public string StateHint => IsStateful
        ? "The rule turns on while all conditions hold and off when one ends."
        : "The rule runs its actions each time the event happens while the other conditions hold.";

    public bool CanAddAction => RuleActions.Count < RuleDefinition.MaxActions;

    public bool CanAddCondition => Conditions.Count < RuleDefinition.MaxConditions;

    /// <summary>The first problem of the whole rule (Save tooltip), null when it can be saved.</summary>
    public string? Problem =>
        FormError
        ?? Conditions.Select(c => c.Problem).FirstOrDefault(p => p is not null)
        ?? RuleActions.Select(a => a.Problem ?? (a.HasFieldErrors ? $"{a.SelectedAction?.Entry.DisplayName}: {a.Fields.First(f => f.IsVisible && f.Problem is not null).Label}: {a.Fields.First(f => f.IsVisible && f.Problem is not null).Problem}" : null)).FirstOrDefault(p => p is not null);

    /// <summary>The saved rule after Save (the dialog closed with true).</summary>
    public RuleDetail? Saved { get; private set; }

    [RelayCommand]
    private void AddEventCondition()
    {
        Conditions.Add(new ConditionViewModel(this, RuleConditionKind.Event, isTrigger: false, null));
        Revalidate();
    }

    [RelayCommand]
    private void AddSchedule()
    {
        Conditions.Add(new ConditionViewModel(this, RuleConditionKind.Schedule, isTrigger: false, null));
        Revalidate();
    }

    [RelayCommand]
    private void AddAction()
    {
        if (CanAddAction)
        {
            RuleActions.Add(new RuleActionViewModel(this, null));
            Revalidate();
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        foreach (var condition in Conditions)
        {
            condition.ShowErrors = true;
        }

        foreach (var action in RuleActions)
        {
            action.ShowAllErrors();
        }

        Revalidate();
        if (Problem is not null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Saved = await _api.SaveRuleAsync(ToDefinition().ToJson(), CancellationToken.None).ConfigureAwait(true);
            ServerError = null;
            CloseRequested?.Invoke(this, true);
        }
        catch (RpcException ex)
        {
            ServerError = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public RuleDefinition ToDefinition() => new()
    {
        Id = _original.Id,
        Name = Name.Trim(),
        Enabled = Enabled,
        Conditions = [.. Conditions.Select(c => c.ToCondition())],
        Actions = [.. RuleActions.Select(a => a.ToAction())],
        CooldownSeconds = int.TryParse(Cooldown, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) ? cooldown : RuleDefinition.DefaultCooldownSeconds,
        MaxRunsPerMinute = int.TryParse(MaxRuns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs) ? runs : RuleDefinition.DefaultMaxRunsPerMinute,
    };

    internal PickListViewModel NewDeviceList() =>
        new(_deviceItems.Select(i => new PickItem(i.Key, i.Text)), "device", "devices");

    internal PickListViewModel NewTagList() =>
        new(_tagNames.Select(n => new PickItem(n, n)), "tag", "tags");

    internal void RemoveCondition(ConditionViewModel condition)
    {
        if (!condition.IsTrigger)
        {
            Conditions.Remove(condition);
            Revalidate();
        }
    }

    internal void RemoveAction(RuleActionViewModel action)
    {
        if (RuleActions.Count > 1)
        {
            RuleActions.Remove(action);
            Revalidate();
        }
    }

    internal void MoveAction(RuleActionViewModel action, int step)
    {
        var index = RuleActions.IndexOf(action);
        var target = index + step;
        if (index >= 0 && target >= 0 && target < RuleActions.Count)
        {
            RuleActions.Move(index, target);
            Revalidate();
        }
    }

    internal async Task LoadChoicesAsync(string actionId, ActionFieldViewModel field)
    {
        try
        {
            var choices = await _api.ListActionChoicesAsync(actionId, field.Key, CancellationToken.None).ConfigureAwait(true);
            var shown = field.ShowError;
            field.SetChoices(choices);
            field.ShowError = shown;
            Revalidate();
        }
        catch (RpcException ex)
        {
            field.Problem = "The list could not be read: " + ex.Status.Detail;
            field.ShowError = true;
        }
    }

    /// <summary>Re-checks every part after any change (cheap: a rule has few parts).</summary>
    internal void Revalidate()
    {
        if (_revalidating)
        {
            return;
        }

        _revalidating = true;
        try
        {
            var stateful = IsStateful;
            for (var i = 0; i < RuleActions.Count; i++)
            {
                RuleActions[i].Title = string.Create(CultureInfo.InvariantCulture, $"Action {i + 1}");
                RuleActions[i].RefreshFields(stateful);
            }

            foreach (var condition in Conditions)
            {
                condition.Refresh();
            }

            OnPropertyChanged(nameof(IsStateful));
            OnPropertyChanged(nameof(StateHint));
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(CanAddAction));
            OnPropertyChanged(nameof(CanAddCondition));
        }
        finally
        {
            _revalidating = false;
        }
    }

    protected override void OnValidationChanged() => OnPropertyChanged(nameof(Problem));
}
