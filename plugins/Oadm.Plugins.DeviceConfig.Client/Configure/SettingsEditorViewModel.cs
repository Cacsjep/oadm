using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.DeviceConfig.Client.Configure;

/// <summary>A parameter of the merged definitions: how many of the selection's models and AXIS OS versions have it.</summary>
public sealed record MergedParam(ParamDefinition Definition, int GroupCount, int TotalGroups, bool NarrowedChoices)
{
    public bool InAll => GroupCount >= TotalGroups;
}

/// <summary>
/// Merges the parameter definitions of every model and AXIS OS of the selection (one device read per group): a parameter
/// keeps the definition of the first group that has it; choices are those every group with it shares, ranges the overlap.
/// O(total parameters).
/// </summary>
public static class DefinitionMerge
{
    public static IReadOnlyList<MergedParam> Merge(IReadOnlyList<IReadOnlyList<ParamDefinition>> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var byName = new Dictionary<string, (ParamDefinition Definition, int Count, bool Narrowed)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var group in groups)
        {
            foreach (var definition in group)
            {
                if (!byName.TryGetValue(definition.Name, out var known))
                {
                    byName[definition.Name] = (definition, 1, false);
                    order.Add(definition.Name);
                    continue;
                }

                var merged = known.Definition;
                var narrowed = known.Narrowed;
                if (merged.Kind != definition.Kind)
                {
                    merged = merged with { Kind = ParamKind.Text, Choices = [], Min = null, Max = null };
                }
                else if (merged.Kind == ParamKind.Enum)
                {
                    var values = definition.Choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
                    var shared = merged.Choices.Where(c => values.Contains(c.Value)).ToList();
                    narrowed |= shared.Count < merged.Choices.Count || shared.Count < definition.Choices.Count;
                    merged = merged with { Choices = shared };
                }
                else if (merged.Kind == ParamKind.Number)
                {
                    var min = Max(merged.Min, definition.Min);
                    var max = Min(merged.Max, definition.Max);

                    // Ranges without overlap: no common range; each device checks its own when the task runs.
                    merged = min > max ? merged with { Min = null, Max = null } : merged with { Min = min, Max = max };
                }

                byName[definition.Name] = (merged, known.Count + 1, narrowed);
            }
        }

        return [.. order.Select(name => byName[name]).Select(e => new MergedParam(e.Definition, e.Count, groups.Count, e.Narrowed))];
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>A group heading of the parameter list ("Image.I0.Appearance"), collapsible.</summary>
public sealed partial class ParamGroupRow(string name, Action changed) : ObservableObject
{
    public string Name { get; } = name;

    public List<ParamRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    partial void OnIsExpandedChanged(bool value) => changed();

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// One parameter: check box, the device's name for it, the right input for its type (text, drop-down, password), the hint and
/// the warnings (dangerous, not on every device, fewer choices). The value is checked against the definition below the input
/// (text, drop-down and password alike) as soon as the row is checked.
/// </summary>
public sealed partial class ParamRowViewModel : ValidatingViewModel
{
    private readonly Action<ParamRowViewModel, bool> _changed;
    private bool _ready;

    /// <param name="changed">Called with true when the check box changed, with false when the value of a checked row changed.</param>
    public ParamRowViewModel(MergedParam merged, string? value, Action<ParamRowViewModel, bool> changed)
    {
        ArgumentNullException.ThrowIfNull(merged);
        _changed = changed;
        Definition = merged.Definition;
        InAll = merged.InAll;
        NarrowedChoices = merged.NarrowedChoices;
        Choices = Definition.Kind switch
        {
            ParamKind.Enum => Definition.Choices,
            ParamKind.Bool => [new ParamChoice(Definition.TrueValue ?? "yes"), new ParamChoice(Definition.FalseValue ?? "no")],
            _ => [],
        };
        var start = value ?? Definition.Value ?? string.Empty;
        if (IsChoice && start.Length > 0 && Choices.All(c => c.Value != start))
        {
            // A value of the file or source that not every device offers: shown, and the rule below says so.
            Choices = [.. Choices, new ParamChoice(start)];
        }

        Value = start;
        Validation
            .Rule(nameof(Value), () => IsChecked && !IsChoice ? ParamRules.Problem(Definition, Value) : null)
            .Rule(nameof(SelectedChoice), () => IsChecked && IsChoice ? ParamRules.Problem(Definition, Value) : null);
        Validation.Reset();
        _ready = true;
    }

    public ParamDefinition Definition { get; }

    public string Name => Definition.Name;

    public string DisplayName => Definition.DisplayName;

    public string Group => Definition.Group;

    public bool InAll { get; }

    public bool NarrowedChoices { get; }

    public IReadOnlyList<ParamChoice> Choices { get; }

    public bool IsChoice => Definition.Kind is ParamKind.Enum or ParamKind.Bool;

    public bool IsPassword => Definition.Kind == ParamKind.Password;

    public bool IsText => !IsChoice && !IsPassword;

    public string? Hint => ParamRules.Hint(Definition);

    public bool HasHint => Hint is not null;

    public string? DangerReason => DangerousParameters.Reason(Definition.Name);

    public bool IsDangerous => DangerReason is not null;

    /// <summary>The amber note on the row: not on every device, or fewer choices than some devices have.</summary>
    public string? Note => !InAll ? "Not every selected device has this parameter." : NarrowedChoices ? "Only the choices every selected device has." : null;

    public bool HasNote => Note is not null;

    /// <summary>The technical name ("Image.I0.Appearance.Compression") for the tooltip.</summary>
    public string ToolTipText => Definition.Name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedChoice))]
    public partial string Value { get; set; }

    /// <summary>
    /// The drop-down's item, always derived from <see cref="Value"/>: a recycled list row that writes null into it (Avalonia
    /// clears the selection when the items change) never loses the value; the drop-down reads it again.
    /// </summary>
    public ParamChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set
        {
            if (value is null)
            {
                OnPropertyChanged(nameof(SelectedChoice));
                return;
            }

            if (value.Value != Value)
            {
                Value = value.Value;
            }
        }
    }

    public string? Problem => IsChecked ? ParamRules.Problem(Definition, Value) : null;

    partial void OnIsCheckedChanged(bool value)
    {
        // A checked row shows its error at once (a pre-checked file value, a range narrowed by other models); an unchecked
        // one never does.
        if (value)
        {
            Validation.ShowAll(nameof(Value), nameof(SelectedChoice));
        }
        else
        {
            Validation.Reset(nameof(Value), nameof(SelectedChoice));
        }

        if (_ready)
        {
            _changed(this, true);
        }
    }

    partial void OnValueChanged(string value)
    {
        if (!_ready)
        {
            return;
        }

        // Typing a value checks the parameter, like ADM.
        if (!IsChecked)
        {
            IsChecked = true;
        }
        else
        {
            Validation.ShowAll(nameof(Value), nameof(SelectedChoice));
            _changed(this, false);
        }
    }

    /// <summary>Text the search looks in: group, display name and technical name.</summary>
    internal string SearchText => Name + " " + DisplayName;
}

/// <summary>An action rule of the source: check box, name, what starts it and what it does.</summary>
public sealed partial class RuleRow(ConfigActionRule rule, Action changed) : ObservableObject
{
    public ConfigActionRule Rule { get; } = rule;

    public string Name => Rule.Name;

    /// <summary>"Recurring pulse", the last part of the event topic.</summary>
    public string When => Rule.StartEvent is { } start ? TopicName(start.Topic) : "No start event";

    public string Then => Rule.Action.Name;

    public string State => Rule.Enabled ? "On" : "Off";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();

    /// <summary>"tns1:UserAlarm/tnsaxis:Recurring/Pulse" -> "Recurring pulse".</summary>
    public static string TopicName(string topic)
    {
        var parts = topic.Split('/').Select(p => p.Contains(':', StringComparison.Ordinal) ? p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..] : p).ToList();
        var tail = parts.Skip(Math.Max(0, parts.Count - 2)).ToList();
        var text = string.Join(' ', tail.Select(Words));
        return text.Length == 0 ? topic : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    private static string Words(string part) =>
        string.Concat(part.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(part[i - 1]) ? " " + c : c.ToString()));
}

/// <summary>A stream profile of the source: check box, name, description and parameters.</summary>
public sealed partial class ProfileRow(ConfigStreamProfile profile, Action changed) : ObservableObject
{
    public ConfigStreamProfile Profile { get; } = profile;

    public string Name => Profile.Name;

    public string Description => Profile.Description ?? string.Empty;

    public string Parameters => Profile.Parameters;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Which part of the settings the editor shows.</summary>
public enum SettingsTab
{
    Parameters,
    ActionRules,
    StreamProfiles,
}

/// <summary>
/// The settings of a source (device or file) to choose from: the parameter list (groups, search, check boxes, inputs), the
/// action rules and the stream profiles. Shared by Configure devices and Create configuration file. Built for about 1,000
/// parameters: the visible list is one reset per change of search, filter or a group.
/// </summary>
public sealed partial class SettingsEditorViewModel : ObservableObject
{
    private readonly List<ParamGroupRow> _groups;
    private readonly List<ParamRowViewModel> _rows;
    private int _checked;

    /// <param name="parameters">The merged definitions; dangerous ones only when <paramref name="allowDangerous"/>.</param>
    /// <param name="values">The source's values by name (null = the definitions' values).</param>
    /// <param name="preChecked">Parameters checked at the start (those of a file).</param>
    /// <param name="severalDevices">Several devices are selected: addresses and names that differ per device are not offered.</param>
    public SettingsEditorViewModel(
        IReadOnlyList<MergedParam> parameters,
        IReadOnlyDictionary<string, string>? values,
        IReadOnlySet<string>? preChecked,
        bool allowDangerous,
        IReadOnlyList<ConfigActionRule> rules,
        IReadOnlyList<ConfigSchedule> schedules,
        IReadOnlyList<ConfigStreamProfile> profiles,
        bool severalDevices = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _rows = [.. parameters
            .Where(p => allowDangerous || !DangerousParameters.IsDangerous(p.Definition.Name))
            .Where(p => !DangerousParameters.IsAddress(p.Definition.Name))
            .Where(p => !severalDevices || !DangerousParameters.IsPerDevice(p.Definition.Name))
            .Select(p => new ParamRowViewModel(p, values?.GetValueOrDefault(p.Definition.Name), OnRowChanged))];
        _groups = [.. _rows.GroupBy(r => r.Group, StringComparer.Ordinal).Select(g =>
        {
            var group = new ParamGroupRow(g.Key, Refresh);
            group.Items.AddRange(g);
            return group;
        })];
        HasHiddenDangerous = !allowDangerous && parameters.Any(p => DangerousParameters.IsDangerous(p.Definition.Name));
        Rules = [.. rules.Select(r => new RuleRow(r, OnOtherChanged))];
        Schedules = schedules;
        Profiles = [.. profiles.Select(p => new ProfileRow(p, OnOtherChanged))];
        foreach (var row in _rows.Where(r => preChecked?.Contains(r.Name) == true))
        {
            row.IsChecked = true;
        }

        // A checked parameter is never hidden: show the ones not every device has when a file checked one of them.
        ShowAll = _rows.Any(r => r.IsChecked && !r.InAll);
        Refresh();
    }

    public RangeObservableCollection<object> Items { get; } = [];

    public IReadOnlyList<ParamRowViewModel> Parameters => _rows;

    public IReadOnlyList<RuleRow> Rules { get; }

    public IReadOnlyList<ConfigSchedule> Schedules { get; }

    public IReadOnlyList<ProfileRow> Profiles { get; }

    /// <summary>Dangerous parameters exist but the role may not set them (they are not offered).</summary>
    public bool HasHiddenDangerous { get; }

    /// <summary>
    /// Only the source device is selected: its action rules and stream profiles are not offered (they are already on it); the
    /// two tabs show this text instead.
    /// </summary>
    public string? OwnSettingsNote { get; init; }

    public string RulesEmptyText => OwnSettingsNote ?? "The source has no action rules.";

    public string ProfilesEmptyText => OwnSettingsNote ?? "The source has no stream profiles.";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersTab), nameof(IsRulesTab), nameof(IsProfilesTab))]
    public partial SettingsTab Tab { get; set; }

    public bool IsParametersTab => Tab == SettingsTab.Parameters;

    public bool IsRulesTab => Tab == SettingsTab.ActionRules;

    public bool IsProfilesTab => Tab == SettingsTab.StreamProfiles;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Also the parameters not every selected device has (ADM's check box).</summary>
    [ObservableProperty]
    public partial bool ShowAll { get; set; }

    public bool HasPartial => _rows.Any(r => !r.InAll);

    public int CheckedCount => _checked;

    public int CheckedRuleCount => Rules.Count(r => r.IsChecked);

    public int CheckedProfileCount => Profiles.Count(p => p.IsChecked);

    public string ParametersTabText => TabText("Parameters", _checked);

    public string RulesTabText => TabText("Action rules", CheckedRuleCount);

    public string ProfilesTabText => TabText("Stream profiles", CheckedProfileCount);

    public bool HasRules => Rules.Count > 0;

    public bool HasProfiles => Profiles.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => _rows.Count == 0 ? "No parameters to configure." : "No parameter matches the search.";

    public bool HasChoice => _checked > 0 || CheckedRuleCount > 0 || CheckedProfileCount > 0;

    /// <summary>
    /// The first problem of what is checked ("Image.I0.Appearance Compression: Enter 0 to 100.", two checked rules or profiles
    /// with one name), or null.
    /// </summary>
    public string? FirstProblem
    {
        get
        {
            if (_rows.FirstOrDefault(r => r.IsChecked && r.Problem is not null) is { } row)
            {
                return $"{row.Group} {row.DisplayName}: {row.Problem}";
            }

            if (CheckedRules.GroupBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } rule)
            {
                return $"Two checked action rules are named {rule.Key}. Check one of them.";
            }

            return CheckedProfiles.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } profile
                ? $"Two checked stream profiles are named {profile.Key}. Check one of them."
                : null;
        }
    }

    public IReadOnlyList<NameValue> CheckedParameters => [.. _rows.Where(r => r.IsChecked).Select(r => new NameValue(r.Name, r.Value))];

    public IReadOnlyList<ConfigActionRule> CheckedRules => [.. Rules.Where(r => r.IsChecked).Select(r => r.Rule)];

    /// <summary>The own schedules the checked rules use (built-in ones every device has).</summary>
    public IReadOnlyList<ConfigSchedule> CheckedSchedules
    {
        get
        {
            var ids = CheckedRules.SelectMany(r => r.ScheduleIds).ToHashSet(StringComparer.Ordinal);
            return [.. Schedules.Where(s => ids.Contains(s.Id))];
        }
    }

    public IReadOnlyList<ConfigStreamProfile> CheckedProfiles => [.. Profiles.Where(p => p.IsChecked).Select(p => p.Profile)];

    /// <summary>Raised when a check box changes (the dialog refreshes its buttons).</summary>
    public event EventHandler? ChoiceChanged;

    [RelayCommand]
    private void ShowParameters() => Tab = SettingsTab.Parameters;

    [RelayCommand]
    private void ShowRules() => Tab = SettingsTab.ActionRules;

    [RelayCommand]
    private void ShowProfiles() => Tab = SettingsTab.StreamProfiles;

    [RelayCommand]
    private void SelectAllRules() => SetAll(Rules.Select(r => (Action<bool>)(v => r.IsChecked = v)), !Rules.All(r => r.IsChecked));

    [RelayCommand]
    private void SelectAllProfiles() => SetAll(Profiles.Select(p => (Action<bool>)(v => p.IsChecked = v)), !Profiles.All(p => p.IsChecked));

    [RelayCommand]
    private void ExpandAll() => SetExpanded(true);

    [RelayCommand]
    private void CollapseAll() => SetExpanded(false);

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnShowAllChanged(bool value) => Refresh();

    /// <summary>Rebuilds the visible list: groups with their matching rows (a search shows matches of collapsed groups too).</summary>
    private void Refresh()
    {
        var text = SearchText.Trim();
        var items = new List<object>();
        foreach (var group in _groups)
        {
            var matches = group.Items
                .Where(r => (ShowAll || r.InAll) && (text.Length == 0 || r.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            items.Add(group);
            if (group.IsExpanded || text.Length > 0)
            {
                items.AddRange(matches);
            }
        }

        Items.ReplaceAll(items);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void SetExpanded(bool value)
    {
        foreach (var group in _groups)
        {
            group.IsExpanded = value;
        }

        Refresh();
    }

    private void OnRowChanged(ParamRowViewModel row, bool checkedChanged)
    {
        if (checkedChanged)
        {
            _checked += row.IsChecked ? 1 : -1;
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(ParametersTabText));
        }

        RaiseChoice();
    }

    private void OnOtherChanged()
    {
        OnPropertyChanged(nameof(RulesTabText));
        OnPropertyChanged(nameof(ProfilesTabText));
        RaiseChoice();
    }

    private void RaiseChoice()
    {
        OnPropertyChanged(nameof(HasChoice));
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetAll(IEnumerable<Action<bool>> setters, bool value)
    {
        foreach (var set in setters)
        {
            set(value);
        }
    }

    private static string TabText(string name, int count) =>
        count == 0 ? name : string.Create(CultureInfo.CurrentCulture, $"{name} ({count:N0})");

    /// <summary>
    /// The overview of what is chosen as one flat list (virtualized): a heading per part ("Parameters (12)"), then one line per
    /// parameter ("Image.I0.Appearance: Compression = 45", passwords masked), rule and profile.
    /// </summary>
    public IReadOnlyList<OverviewItem> Overview()
    {
        var items = new List<OverviewItem>();
        var parameters = _rows.Where(r => r.IsChecked).ToList();
        if (parameters.Count > 0)
        {
            items.Add(new OverviewItem(TabText("Parameters", parameters.Count), IsHeader: true));
            items.AddRange(parameters.Select(r => new OverviewItem($"{r.Group}: {r.DisplayName} = {(r.IsPassword ? "••••••••" : r.Value)}", IsHeader: false)
            {
                Warning = r.DangerReason,
            }));
        }

        var rules = CheckedRules;
        if (rules.Count > 0)
        {
            items.Add(new OverviewItem(TabText("Action rules", rules.Count), IsHeader: true));
            items.AddRange(rules.Select(r => new OverviewItem($"{r.Name}: {RuleRow.TopicName(r.StartEvent?.Topic ?? string.Empty)} → {r.Action.Name}", IsHeader: false)));
        }

        var profiles = CheckedProfiles;
        if (profiles.Count > 0)
        {
            items.Add(new OverviewItem(TabText("Stream profiles", profiles.Count), IsHeader: true));
            items.AddRange(profiles.Select(p => new OverviewItem($"{p.Name}: {p.Parameters}", IsHeader: false)));
        }

        return items;
    }
}

/// <summary>One line of the overview: a heading or an item, optionally with the dangerous-parameter warning.</summary>
public sealed record OverviewItem(string Text, bool IsHeader)
{
    public string? Warning { get; init; }

    public bool HasWarning => Warning is not null;

    public bool IsItem => !IsHeader;
}
