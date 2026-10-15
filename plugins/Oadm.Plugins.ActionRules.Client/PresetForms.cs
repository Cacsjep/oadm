using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>The kind of a new recipient (FTP, HTTPS, ...).</summary>
public sealed record RecipientKind(string Token, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// "Edit recipients": name and settings with a Change check box each; unchecked parts stay as each device has them. "Also
/// change the rules that send to it" (checked) gives the rules that copied the old settings the changed ones.
/// "New recipient" (<see cref="ForNew"/>): the kind every chosen device offers, a name and every setting with the catalog's
/// defaults.
/// </summary>
public sealed partial class RecipientFormViewModel : ValidatingViewModel
{
    private readonly IReadOnlyList<RecipientTemplate> _kinds = [];

    private RecipientFormViewModel(IReadOnlyList<RecipientTemplate> kinds, int devices)
    {
        IsNew = true;
        _kinds = kinds;
        ChangeName = true;
        Intro = devices == 1 ? "Adds the recipient to 1 device." : string.Create(CultureInfo.CurrentCulture, $"Adds the recipient to {devices:N0} devices.");
        Kinds = [.. kinds.Select(k => new RecipientKind(k.Token, ActionCatalog.RecipientLabel(k.Token) ?? ActionCatalog.Humanize(k.Token.Split('.')[^1])))
            .OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)];
        Validation.Rule(nameof(Name), () => Name.Trim().Length is > 0 and <= RulePatchApply.MaxNameLength
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."));
        SelectedKind = Kinds.Count > 0 ? Kinds[0] : null;
        Validation.Reset();
    }

    /// <summary>The form of a new recipient for the chosen devices: the kinds every one of them offers.</summary>
    public static RecipientFormViewModel ForNew(IReadOnlyList<Guid> devices, IReadOnlyDictionary<Guid, TemplateSet> sets)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sets);
        Dictionary<string, RecipientTemplate>? common = null;
        foreach (var set in devices.Select(d => sets.GetValueOrDefault(d)).DistinctBy(s => s?.Id))
        {
            var kinds = set?.Recipients ?? [];
            if (common is null)
            {
                common = kinds.DistinctBy(k => k.Token).ToDictionary(k => k.Token, StringComparer.Ordinal);
            }
            else
            {
                foreach (var token in common.Keys.Except(kinds.Select(k => k.Token)).ToList())
                {
                    common.Remove(token);
                }
            }
        }

        return new RecipientFormViewModel([.. (common ?? []).Values], devices.Count);
    }

    public bool IsNew { get; }

    public bool IsEdit => !IsNew;

    public string Title => IsNew ? "New recipient" : "Edit recipients";

    public string ApplyText => IsNew ? "Add" : "Apply";

    public IReadOnlyList<RecipientKind> Kinds { get; } = [];

    [ObservableProperty] public partial RecipientKind? SelectedKind { get; set; }

    partial void OnSelectedKindChanged(RecipientKind? value)
    {
        foreach (var field in Parameters)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Parameters.Clear();
        if (value is not null && _kinds.FirstOrDefault(k => k.Token == value.Token) is { } kind)
        {
            foreach (var name in ActionCatalog.Order(string.Empty, kind.Token, kind.Parameters))
            {
                var catalog = ActionCatalog.ForNewValue(ActionCatalog.RecipientField(kind.Token, name));
                var field = new ParameterField(name, catalog, isRequired: true, string.Empty, catalog.Default ?? string.Empty) { NeedsValue = true };
                field.PropertyChanged += OnFieldChanged;
                Parameters.Add(field);
            }
        }

        Notify();
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Notify();

    public RecipientFormViewModel(IReadOnlyList<RecipientMember> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var devices = targets.Select(t => t.Device.DeviceId).Distinct().Count();
        Intro = (targets.Count == 1 ? "Changes 1 recipient" : string.Create(CultureInfo.CurrentCulture, $"Changes {targets.Count:N0} recipients"))
            + (devices == 1 ? " on 1 device." : string.Create(CultureInfo.CurrentCulture, $" on {devices:N0} devices.")) + " Parts without a check stay as each device has them.";
        var names = targets.Select(t => t.Recipient.Name.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        Name = names.Count == 1 ? names[0] : string.Empty;
        var kind = targets.Count > 0 ? targets[0].Recipient.Template : string.Empty;
        var all = targets.SelectMany(t => t.Recipient.Parameters.Select(p => p.Name)).Distinct(StringComparer.Ordinal);
        foreach (var name in ActionCatalog.Order(string.Empty, kind, all))
        {
            var catalog = ActionCatalog.RecipientField(kind, name);
            if (catalog.Kind == FieldKinds.Hidden)
            {
                continue; // never shown by the device either: each keeps its own
            }

            var secret = catalog.Kind == FieldKinds.Password;
            var values = targets.Select(t => t.Recipient.Get(name)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var hint = secret ? (targets.All(t => t.Recipient.StoredSecrets.Contains(name)) ? "Stays as it is unless you enter a new one." : "Empty on some recipients.")
                : values.Count <= 1 ? string.Empty
                : string.Create(CultureInfo.CurrentCulture, $"{values.Count:N0} different values; each keeps its own unless checked.");
            var field = new ParameterField(name, catalog, false, hint, !secret && values.Count == 1 ? values[0] : string.Empty);
            field.PropertyChanged += (_, _) => Notify();
            Parameters.Add(field);
        }

        Validation.Rule(nameof(Name), () => !ChangeName || Name.Trim().Length is > 0 and <= RulePatchApply.MaxNameLength
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter a name of 1 to {RulePatchApply.MaxNameLength} characters."));
        Validation.Reset();
    }

    public event EventHandler<bool>? CloseRequested;

    public string Intro { get; }

    public ObservableCollection<ParameterField> Parameters { get; } = [];

    [ObservableProperty] public partial bool ChangeName { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial bool UpdateRules { get; set; } = true;

    partial void OnChangeNameChanged(bool value) => Revalidate();

    partial void OnNameChanged(string value) => Revalidate();

    public string? ApplyBlockedReason =>
        Validation.FirstError
        ?? Parameters.Select(p => p.Problem is { } problem ? p.Label + ": " + problem : null).FirstOrDefault(p => p is not null)
        ?? (IsNew && SelectedKind is null ? "No kind of recipient is offered by every chosen device." : null)
        ?? (!IsNew && !ChangeName && !Parameters.Any(p => p.Change) ? "Check at least one part to change." : null);

    public bool CanApply => ApplyBlockedReason is null;

    public RecipientPatch Patch()
    {
        var parameters = Parameters.Where(p => p.Change).ToDictionary(p => p.Name, p => p.Value ?? string.Empty, StringComparer.Ordinal);
        return new RecipientPatch(ChangeName ? Name.Trim() : null, parameters.Count > 0 ? parameters : null, UpdateRules);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
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
}

/// <summary>A weekday toggle of the schedule form.</summary>
public sealed partial class DayToggle(DayOfWeek day, string text) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Text { get; } = text;

    [ObservableProperty] public partial bool IsOn { get; set; }
}

/// <summary>
/// "New schedule" / "Edit schedules": a name and when it is active: weekdays with a time range (may end the next day),
/// every N minutes, or the device's text for anything else. Editing several schedules changes only the checked parts.
/// </summary>
public sealed partial class ScheduleFormViewModel : ValidatingViewModel
{
    public static readonly IReadOnlyList<string> KindNames = ["Days and times", "Every few minutes", "Text"];

    private static readonly string[] DayTexts = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private ScheduleFormViewModel(bool isNew, string intro, string name, string definition)
    {
        IsNew = isNew;
        Intro = intro;
        Name = name;
        ChangeName = isNew;
        ChangeWhen = isNew;
        Days = [.. ScheduleText.Week.Select((d, i) => new DayToggle(d, DayTexts[i]))];
        foreach (var day in Days)
        {
            day.PropertyChanged += (_, _) => Revalidate();
        }

        var shape = ScheduleText.Parse(definition);
        KindIndex = (int)shape.Kind;
        foreach (var day in Days)
        {
            day.IsOn = shape.Kind == ScheduleKind.Weekly ? shape.Days.Contains(day.Day) : day.Day is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        }

        From = ScheduleText.Clock(shape.Kind == ScheduleKind.Weekly ? shape.From : TimeSpan.FromHours(8));
        To = ScheduleText.Clock(shape.Kind == ScheduleKind.Weekly ? shape.To : TimeSpan.FromHours(18));
        EveryMinutes = shape.Kind == ScheduleKind.Every ? shape.EveryMinutes : 15;
        Text = shape.Kind == ScheduleKind.Custom ? shape.Text : ScheduleText.Build(shape);

        Validation.Rule(nameof(Name), () => !ChangeName || Name.Trim().Length is > 0 and <= ScheduleText.MaxNameLength
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter a name of 1 to {ScheduleText.MaxNameLength} characters."));
        Validation.Rule(nameof(From), () => !ChangeWhen || KindIndex != 0 || ScheduleText.ParseClock(From) is not null ? null : "Enter the time as hh:mm.");
        Validation.Rule(nameof(To), () => !ChangeWhen || KindIndex != 0 || ScheduleText.ParseClock(To) is not null ? null : "Enter the time as hh:mm.");
        Validation.Rule(WhenField, () => !ChangeWhen ? null : WhenProblem());
        Validation.Reset();
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>The form of a new schedule for <paramref name="devices"/> devices.</summary>
    public static ScheduleFormViewModel ForNew(int devices) => new(
        true,
        devices == 1 ? "Adds the schedule to 1 device." : string.Create(CultureInfo.CurrentCulture, $"Adds the schedule to {devices:N0} devices."),
        string.Empty,
        "DTSTART:19700101T080000\nDTEND:19700101T180000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");

    /// <summary>The form to change schedules; values from the first one.</summary>
    public static ScheduleFormViewModel ForEdit(IReadOnlyList<ScheduleMember> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var devices = targets.Select(t => t.Device.DeviceId).Distinct().Count();
        var intro = (targets.Count == 1 ? "Changes 1 schedule" : string.Create(CultureInfo.CurrentCulture, $"Changes {targets.Count:N0} schedules"))
            + (devices == 1 ? " on 1 device." : string.Create(CultureInfo.CurrentCulture, $" on {devices:N0} devices.")) + " The rules that use them keep working.";
        return new(false, intro, targets[0].Schedule.Name, targets[0].Schedule.Definition);
    }

    public bool IsNew { get; }

    public bool IsEdit => !IsNew;

    public string Title => IsNew ? "New schedule" : "Edit schedules";

    public string ApplyText => IsNew ? "Add" : "Apply";

    public string Intro { get; }

    public IReadOnlyList<DayToggle> Days { get; }

    [ObservableProperty] public partial bool ChangeName { get; set; }

    [ObservableProperty] public partial string Name { get; set; }

    [ObservableProperty] public partial bool ChangeWhen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekly), nameof(IsEvery), nameof(IsText))]
    public partial int KindIndex { get; set; }

    /// <summary>The rule for the times as a whole (shown below the input it concerns through <see cref="WhenError"/>).</summary>
    private const string WhenField = "When";

    /// <summary>What is wrong with the days, minutes or text, shown below that input.</summary>
    public string? WhenError => ChangeWhen ? WhenProblem() : null;

    public bool IsWeekly => KindIndex == 0;

    public bool IsEvery => KindIndex == 1;

    public bool IsText => KindIndex == 2;

    [ObservableProperty] public partial string From { get; set; } = "08:00";

    [ObservableProperty] public partial string To { get; set; } = "18:00";

    [ObservableProperty] public partial decimal? EveryMinutes { get; set; } = 15;

    [ObservableProperty] public partial string Text { get; set; } = string.Empty;

    /// <summary>"Mon to Fri 08:00 to 18:00" while the input is valid.</summary>
    public string Summary => WhenProblem() is null ? ScheduleText.Describe(Definition()) : string.Empty;

    partial void OnChangeNameChanged(bool value) => Revalidate();

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnChangeWhenChanged(bool value) => Revalidate();

    partial void OnKindIndexChanged(int value) => Revalidate();

    partial void OnFromChanged(string value) => Revalidate();

    partial void OnToChanged(string value) => Revalidate();

    partial void OnEveryMinutesChanged(decimal? value) => Revalidate();

    partial void OnTextChanged(string value) => Revalidate();

    public string? ApplyBlockedReason => Validation.FirstError ?? (!ChangeName && !ChangeWhen ? "Check at least one part to change." : null);

    public bool CanApply => ApplyBlockedReason is null;

    /// <summary>The device's text of the chosen times.</summary>
    public string Definition() => ScheduleText.Build(Shape());

    public SchedulePatch Patch() => new(ChangeName ? Name.Trim() : null, ChangeWhen ? Definition() : null);

    private ScheduleShape Shape() => (ScheduleKind)Math.Clamp(KindIndex, 0, 2) switch
    {
        ScheduleKind.Weekly => new ScheduleShape(ScheduleKind.Weekly, [.. Days.Where(d => d.IsOn).Select(d => d.Day)], ScheduleText.ParseClock(From) ?? TimeSpan.Zero, ScheduleText.ParseClock(To) ?? TimeSpan.Zero, 0, string.Empty),
        ScheduleKind.Every => new ScheduleShape(ScheduleKind.Every, [], TimeSpan.Zero, TimeSpan.Zero, EveryMinutes is { } m && m == decimal.Truncate(m) && m is >= 0 and <= int.MaxValue ? (int)m : 0, string.Empty),
        _ => new ScheduleShape(ScheduleKind.Custom, [], TimeSpan.Zero, TimeSpan.Zero, 0, Text ?? string.Empty),
    };

    private string? WhenProblem() =>
        KindIndex == 0 && (ScheduleText.ParseClock(From) is null || ScheduleText.ParseClock(To) is null) ? null // shown below the time
        : ScheduleText.Problem(Shape());

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Validation.ShowAll();
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
        if (Days is null)
        {
            return; // still in the constructor
        }

        Validation.Validate();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ApplyBlockedReason));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WhenError));
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
