using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.EventRules;

/// <summary>A rule in the Rules tab, updated in place by id.</summary>
public sealed partial class RuleRowViewModel(EventRulesViewModel owner, RuleSummary summary) : ObservableObject
{
    private bool _applying;

    public string Id { get; } = summary.Id;

    [ObservableProperty] public partial RuleSummary Summary { get; private set; } = summary;

    [ObservableProperty] public partial bool Enabled { get; set; } = summary.Enabled;

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    public string Name => Summary.Name;

    public string ConditionsText => Summary.ConditionsText;

    public string ActionsText => Summary.ActionsText;

    public string StateText => Summary.Problem.Length > 0 ? Summary.Problem : Summary.StateText;

    public bool IsOn => Summary.Enabled && Summary.ActiveCount > 0 && Summary.Problem.Length == 0;

    public bool HasProblem => Summary.Problem.Length > 0 && Summary.Enabled;

    public bool IsWaiting => Summary.StateText.StartsWith("Waiting", StringComparison.Ordinal);

    /// <summary>Local time "2026-10-10 14:03", empty before the first run.</summary>
    public string LastFiredText => Summary.LastFired is { } fired
        ? fired.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : string.Empty;

    public DateTime LastFiredSortKey => Summary.LastFired?.ToDateTime() ?? DateTime.MinValue;

    public bool CanToggle => owner.MayChange && !IsBusy;

    public string SearchText => $"{Name} {ConditionsText} {ActionsText}";

    public void Apply(RuleSummary summary)
    {
        Summary = summary;
        _applying = true;
        Enabled = summary.Enabled;
        _applying = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ConditionsText));
        OnPropertyChanged(nameof(ActionsText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(LastFiredText));
    }

    public void NotifyRole() => OnPropertyChanged(nameof(CanToggle));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    partial void OnEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _ = owner.SetEnabledAsync(this, value);
        }
    }

    /// <summary>Undoes a refused change without calling the server again.</summary>
    internal void Revert()
    {
        _applying = true;
        Enabled = Summary.Enabled;
        _applying = false;
    }

    internal void SetBusy(bool busy) => IsBusy = busy;
}

/// <summary>A recipient in the Recipients list; a persistent one with a live connection status once a rule uses it.</summary>
public sealed partial class RecipientRowViewModel(RecipientEntry entry, string typeText, bool persistent = false) : ObservableObject
{
    public RecipientEntry Entry { get; } = entry;

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string TypeText { get; } = typeText;

    public string Target => Entry.Target;

    /// <summary>The server's status; null: not used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(StatusText), nameof(IsConnected), nameof(IsConnecting), nameof(IsNotConnected), nameof(StatusTip), nameof(StatusWordTip))]
    public partial RecipientStatusEntry? Status { get; internal set; }

    private RecipientConnection Connection => persistent ? Status?.Connection ?? RecipientConnection.RecipientNotUsed : RecipientConnection.RecipientNotUsed;

    /// <summary>One shot recipients and recipients no rule uses show nothing.</summary>
    public bool HasStatus => Connection != RecipientConnection.RecipientNotUsed;

    public string? StatusText => Connection switch
    {
        RecipientConnection.RecipientConnected => Oadm.Sdk.Actions.RecipientSessionTexts.Connected,
        RecipientConnection.RecipientConnecting => Oadm.Sdk.Actions.RecipientSessionTexts.Connecting,
        RecipientConnection.RecipientNotConnected => Oadm.Sdk.Actions.RecipientSessionTexts.NotConnected(null),
        _ => null,
    };

    public bool IsConnected => Connection == RecipientConnection.RecipientConnected;

    public bool IsConnecting => Connection == RecipientConnection.RecipientConnecting;

    public bool IsNotConnected => Connection == RecipientConnection.RecipientNotConnected;

    /// <summary>Why it is not connected.</summary>
    public string? StatusTip => IsNotConnected && Status is { Reason.Length: > 0 } status ? status.Reason : null;

    /// <summary>The word and the reason, for a cell too narrow for the word.</summary>
    public string? StatusWordTip => IsNotConnected ? Oadm.Sdk.Actions.RecipientSessionTexts.NotConnected(StatusTip) : StatusText;

    public bool Matches(string text) =>
        Name.Contains(text, StringComparison.OrdinalIgnoreCase) || TypeText.Contains(text, StringComparison.OrdinalIgnoreCase) || Target.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line of the event log tab.</summary>
public sealed class EventLogRowViewModel
{
    public EventLogRowViewModel(EventLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Id = entry.Id;
        var local = entry.Time.ToDateTime().ToLocalTime();
        TimeText = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        What = entry.What;
        Device = entry.Device;
        Rule = entry.Rule;
        RuleId = entry.RuleId;
        Message = entry.Message;
        KindText = entry.Kind switch
        {
            EventLogKind.EventLogEvent => "Event",
            EventLogKind.EventLogRule => "Rule",
            EventLogKind.EventLogAction => "Action",
            _ => "Server",
        };
        ResultText = entry.Result switch
        {
            EventLogResult.EventLogOk => "Done",
            EventLogResult.EventLogFailed => "Failed",
            EventLogResult.EventLogSkipped => "Skipped",
            _ => string.Empty,
        };
        IsOk = entry.Result == EventLogResult.EventLogOk;
        IsError = entry.Result == EventLogResult.EventLogFailed;
        IsWarning = entry.Result == EventLogResult.EventLogSkipped;
        IsInfo = entry.Result == EventLogResult.EventLogInfo;
        _search = string.Join(' ', What, Device, Rule, Message, KindText, ResultText);
    }

    private readonly string _search;

    public long Id { get; }

    public string TimeText { get; }

    public string KindText { get; }

    public string What { get; }

    public string Device { get; }

    public string Rule { get; }

    public string RuleId { get; }

    public string Message { get; }

    public string ResultText { get; }

    /// <summary>Something happened (an event, a rule turned on or off): the blue info icon.</summary>
    public bool IsInfo { get; }

    public bool IsOk { get; }

    public bool IsError { get; }

    public bool IsWarning { get; }

    public bool Matches(string text) => _search.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Rail page "Event rules" (Automation): Rules (every rule with its live state; administrators create, change, copy, turn
/// on / off and delete), Recipients (the shared targets of the rules' actions) and the Event log (newest first, live,
/// searchable). Streams run while the page is shown (<see cref="Activate"/> / <see cref="Deactivate"/>).
/// </summary>
public sealed partial class EventRulesViewModel : ObservableObject, IDisposable
{
    public const int LogLimit = 10_000;

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly DeviceStore _devices;
    private readonly UserSession? _session;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, RuleRowViewModel> _rulesById = [];
    private readonly List<EventLogRowViewModel> _log = [];
    private readonly Dictionary<string, RecipientStatusEntry> _recipientStatus = new(StringComparer.Ordinal);
    private List<RecipientRowViewModel> _recipients = [];
    private string? _ruleLinks;
    private CancellationTokenSource? _active;

    public EventRulesViewModel(IOadmApi api, IDialogService dialogs, DeviceStore devices, IUiDispatcher ui, UserSession? session = null, TimeProvider? time = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _session = session;
        _time = time ?? TimeProvider.System;
        if (session is not null)
        {
            session.PropertyChanged += OnSessionChanged;
            session.PermissionsChanged += OnPermissionsChanged;
        }
    }

    public RangeObservableCollection<RuleRowViewModel> Rules { get; } = [];

    public RangeObservableCollection<EventLogRowViewModel> LogEntries { get; } = [];

    public RangeObservableCollection<RecipientRowViewModel> Recipients { get; } = [];

    /// <summary>The role may change event rules ("events.manage"; without a session, in tests: yes).</summary>
    public bool MayChange => _session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsManage) ?? true;

    /// <summary>The role may delete the event log ("events.clearLog").</summary>
    public bool MayClearLog => (_session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsClearLog) ?? true) && (_session?.HasAllDevices ?? true);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DuplicateCommand), nameof(DeleteCommand))]
    public partial RuleRowViewModel? SelectedRule { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditRecipientCommand), nameof(DeleteRecipientCommand))]
    public partial RecipientRowViewModel? SelectedRecipient { get; set; }

    [ObservableProperty] public partial string RecipientSearchText { get; set; } = string.Empty;

    /// <summary>A refused recipient action or a list that could not be read, directly below the recipients list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipientsError))]
    public partial string? RecipientsErrorText { get; private set; }

    public bool HasRecipientsError => !string.IsNullOrEmpty(RecipientsErrorText);

    /// <summary>The role may see event rules ("events.view"), so also their recipients.</summary>
    public bool ShowRecipients => _session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsView) ?? true;

    public bool ShowRecipientsEmpty => Recipients.Count == 0;

    public string RecipientsEmptyText => _recipients.Count == 0
        ? MayChange ? "Create a recipient, then choose it in a rule." : "No recipients yet."
        : "No recipient matches the search.";

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial string LogSearchText { get; set; } = string.Empty;

    /// <summary>A refused rule action (open, copy, delete, turn on / off), directly below the rules list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRulesError))]
    public partial string? RulesErrorText { get; private set; }

    public bool HasRulesError => !string.IsNullOrEmpty(RulesErrorText);

    /// <summary>The event log could not be read, directly below the log.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogError))]
    public partial string? LogErrorText { get; private set; }

    public bool HasLogError => !string.IsNullOrEmpty(LogErrorText);

    [ObservableProperty] public partial string RulesStatus { get; private set; } = string.Empty;

    [ObservableProperty] public partial string LogStatus { get; private set; } = string.Empty;

    public bool ShowRulesEmpty => Rules.Count == 0;

    public string RulesEmptyText => _rulesById.Count == 0
        ? MayChange ? "No event rules yet. Click New rule." : "No event rules yet."
        : "No rule matches the search.";

    public string ReadOnlyHint => MayChange ? string.Empty : "Your role does not allow changing event rules.";

    partial void OnSearchTextChanged(string value) => FilterRules();

    partial void OnLogSearchTextChanged(string value) => FilterLog();

    partial void OnRecipientSearchTextChanged(string value) => FilterRecipients();

    /// <summary>The page is shown: load and watch the rules and the log.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = WatchRulesAsync(token);
        _ = WatchLogAsync(token);
        _ = LoadRecipientsAsync(token);
        _ = WatchRecipientStatusAsync(token);
    }

    public void Dispose() => Deactivate();

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
            ApplyRecipientStatus([]); // a status of a page that does not watch would be a guess
        }
    }

    private bool CanChange => MayChange;

    private bool CanChangeSelected => MayChange && SelectedRule is not null;

    private bool CanChangeSelectedRecipient => MayChange && SelectedRecipient is not null;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task NewRuleAsync()
    {
        await EditRuleAsync(await CreateEditorAsync(null).ConfigureAwait(true)).ConfigureAwait(true);
    }

    /// <summary>The rule dialog; its New beside a recipient saves at once, so the list is read also after Cancel.</summary>
    private async Task EditRuleAsync(RuleEditorViewModel? editor)
    {
        if (editor is null)
        {
            return;
        }

        if (await _dialogs.ShowRuleEditorAsync(editor).ConfigureAwait(true))
        {
            ApplySummary(editor.Saved!.Summary);
        }

        await LoadRecipientsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task NewRecipientAsync() => EditRecipientAsync(null, null);

    [RelayCommand(CanExecute = nameof(CanChangeSelectedRecipient))]
    private Task EditRecipientAsync(RecipientRowViewModel? row) => EditRecipientAsync(row ?? SelectedRecipient, row ?? SelectedRecipient);

    /// <summary>The recipient dialog for a new recipient or for <paramref name="row"/>; the list follows a save.</summary>
    private async Task EditRecipientAsync(RecipientRowViewModel? row, RecipientRowViewModel? keepSelected)
    {
        RecipientEditorViewModel editor;
        try
        {
            var catalog = await _api.GetEventCatalogAsync(CancellationToken.None).ConfigureAwait(true);
            editor = new RecipientEditorViewModel(_api, catalog.RecipientTypes, row?.Entry, [.. _recipients.Where(r => r != row).Select(r => r.Name)]);
            RecipientsErrorText = null;
        }
        catch (RpcException ex)
        {
            RecipientsErrorText = "The recipient could not be opened: " + ex.Status.Detail;
            return;
        }

        if (await _dialogs.ShowRecipientEditorAsync(editor).ConfigureAwait(true))
        {
            await LoadRecipientsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            SelectedRecipient = _recipients.Find(r => r.Id == (editor.Saved?.Id ?? keepSelected?.Id));
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelectedRecipient))]
    private async Task DeleteRecipientAsync(RecipientRowViewModel? row)
    {
        row ??= SelectedRecipient;
        if (row is null || !await _dialogs.ConfirmAsync("Delete recipient", $"Delete the recipient \"{row.Name}\"?", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteRecipientAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            RecipientsErrorText = null;
            await LoadRecipientsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            RecipientsErrorText = ex.Status.Detail;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task EditAsync(RuleRowViewModel? row)
    {
        row ??= SelectedRule;
        if (row is null)
        {
            return;
        }

        await EditRuleAsync(await CreateEditorAsync(row.Id).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task DuplicateAsync(RuleRowViewModel? row)
    {
        row ??= SelectedRule;
        if (row is null)
        {
            return;
        }

        try
        {
            var copy = await _api.DuplicateRuleAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            ApplySummary(copy.Summary);
            SelectedRule = _rulesById.GetValueOrDefault(copy.Summary.Id);
            RulesErrorText = null;
        }
        catch (RpcException ex)
        {
            RulesErrorText = "The rule could not be copied: " + ex.Status.Detail;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task DeleteAsync(RuleRowViewModel? row)
    {
        row ??= SelectedRule;
        if (row is null)
        {
            return;
        }

        var message = row.IsOn
            ? $"Delete the rule \"{row.Name}\"? It is on: its actions for turning off run first."
            : $"Delete the rule \"{row.Name}\"?";
        if (!await _dialogs.ConfirmAsync("Delete event rule", message, "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteRuleAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            _rulesById.Remove(row.Id);
            FilterRules();
            RulesErrorText = null;
        }
        catch (RpcException ex)
        {
            RulesErrorText = "The rule could not be deleted: " + ex.Status.Detail;
        }
    }

    /// <summary>Reads the log again (the live watch keeps running).</summary>
    [RelayCommand]
    private async Task RefreshLogAsync()
    {
        try
        {
            await LoadLogAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown below the log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogErrorText = LogReadError(ex);
        }
    }

    /// <summary>Delete all (the role may delete the event log; red, confirmed): every entry of the event log goes, for every client.</summary>
    [RelayCommand(CanExecute = nameof(MayClearLog))]
    private async Task ClearLogAsync()
    {
        if (!await _dialogs.ConfirmAsync("Delete all", "Delete every entry of the event log? Other clients see the empty log too.", "Delete all").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.ClearEventLogAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            _log.Clear();
            FilterLog();
            LogErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown below the log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogErrorText = "The event log could not be cleared: " + (ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

    internal async Task SetEnabledAsync(RuleRowViewModel row, bool enabled)
    {
        if (!MayChange)
        {
            row.Revert();
            return;
        }

        row.SetBusy(true);
        try
        {
            ApplySummary(await _api.SetRuleEnabledAsync(row.Id, enabled, CancellationToken.None).ConfigureAwait(true));
            RulesErrorText = null;
        }
#pragma warning disable CA1031 // Fire and forget from the check box: every failure undoes it and says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.Revert();
            RulesErrorText = "The rule could not be changed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
        finally
        {
            row.SetBusy(false);
        }
    }

    /// <summary>The dialog for a new rule (null id) or an existing one; null when the server could not be read.</summary>
    internal async Task<RuleEditorViewModel?> CreateEditorAsync(string? ruleId)
    {
        try
        {
            var catalog = await _api.GetEventCatalogAsync(CancellationToken.None).ConfigureAwait(true);
            RuleDetail? detail = ruleId is null ? null : await _api.GetRuleAsync(ruleId, CancellationToken.None).ConfigureAwait(true);
            var recipients = await _api.ListRecipientsAsync(CancellationToken.None).ConfigureAwait(true);
            RulesErrorText = null;
            return new RuleEditorViewModel(_api, catalog, detail, _devices, recipients);
        }
        catch (RpcException ex)
        {
            RulesErrorText = "The rule could not be opened: " + ex.Status.Detail;
            return null;
        }
    }

    /// <summary>Applies a full list (watch stream, tests).</summary>
    public void ApplyRules(IReadOnlyList<RuleSummary> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var seen = new HashSet<string>();
        foreach (var summary in rules)
        {
            seen.Add(summary.Id);
            if (_rulesById.TryGetValue(summary.Id, out var row))
            {
                row.Apply(summary);
            }
            else
            {
                _rulesById[summary.Id] = new RuleRowViewModel(this, summary);
            }
        }

        foreach (var gone in _rulesById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rulesById.Remove(gone);
        }

        FilterRules();
        FollowRuleLinks(rules);
    }

    /// <summary>
    /// A change of what the rules run (a rule of another client, a recipient changed there) reads the recipients again; a
    /// change of the live state does not.
    /// </summary>
    private void FollowRuleLinks(IReadOnlyList<RuleSummary> rules)
    {
        var links = string.Join('\n', rules.Select(r => r.Id + r.Name + r.ActionsText));
        var changed = _ruleLinks is not null && links != _ruleLinks;
        _ruleLinks = links;
        if (changed && _active is { } active)
        {
            _ = LoadRecipientsAsync(active.Token);
        }
    }

    /// <summary>Reads the recipients and their type names; a failure shows below the list (fire and forget: nothing escapes).</summary>
    private async Task LoadRecipientsAsync(CancellationToken ct)
    {
        if (!ShowRecipients)
        {
            return;
        }

        try
        {
            var catalog = await _api.GetEventCatalogAsync(ct).ConfigureAwait(true);
            var types = catalog.RecipientTypes.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var rows = await _api.ListRecipientsAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            ApplyRecipients(rows, types);
            RecipientsErrorText = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RecipientsErrorText = "The recipients could not be read: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

    private void ApplyRecipients(IReadOnlyList<RecipientEntry> rows, IReadOnlyDictionary<string, RecipientTypeEntry> types)
    {
        _recipients = [.. rows.Select(r =>
        {
            var type = types.GetValueOrDefault(r.TypeId);
            return new RecipientRowViewModel(r, type?.DisplayName ?? r.TypeId, type?.Persistent == true) { Status = _recipientStatus.GetValueOrDefault(r.Id) };
        })];
        FilterRecipients();
    }

    /// <summary>The whole list of statuses, on the UI thread: the rows follow in place, no reset of the list.</summary>
    private void ApplyRecipientStatus(IEnumerable<RecipientStatusEntry> statuses)
    {
        _recipientStatus.Clear();
        foreach (var status in statuses)
        {
            _recipientStatus[status.Id] = status;
        }

        foreach (var row in _recipients)
        {
            row.Status = _recipientStatus.GetValueOrDefault(row.Id);
        }
    }

    /// <summary>
    /// The live status of the recipients while the page is shown, one post per burst; each message is the whole list, so the
    /// newest of a burst is enough. A stream that ended clears the chips: a status nobody watches is a guess. Users limited to
    /// a device group read no recipients.
    /// </summary>
    private Task WatchRecipientStatusAsync(CancellationToken ct)
    {
        if (!ShowRecipients || _session?.HasAllDevices == false)
        {
            return Task.CompletedTask;
        }

        var batcher = new ChangeBatcher<RecipientStatusList>(_ui, batch =>
        {
            if (!ct.IsCancellationRequested)
            {
                ApplyRecipientStatus(batch[^1].Recipients);
            }
        });
        return WatchAsync(_api.WatchRecipientStatusAsync, batcher.Add, () => batcher.Add(new RecipientStatusList()), ct);
    }

    private void FilterRecipients()
    {
        var text = RecipientSearchText.Trim();
        var selected = SelectedRecipient?.Id;
        var rows = text.Length == 0 ? _recipients : [.. _recipients.Where(r => r.Matches(text))];
        Recipients.ReplaceAll(rows);
        SelectedRecipient = rows.Find(r => r.Id == selected);
        OnPropertyChanged(nameof(ShowRecipientsEmpty));
        OnPropertyChanged(nameof(RecipientsEmptyText));
    }

    private void ApplySummary(RuleSummary summary)
    {
        if (_rulesById.TryGetValue(summary.Id, out var row))
        {
            row.Apply(summary);
            UpdateRulesStatus();
        }
        else
        {
            _rulesById[summary.Id] = new RuleRowViewModel(this, summary);
            FilterRules();
        }
    }

    /// <summary>New log lines (watch stream, tests): newest first, at most <see cref="LogLimit"/>.</summary>
    public void AppendLog(IEnumerable<EventLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var added = entries.OrderByDescending(e => e.Id).Select(e => new EventLogRowViewModel(e)).ToList();
        if (added.Count == 0)
        {
            return;
        }

        _log.InsertRange(0, added);
        if (_log.Count > LogLimit)
        {
            _log.RemoveRange(LogLimit, _log.Count - LogLimit);
        }

        FilterLog();
    }

    private void FilterRules()
    {
        var text = SearchText.Trim();
        var rows = _rulesById.Values.Where(r => text.Length == 0 || r.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var selected = SelectedRule;
        Rules.ReplaceAll(rows);
        SelectedRule = selected is not null && rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(ShowRulesEmpty));
        OnPropertyChanged(nameof(RulesEmptyText));
        UpdateRulesStatus();
    }

    private void UpdateRulesStatus()
    {
        var total = _rulesById.Count;
        var on = _rulesById.Values.Count(r => r.Summary.Enabled);
        var active = _rulesById.Values.Count(r => r.IsOn);
        var text = string.Create(CultureInfo.CurrentCulture, $"{total:N0} {(total == 1 ? "rule" : "rules")} · {on:N0} turned on · {active:N0} active now");
        RulesStatus = Rules.Count == total ? text : text + string.Create(CultureInfo.CurrentCulture, $" · {Rules.Count:N0} shown");
    }

    private void FilterLog()
    {
        var text = LogSearchText.Trim();
        LogEntries.ReplaceAll(text.Length == 0 ? _log : _log.Where(e => e.Matches(text)).ToList());
        LogStatus = text.Length == 0
            ? string.Create(CultureInfo.CurrentCulture, $"Newest {_log.Count:N0} entries")
            : string.Create(CultureInfo.CurrentCulture, $"{LogEntries.Count:N0} match · newest {_log.Count:N0} entries");
    }

    private Task WatchRulesAsync(CancellationToken ct) => WatchAsync(_api.WatchRulesAsync, list =>
    {
        var rules = list.Rules.ToList();
        _ui.Post(() => ApplyRules(rules));
    }, null, ct);

    /// <summary>Follows a server stream until <paramref name="ct"/> ends; after a failure or an end it watches again (1, 2, 4 ... 15 s).</summary>
    private async Task WatchAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> watch, Action<T> received, Action? ended, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var item in watch(ct).ConfigureAwait(false))
                {
                    received(item);
                    delay = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            ended?.Invoke();
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
        }
    }

    /// <summary>Reads the newest <see cref="LogLimit"/> entries (UI thread) and replaces the list.</summary>
    private async Task LoadLogAsync(CancellationToken ct)
    {
        var page = await _api.ListEventLogAsync(LogLimit, null, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        _log.Clear();
        AppendLog(page.Entries);
        LogErrorText = null;
    }

    private static string LogReadError(Exception ex) =>
        "The event log could not be read: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);

    /// <summary>
    /// Keeps the log live while the page is shown: reads the list, then watches new entries; after a failure or a dropped
    /// stream it reads and watches again with the same backoff as the rules (1, 2, 4 ... 15 s). One loop per activation:
    /// <see cref="Deactivate"/> cancels it, entries of a cancelled loop are ignored.
    /// </summary>
    private async Task WatchLogAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var loaded = false;
            try
            {
                await LoadLogAsync(ct).ConfigureAwait(true);
                loaded = true;
                await foreach (var page in _api.WatchEventLogAsync(ct).ConfigureAwait(true))
                {
                    var entries = page.Entries.ToList();
                    _ui.Post(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            AppendLog(entries);
                        }
                    });
                    delay = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connection dropped: read and watch again after a short delay.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A dropped watch stays quiet (the list is read again); a list that cannot be read shows below the log.
                if (!loaded && !ct.IsCancellationRequested)
                {
                    LogErrorText = LogReadError(ex);
                }
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSession.IsAdmin))
        {
            OnPermissionsChanged(sender, EventArgs.Empty);
        }
    }

    /// <summary>A role change: what the page offers follows the role at once.</summary>
    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MayChange));
        OnPropertyChanged(nameof(MayClearLog));
        ClearLogCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReadOnlyHint));
        OnPropertyChanged(nameof(RulesEmptyText));
        NewRuleCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRecipients));
        OnPropertyChanged(nameof(RecipientsEmptyText));
        NewRecipientCommand.NotifyCanExecuteChanged();
        EditRecipientCommand.NotifyCanExecuteChanged();
        DeleteRecipientCommand.NotifyCanExecuteChanged();
        foreach (var row in _rulesById.Values)
        {
            row.NotifyRole();
        }
    }
}
