using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>A row of the device grid of the Recipients or Schedules tab.</summary>
public sealed class PresetMemberRow
{
    public PresetMemberRow(RecipientMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Recipient = member;
        Device = member.Device;
        Name = member.Recipient.Name;
        Detail = string.Join(" · ", member.Recipient.Parameters.Select(p => RuleText.IsSecret(p.Name)
            ? p.Name + (member.Recipient.StoredSecrets.Contains(p.Name) ? " set" : " empty")
            : p.Name + " " + (p.Value.Length == 0 ? "(empty)" : p.Value)));
    }

    public PresetMemberRow(ScheduleMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Schedule = member;
        Device = member.Device;
        Name = member.Schedule.Name;
        Detail = ScheduleText.Describe(member.Schedule.Definition)
            + (member.Schedule.UsedBy switch { 0 => string.Empty, 1 => " · used by 1 rule", var n => string.Create(CultureInfo.CurrentCulture, $" · used by {n:N0} rules") });
    }

    public RecipientMember? Recipient { get; }

    public ScheduleMember? Schedule { get; }

    public DeviceRulesRow Device { get; }

    public string Address => Device.Address;

    public string AddressSortKey => RuleMemberRow.AddressKey(Device.Address);

    public string Model => Device.Model ?? string.Empty;

    public string Firmware => Device.Firmware ?? string.Empty;

    public string Name { get; }

    public string Detail { get; }
}

/// <summary>A row of the group grid of the Recipients or Schedules tab.</summary>
public sealed class PresetGroupRow
{
    public PresetGroupRow(RecipientGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Recipients = group;
        Key = group.Key;
        Name = group.Name;
        Kind = group.KindText;
        Detail = group.ValuesText;
        DeviceCount = group.DeviceCount;
    }

    public PresetGroupRow(ScheduleGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Schedules = group;
        Key = group.Key;
        Name = group.Name;
        var used = group.UsedBy switch { 0 => "Not used", 1 => "Used by 1 rule", var n => string.Create(CultureInfo.CurrentCulture, $"Used by {n:N0} rules") };
        Kind = group.IsBuiltIn ? "Built in · " + used.ToLowerInvariant() : used;
        Detail = group.WhenText;
        DeviceCount = group.DeviceCount;
    }

    public RecipientGroup? Recipients { get; }

    public ScheduleGroup? Schedules { get; }

    public string Key { get; }

    public string Name { get; }

    public string Kind { get; }

    public string Detail { get; }

    public int DeviceCount { get; }

    public string DevicesText => DeviceCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SearchText => Name + " " + Kind + " " + Detail;
}

/// <summary>
/// The Recipients or Schedules tab: the groups of all devices left, each device's items of the selected groups right (a
/// selection there narrows the target), and the tools; the page owns the rows, the server calls and the dialogs.
/// </summary>
public sealed partial class PresetTabViewModel(ActionRulesViewModel page, bool schedules) : ObservableObject
{
    private List<PresetGroupRow> _all = [];
    private IReadOnlyList<PresetGroupRow> _selectedGroups = [];
    private IReadOnlyList<PresetMemberRow> _selectedMembers = [];

    public bool IsSchedules => schedules;

    public RangeObservableCollection<PresetGroupRow> Groups { get; } = [];

    public RangeObservableCollection<PresetMemberRow> Members { get; } = [];

    public bool ShowEmpty => Groups.Count == 0;

    public string EmptyText => schedules ? "The devices have no schedules." : "The devices have no recipients.";

    [ObservableProperty] public partial string MembersTitle { get; private set; } = string.Empty;

    public bool ShowMembersEmpty => Members.Count == 0;

    public string MembersEmptyText => schedules ? "Select a schedule to see it on each device." : "Select a recipient to see it on each device.";

    /// <summary>The selected items: those selected right, else all of the selected groups.</summary>
    public IReadOnlyList<PresetMemberRow> Targets =>
        _selectedMembers.Count > 0 ? _selectedMembers : [.. _selectedGroups.SelectMany(Rows)];

    public string TargetText
    {
        get
        {
            var targets = Targets;
            var devices = targets.Select(t => t.Device.DeviceId).Distinct().Count();
            var what = schedules ? "schedule" : "recipient";
            return targets.Count == 0 ? string.Empty
                : (targets.Count == 1 ? "1 " + what : string.Create(CultureInfo.CurrentCulture, $"{targets.Count:N0} {what}s"))
                  + (devices == 1 ? " on 1 device" : string.Create(CultureInfo.CurrentCulture, $" on {devices:N0} devices"));
        }
    }

    public string? ToolsBlockedReason => Targets.Count > 0 ? null : schedules ? "Select schedules first." : "Select recipients first.";

    /// <summary>Copy needs exactly one item to copy from.</summary>
    public string? CopyBlockedReason => Targets.Count == 1 ? null : "Select one item on the right to copy.";

    /// <summary>Built-in schedules are each device's own: never changed or deleted.</summary>
    public string? ChangeBlockedReason =>
        ToolsBlockedReason ?? (schedules && Targets.All(t => t.Schedule!.Schedule.IsBuiltIn) ? "Built-in schedules are not changed." : null);

    public event EventHandler<IReadOnlyList<PresetGroupRow>>? SelectionRestored;

    /// <summary>The rows changed: group again, keep the selection by key.</summary>
    public void Update(IEnumerable<DeviceRulesRow> rows, string search)
    {
        _all = schedules
            ? [.. PresetGrouping.Schedules(rows).Select(g => new PresetGroupRow(g))]
            : [.. PresetGrouping.Recipients(rows).Select(g => new PresetGroupRow(g))];
        Filter(search);
    }

    public void Filter(string search)
    {
        var text = search.Trim();
        List<PresetGroupRow> shown = text.Length == 0 ? [.. _all] : [.. _all.Where(g => g.SearchText.Contains(text, StringComparison.CurrentCultureIgnoreCase))];
        var keys = _selectedGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        Groups.ReplaceAll(shown);
        OnPropertyChanged(nameof(ShowEmpty));
        var keep = shown.Where(g => keys.Contains(g.Key)).ToList();
        SelectionRestored?.Invoke(this, keep);
        SetGroupSelection(keep);
    }

    public void SetGroupSelection(IEnumerable<PresetGroupRow> selected)
    {
        _selectedGroups = [.. selected];
        _selectedMembers = [];
        Members.ReplaceAll(_selectedGroups.SelectMany(Rows).OrderBy(m => m.AddressSortKey, StringComparer.Ordinal));
        MembersTitle = schedules ? "Schedules" : "Recipients";
        if (_selectedGroups.Count > 0)
        {
            MembersTitle += string.Create(CultureInfo.CurrentCulture, $" ({Members.Count:N0})");
        }

        OnPropertyChanged(nameof(ShowMembersEmpty));
        NotifyTargets();
    }

    public void SetMemberSelection(IEnumerable<PresetMemberRow> selected)
    {
        _selectedMembers = [.. selected];
        NotifyTargets();
    }

    private bool CanManage => page.MayManage && ChangeBlockedReason is null;

    private bool CanCopy => page.MayManage && CopyBlockedReason is null;

    private bool CanDelete => page.MayDelete && ChangeBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task EditAsync() => schedules
        ? page.EditSchedulesAsync([.. Targets.Select(t => t.Schedule!).Where(s => !s.Schedule.IsBuiltIn)])
        : page.EditRecipientsAsync([.. Targets.Select(t => t.Recipient!)]);

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopyAsync() => schedules
        ? page.CopyScheduleAsync(Targets[0].Schedule!)
        : page.CopyRecipientAsync(Targets[0].Recipient!);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => schedules
        ? page.DeleteSchedulesAsync([.. Targets.Select(t => t.Schedule!).Where(s => !s.Schedule.IsBuiltIn)])
        : page.DeleteRecipientsAsync([.. Targets.Select(t => t.Recipient!)]);

    [RelayCommand(CanExecute = nameof(CanNew))]
    private Task NewAsync() => schedules ? page.NewScheduleAsync() : page.NewRecipientAsync();

    private bool CanNew => page.MayManage;

    public void NotifyTargets()
    {
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(ToolsBlockedReason));
        OnPropertyChanged(nameof(CopyBlockedReason));
        OnPropertyChanged(nameof(ChangeBlockedReason));
        EditCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        NewCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<PresetMemberRow> Rows(PresetGroupRow group) =>
        group.Schedules is { } s ? s.Members.Select(m => new PresetMemberRow(m))
        : group.Recipients!.Members.Select(m => new PresetMemberRow(m));
}
