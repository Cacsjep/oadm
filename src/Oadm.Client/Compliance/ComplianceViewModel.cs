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

/// <summary>One count card of the Compliance page: a check that is on, how many devices fail it; a click filters to them.</summary>
public sealed partial class ComplianceCardViewModel(ComplianceCheckSummary check) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Value), nameof(Detail), nameof(IsError), nameof(IsWarning), nameof(IsOk), nameof(ToolTip))]
    public partial ComplianceCheckSummary Check { get; set; } = check;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string Id => Check.Id;

    public string Title => Check.Name;

    /// <summary>The failed devices ("12"), "–" while the check does not run.</summary>
    public string Value => Check.Problem.Length > 0 ? "–" : Check.Failed.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"failed · 3 not checked", the reason it does not run, or "All passed".</summary>
    public string Detail
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
/// and the devices with one icon column per check. Roles: "See compliance" reads and checks again, "Change compliance
/// checks" creates, changes and deletes checks. Every list is O(n) over the devices; changes arrive in batches.
/// </summary>
public sealed partial class ComplianceViewModel : ObservableObject, IDisposable
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly DeviceStore _devices;
    private readonly UserSession? _session;
    private readonly ComplianceStore _store;
    private readonly IUiDispatcher _ui;
    private bool _refilterQueued;

    public ComplianceViewModel(IOadmApi api, IDialogService dialogs, DeviceStore devices, IUiDispatcher ui, UserSession? session = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _session = session;
        _store = devices.Compliance;
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

    /// <summary>The checks changed in a way that changes the per-check columns (names, which checks).</summary>
    public event EventHandler? ColumnsChanged;

    public RangeObservableCollection<ComplianceCardViewModel> Cards { get; } = [];

    [ObservableProperty] public partial bool HasCards { get; private set; }

    /// <summary>The devices the tab, the card and the search show.</summary>
    public RangeObservableCollection<DeviceRowViewModel> Rows { get; } = [];

    /// <summary>The grid's selection (Check now takes it).</summary>
    public RangeObservableCollection<object> SelectedRows { get; } = [];

    /// <summary>The checks of the per-check columns, in order (the ones that are on).</summary>
    public IReadOnlyList<ComplianceCheckSummary> ColumnChecks { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsTab), nameof(IsWarningsTab), nameof(IsAllTab))]
    public partial ComplianceTab Tab { get; set; } = ComplianceTab.Problems;

    public bool IsProblemsTab => Tab == ComplianceTab.Problems;

    public bool IsWarningsTab => Tab == ComplianceTab.Warnings;

    public bool IsAllTab => Tab == ComplianceTab.All;

    [ObservableProperty] public partial string ProblemsText { get; private set; } = "Problems";

    [ObservableProperty] public partial string WarningsText { get; private set; } = "Warnings";

    [ObservableProperty] public partial string AllText { get; private set; } = "All";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(CardActionBlockedReason))]
    public partial ComplianceCardViewModel? SelectedCard { get; private set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"12 devices · 3 selected".</summary>
    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>One line when the list is empty: what to do first.</summary>
    [ObservableProperty] public partial string? EmptyText { get; private set; }

    /// <summary>A refused action (save, delete, check now), directly below the list.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    /// <summary>The role may create, change and delete checks.</summary>
    public bool MayManage => _session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.ComplianceManage) ?? true;

    public string? CardActionBlockedReason => SelectedCard is null ? "Click a check first" : null;

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

    partial void OnTabChanged(ComplianceTab value) => Refilter();

    partial void OnSearchTextChanged(string value) => Refilter();

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

    private bool CanChangeCard() => SelectedCard is not null;

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task EditAsync()
    {
        if (SelectedCard is not { } card)
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

    [RelayCommand(CanExecute = nameof(CanChangeCard))]
    private async Task DeleteAsync()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        ErrorText = null;
        if (card.Check.BuiltIn)
        {
            ErrorText = card.Title + " cannot be deleted.";
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete compliance check", $"Delete the check {card.Title}? Its results go too.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteComplianceCheckAsync(card.Id, CancellationToken.None).ConfigureAwait(true);
            SetCard(null);
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
                SelectedRows.OfType<DeviceRowViewModel>().Select(r => r.Id),
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
        var rows = SelectedRows.OfType<DeviceRowViewModel>().ToList() is { Count: > 0 } selected ? selected : [.. Rows];
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

        return new ComplianceCheckEditorViewModel(
            _api, _dialogs, catalog, [.. _devices.Devices.Cast<IDeviceInfo>()], existing, _devices.Tags.Tags.Select(t => t.Name));
    }

    private void OnPermissionsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(MayManage));

    private void OnChecksChanged(object? sender, EventArgs e)
    {
        var shown = _store.Checks.Where(c => c.Enabled).ToList();
        var columnsChanged = !shown.Select(c => (c.Id, c.Name)).SequenceEqual(ColumnChecks.Select(c => (c.Id, c.Name)));
        ColumnChecks = shown;

        var byId = Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var cards = shown.Select(c =>
        {
            if (byId.TryGetValue(c.Id, out var card))
            {
                card.Check = c;
                return card;
            }

            return new ComplianceCardViewModel(c);
        }).ToList();
        if (!cards.SequenceEqual(Cards))
        {
            Cards.ReplaceAll(cards);
        }

        HasCards = Cards.Count > 0;

        if (SelectedCard is { } selected && !cards.Contains(selected))
        {
            SetCard(null);
        }

        if (columnsChanged)
        {
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueRefilter();
    }

    private void OnResultsChanged(object? sender, DeviceStoreChangedEventArgs e) => QueueRefilter();

    private void OnDevicesChanged(object? sender, DeviceStoreChangedEventArgs e)
    {
        if (e.IsReset || e.DeviceIds.Count > 0)
        {
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
            if (level != ComplianceLevel.None || compliance.Results.Any())
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
                    _ => level != ComplianceLevel.None || compliance.Results.Any(),
                };
            if (wanted && (search.Length == 0 || row.Matches(search)))
            {
                rows.Add(row);
            }
        }

        ProblemsText = problems == 0 ? "Problems" : string.Create(CultureInfo.CurrentCulture, $"Problems {problems:N0}");
        WarningsText = warnings == 0 ? "Warnings" : string.Create(CultureInfo.CurrentCulture, $"Warnings {warnings:N0}");
        AllText = all == 0 ? "All" : string.Create(CultureInfo.CurrentCulture, $"All {all:N0}");
        if (!rows.SequenceEqual(Rows))
        {
            Rows.ReplaceAll(rows);
        }

        StatusText = rows.Count == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{rows.Count:N0} devices");
        EmptyText = rows.Count > 0 ? null
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
