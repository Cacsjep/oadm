using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>
/// One event of the new rule: the event select (grouped like the device's event list) and per source item Any or one value.
/// Used for the rule's event and for each further condition (those only list events that are on or off).
/// </summary>
public sealed partial class EventChoiceViewModel : ObservableObject
{
    private readonly int _devices;

    public EventChoiceViewModel(IReadOnlyList<OfferedEvent> events, int devices, bool statefulOnly)
    {
        ArgumentNullException.ThrowIfNull(events);
        _devices = devices;
        Choices = GroupedChoice.Build(events.Where(e => !statefulOnly || e.Event.Stateful), e => e.Event.Group, e => e.Event.Title);
    }

    public IReadOnlyList<GroupedChoice> Choices { get; }

    public RangeObservableCollection<FilterRowViewModel> Filters { get; } = [];

    public bool HasFilters => Filters.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Event), nameof(Hint), nameof(Error))]
    public partial GroupedChoice? SelectedChoice { get; set; }

    public OfferedEvent? Event => SelectedChoice?.Value as OfferedEvent;

    /// <summary>"Offered by 480 of 500 devices" when not every device has it.</summary>
    public string Hint => Event is { } e && e.Devices < _devices
        ? string.Create(CultureInfo.CurrentCulture, $"Offered by {e.Devices:N0} of {_devices:N0} devices; the others fail.")
        : string.Empty;

    /// <summary>Set by the dialog once Create was tried: a condition without an event says so below its select.</summary>
    public bool ShowErrors
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Error));
        }
    }

    public string? Error => ShowErrors && Event is null ? "Choose the event or remove the condition." : null;

    /// <summary>Set by the dialog to remove this condition.</summary>
    public IRelayCommand? RemoveCommand { get; init; }

    partial void OnSelectedChoiceChanged(GroupedChoice? value)
    {
        Filters.ReplaceAll(Event?.Event.Sources.Select(s => new FilterRowViewModel(s, null)).ToList() ?? []);
        OnPropertyChanged(nameof(HasFilters));
    }

    /// <summary>The filter: the chosen source values (the server adds "active" for an event that is on or off).</summary>
    public NewFilter? Filter() =>
        Event?.Event is { } e
            ? new NewFilter(e.Topic, [.. Filters.Where(f => f.Selected.Value is not null).Select(f => new ActionEngine.NameValue(f.Item.Name, f.Selected.Value!))])
            : null;
}

/// <summary>
/// "New action rule": name, the event (once when it starts, or while it is active), further conditions, a schedule, the
/// action and its settings as the action catalog describes them. The actions offered are those every chosen device has;
/// a recipient can be one of the devices' own by name. The server checks everything again and each device fits the rule
/// to its own action (a rule that is there already stays as it is).
/// </summary>
public sealed partial class NewRuleViewModel : ValidatingViewModel
{
    public static readonly IReadOnlyList<string> ModeNames = ["Once when the event starts", "While the event is active"];

    private readonly IReadOnlyList<Guid> _devices;
    private readonly IReadOnlyDictionary<Guid, TemplateSet> _sets;
    private readonly IReadOnlyList<OfferedEvent> _events;
    private readonly IReadOnlyDictionary<Guid, DeviceRulesRow> _rows;

    public event EventHandler<bool>? CloseRequested;

    /// <param name="devices">The devices that get the rule.</param>
    /// <param name="sets">Their action templates.</param>
    /// <param name="events">The events they offered when they were read.</param>
    /// <param name="rows">Their rows (recipients and schedules).</param>
    /// <param name="newSchedule">Opens the schedule form; null in tests that do not need it.</param>
    public NewRuleViewModel(IReadOnlyList<Guid> devices, IReadOnlyDictionary<Guid, TemplateSet> sets, IReadOnlyList<OfferedEvent> events,
        IReadOnlyDictionary<Guid, DeviceRulesRow> rows, Func<Task<SchedulePatch?>>? newSchedule = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _sets = sets ?? throw new ArgumentNullException(nameof(sets));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Schedule = new SchedulePickerViewModel(devices, rows, newSchedule);
        Recipient = new RecipientPickerViewModel(devices, rows, enterSettings: true);
        Recipient.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecipientPickerViewModel.Selected))
            {
                BuildParameters();
            }
        };
        Intro = devices.Count == 1 ? "Creates the rule on 1 device." : string.Create(CultureInfo.CurrentCulture, $"Creates the rule on {devices.Count:N0} devices.");
        Event = new EventChoiceViewModel(events, devices.Count, statefulOnly: false);
        Event.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventChoiceViewModel.SelectedChoice))
            {
                OnEventChanged();
            }
        };
        BuildActions();

        Validation.Rule(nameof(Name), () => Name.Trim().Length is > 0 and <= RulePatchApply.MaxNameLength
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."));
        Validation.Rule(nameof(TimeoutSeconds), () => ModeIndex == 1 || TimeoutSeconds is >= 0 and <= RulePatchApply.MaxTimeoutSeconds && TimeoutSeconds == decimal.Truncate(TimeoutSeconds.Value)
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter 0 to {RulePatchApply.MaxTimeoutSeconds:N0} seconds."));
        Validation.Reset();
    }

    public static string Title => "New action rule";

    public string Intro { get; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    public EventChoiceViewModel Event { get; }

    /// <summary>0 = once when the event starts (start event), 1 = while it is active (condition).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnce))]
    public partial int ModeIndex { get; set; }

    public bool IsOnce => ModeIndex == 0;

    /// <summary>Only events that are on or off can be "while active".</summary>
    public bool CanChooseMode => Event.Event?.Event.Stateful ?? true;

    [ObservableProperty] public partial decimal? TimeoutSeconds { get; set; } = 0;

    public ObservableCollection<EventChoiceViewModel> Conditions { get; } = [];

    public bool CanAddCondition => Conditions.Count < ActionRulesPlugin.MaxConditions;

    /// <summary>Only while this schedule is active (Always = no schedule).</summary>
    public SchedulePickerViewModel Schedule { get; }

    public RangeObservableCollection<GroupedChoice> Actions { get; } = [];

    [ObservableProperty] public partial GroupedChoice? SelectedAction { get; set; }

    public string? Template => (SelectedAction?.Value as ActionOption)?.Token;

    /// <summary>Enter the recipient's settings, or each device's own recipient of a name.</summary>
    public RecipientPickerViewModel Recipient { get; }

    public ObservableCollection<ParameterField> Parameters { get; } = [];

    public bool HasParameters => Parameters.Any(p => p.IsShown);

    public string? EventError => ShowAll && Event.Event is null ? "Choose the event." : null;

    public string? ActionError => ShowAll && Template is null ? (Actions.Count == 0 ? "No action is offered by every chosen device." : "Choose the action.") : null;

    private bool ShowAll { get; set; }

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnTimeoutSecondsChanged(decimal? value) => Revalidate();

    partial void OnModeIndexChanged(int value)
    {
        BuildActions();
        Revalidate();
    }

    partial void OnSelectedActionChanged(GroupedChoice? value)
    {
        Recipient.Build(RecipientTemplate);
        BuildParameters();
        OnPropertyChanged(nameof(ActionError));
    }

    private void OnEventChanged()
    {
        if (!CanChooseMode)
        {
            ModeIndex = 0;
        }

        OnPropertyChanged(nameof(CanChooseMode));
        OnPropertyChanged(nameof(EventError));
        Revalidate();
    }

    [RelayCommand]
    private void AddCondition()
    {
        if (!CanAddCondition)
        {
            return;
        }

        EventChoiceViewModel? row = null;
        row = new EventChoiceViewModel(_events, _devices.Count, statefulOnly: true)
        {
            RemoveCommand = new RelayCommand(() =>
            {
                Conditions.Remove(row!);
                OnPropertyChanged(nameof(CanAddCondition));
                Revalidate();
            }),
        };
        row.PropertyChanged += (_, _) => Revalidate();
        Conditions.Add(row);
        OnPropertyChanged(nameof(CanAddCondition));
        Revalidate();
    }

    /// <summary>What is missing before the rule can be created (tooltip of Create), or null.</summary>
    public string? ApplyBlockedReason =>
        Validation.FirstError
        ?? (Event.Event is null ? "Choose the event." : null)
        ?? (Conditions.Any(c => c.Event is null) ? "Choose the event of every condition, or remove it." : null)
        ?? (Template is null ? "Choose the action." : null)
        ?? Parameters.Select(p => p.Problem is { } problem ? p.Label + ": " + problem : null).FirstOrDefault(p => p is not null);

    public bool CanApply => ApplyBlockedReason is null;

    /// <summary>The rule as the server takes it.</summary>
    public NewRule Rule()
    {
        var parameters = Parameters.ToDictionary(p => p.Name, p => p.Value ?? string.Empty, StringComparer.Ordinal);
        return new NewRule(
            Name.Trim(),
            Enabled,
            Event.Filter()!,
            ModeIndex == 1,
            [.. Conditions.Select(c => c.Filter()).OfType<NewFilter>()],
            Schedule.Selected?.Id,
            ModeIndex == 1 ? 0 : (int)(TimeoutSeconds ?? 0),
            Template!,
            parameters)
        {
            RecipientName = Recipient.Selected?.Name,
            NewSchedule = Schedule.Selected?.Create,
        };
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        ShowAll = true;
        Validation.ShowAll();
        foreach (var field in Parameters)
        {
            field.ShowErrors = true;
        }

        foreach (var condition in Conditions)
        {
            condition.ShowErrors = true;
        }

        OnPropertyChanged(nameof(EventError));
        OnPropertyChanged(nameof(ActionError));
        if (CanApply)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        Notify();
    }

    private void Revalidate()
    {
        Validation.Validate();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ApplyBlockedReason));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The actions every chosen device has; once: only those that run once (the others need "while active").</summary>
    private void BuildActions()
    {
        var keep = Template;
        HashSet<string>? common = null;
        foreach (var device in _devices)
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

        var options = (common ?? [])
            .Where(t => !ActionNames.IsRefused(t) && (ModeIndex == 1 || !t.StartsWith("com.axis.action.unlimited.", StringComparison.Ordinal)))
            .Select(t => new ActionOption(t, ActionNames.Of(t)));
        Actions.ReplaceAll(GroupedChoice.Build(options, o => ActionGroups.Of(o.Token), o => o.Name));
        SelectedAction = Actions.FirstOrDefault(a => (a.Value as ActionOption)?.Token == keep);
        OnPropertyChanged(nameof(ActionError));
    }

    /// <summary>The recipient kind of the chosen action on the devices, or null.</summary>
    private string? RecipientTemplate =>
        Template is { } token ? _devices.Select(d => _sets.TryGetValue(d, out var set) ? set.Action(token)?.RecipientTemplate : null).FirstOrDefault(r => r is not null) : null;

    private void BuildParameters()
    {
        var previous = Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        foreach (var field in Parameters)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Parameters.Clear();
        if (Template is { } token)
        {
            var recipient = RecipientTemplate;
            var own = _devices.SelectMany(d => _sets.TryGetValue(d, out var set) ? set.Action(token)?.Parameters ?? [] : []);
            var fromRecipient = recipient is null || Recipient.Selected?.Name is not null
                ? []
                : _devices.SelectMany(d => _sets.TryGetValue(d, out var set) ? set.Recipients.FirstOrDefault(r => r.Token == recipient)?.Parameters ?? [] : []);
            foreach (var name in ActionCatalog.Order(token, recipient, own.Concat(fromRecipient)))
            {
                var catalog = ActionCatalog.ForNewValue(ActionCatalog.Field(token, recipient, name));
                var field = new ParameterField(name, catalog, isRequired: true, string.Empty, previous.GetValueOrDefault(name) ?? catalog.Default ?? string.Empty)
                {
                    NeedsValue = true,
                };
                field.PropertyChanged += OnFieldChanged;
                Parameters.Add(field);
            }
        }

        OnPropertyChanged(nameof(HasParameters));
        Revalidate();
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Revalidate();
}

/// <summary>The groups of the action select, by what the action does.</summary>
public static class ActionGroups
{
    public static string Of(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var parts = token.Split('.');
        var kind = parts.Length > 4 ? parts[4] : token;
        return kind switch
        {
            "notification" or "snmp" or "mqttpublish" => "Notifications",
            "send_images" => "Send images",
            "send_videoclip" => "Send video clips",
            "recording" or "recording_group" => "Recordings",
            "io" => "I/O",
            "play" or "stop" => "Audio",
            "irlight" or "ledcontrol" => "Lights",
            "set_overlay" => "Overlay text",
            "defog" or "wdr" or "ircutfilter" => "Image",
            _ => "Other",
        };
    }
}
