using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Client.Tags;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Compliance;

/// <summary>The tabs of the Compliance page.</summary>
public enum ComplianceTab
{
    Problems = 0,
    Warnings = 1,
    All = 2,
}

/// <summary>
/// One count card of the Compliance page: a check that is on, how many devices fail it; a click filters to them, Edit and
/// Delete sit on the card (on hover).
/// </summary>
public sealed partial class ComplianceCardViewModel(ComplianceCheckSummary check, bool showOwner = false) : ObservableObject
{
    /// <summary>The viewer has all devices: the card names a device group that owns the check.</summary>
    public bool ShowOwner { get; } = showOwner;

    /// <summary>"Building B" for a check of a device group, "No devices" for one whose group was deleted; null for all devices.</summary>
    public string? OwnerText => Check.Owner is not { } owner || owner.AllDevices ? null
        : owner.GroupName.Length > 0 ? owner.GroupName : Settings.DeviceGroupWords.NoDevices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Value), nameof(Detail), nameof(IsError), nameof(IsWarning), nameof(IsOk), nameof(ToolTip))]
    public partial ComplianceCheckSummary Check { get; set; } = check;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string Id => Check.Id;

    public string Title => Check.Name;

    /// <summary>The failed devices ("12"), "–" while the check does not run.</summary>
    public string Value => Check.Problem.Length > 0 ? "–" : Check.Failed.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// "failed · 3 not checked", the reason it does not run, or "All passed"; for a viewer with all devices the owning device
    /// group follows ("failed · Building B").
    /// </summary>
    public string Detail => ShowOwner && OwnerText is { } owner ? ResultDetail + " · " + owner : ResultDetail;

    private string ResultDetail
    {
        get
        {
            if (Check.Problem.Length > 0)
            {
                return Check.Problem;
            }

            var notChecked = Check.NotChecked + Check.CouldNotCheck;
            var text = Check.Failed > 0 ? "failed" : Check.Passed > 0 ? "All passed" : "Not checked yet";
            return notChecked > 0 && (Check.Failed > 0 || Check.Passed > 0)
                ? text + string.Create(CultureInfo.CurrentCulture, $" · {notChecked:N0} not checked")
                : text;
        }
    }

    public bool IsError => Check.Problem.Length == 0 && Check.Failed > 0 && Check.Severity == ComplianceSeverity.ComplianceProblem;

    public bool IsWarning => Check.Problem.Length == 0 && Check.Failed > 0 && Check.Severity == ComplianceSeverity.ComplianceWarning;

    public bool IsOk => Check.Problem.Length == 0 && Check.Failed == 0 && Check.Passed > 0;

    public string ToolTip => Check.Summary;
}

/// <summary>
/// The rail page Compliance (key <see cref="HostPages.Compliance"/>): a count card per check, the tabs Problems | Warnings | All
/// (All first), the devices with one column per check, grouped by tag on request, columns the user can turn off and the
/// result text next to each icon (on by default). Roles: "See compliance" reads and checks again, "Change compliance
/// checks" creates, changes and deletes checks. Every list is O(n) over the devices; changes arrive in batches.
/// </summary>
public sealed partial class ComplianceViewModel : ObservableObject, IDisposable
{
    public const string ModelColumn = "model";
    public const string TagsColumn = "tags";
    public const string ResultColumn = "result";
    public const string CheckColumnPrefix = "check:";

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly DeviceStore _devices;
    private readonly UserSession? _session;
    private readonly ComplianceStore _store;
    private readonly IUiDispatcher _ui;
    private readonly IClientSettingsStore _settings;
    private bool _refilterQueued;
    private bool _devicesChanged;

    public ComplianceViewModel(IOadmApi api, IDialogService dialogs, DeviceStore devices, IUiDispatcher ui, IClientSettingsStore settings, UserSession? session = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session;
        _store = devices.Compliance;
        ShowText = settings.Current.ComplianceShowText;
        GroupByTag = settings.Current.ComplianceGroupByTag;
        _store.ChecksChanged += OnChecksChanged;
        _store.ResultsChanged += OnResultsChanged;
        _devices.Changed += OnDevicesChanged;
        if (session is not null)
        {
            session.PermissionsChanged += OnPermissionsChanged;
        }

        OnChecksChanged(this, EventArgs.Empty);
    }

    /// <summary>The checks and results the page shows.</summary>
    public ComplianceStore Store => _store;

    /// <summary>The per-check columns or the columns the user turned on or off changed.</summary>
    public event EventHandler? ColumnsChanged;

    public RangeObservableCollection<ComplianceCardViewModel> Cards { get; } = [];

    /// <summary>Cards show while compliance is on and has checks.</summary>
    [ObservableProperty] public partial bool HasCards { get; private set; }

    /// <summary>The devices the tab, the card and the search show.</summary>
    public RangeObservableCollection<DeviceRowViewModel> Rows { get; } = [];

    /// <summary>Group by tag: the (device x tag) rows of <see cref="Rows"/>.</summary>
    public DeviceTagGrouping TagGrouping { get; } = new();

    /// <summary>The grid's selection (device rows or tag group rows; Check now and Export take it).</summary>
    public RangeObservableCollection<object> SelectedRows { get; } = [];

    /// <summary>The checks of the per-check columns, in order (the ones that are on).</summary>
    public IReadOnlyList<ComplianceCheckSummary> ColumnChecks { get; private set; } = [];

    /// <summary>The column chooser: Model, Tags, Result, then one entry per check.</summary>
    public ObservableCollection<ColumnOptionViewModel> ColumnOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsTab), nameof(IsWarningsTab), nameof(IsAllTab))]
    public partial ComplianceTab Tab { get; set; } = ComplianceTab.All;

    public bool IsProblemsTab => Tab == ComplianceTab.Problems;

    public bool IsWarningsTab => Tab == ComplianceTab.Warnings;

    public bool IsAllTab => Tab == ComplianceTab.All;

    [ObservableProperty] public partial string ProblemsText { get; private set; } = "Problems";

    [ObservableProperty] public partial string WarningsText { get; private set; } = "Warnings";

    [ObservableProperty] public partial string AllText { get; private set; } = "All";

    /// <summary>The card whose failing devices the list shows; null = the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckView), nameof(CheckViewText))]
    public partial ComplianceCardViewModel? SelectedCard { get; private set; }

    /// <summary>A card is chosen: the tabs are greyed and "Show all devices" leaves the view.</summary>
    public bool IsCheckView => SelectedCard is not null;

    /// <summary>"Failing: Object Analytics running".</summary>
    public string? CheckViewText => SelectedCard is { } card ? "Failing: " + card.Title : null;

    /// <summary>Leaves the view of one check (the tab All again).</summary>
    [RelayCommand]
    private void LeaveCheckView() => SelectTab(ComplianceTab.All);

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The result text next to each icon of the check columns. Persisted per client.</summary>
    [ObservableProperty] public partial bool ShowText { get; set; }

    /// <summary>A group per tag (a device under each of its tags). Persisted per client.</summary>
    [ObservableProperty] public partial bool GroupByTag { get; set; }

    /// <summary>"12 devices".</summary>
    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>One line when the list is empty: what to do first.</summary>
    [ObservableProperty] public partial string? EmptyText { get; private set; }

    /// <summary>A refused action (save, delete, check now), directly below the list.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    /// <summary>The role may create, change and delete checks, and turn compliance on or off.</summary>
    public bool MayManage => _session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.ComplianceManage) ?? true;

    /// <summary>Turning compliance on or off for the server: the role may manage checks and the user has all devices.</summary>
    public bool MayTurnOnOff => MayManage && (_session?.HasAllDevices ?? true);

    /// <summary>Compliance is on for the server: the cards and the list show; else the empty state with Enable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOffState))]
    public partial bool IsEnabled { get; private set; }

    /// <summary>The server has compliance, it is off: "Enable Compliance Checks".</summary>
    public bool ShowOffState => _store.IsAvailable && !IsEnabled;

    /// <summary>The text of the dialog after Enable.</summary>
    public const string EnabledInfo =
        "OADM now checks every device with **Reachable** and **Login works**.\n"
        + "Add your own checks with **New check**.\n\n"
        + "**In the device list too:** turn on the **Compliance** column with **Choose columns** on the Devices page.";

    /// <summary>"Enable Compliance Checks" of the empty state, then a short note on the device list's column.</summary>
    [RelayCommand]
    private async Task EnableAsync()
    {
        ErrorText = null;
        try
        {
            _store.ApplyChecks(await _api.SetComplianceEnabledAsync(true, CancellationToken.None).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
            return;
        }

        await _dialogs.ShowMessageAsync("Compliance checks are on", EnabledInfo).ConfigureAwait(true);
    }

    /// <summary>"Turn off compliance": nothing is checked any more, the results go, the checks stay (confirmed).</summary>
    [RelayCommand]
    private async Task TurnOffAsync()
    {
        ErrorText = null;
        if (!await _dialogs.ConfirmAsync(
            "Turn off compliance", "Turn off compliance checks? OADM stops checking the devices and removes the results. Your checks stay.", "Turn off")
            .ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _store.ApplyChecks(await _api.SetComplianceEnabledAsync(false, CancellationToken.None).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }

    /// <summary>The devices of the selection, each once.</summary>
    public IReadOnlyList<DeviceRowViewModel> SelectedDevices => DeviceTagGrouping.DistinctDevices(SelectedRows);

    public void Dispose()
    {
        _store.ChecksChanged -= OnChecksChanged;
        _store.ResultsChanged -= OnResultsChanged;
        _devices.Changed -= OnDevicesChanged;
        if (_session is not null)
        {
            _session.PermissionsChanged -= OnPermissionsChanged;
        }
    }

    /// <summary>Whether a column is on (the chooser); unknown keys are on.</summary>
    public bool IsColumnVisible(string key) => !_settings.Current.ComplianceHiddenColumns.Contains(key);

    partial void OnTabChanged(ComplianceTab value) => Refilter();

    partial void OnSearchTextChanged(string value) => Refilter();

    partial void OnShowTextChanged(bool value)
    {
        _settings.Current.ComplianceShowText = value;
        _settings.Save();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnGroupByTagChanged(bool value)
    {
        _settings.Current.ComplianceGroupByTag = value;
        _settings.Save();
        if (value)
        {
            TagGrouping.Rebuild(Rows);
        }
    }

    [RelayCommand]
    private void ToggleShowText() => ShowText = !ShowText;

    [RelayCommand]
    private void ToggleGroupByTag() => GroupByTag = !GroupByTag;

    [RelayCommand]
    private void ShowProblems() => SelectTab(ComplianceTab.Problems);

    [RelayCommand]
    private void ShowWarnings() => SelectTab(ComplianceTab.Warnings);

    [RelayCommand]
    private void ShowAll() => SelectTab(ComplianceTab.All);

    private void SelectTab(ComplianceTab tab)
    {
        SetCard(null);
        Tab = tab;
        Refilter();
    }

    /// <summary>A card: the devices that fail its check (again: back to the tab).</summary>
    [RelayCommand]
    private void SelectCard(ComplianceCardViewModel? card)
    {
        if (card is null || card == SelectedCard)
        {
            SetCard(null);
        }
        else
        {
            SetCard(card);
            Tab = ComplianceTab.All;
        }

        Refilter();
    }

    private void SetCard(ComplianceCardViewModel? card)
    {
        foreach (var c in Cards)
        {
            c.IsSelected = c == card;
        }

        SelectedCard = card;
    }

    [RelayCommand]
    private async Task NewCheckAsync()
    {
        var editor = await EditorAsync(null).ConfigureAwait(true);
        if (editor is not null)
        {
            await _dialogs.ShowComplianceCheckAsync(editor).ConfigureAwait(true);
        }
    }

    /// <summary>The pen of a card.</summary>
    [RelayCommand]
    private async Task EditAsync(ComplianceCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        ErrorText = null;
        ComplianceCheckDetail detail;
        try
        {
            detail = await _api.GetComplianceCheckAsync(card.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
            return;
        }

        var editor = await EditorAsync(detail).ConfigureAwait(true);
        if (editor is not null)
        {
            await _dialogs.ShowComplianceCheckAsync(editor).ConfigureAwait(true);
        }
    }

    /// <summary>The bin of a card (not on the default checks).</summary>
    [RelayCommand]
    private async Task DeleteAsync(ComplianceCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        ErrorText = null;
        if (!await _dialogs.ConfirmAsync("Delete compliance check", $"Delete the check {card.Title}? Its results go too.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteComplianceCheckAsync(card.Id, CancellationToken.None).ConfigureAwait(true);
            if (SelectedCard == card)
            {
                SetCard(null);
                Refilter();
            }
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }

    /// <summary>Checks again now: the selected devices, else every device; the selected check, else every check.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        ErrorText = null;
        try
        {
            await _api.CheckComplianceNowAsync(
                SelectedCard is { } card ? [card.Id] : [],
                SelectedDevices.Select(r => r.Id),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }

    /// <summary>The shown devices as CSV: device columns, the result, then one column per check.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var rows = SelectedDevices is { Count: > 0 } selected ? selected : [.. Rows];
        if (rows.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Export", "There is nothing to export.").ConfigureAwait(true);
            return;
        }

        var sb = new StringBuilder();
        Csv.AppendRecord(sb, ["Address", "Model", "MAC address", "Tags", "Result", .. ColumnChecks.Select(c => c.Name)]);
        foreach (var row in rows)
        {
            Csv.AppendRecord(sb, [row.DisplayAddress, row.Model, row.Serial, row.TagsText, row.Compliance.Text, .. ColumnChecks.Select(c => ResultText(row.Compliance.Get(c.Id)))]);
        }

        await _dialogs.SaveFileAsync(
            "Export compliance", $"oadm-compliance-{DateTime.Now:yyyy-MM-dd}.csv", DevicesViewModel.CsvFile,
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())]).ConfigureAwait(true);
    }

    /// <summary>"Failed: Object Analytics is stopped", "Passed", "Not checked: ..." (export, tooltips).</summary>
    public static string ResultText(ComplianceResultEntry? result) => result is null ? string.Empty : result.Outcome switch
    {
        ComplianceOutcome.CompliancePassed => "Passed",
        ComplianceOutcome.ComplianceFailed => Join("Failed", result.Detail),
        ComplianceOutcome.ComplianceNotApplicable => Join("Not applicable", result.Detail),
        ComplianceOutcome.ComplianceCouldNotCheck => Join("Could not check", result.Detail),
        _ => Join("Not checked", result.Detail),
    };

    private static string Join(string outcome, string detail) => detail.Length == 0 ? outcome : outcome + ": " + detail.Replace('\n', ' ');

    private async Task<ComplianceCheckEditorViewModel?> EditorAsync(ComplianceCheckDetail? existing)
    {
        ComplianceCatalog catalog;
        try
        {
            catalog = await _api.GetComplianceCatalogAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
            return null;
        }

        IReadOnlyList<Settings.DeviceAccessOption>? owners = null;
        if (_session?.IsAdmin == true)
        {
            try
            {
                var groups = await _api.ListDeviceGroupsAsync(CancellationToken.None).ConfigureAwait(true);
                owners = [Settings.DeviceAccessOption.All, .. groups.Groups.Select(g => new Settings.DeviceAccessOption(g.Id, g.Name))];
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // A server before device groups: its checks have no owner, the form has no Owner field.
            }
            catch (RpcException ex)
            {
                ErrorText = ex.Status.Detail;
                return null;
            }
        }

        return new ComplianceCheckEditorViewModel(
            _api, _dialogs, catalog, [.. _devices.Devices.Cast<IDeviceInfo>()], existing, _devices.Tags.Tags.Select(t => t.Name), owners);
    }

    /// <summary>Cards name the device group that owns a check for users with all devices (group users see only their own).</summary>
    private bool ShowOwners => _session?.HasAllDevices ?? true;

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(MayTurnOnOff));
    }

    private void OnChecksChanged(object? sender, EventArgs e)
    {
        IsEnabled = _store.IsEnabled;
        OnPropertyChanged(nameof(ShowOffState));
        if (!IsEnabled)
        {
            SetCard(null);
        }
        var shown = _store.Checks.Where(c => c.Enabled).ToList();
        var columnsChanged = !shown.Select(c => (c.Id, c.Name)).SequenceEqual(ColumnChecks.Select(c => (c.Id, c.Name))) || ColumnOptions.Count == 0;
        ColumnChecks = shown;

        var byId = Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var cards = shown.Select(c =>
        {
            if (byId.TryGetValue(c.Id, out var card))
            {
                card.Check = c;
                return card;
            }

            return new ComplianceCardViewModel(c, ShowOwners);
        }).ToList();
        if (!cards.SequenceEqual(Cards))
        {
            Cards.ReplaceAll(cards);
        }

        HasCards = Cards.Count > 0 && _store.IsEnabled;
        if (SelectedCard is { } selected && !cards.Contains(selected))
        {
            SetCard(null);
        }

        if (columnsChanged)
        {
            BuildColumnOptions();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueRefilter();
    }

    /// <summary>The chooser's entries; a change is saved at once and the view shows or hides the column.</summary>
    private void BuildColumnOptions()
    {
        ColumnOptions.Clear();
        AddOption(ModelColumn, "Model");
        AddOption(TagsColumn, "Tags");
        AddOption(ResultColumn, "Result");
        foreach (var check in ColumnChecks)
        {
            AddOption(CheckColumnPrefix + check.Id, check.Name);
        }
    }

    private void AddOption(string key, string header)
    {
        var option = new ColumnOptionViewModel(key, header, canHide: true) { IsVisible = IsColumnVisible(key) };
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ColumnOptionViewModel.IsVisible))
            {
                return;
            }

            var hidden = _settings.Current.ComplianceHiddenColumns;
            hidden.Remove(key);
            if (!option.IsVisible)
            {
                hidden.Add(key);
            }

            _settings.Save();
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        };
        ColumnOptions.Add(option);
    }

    private void OnResultsChanged(object? sender, DeviceStoreChangedEventArgs e) => QueueRefilter();

    private void OnDevicesChanged(object? sender, DeviceStoreChangedEventArgs e)
    {
        if (e.IsReset || e.DeviceIds.Count > 0)
        {
            _devicesChanged = true; // tags may have changed: the groups are built again
            QueueRefilter();
        }
    }

    /// <summary>A burst of changes filters once (after the UI thread is free again).</summary>
    private void QueueRefilter()
    {
        if (_refilterQueued)
        {
            return;
        }

        _refilterQueued = true;
        _ui.Post(() =>
        {
            _refilterQueued = false;
            Refilter();
        });
    }

    /// <summary>The tab counts and the shown devices, O(n) over the devices.</summary>
    internal void Refilter()
    {
        int problems = 0, warnings = 0, all = 0;
        var search = SearchText.Trim();
        var card = SelectedCard?.Id;
        var rows = new List<DeviceRowViewModel>();
        foreach (var row in _devices.Devices)
        {
            var compliance = row.Compliance;
            var level = compliance.Level;
            var hasResults = level != ComplianceLevel.None || compliance.Results.Any();
            if (hasResults)
            {
                all++;
            }

            if (level == ComplianceLevel.Problem)
            {
                problems++;
            }
            else if (level == ComplianceLevel.Warning)
            {
                warnings++;
            }

            var wanted = card is not null
                ? compliance.Get(card)?.Outcome == ComplianceOutcome.ComplianceFailed
                : Tab switch
                {
                    ComplianceTab.Problems => level == ComplianceLevel.Problem,
                    ComplianceTab.Warnings => level == ComplianceLevel.Warning,
                    _ => hasResults,
                };
            if (wanted && (search.Length == 0 || row.Matches(search)))
            {
                rows.Add(row);
            }
        }

        ProblemsText = problems == 0 ? "Problems" : string.Create(CultureInfo.CurrentCulture, $"Problems {problems:N0}");
        WarningsText = warnings == 0 ? "Warnings" : string.Create(CultureInfo.CurrentCulture, $"Warnings {warnings:N0}");
        AllText = all == 0 ? "All" : string.Create(CultureInfo.CurrentCulture, $"All {all:N0}");
        var rowsChanged = !rows.SequenceEqual(Rows);
        if (rowsChanged)
        {
            Rows.ReplaceAll(rows);
        }

        if (GroupByTag && (rowsChanged || _devicesChanged))
        {
            TagGrouping.Rebuild(Rows);
        }

        _devicesChanged = false;

        StatusText = rows.Count == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{rows.Count:N0} devices");
        EmptyText = rows.Count > 0 || ShowOffState ? null
            : !_store.IsAvailable ? "Compliance is not available on this server."
            : card is not null ? "No device fails this check."
            : Tab switch
            {
                ComplianceTab.Problems => all == 0 ? "No results yet. Add a check with New check." : "No device has a problem.",
                ComplianceTab.Warnings => "No device has a warning.",
                _ => "No results yet. Add a check with New check.",
            };
    }
}
