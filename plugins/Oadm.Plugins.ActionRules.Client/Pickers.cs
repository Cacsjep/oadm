using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>A choice of the schedule select: none, a schedule every device has, or one OADM creates where missing.</summary>
public sealed record ScheduleChoice(string Text, string? Id, SchedulePatch? Create)
{
    public override string ToString() => Text;
}

/// <summary>A choice of the recipient select: enter the settings, or a recipient of that name on each device.</summary>
public sealed record RecipientChoice(string Text, string? Name)
{
    public override string ToString() => Text;
}

/// <summary>
/// The schedule of a rule (New rule, Edit, Copy): Always, the built-in schedules, OADM's own ones of the devices (by id, so
/// renamed ones too; created where a device lacks them) or New schedule.
/// </summary>
public sealed partial class SchedulePickerViewModel : ObservableObject
{
    private readonly Func<Task<SchedulePatch?>>? _newSchedule;

    public SchedulePickerViewModel(IEnumerable<Guid> devices, IReadOnlyDictionary<Guid, DeviceRulesRow> rows, Func<Task<SchedulePatch?>>? newSchedule)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(rows);
        _newSchedule = newSchedule;
        Choices.Add(new ScheduleChoice("Always", null, null));
        var chosen = devices.ToHashSet();
        foreach (var s in rows.Where(r => chosen.Contains(r.Key)).SelectMany(r => r.Value.Schedules)
                     .Where(s => s.IsBuiltIn || s.Id.StartsWith(ScheduleText.OwnIdPrefix, StringComparison.Ordinal))
                     .DistinctBy(s => s.Id).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Choices.Add(new ScheduleChoice(s.Name, s.Id, s.IsBuiltIn ? null : new SchedulePatch(s.Name, s.Definition)));
        }

        Selected = Choices[0];
    }

    public ObservableCollection<ScheduleChoice> Choices { get; } = [];

    [ObservableProperty] public partial ScheduleChoice? Selected { get; set; }

    /// <summary>A new schedule from the schedule form, created on the devices that lack it.</summary>
    [RelayCommand]
    private async Task NewScheduleAsync()
    {
        if (_newSchedule is not null && await _newSchedule().ConfigureAwait(true) is { Name: { } name, Definition: { } definition })
        {
            var choice = new ScheduleChoice(name.Trim(), ScheduleText.OwnId(name), new SchedulePatch(name.Trim(), definition));
            var same = Choices.FirstOrDefault(s => s.Id == choice.Id);
            if (same is not null)
            {
                Choices.Remove(same);
            }

            Choices.Add(choice);
            Selected = choice;
        }
    }
}

/// <summary>
/// The recipient of a rule's action by name: each device uses its own recipient of that name and kind (with its password).
/// New rule also offers "Enter the settings".
/// </summary>
public sealed partial class RecipientPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<Guid> _devices;
    private readonly IReadOnlyDictionary<Guid, DeviceRulesRow> _rows;
    private readonly bool _enterSettings;

    public RecipientPickerViewModel(IReadOnlyList<Guid> devices, IReadOnlyDictionary<Guid, DeviceRulesRow> rows, bool enterSettings)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _enterSettings = enterSettings;
    }

    /// <summary>The recipient kind of the chosen action (<c>com.axis.recipient.tcp</c>), or null.</summary>
    public string? Kind { get; private set; }

    public RangeObservableCollection<RecipientChoice> Choices { get; } = [];

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial RecipientChoice? Selected { get; set; }

    /// <summary>How many devices have the chosen recipient.</summary>
    public string Hint
    {
        get
        {
            if (Selected?.Name is not { } name || Kind is not { } kind)
            {
                return string.Empty;
            }

            var have = _devices.Count(d => _rows.TryGetValue(d, out var row) && row.Recipients.Any(r => r.Template == kind && r.Name == name));
            return have == _devices.Count ? "Each device uses its own recipient of this name."
                : string.Create(CultureInfo.CurrentCulture, $"{have:N0} of {_devices.Count:N0} devices have it; the others fail.");
        }
    }

    /// <summary>Lists the recipients of <paramref name="kind"/> on the devices (none without a kind); keeps the choice by name.</summary>
    public void Build(string? kind)
    {
        Kind = kind;
        var keep = Selected?.Name;
        if (kind is null)
        {
            Choices.ReplaceAll([]);
        }
        else
        {
            var names = _devices.SelectMany(d => _rows.TryGetValue(d, out var row) ? row.Recipients : [])
                .Where(r => r.Template == kind).Select(r => r.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)
                .Select(n => new RecipientChoice(n, n));
            Choices.ReplaceAll(_enterSettings ? [new RecipientChoice("Enter the settings", null), .. names] : [.. names]);
        }

        Selected = Choices.FirstOrDefault(r => r.Name == keep) ?? Choices.FirstOrDefault();
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Hint));
    }
}
