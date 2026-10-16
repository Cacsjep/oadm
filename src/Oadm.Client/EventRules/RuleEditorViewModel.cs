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
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
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
        Selector = isTrigger
            ? new DeviceSelectorViewModel(owner.DeviceList, DevicePickerOptions.Full(null, owner.TagNames), DeviceChoice.AllDevices, initial?.Devices, owner.PickDevicesAsync)
            : new DeviceSelectorViewModel(owner.DeviceList, DevicePickerOptions.Full("The same device", owner.TagNames), DeviceChoice.EventDevice, initial?.Devices, owner.PickDevicesAsync);
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

            if (ShowSelector && Selector.IsEventDevice && _owner.Trigger?.SelectedEventType?.DeviceScope != true)
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
        foreach (var field in value?.Entry.Fields.Where(f => !f.NoFilter) ?? [])
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

    /// <summary>One line for the overview: "Input ports: Digital input · Chosen devices: 10.0.0.48 · port = 1".</summary>
    internal string Summary()
    {
        if (IsSchedule)
        {
            var days = string.Join(", ", Days.Where(d => d.IsChecked).Select(d => d.Text));
            return $"Schedule: {days}, {From.Trim()} to {To.Trim()}";
        }

        var parts = new List<string> { SelectedEventType?.Text ?? "No event" };
        if (ShowSelector)
        {
            parts.Add(Selector.Summary());
        }

        parts.AddRange(Filters.Where(f => f.Value.Trim().Length > 0).Select(f => $"{f.Label} = {f.Value.Trim()}"));
        return string.Join(" · ", parts);
    }

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
            owner.DeviceList,
            DevicePickerOptions.Full("The device of the event", owner.TagNames),
            DeviceChoice.EventDevice,
            initial?.Targets,
            owner.PickDevicesAsync);
        Targets.Changed += (_, _) => owner.Revalidate();
        _loading = true;
        SelectedAction = owner.Actions.FirstOrDefault(a => a.Id == initial?.ActionId);
        _loading = false;
        BuildFields(initial);
    }

    public Guid Key { get; }

    public IReadOnlyList<ActionOption> Actions => _owner.Actions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTargets), nameof(Description), nameof(CanTest))]
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

            if (ShowTargets && Targets.IsEventDevice && _owner.Trigger?.SelectedEventType?.DeviceScope == false)
            {
                return "The trigger is not about a device; choose the devices.";
            }

            return ShowTargets ? Targets.Problem : null;
        }
    }

    public bool HasFieldErrors => Fields.Any(f => f.IsVisible && f.Problem is not null);

    /// <summary>"Send test": only actions without devices (email, HTTP); device actions would change devices.</summary>
    public bool CanTest => SelectedAction?.Entry is { NeedsDevice: false, Available: true };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool TestOk { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool IsTesting { get; set; }

    public bool TestFailed => !TestOk && !IsTesting;

    public bool HasTestResult => TestResult is not null;

    /// <summary>Sends the action once now with sample values for the event (the server fills the placeholders).</summary>
    [RelayCommand(CanExecute = nameof(NotTesting))]
    private async Task TestAsync()
    {
        RefreshFields(_owner.IsStateful);
        if (HasFieldErrors)
        {
            ShowAllErrors();
            TestResult = "Fix the settings first.";
            TestOk = false;
            return;
        }

        IsTesting = true;
        TestResult = "Sending";
        try
        {
            var reply = await _owner.TestActionAsync(this).ConfigureAwait(true);
            TestOk = reply.Ok;
            TestResult = reply.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestOk = false;
            TestResult = ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool NotTesting() => !IsTesting;

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
        TestResult = null;
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
            field.Problem = field.LoadError ?? errors.GetValueOrDefault(field.Key);
            if (field.IsCode)
            {
                field.Language = ActionFieldViewModel.LanguageOf(field.Field.Language ?? (field.Field.LanguageKey is { } key ? ActionSettings.Text(settings, key) : null));
            }
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
                ActionFieldType.KeyValues => field.RowsJson(),
                ActionFieldType.Number when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                ActionFieldType.Bool => JsonValue.Create(field.IsChecked),
                _ => JsonValue.Create(value),
            };
        }

        return settings;
    }

    /// <summary>One line for the overview: "Set output · Chosen devices: 10.0.0.30 · Output: 1, Mode: Pulse".</summary>
    internal string Summary()
    {
        var parts = new List<string> { SelectedAction?.Text ?? "No action" };
        if (ShowTargets)
        {
            parts.Add(Targets.Summary());
        }

        var settings = Fields
            .Where(f => f.IsVisible && !f.IsPassword && !f.IsCode && !f.IsKeyValues)
            .Select(f => (f.Label, Text: f.IsBool ? (f.IsChecked ? "Yes" : "No") : f.IsChoice ? f.SelectedChoice?.Text ?? string.Empty : f.Text.Trim()))
            .Where(f => f.Text.Length > 0)
            .Select(f => $"{f.Label}: {f.Text}")
            .ToList();
        if (settings.Count > 0)
        {
            parts.Add(string.Join(", ", settings));
        }

        return string.Join(" · ", parts);
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
    private readonly RuleDefinition _original;
    private bool _revalidating;

    public RuleEditorViewModel(IOadmApi api, EventCatalog catalog, RuleDetail? detail, DeviceStore devices)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(devices);
        AllEventTypes = [.. catalog.EventTypes.OrderBy(t => t.Group == "Device status" ? 0 : 1).ThenBy(t => t.Group).ThenBy(t => t.DisplayName).Select(t => new EventTypeOption(t))];
        Actions = [.. catalog.Actions.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(a => new ActionOption(a))];
        DeviceList = [.. devices.Devices];
        TagNames = [.. devices.Tags.Tags.Where(t => t.IsDefined).Select(t => t.Name)];
        Steps = [.. StepNames.Select((name, i) => new WizardStepItem(i, name, () => Step, CanOpen, GoTo))];
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
    public string? Problem => ProblemOf(RuleStep) ?? ProblemOf(WhenStep) ?? ProblemOf(ThenStep);

    // Wizard: 1 Rule (name, turned on, limits), 2 When (conditions), 3 Then (actions),
    // 4 Overview (everything in short lines, Save). Next is disabled with the step's first problem as tooltip.
    public const int RuleStep = 0;
    public const int WhenStep = 1;
    public const int ThenStep = 2;
    public const int OverviewStep = 3;

    private static readonly string[] StepNames = ["Rule", "When", "Then", "Overview"];

    public IReadOnlyList<WizardStepItem> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRuleStep), nameof(IsWhenStep), nameof(IsThenStep), nameof(IsOverviewStep), nameof(CanGoBack), nameof(StepProblem))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial int Step { get; private set; }

    public bool IsRuleStep => Step == RuleStep;

    public bool IsWhenStep => Step == WhenStep;

    public bool IsThenStep => Step == ThenStep;

    public bool IsOverviewStep => Step == OverviewStep;

    public bool CanGoBack => Step > RuleStep;

    /// <summary>The first problem of the shown step (Next tooltip).</summary>
    public string? StepProblem => Step == OverviewStep ? null : ProblemOf(Step);

    /// <summary>The overview: one line per part of the rule.</summary>
    public ObservableCollection<OverviewLine> Overview { get; } = [];

    internal string? ProblemOf(int step) => step switch
    {
        RuleStep => FormError,
        WhenStep => Conditions.Select(c => c.Problem).FirstOrDefault(p => p is not null),
        ThenStep => RuleActions.Select(a => a.Problem ?? (a.HasFieldErrors ? $"{a.SelectedAction?.Entry.DisplayName}: {a.Fields.First(f => f.IsVisible && f.Problem is not null).Label}: {a.Fields.First(f => f.IsVisible && f.Problem is not null).Problem}" : null)).FirstOrDefault(p => p is not null),
        _ => null,
    };

    /// <summary>A step can be opened when every step before it is complete (going back always works).</summary>
    internal bool CanOpen(int step) => step <= Step || Enumerable.Range(0, step).All(s => ProblemOf(s) is null);

    private bool CanGoNext() => Step < OverviewStep && ProblemOf(Step) is null;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => GoTo(Step + 1);

    [RelayCommand]
    private void Back()
    {
        if (Step > RuleStep)
        {
            GoTo(Step - 1);
        }
    }

    internal void GoTo(int step)
    {
        if (step < RuleStep || step > OverviewStep || !CanOpen(step))
        {
            return;
        }

        ShowErrorsOf(Step);
        if (step == OverviewStep)
        {
            BuildOverview();
        }

        Step = step;
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }

    private void ShowErrorsOf(int step)
    {
        switch (step)
        {
            case RuleStep:
                Validation.ShowAll();
                break;
            case WhenStep:
                foreach (var condition in Conditions)
                {
                    condition.ShowErrors = true;
                }

                break;
            case ThenStep:
                foreach (var action in RuleActions)
                {
                    action.ShowAllErrors();
                }

                break;
        }

        Revalidate();
    }

    private void BuildOverview()
    {
        var lines = new List<OverviewLine>
        {
            new("Name", Name.Trim()),
            new("State", Enabled ? "Turned on" : "Turned off"),
        };
        for (var i = 0; i < Conditions.Count; i++)
        {
            lines.Add(new(i == 0 ? "When" : "And", Conditions[i].Summary()));
        }

        foreach (var action in RuleActions)
        {
            lines.Add(new("Then", action.Summary()));
        }

        lines.Add(new("Limits", string.Create(CultureInfo.CurrentCulture, $"Dead time {Cooldown.Trim()} s, max {MaxRuns.Trim()} per minute")));
        Overview.Clear();
        foreach (var line in lines)
        {
            Overview.Add(line);
        }
    }

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

    /// <summary>Every managed device, for the shared device picker of conditions and actions.</summary>
    internal IReadOnlyList<IDeviceInfo> DeviceList { get; }

    /// <summary>The defined tags: the picker offers them also before any device carries them.</summary>
    internal IReadOnlyList<string> TagNames { get; }

    /// <summary>Shows the shared device picker (set by the window); true = OK.</summary>
    public Func<DevicePickerViewModel, Task<bool>>? ShowPicker { get; set; }

    internal Task<bool> PickDevicesAsync(DevicePickerViewModel picker) =>
        ShowPicker is { } show ? show(picker) : Task.FromResult(false);


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

    /// <summary>
    /// The placeholder panel of the Then step (a list with copy buttons): the values of the
    /// trigger, then the rule and the device (<see cref="ActionPlaceholders.Fixed"/>). Rebuilt when the trigger changes.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<PlaceholderGroup> PlaceholderGroups { get; private set; } = [];

    /// <summary>The panel shows only while an action of the rule writes texts with placeholders (email, webhook).</summary>
    [ObservableProperty] public partial bool ShowPlaceholders { get; private set; }

    private string? _placeholderTrigger = "\0";

    private void RefreshPlaceholders()
    {
        var type = Trigger?.SelectedEventType?.Entry;
        if (_placeholderTrigger == type?.Id)
        {
            return;
        }

        _placeholderTrigger = type?.Id;
        var groups = new List<PlaceholderGroup>();
        var fixedNames = ActionPlaceholders.Fixed.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (type is not null && type.Fields.Any(f => !fixedNames.Contains(f.Name)))
        {
            groups.Add(new PlaceholderGroup(
                "Event: " + type.DisplayName,
                [.. type.Fields.Where(f => !fixedNames.Contains(f.Name)).Select(f => new PlaceholderItem("{" + f.Name + "}", f.DisplayName))]));
        }

        foreach (var group in ActionPlaceholders.Fixed.GroupBy(p => p.Group))
        {
            groups.Add(new PlaceholderGroup(group.Key, [.. group.Select(p => new PlaceholderItem(p.Text, p.Description))]));
        }

        PlaceholderGroups = groups;
    }

    /// <summary>
    /// "Send test" of an action without devices: the server sends it once with sample values for the trigger's values; the
    /// device of the event is the first chosen device of the trigger, else the first device.
    /// </summary>
    internal Task<TestActionReply> TestActionAsync(RuleActionViewModel action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var device = Trigger?.Selector.ChosenIds.FirstOrDefault() is { } chosen && chosen != Guid.Empty ? chosen
            : DeviceList.Count > 0 ? DeviceList[0].Id : Guid.Empty;
        return _api.TestEventActionAsync(
            new TestActionRequest
            {
                ActionId = action.SelectedAction?.Id ?? string.Empty,
                SettingsJson = action.Settings().ToJsonString(),
                RuleName = Name.Trim(),
                RuleId = IsNew ? string.Empty : _original.Id.ToString(),
                ActionKey = action.Key.ToString(),
                EventTypeId = Trigger?.SelectedEventType?.Id ?? string.Empty,
                DeviceId = device == Guid.Empty ? string.Empty : device.ToString(),
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Fills a list the action provides (fire and forget: every error ends as the field's problem, nothing escapes).
    /// </summary>
    internal async Task LoadChoicesAsync(string actionId, ActionFieldViewModel field)
    {
        try
        {
            var choices = await _api.ListActionChoicesAsync(actionId, field.Key, CancellationToken.None).ConfigureAwait(true);
            var shown = field.ShowError;
            field.LoadError = null;
            field.SetChoices(choices);
            field.ShowError = shown;
        }
        catch (Exception ex)
        {
            field.LoadError = "The list could not be read: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
            field.ShowError = true;
        }

        Revalidate();
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
            RefreshPlaceholders();
            ShowPlaceholders = RuleActions.Any(a => a.SelectedAction?.Entry.UsesPlaceholders == true);
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
            OnPropertyChanged(nameof(StepProblem));
            NextCommand.NotifyCanExecuteChanged();
            foreach (var item in Steps)
            {
                item.Refresh();
            }

            OnPropertyChanged(nameof(CanAddAction));
            OnPropertyChanged(nameof(CanAddCondition));
        }
        finally
        {
            _revalidating = false;
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(StepProblem));
        NextCommand.NotifyCanExecuteChanged();
        foreach (var item in Steps ?? [])
        {
            item.Refresh();
        }
    }
}
