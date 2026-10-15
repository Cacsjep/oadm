using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>An action the rules can get: offered by every target device.</summary>
public sealed record ActionOption(string Token, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// "Edit action rules": a form over the target rules where every part has a Change check box. Unchecked parts stay as each
/// device has them (a message with a camera's own number stays). The action select offers the actions every target device
/// has; changing it lists the new action's settings, those the rules lack must be set. The event, the schedule and the
/// recipient (each device's own one of a name) can change too, from what the devices offered when they were read.
/// </summary>
public sealed partial class EditRulesViewModel : ValidatingViewModel
{
    public static readonly IReadOnlyList<string> TriggerKindNames = ["Once when the event starts", "While the event is active"];

    private readonly IReadOnlyList<RuleMember> _targets;
    private readonly IReadOnlyDictionary<Guid, TemplateSet> _sets;
    private readonly string? _sharedTemplate;

    public event EventHandler<bool>? CloseRequested;

    /// <param name="targets">The rules to change, or the one rule to copy.</param>
    /// <param name="sets">The action templates per device.</param>
    /// <param name="copyTo">Copy mode: the devices that get the copy (their actions are offered); null = change the rules.</param>
    /// <param name="events">The events the devices offered when they were read (none: the event cannot change).</param>
    /// <param name="rows">The devices' rows (their recipients and schedules).</param>
    /// <param name="newSchedule">Opens the schedule form; null in tests that do not need it.</param>
    public EditRulesViewModel(IReadOnlyList<RuleMember> targets, IReadOnlyDictionary<Guid, TemplateSet> sets, IReadOnlyList<Guid>? copyTo = null,
        IReadOnlyList<OfferedEvent>? events = null, IReadOnlyDictionary<Guid, DeviceRulesRow>? rows = null, Func<Task<SchedulePatch?>>? newSchedule = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _sets = sets ?? throw new ArgumentNullException(nameof(sets));
        _templateDevices = copyTo ?? [.. targets.Select(t => t.Device.DeviceId).Distinct()];
        var known = rows ?? targets.Select(t => t.Device).DistinctBy(d => d.DeviceId).ToDictionary(d => d.DeviceId) as IReadOnlyDictionary<Guid, DeviceRulesRow>;
        // Rules that run while active need an event that is on or off.
        Event = new EventChoiceViewModel(events ?? [], _templateDevices.Count, statefulOnly: targets.All(t => t.Rule.StartEvent is null));
        Event.PropertyChanged += (_, _) => Revalidate();
        HasEvents = Event.Choices.Count > 0;
        Schedule = new SchedulePickerViewModel(_templateDevices, known, newSchedule);
        Schedule.PropertyChanged += (_, _) => Revalidate();
        Recipient = new RecipientPickerViewModel(_templateDevices, known, enterSettings: false);
        Recipient.PropertyChanged += (_, _) => Revalidate();
        IsCopy = copyTo is not null;
        var templates = targets.Select(t => t.Rule.Template).Distinct(StringComparer.Ordinal).ToList();
        _sharedTemplate = templates.Count == 1 ? templates[0] : null;
        Intro = IsCopy
            ? $"Copies the rule \"{targets[0].Rule.Name}\" to {Devices(copyTo!.Count)}. Checked parts are set on the copies; the rest is copied as it is."
            : $"Changes {ActionRulesViewModel.Describe(targets)}. Parts without a check stay as each device has them.";

        var names = targets.Select(t => t.Rule.Name.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        Name = names.Count == 1 ? names[0] : string.Empty;
        Enabled = targets.All(t => t.Rule.Enabled);
        TriggerKindIndex = targets.All(t => t.Rule.StartEvent is null) ? 1 : 0;
        var timeouts = targets.Select(t => RuleText.TimeoutSeconds(t.Rule.Timeout) ?? 0).Distinct().ToList();
        TimeoutSeconds = timeouts.Count == 1 ? timeouts[0] : 0;

        Actions = [.. CommonTemplates().Where(t => !ActionNames.IsRefused(t)).Select(t => new ActionOption(t, ActionNames.Of(t))).OrderBy(a => a.Name, StringComparer.CurrentCulture)];
        SelectedAction = Actions.FirstOrDefault(a => a.Token == _sharedTemplate) ?? (Actions.Count > 0 ? Actions[0] : null);
        CurrentActionText = _sharedTemplate is null
            ? string.Create(CultureInfo.CurrentCulture, $"{templates.Count:N0} different actions")
            : ActionNames.Of(_sharedTemplate);

        Validation.Rule(nameof(Name), () => !ChangeName || Name.Trim().Length is > 0 and <= RulePatchApply.MaxNameLength
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."));
        Validation.Rule(nameof(TimeoutSeconds), () => !ChangeTimeout || TimeoutSeconds is >= 0 and <= RulePatchApply.MaxTimeoutSeconds && TimeoutSeconds == decimal.Truncate(TimeoutSeconds.Value)
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter 0 to {RulePatchApply.MaxTimeoutSeconds:N0} seconds."));
        Validation.Rule(nameof(SelectedAction), () => !ChangeAction || SelectedAction is not null ? null : "No action is offered by every selected device.");
        BuildParameters();
        Validation.Reset();
    }

    private readonly IReadOnlyList<Guid> _templateDevices;

    /// <summary>The new event of the rules (replaces the start event, else the first condition that is not a schedule).</summary>
    public EventChoiceViewModel Event { get; }

    public bool HasEvents { get; }

    [ObservableProperty] public partial bool ChangeEvent { get; set; }

    public SchedulePickerViewModel Schedule { get; }

    [ObservableProperty] public partial bool ChangeSchedule { get; set; }

    /// <summary>The recipient of the action by name: each device uses its own one of the action's kind.</summary>
    public RecipientPickerViewModel Recipient { get; }

    [ObservableProperty] public partial bool ChangeRecipient { get; set; }

    private bool _tried;

    public string? EventError => _tried && ChangeEvent && Event.Event is null ? "Choose the event." : null;

    public string? RecipientError => _tried && ChangeRecipient && Recipient.Selected?.Name is null ? "Choose the recipient." : null;

    partial void OnChangeEventChanged(bool value) => Revalidate();

    partial void OnChangeScheduleChanged(bool value) => Revalidate();

    partial void OnChangeRecipientChanged(bool value) => Revalidate();

    public bool IsCopy { get; }

    public string Title => IsCopy ? "Copy action rule" : "Edit action rules";

    public string ApplyText => IsCopy ? "Copy" : "Apply";

    private static string Devices(int n) => n == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{n:N0} devices");

    public string Intro { get; }

    public IReadOnlyList<ActionOption> Actions { get; }

    public string CurrentActionText { get; }

    public ObservableCollection<ParameterField> Parameters { get; } = [];

    public bool HasParameters => Parameters.Any(p => p.IsShown);

    /// <summary>The action has a recipient and some device has one of its kind.</summary>
    public bool HasRecipient => Recipient.HasChoices;

    /// <summary>Why no settings are listed, or null.</summary>
    public string? ParametersNote =>
        HasParameters ? null
        : EffectiveTemplate is null ? "The rules have different actions. Change the action to set its settings."
        : "This action has no settings.";

    [ObservableProperty] public partial bool ChangeName { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial bool ChangeEnabled { get; set; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    [ObservableProperty] public partial bool ChangeTrigger { get; set; }

    [ObservableProperty] public partial int TriggerKindIndex { get; set; }

    [ObservableProperty] public partial bool ChangeTimeout { get; set; }

    [ObservableProperty] public partial decimal? TimeoutSeconds { get; set; }

    [ObservableProperty] public partial bool ChangeAction { get; set; }

    [ObservableProperty] public partial ActionOption? SelectedAction { get; set; }

    /// <summary>The action the rules have after the change (null: different actions, unchanged).</summary>
    public string? EffectiveTemplate => ChangeAction ? SelectedAction?.Token : _sharedTemplate;

    partial void OnChangeNameChanged(bool value) => Revalidate();

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnChangeEnabledChanged(bool value) => Revalidate();

    partial void OnChangeTriggerChanged(bool value) => Revalidate();

    partial void OnChangeTimeoutChanged(bool value) => Revalidate();

    partial void OnTimeoutSecondsChanged(decimal? value) => Revalidate();

    partial void OnChangeActionChanged(bool value) => BuildParameters();

    partial void OnSelectedActionChanged(ActionOption? value)
    {
        if (ChangeAction)
        {
            BuildParameters();
        }
    }

    /// <summary>What is missing before the form can be applied (tooltip of Apply), or null.</summary>
    public string? ApplyBlockedReason =>
        Validation.FirstError
        ?? (ChangeEvent && Event.Event is null ? "Choose the event." : null)
        ?? (ChangeRecipient && Recipient.Selected?.Name is null ? "Choose the recipient." : null)
        ?? Parameters.Select(p => p.Problem is { } problem ? p.Label + ": " + problem : null).FirstOrDefault(p => p is not null)
        ?? (IsEmpty && !IsCopy ? "Check at least one part to change." : null);

    /// <summary>Nothing chosen by the user (settings that must be set do not count by themselves).</summary>
    private bool IsEmpty => !ChangeName && !ChangeEnabled && !ChangeTrigger && !ChangeTimeout && !ChangeAction && !ChangeEvent && !ChangeSchedule
        && !ChangeRecipient && !Parameters.Any(p => p.Change && !p.IsRequired);

    public bool CanApply => ApplyBlockedReason is null;

    /// <summary>The patch of the checked parts.</summary>
    public RulePatch Patch()
    {
        var parameters = Parameters.Where(p => p.Change).ToDictionary(p => p.Name, p => p.Value ?? string.Empty, StringComparer.Ordinal);
        return new RulePatch
        {
            Name = ChangeName ? Name.Trim() : null,
            Enabled = ChangeEnabled ? Enabled : null,
            TriggerKind = ChangeTrigger ? (TriggerKindIndex == 1 ? Rules.TriggerKinds.Condition : Rules.TriggerKinds.Start) : null,
            TimeoutSeconds = ChangeTimeout ? (int)(TimeoutSeconds ?? 0) : null,
            Template = ChangeAction && SelectedAction?.Token != _sharedTemplate ? SelectedAction?.Token : null,
            Parameters = parameters.Count > 0 ? parameters : null,
            NewEvent = ChangeEvent ? Event.Filter() : null,
            ScheduleId = ChangeSchedule ? Schedule.Selected?.Id ?? string.Empty : null,
            NewSchedule = ChangeSchedule ? Schedule.Selected?.Create : null,
            RecipientName = ChangeRecipient ? Recipient.Selected?.Name : null,
        };
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        _tried = true;
        OnPropertyChanged(nameof(EventError));
        OnPropertyChanged(nameof(RecipientError));
        Validation.ShowAll();
        foreach (var field in Parameters)
        {
            field.ShowErrors = true;
        }

        if (CanApply)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        NotifyApply();
    }

    /// <summary>A part was checked or edited: the rules that depend on the check boxes run again.</summary>
    private void Revalidate()
    {
        Validation.Validate();
        NotifyApply();
    }

    private void NotifyApply()
    {
        OnPropertyChanged(nameof(EventError));
        OnPropertyChanged(nameof(RecipientError));
        OnPropertyChanged(nameof(ApplyBlockedReason));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The actions every target device offers (a device whose actions are unknown offers none).</summary>
    private HashSet<string> CommonTemplates()
    {
        HashSet<string>? common = null;
        foreach (var device in _templateDevices)
        {
            var tokens = _sets.TryGetValue(device, out var set) ? set.Actions.Select(a => a.Token) : [];
            if (common is null)
            {
                common = new HashSet<string>(tokens, StringComparer.Ordinal);
            }
            else
            {
                common.IntersectWith(tokens);
            }
        }

        return common ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private void BuildParameters()
    {
        foreach (var field in Parameters)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Parameters.Clear();
        Recipient.Build(EffectiveTemplate is { } kindOf
            ? _templateDevices.Select(d => _sets.TryGetValue(d, out var set) ? set.Action(kindOf)?.RecipientTemplate : null).FirstOrDefault(r => r is not null)
            : null);
        OnPropertyChanged(nameof(HasRecipient));
        if (EffectiveTemplate is { } token)
        {
            var names = _templateDevices
                .SelectMany(d => _sets.TryGetValue(d, out var set) ? set.ParameterNames(token) : [])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count == 0 && token == _sharedTemplate)
            {
                names = [.. _targets[0].Rule.Parameters.Select(p => p.Name)];
            }

            var recipient = _templateDevices.Select(d => _sets.TryGetValue(d, out var set) ? set.Action(token)?.RecipientTemplate : null).FirstOrDefault(r => r is not null);
            foreach (var name in ActionCatalog.Order(token, recipient, names))
            {
                var values = _targets.Select(t => t.Rule.Get(name)).ToList();
                var missing = values.Any(v => v is null);
                var catalog = ActionCatalog.Field(token, recipient, name);
                if (missing)
                {
                    catalog = ActionCatalog.ForNewValue(catalog);
                }

                var secret = catalog.Kind == FieldKinds.Password;
                if (catalog.Kind == FieldKinds.Hidden)
                {
                    if (missing)
                    {
                        // Never shown by the device: a rule that lacks it gets the default without a question.
                        var hidden = new ParameterField(name, catalog, true, string.Empty, catalog.Default ?? string.Empty);
                        hidden.PropertyChanged += OnFieldChanged;
                        Parameters.Add(hidden);
                    }

                    continue;
                }

                var distinct = values.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var hint = missing ? (catalog.Default is { Length: > 0 } ? "New setting: check its value." : "New setting: enter its value.")
                    : secret ? (_targets.All(t => t.Rule.StoredSecrets.Contains(name)) ? "Stays as it is unless you enter a new one." : "Empty on some rules.")
                    : distinct.Count == 1 ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $"{distinct.Count:N0} different values; each rule keeps its own unless checked.");
                var start = !secret && distinct.Count == 1 && !missing ? distinct[0] : missing ? catalog.Default ?? string.Empty : string.Empty;
                var field = new ParameterField(name, catalog, missing, hint, start);
                field.PropertyChanged += OnFieldChanged;
                Parameters.Add(field);
            }
        }

        OnPropertyChanged(nameof(HasParameters));
        OnPropertyChanged(nameof(ParametersNote));
        Revalidate();
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Revalidate();
}
