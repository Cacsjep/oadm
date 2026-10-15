using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>The dialogs of the page (the view opens them; tests answer them).</summary>
public interface IRuleDialogs
{
    /// <summary>Shows the edit form; the patch, or null when cancelled.</summary>
    Task<RulePatch?> EditAsync(EditRulesViewModel form);

    /// <summary>Shows the list of changes that can be undone; the run to undo, or null.</summary>
    Task<Guid?> ChooseUndoAsync(UndoViewModel list);

    /// <summary>The shared device picker; the chosen ids, or null when cancelled.</summary>
    Task<IReadOnlyList<Guid>?> ChooseDevicesAsync(string heading, Func<Guid, string?> unavailableReason);

    /// <summary>Shows the recipient form; the patch, or null when cancelled.</summary>
    Task<RecipientPatch?> EditRecipientsAsync(RecipientFormViewModel form);

    /// <summary>Shows the schedule form; true when applied.</summary>
    Task<bool> EditScheduleAsync(ScheduleFormViewModel form);

    /// <summary>Shows the rule builder; the new rule, or null when cancelled.</summary>
    Task<NewRule?> NewRuleAsync(NewRuleViewModel form);
}

/// <summary>
/// Page "Action Rule Editor". <see cref="Activate"/> reads the action rules of every device once (over the server) and
/// watches the pushed rows; Refresh reads again. The upper grid groups the rules (what they do, name, action, trigger);
/// selecting groups lists their rules per device below, where a selection narrows the target further. Edit, Turn on,
/// Turn off and Delete start one task per device for the target rules (tasks pane below); Undo puts a run back. The tools
/// follow the role live ("manage", "delete"); the server checks again.
/// </summary>
public sealed partial class ActionRulesViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<string> GroupByNames = ["What it does", "Name", "Action", "Trigger"];

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<Guid, DeviceRulesRow> _rows = [];
    private readonly Dictionary<string, TemplateSet> _templates = new(StringComparer.Ordinal);
    private List<RuleGroupRow> _allGroups = [];
    private IReadOnlyList<RuleGroupRow> _selectedGroups = [];
    private IReadOnlyList<RuleMemberRow> _selectedMembers = [];
    private CancellationTokenSource? _active;
    private RulesState? _state;

    public ActionRulesViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.PermissionsChanged += OnPermissionsChanged;
        Recipients = new PresetTabViewModel(this, schedules: false);
        Schedules = new PresetTabViewModel(this, schedules: true);
    }

    /// <summary>The Recipients tab.</summary>
    public PresetTabViewModel Recipients { get; }

    /// <summary>The Schedules tab.</summary>
    public PresetTabViewModel Schedules { get; }

    /// <summary>0 Rules, 1 Recipients, 2 Schedules.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesTab), nameof(IsRecipientsTab), nameof(IsSchedulesTab), nameof(SearchPlaceholder))]
    public partial int TabIndex { get; set; }

    public bool IsRulesTab => TabIndex == 0;

    public bool IsRecipientsTab => TabIndex == 1;

    public bool IsSchedulesTab => TabIndex == 2;

    public string SearchPlaceholder => TabIndex switch { 1 => "Search recipients", 2 => "Search schedules", _ => "Search rules" };

    partial void OnTabIndexChanged(int value) => Filter();

    [RelayCommand]
    private void ShowTab(string index) => TabIndex = int.Parse(index, CultureInfo.InvariantCulture);

    /// <summary>The devices of the client (the device picker lists them).</summary>
    public IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> AllDevices => _ctx.Devices;

    /// <summary>Why a device cannot get a copy: its rules were not read.</summary>
    public string? CopyUnavailable(Guid deviceId, Guid source) =>
        deviceId == source ? "This is the device the copy comes from."
        : _rows.TryGetValue(deviceId, out var row) && row.State == RowStates.Ok ? null
        : "Its rules were not read. Refresh first.";

    /// <summary>How often the page asks for the state while a read runs (its progress events may be missed).</summary>
    public static TimeSpan StatePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait before watching the events again after the stream broke (tests shorten it).</summary>
    public static TimeSpan ReconnectDelay { get; set; } = PluginEventWatch.DefaultRetryDelay;

    /// <summary>Set by the view; null in tests that do not open dialogs.</summary>
    public IRuleDialogs? Dialogs { get; set; }

    public RangeObservableCollection<RuleGroupRow> Groups { get; } = [];

    public RangeObservableCollection<RuleMemberRow> Members { get; } = [];

    [ObservableProperty]
    public partial int GroupByIndex { get; set; }

    partial void OnGroupByIndexChanged(int value) => Rebuild();

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>The rules tab's Copy: exactly one rule.</summary>
    public string? CopyBlockedReason => Targets.Count == 1 ? null : "Select one rule on the right to copy.";

    private bool CanCopy => MayManage && CopyBlockedReason is null;

    /// <summary>Copies the target rule to chosen devices, with the edit form for what the copies get.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        var source = Targets[0];
        if (Dialogs is null || await Dialogs.ChooseDevicesAsync("Copy the rule to", id => CopyUnavailable(id, source.Device.DeviceId)).ConfigureAwait(true) is not { Count: > 0 } devices)
        {
            return;
        }

        try
        {
            var ct = _active?.Token ?? CancellationToken.None;
            var rows = devices.Select(d => _rows.GetValueOrDefault(d)).OfType<DeviceRulesRow>().Append(source.Device).ToList();
            var sets = await TemplatesAsync(rows, ct).ConfigureAwait(true);
            var events = await EventsAsync(devices, ct).ConfigureAwait(true);
            var form = new EditRulesViewModel([source], sets, devices, events, rows.DistinctBy(r => r.DeviceId).ToDictionary(r => r.DeviceId), () => NewScheduleFormAsync(devices.Count));
            if (await Dialogs.EditAsync(form).ConfigureAwait(true) is { } patch)
            {
                await InvokeRunAsync(ActionRulesMethods.Deploy, new DeployRequest(source.Device.DeviceId, source.Rule.Id, source.Rule.Version, devices, patch.IsEmpty ? null : patch)).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    /// <summary>Why a device cannot get a new rule: its rules were not read.</summary>
    public string? NewRuleUnavailable(Guid deviceId) =>
        _rows.TryGetValue(deviceId, out var row) && row.State == RowStates.Ok ? null : "Its rules were not read. Refresh first.";

    /// <summary>The rule builder: choose the devices, then the rule; one task per device creates it.</summary>
    [RelayCommand(CanExecute = nameof(MayManage))]
    private async Task NewRuleAsync()
    {
        if (Dialogs is null || await Dialogs.ChooseDevicesAsync("Create the rule on", NewRuleUnavailable).ConfigureAwait(true) is not { Count: > 0 } devices)
        {
            return;
        }

        try
        {
            var ct = _active?.Token ?? CancellationToken.None;
            var rows = devices.Select(d => _rows.GetValueOrDefault(d)).OfType<DeviceRulesRow>().ToList();
            var sets = await TemplatesAsync(rows, ct).ConfigureAwait(true);
            var events = await EventsAsync(devices, ct).ConfigureAwait(true);
            var form = new NewRuleViewModel(devices, sets, events, rows.ToDictionary(r => r.DeviceId), () => NewScheduleFormAsync(devices.Count));
            if (await Dialogs.NewRuleAsync(form).ConfigureAwait(true) is { } rule)
            {
                await InvokeRunAsync(ActionRulesMethods.Build, new BuildRequest(devices, rule)).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    /// <summary>The events the devices offered when they were read, merged by the server.</summary>
    private async Task<IReadOnlyList<OfferedEvent>> EventsAsync(IReadOnlyList<Guid> devices, CancellationToken ct) =>
        ActionRulesJson.Deserialize<EventsReply>(await _ctx.InvokeAsync(ActionRulesMethods.Events, ActionRulesJson.Serialize(new EventsRequest(devices)), ct).ConfigureAwait(true)).Events;

    private async Task<SchedulePatch?> NewScheduleFormAsync(int devices)
    {
        var form = ScheduleFormViewModel.ForNew(devices);
        return Dialogs is not null && await Dialogs.EditScheduleAsync(form).ConfigureAwait(true) ? form.Patch() : null;
    }

    internal async Task EditRecipientsAsync(IReadOnlyList<RecipientMember> targets)
    {
        if (Dialogs is not null && targets.Count > 0 && await Dialogs.EditRecipientsAsync(new RecipientFormViewModel(targets)).ConfigureAwait(true) is { } patch)
        {
            await InvokeRunAsync(ActionRulesMethods.RecipientEdit, new RecipientRunRequest(Refs(targets.Select(t => (t.Device.DeviceId, t.Recipient.Id, t.Recipient.Version))), patch)).ConfigureAwait(true);
        }
    }

    internal async Task DeleteRecipientsAsync(IReadOnlyList<RecipientMember> targets)
    {
        if (targets.Count > 0 && await _ctx.ConfirmAsync("Delete recipients", $"Delete {Count(targets.Count, "recipient")} on {Count(targets.Select(t => t.Device.DeviceId).Distinct().Count(), "device")}? Rules that send to them keep their settings. Undo can create them again for 30 days.", "Delete").ConfigureAwait(true))
        {
            await InvokeRunAsync(ActionRulesMethods.RecipientDelete, new RecipientRunRequest(Refs(targets.Select(t => (t.Device.DeviceId, t.Recipient.Id, t.Recipient.Version))), null)).ConfigureAwait(true);
        }
    }

    internal async Task CopyRecipientAsync(RecipientMember source)
    {
        if (Dialogs is not null && await Dialogs.ChooseDevicesAsync("Copy the recipient to", id => CopyUnavailable(id, source.Device.DeviceId)).ConfigureAwait(true) is { Count: > 0 } devices)
        {
            await InvokeRunAsync(ActionRulesMethods.RecipientDeploy, new DeployRequest(source.Device.DeviceId, source.Recipient.Id, source.Recipient.Version, devices)).ConfigureAwait(true);
        }
    }

    internal async Task EditSchedulesAsync(IReadOnlyList<ScheduleMember> targets)
    {
        var form = targets.Count > 0 ? ScheduleFormViewModel.ForEdit(targets) : null;
        if (Dialogs is not null && form is not null && await Dialogs.EditScheduleAsync(form).ConfigureAwait(true))
        {
            await InvokeRunAsync(ActionRulesMethods.ScheduleEdit, new ScheduleRunRequest(Refs(targets.Select(t => (t.Device.DeviceId, t.Schedule.Id, t.Schedule.Version))), form.Patch())).ConfigureAwait(true);
        }
    }

    internal async Task DeleteSchedulesAsync(IReadOnlyList<ScheduleMember> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        // The rules that use the schedules, as the page read them: deleted with them, each only at that version.
        var rules = targets.SelectMany(t => t.Device.Rules
            .Where(r => r.Conditions.Prepend(r.StartEvent).OfType<Oadm.Plugins.ActionEngine.EventFilter>().Any(f => RuleText.ScheduleId(f) == t.Schedule.Id))
            .Select(r => (t.Device.DeviceId, Rule: r)))
            .DistinctBy(x => (x.DeviceId, x.Rule.Id))
            .ToList();
        var names = rules.Select(r => r.Rule.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var message = $"Delete {Count(targets.Count, "schedule")} on {Count(targets.Select(t => t.Device.DeviceId).Distinct().Count(), "device")}?"
            + (rules.Count == 0 ? string.Empty
                : $" {Count(rules.Count, "rule")} use them and are deleted too: " + string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", …" : string.Empty) + ".")
            + " Undo can create them again for 30 days.";
        if (await _ctx.ConfirmAsync("Delete schedules", message, "Delete").ConfigureAwait(true))
        {
            await InvokeRunAsync(ActionRulesMethods.ScheduleDelete, new ScheduleRunRequest(
                Refs(targets.Select(t => (t.Device.DeviceId, t.Schedule.Id, t.Schedule.Version))),
                null,
                rules.Count == 0 ? null : Refs(rules.Select(r => (r.DeviceId, r.Rule.Id, r.Rule.Version))))).ConfigureAwait(true);
        }
    }

    internal async Task CopyScheduleAsync(ScheduleMember source)
    {
        if (Dialogs is not null && await Dialogs.ChooseDevicesAsync("Copy the schedule to", id => CopyUnavailable(id, source.Device.DeviceId)).ConfigureAwait(true) is { Count: > 0 } devices)
        {
            await InvokeRunAsync(ActionRulesMethods.ScheduleDeploy, new ScheduleDeployRequest(devices, null, source.Device.DeviceId, source.Schedule.Id, source.Schedule.Version)).ConfigureAwait(true);
        }
    }

    /// <summary>New recipient: the devices, then its kind, name and settings; each device gets it by one task.</summary>
    internal async Task NewRecipientAsync()
    {
        if (Dialogs is null || await Dialogs.ChooseDevicesAsync("Add the recipient to", id => CopyUnavailable(id, Guid.Empty)).ConfigureAwait(true) is not { Count: > 0 } devices)
        {
            return;
        }

        try
        {
            var rows = devices.Select(d => _rows.GetValueOrDefault(d)).OfType<DeviceRulesRow>().ToList();
            var sets = await TemplatesAsync(rows, _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            var form = RecipientFormViewModel.ForNew(devices, sets);
            if (await Dialogs.EditRecipientsAsync(form).ConfigureAwait(true) is { } patch && form.SelectedKind is { } kind)
            {
                await InvokeRunAsync(ActionRulesMethods.RecipientNew, new NewRecipientRequest(devices, kind.Token, patch.Name ?? string.Empty,
                    patch.Parameters ?? new Dictionary<string, string>())).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    internal async Task NewScheduleAsync()
    {
        if (Dialogs is null || await Dialogs.ChooseDevicesAsync("Add the schedule to", id => CopyUnavailable(id, Guid.Empty)).ConfigureAwait(true) is not { Count: > 0 } devices)
        {
            return;
        }

        var form = ScheduleFormViewModel.ForNew(devices.Count);
        if (await Dialogs.EditScheduleAsync(form).ConfigureAwait(true))
        {
            await InvokeRunAsync(ActionRulesMethods.ScheduleDeploy, new ScheduleDeployRequest(devices, form.Patch())).ConfigureAwait(true);
        }
    }

    private static List<DeviceTargets> Refs(IEnumerable<(Guid Device, string Id, string Version)> items) =>
        [.. items.GroupBy(i => i.Device).Select(g => new DeviceTargets(g.Key, [.. g.Select(i => new RuleRef(i.Id, i.Version))]))];

    private static string Count(int n, string what) => n == 1 ? "1 " + what : string.Create(CultureInfo.CurrentCulture, $"{n:N0} {what}s");

    /// <summary>Starts a run; its progress shows in the tasks pane, each device is read again when its task ends.</summary>
    private async Task InvokeRunAsync(string method, object request)
    {
        try
        {
            await _ctx.InvokeAsync(method, ActionRulesJson.Serialize(request), _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty] public partial double ProgressValue { get; private set; }

    [ObservableProperty] public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; private set; } = "Reading the action rules.";

    public bool ShowEmpty => Groups.Count == 0;

    [ObservableProperty] public partial string MembersTitle { get; private set; } = "Rules";

    [ObservableProperty] public partial string MembersEmptyText { get; private set; } = "Select a group to see its rules on each device.";

    public bool ShowMembersEmpty => Members.Count == 0;

    /// <summary>Change, turn on or off and undo.</summary>
    public bool MayManage => _ctx.Has(PermissionKeys.Of(ActionRulesInfo.PluginId, ActionRulesMethods.ManagePermission));

    public bool MayDelete => _ctx.Has(PermissionKeys.Of(ActionRulesInfo.PluginId, ActionRulesMethods.DeletePermission));

    /// <summary>The rules the tools work on: the rules selected below, else every rule of the selected groups.</summary>
    public IReadOnlyList<RuleMember> Targets =>
        _selectedMembers.Any(m => m.Member is not null)
            ? [.. _selectedMembers.Select(m => m.Member).OfType<RuleMember>()]
            : [.. _selectedGroups.Where(g => g.Group is not null).SelectMany(g => g.Group!.Members)];

    /// <summary>Why the tools are disabled (tooltip), or null.</summary>
    public string? ToolsBlockedReason => Targets.Count > 0 ? null : "Select rules first.";

    /// <summary>"Edit 3 rules on 2 devices".</summary>
    public string TargetText => Describe(Targets);

    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = _ctx.RunAsync(HandleEvent, ReconnectDelay, token);
        _ = ReadAsync(token);
    }

    public void Deactivate()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Cancel();
        active.Dispose();
    }

    public void Dispose()
    {
        Deactivate();
        _ctx.PermissionsChanged -= OnPermissionsChanged;
    }

    /// <summary>The upper grid's selection (the view hands it over on every change).</summary>
    public void SetGroupSelection(IEnumerable<RuleGroupRow> selected)
    {
        _selectedGroups = [.. selected];
        _selectedMembers = [];
        var members = new List<RuleMemberRow>();
        foreach (var group in _selectedGroups)
        {
            if (group.Group is { } g)
            {
                members.AddRange(g.Members.Select(m => new RuleMemberRow(m)));
            }
            else
            {
                members.AddRange(group.Problems.Select(p => new RuleMemberRow(p)));
            }
        }

        Members.ReplaceAll(members.OrderBy(m => m.AddressSortKey, StringComparer.Ordinal).ThenBy(m => m.RuleName, StringComparer.CurrentCulture));
        MembersTitle = _selectedGroups.Count == 0 ? "Rules" : string.Create(CultureInfo.CurrentCulture, $"Rules ({Members.Count:N0})");
        MembersEmptyText = _selectedGroups.Count == 0 ? "Select a group to see its rules on each device." : "No rules.";
        OnPropertyChanged(nameof(ShowMembersEmpty));
        NotifyTargets();
    }

    /// <summary>The lower grid's selection: narrows the target to these rules.</summary>
    public void SetMemberSelection(IEnumerable<RuleMemberRow> selected)
    {
        _selectedMembers = [.. selected];
        NotifyTargets();
    }

    private bool CanRefresh => !IsReading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReadAsync(_active?.Token ?? CancellationToken.None);

    private bool CanManage => MayManage && Targets.Count > 0;

    private bool CanDelete => MayDelete && Targets.Count > 0;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task EditAsync()
    {
        var targets = Targets;
        if (Dialogs is null || targets.Count == 0)
        {
            return;
        }

        try
        {
            var ct = _active?.Token ?? CancellationToken.None;
            var sets = await TemplatesAsync(targets, ct).ConfigureAwait(true);
            var devices = targets.Select(t => t.Device).DistinctBy(d => d.DeviceId).ToList();
            var events = await EventsAsync([.. devices.Select(d => d.DeviceId)], ct).ConfigureAwait(true);
            var form = new EditRulesViewModel(targets, sets, null, events, devices.ToDictionary(d => d.DeviceId), () => NewScheduleFormAsync(devices.Count));
            if (await Dialogs.EditAsync(form).ConfigureAwait(true) is { } patch)
            {
                await RunAsync(ActionRulesMethods.Edit, targets, patch).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task TurnOnAsync() => SetEnabledAsync(true);

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task TurnOffAsync() => SetEnabledAsync(false);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var targets = Targets;
        if (await _ctx.ConfirmAsync("Delete action rules", $"Delete {Describe(targets)}? Undo can create them again for 30 days.", "Delete").ConfigureAwait(true))
        {
            await RunAsync(ActionRulesMethods.Delete, targets, null).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(MayManage))]
    private async Task UndoAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        try
        {
            var ct = _active?.Token ?? CancellationToken.None;
            var runs = ActionRulesJson.Deserialize<List<UndoRun>>(await _ctx.InvokeAsync(ActionRulesMethods.UndoRuns, null, ct).ConfigureAwait(true));
            if (await Dialogs.ChooseUndoAsync(new UndoViewModel(runs)).ConfigureAwait(true) is not { } runId)
            {
                return;
            }

            var run = runs.First(r => r.RunId == runId);
            if (await _ctx.ConfirmAsync("Undo", $"Undo \"{run.Description}\"? What it changed is put back where it is still as the change left it.", "Undo").ConfigureAwait(true))
            {
                await _ctx.InvokeAsync(ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(runId)), ct).ConfigureAwait(true);
                ErrorText = null;
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        var targets = Targets.Where(t => t.Rule.Enabled != enabled).ToList();
        if (targets.Count == 0)
        {
            ErrorText = enabled ? "The selected rules are on already." : "The selected rules are off already.";
            return;
        }

        await RunAsync(ActionRulesMethods.Edit, targets, new RulePatch { Enabled = enabled }).ConfigureAwait(true);
    }

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Topic)
        {
            case ActionRulesMethods.RowsTopic:
                var rows = ActionRulesJson.Deserialize<RowsEvent>(item.PayloadJson);
                foreach (var row in rows.Rows ?? [])
                {
                    _rows[row.DeviceId] = row;
                }

                foreach (var id in rows.Removed ?? [])
                {
                    _rows.Remove(id);
                }

                Rebuild();
                break;
            case ActionRulesMethods.StateTopic:
                ApplyState(ActionRulesJson.Deserialize<RulesState>(item.PayloadJson), replaceRows: false);
                break;
        }
    }

    /// <summary>A full state (read / getState reply), or the progress only.</summary>
    public void ApplyState(RulesState state, bool replaceRows)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        if (replaceRows)
        {
            _rows.Clear();
            foreach (var row in state.Rows ?? [])
            {
                _rows[row.DeviceId] = row;
            }

            Rebuild();
        }

        IsReading = state.Reading;
        ProgressValue = state.Total == 0 ? 0 : 100.0 * state.Done / state.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Reading action rules {state.Done:N0} of {state.Total:N0}");
        UpdateTexts();
    }

    /// <summary>Groups again (rows, grouping or search changed) and keeps the selected groups that still exist.</summary>
    private void Rebuild()
    {
        var by = (RuleGroupBy)Math.Clamp(GroupByIndex, 0, GroupByNames.Count - 1);
        var ok = _rows.Values.Where(r => r.State == RowStates.Ok).ToList();
        var problems = _rows.Values.Where(r => r.State != RowStates.Ok).OrderBy(r => r.Address, StringComparer.Ordinal).ToList();
        var groups = RuleGrouping.Build(ok, by).Select(RuleGroupRow.For).ToList();
        if (problems.Count > 0)
        {
            groups.Insert(0, RuleGroupRow.NotRead(problems));
        }

        _allGroups = groups;
        Recipients.Update(ok, IsRecipientsTab ? SearchText : string.Empty);
        Schedules.Update(ok, IsSchedulesTab ? SearchText : string.Empty);
        Filter();
    }

    private void Filter()
    {
        Recipients.Filter(IsRecipientsTab ? SearchText : string.Empty);
        Schedules.Filter(IsSchedulesTab ? SearchText : string.Empty);
        var search = IsRulesTab ? SearchText.Trim() : string.Empty;
        List<RuleGroupRow> shown = search.Length == 0
            ? [.. _allGroups]
            : [.. _allGroups.Where(g => g.SearchText.Contains(search, StringComparison.CurrentCultureIgnoreCase))];
        var keys = _selectedGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        Groups.ReplaceAll(shown);
        OnPropertyChanged(nameof(ShowEmpty));
        SelectionRestored?.Invoke(this, [.. shown.Where(g => keys.Contains(g.Key))]);
        SetGroupSelection(shown.Where(g => keys.Contains(g.Key)));
        UpdateTexts();
    }

    /// <summary>The groups that stay selected after a rebuild (the view selects them again).</summary>
    public event EventHandler<IReadOnlyList<RuleGroupRow>>? SelectionRestored;

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(ActionRulesMethods.Read, null, ct).ConfigureAwait(true);
            ApplyState(ActionRulesJson.Deserialize<RulesState>(json), replaceRows: true);
            ErrorText = null;
            // The rows come as events; the page asks only for the progress while the read runs (an event sent before the
            // watch connected is lost) and takes every row once at the end.
            while (IsReading)
            {
                await Task.Delay(StatePollInterval, ct).ConfigureAwait(true);
                json = await _ctx.InvokeAsync(ActionRulesMethods.GetProgress, null, ct).ConfigureAwait(true);
                ApplyState(ActionRulesJson.Deserialize<RulesState>(json), replaceRows: false);
            }

            json = await _ctx.InvokeAsync(ActionRulesMethods.GetState, null, ct).ConfigureAwait(true);
            ApplyState(ActionRulesJson.Deserialize<RulesState>(json), replaceRows: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
            IsReading = false;
            UpdateTexts();
        }
    }

    /// <summary>The template sets of the target devices (asked once per set).</summary>
    private Task<IReadOnlyDictionary<Guid, TemplateSet>> TemplatesAsync(IReadOnlyList<RuleMember> targets, CancellationToken ct) =>
        TemplatesAsync([.. targets.Select(t => t.Device)], ct);

    private async Task<IReadOnlyDictionary<Guid, TemplateSet>> TemplatesAsync(IReadOnlyList<DeviceRulesRow> devices, CancellationToken ct)
    {
        var missing = devices.Select(d => d.TemplateSetId).OfType<string>().Distinct(StringComparer.Ordinal).Where(id => !_templates.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var json = await _ctx.InvokeAsync(ActionRulesMethods.Templates, ActionRulesJson.Serialize(new TemplatesRequest(missing)), ct).ConfigureAwait(true);
            foreach (var set in ActionRulesJson.Deserialize<List<TemplateSet>>(json))
            {
                _templates[set.Id] = set;
            }
        }

        var result = new Dictionary<Guid, TemplateSet>();
        foreach (var device in devices.DistinctBy(d => d.DeviceId))
        {
            if (device.TemplateSetId is { } id && _templates.TryGetValue(id, out var set))
            {
                result[device.DeviceId] = set;
            }
        }

        return result;
    }

    /// <summary>Starts the tasks; their progress shows in the tasks pane, each device is read again when its task ends.</summary>
    private async Task RunAsync(string method, IReadOnlyList<RuleMember> targets, RulePatch? patch)
    {
        var request = new RunRequest(
            [.. targets.GroupBy(t => t.Device.DeviceId).Select(g => new DeviceTargets(g.Key, [.. g.Select(m => new RuleRef(m.Rule.Id, m.Rule.Version))]))],
            patch);
        try
        {
            await _ctx.InvokeAsync(method, ActionRulesJson.Serialize(request), _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(MayDelete));
        UndoCommand.NotifyCanExecuteChanged();
        NewRuleCommand.NotifyCanExecuteChanged();
        NotifyTargets();
        Recipients.NotifyTargets();
        Schedules.NotifyTargets();
    }

    private void NotifyTargets()
    {
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(ToolsBlockedReason));
        OnPropertyChanged(nameof(TargetText));
        EditCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CopyBlockedReason));
    }

    private void UpdateTexts()
    {
        var ok = _rows.Values.Count(r => r.State == RowStates.Ok);
        var notRead = _rows.Count - ok;
        var rules = _rows.Values.Sum(r => r.Rules.Count);
        SummaryText = string.Create(CultureInfo.CurrentCulture, $"{ok:N0} devices · {rules:N0} rules")
            + (notRead > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {notRead:N0} not read") : string.Empty);
        EmptyText = HasError && _rows.Count == 0 ? string.Empty
            : _state is null || _state.Reading ? "Reading the action rules."
            : _rows.Count == 0 ? "No devices yet."
            : _allGroups.Count == 0 ? "The devices have no action rules."
            : "No rule matches the search.";
    }

    /// <summary>"1 rule on 1 device", "3 rules on 2 devices".</summary>
    public static string Describe(IReadOnlyCollection<RuleMember> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var devices = targets.Select(t => t.Device.DeviceId).Distinct().Count();
        return (targets.Count == 1 ? "1 rule" : string.Create(CultureInfo.CurrentCulture, $"{targets.Count:N0} rules"))
            + (devices == 1 ? " on 1 device" : string.Create(CultureInfo.CurrentCulture, $" on {devices:N0} devices"));
    }
}
