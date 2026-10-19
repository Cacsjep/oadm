using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Devices;

/// <summary>
/// The server's waiting list as the client sees it (WaitingDeviceService.Watch through <c>ServerConnection</c>): how many
/// devices of imports wait to be added. The Devices page shows <see cref="Text"/> below the toolbar while it is not empty.
/// </summary>
public sealed partial class WaitingDevicesState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(HasAny))]
    public partial int Count { get; private set; }

    [ObservableProperty] public partial int LoginFailed { get; private set; }

    /// <summary>The server tries the list now.</summary>
    [ObservableProperty] public partial bool IsTrying { get; private set; }

    /// <summary>When the server tries the list next (local time); null while it tries.</summary>
    [ObservableProperty] public partial DateTime? NextTry { get; private set; }

    /// <summary>Entries asked for or in the running round that were not tried yet.</summary>
    [ObservableProperty] public partial int Remaining { get; private set; }

    /// <summary>Raised when a round starts (the dialog reads the list again: those rows say "Checking").</summary>
    public event EventHandler? RoundStarted;

    public bool HasAny => Count > 0;

    /// <summary>"2,412 devices waiting to be added"; empty when none.</summary>
    public string Text => Count switch
    {
        0 => "",
        1 => "1 device waiting to be added",
        _ => string.Create(CultureInfo.CurrentCulture, $"{Count:N0} devices waiting to be added"),
    };

    /// <summary>Raised when a round of the server ended (the dialog reads the list again).</summary>
    public event EventHandler? RoundEnded;

    public void Apply(WaitingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        bool ended = IsTrying && !summary.Trying;
        bool started = !IsTrying && summary.Trying;
        Count = summary.Count;
        LoginFailed = summary.LoginFailed;
        Remaining = summary.Remaining;
        IsTrying = summary.Trying;
        NextTry = summary.NextTry is { } next ? next.ToDateTime().ToLocalTime() : null;
        if (started)
        {
            RoundStarted?.Invoke(this, EventArgs.Empty);
        }

        if (ended)
        {
            RoundEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>No list (signed out, a role without "Add and remove devices", an older server).</summary>
    public void Clear() => Apply(new WaitingSummary());
}

/// <summary>One waiting device in the dialog.</summary>
public sealed class WaitingRowViewModel(WaitingDevice device)
{
    public WaitingDevice Device { get; } = device;

    public string Id => Device.Id;

    public string Address => Device.Address;

    public string MacAddress => Device.ExpectedSerial;

    /// <summary>The site the entry is tried through (the import's Site column); empty = everywhere.</summary>
    public string SiteName => Device.SiteName;

    public string TagsText => string.Join("; ", Device.Tags);

    public string SourceText => Device.Source;

    /// <summary>When the line was kept (sort key of the Kept column).</summary>
    public DateTime? KeptAt { get; } = device.Created?.ToDateTime().ToLocalTime();

    /// <summary>The last try (sort key of the Last try column); null before the first.</summary>
    public DateTime? LastTryAt { get; } = device.LastTry?.ToDateTime().ToLocalTime();

    public string KeptText => KeptAt?.ToString("g", CultureInfo.CurrentCulture) ?? "";

    public string LastTryText => LastTryAt?.ToString("g", CultureInfo.CurrentCulture) ?? "Not yet";

    /// <summary>The server tries it now (or soon, after Try now): the chip spins like the add page's "Checking...".</summary>
    public bool IsChecking => Device.Checking;

    /// <summary>What the last try found; a failed login and an address that answers through several sites wait for Try now.</summary>
    public string ResultText => Device.Checking ? "Checking..."
        : Device.LoginFailed
        ? "Login failed: " + Device.LastResult + " " + WaitsForTryNow
        : Device.Held
        ? Device.LastResult + " " + WaitsForTryNow
        : Device.LastResult.Length > 0 ? Device.LastResult : "Not tried yet";

    /// <summary>A failed login or an address answering through several sites: not tried again by itself (warning chip).</summary>
    public bool IsLoginFailed => (Device.LoginFailed || Device.Held) && !Device.Checking;

    /// <summary>The end of a result OADM does not try again by itself.</summary>
    public const string WaitsForTryNow = "Waits for Try now.";

    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search)
        || Address.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || MacAddress.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || TagsText.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || SiteName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || SourceText.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The "Waiting devices" dialog of the Devices page: every device of an import that did not answer, read in pages
/// (30,000 rows), searchable; Try now, Remove (confirmed) and Export for the selection or, without a selection, for every
/// device the search shows. The list is read again after each of the server's rounds.
/// </summary>
public sealed partial class WaitingDevicesViewModel : ObservableObject
{
    /// <summary>Rows read per call.</summary>
    public const int PageSize = 5_000;

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly List<WaitingRowViewModel> _all = [];
    private CancellationTokenSource? _loading;

    public WaitingDevicesViewModel(IOadmApi api, IDialogService dialogs, WaitingDevicesState state, bool hasSites = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        _api = api;
        _dialogs = dialogs;
        State = state;
        ShowSiteColumn = hasSites;
        Selected.CollectionChanged += (_, _) => OnSelectionChanged();
    }

    public WaitingDevicesState State { get; }

    /// <summary>The Site column: shown while the server has sites (docs/sites.md, Client: one rule for every Site column).</summary>
    public bool ShowSiteColumn { get; }

    public RangeObservableCollection<WaitingRowViewModel> Rows { get; } = [];

    /// <summary>The grid selection (handed over in one step by GridSelection).</summary>
    public RangeObservableCollection<object> Selected { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TryNowCommand), nameof(RemoveCommand), nameof(ExportCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string BusyText { get; private set; } = "";

    /// <summary>A failed server call, below the list.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    /// <summary>"Next try at 14:35" / "Checking: 120 left".</summary>
    public string ScheduleText => State.IsTrying || State.Remaining > 0
        ? (State.Remaining > 0 ? string.Create(CultureInfo.CurrentCulture, $"Checking: {State.Remaining:N0} left") : "Checking")
        : State.NextTry is { } next ? "Next try at " + next.ToString("t", CultureInfo.CurrentCulture) : "";

    /// <summary>"12 of 3,000 devices" (shown) or "3,000 devices".</summary>
    [ObservableProperty] public partial string CountText { get; private set; } = "";

    public string TargetText => Selected.Count > 0
        ? string.Create(CultureInfo.CurrentCulture, $"{Selected.Count:N0} selected")
        : string.Create(CultureInfo.CurrentCulture, $"all {Rows.Count:N0} shown");

    public string TryNowTip => "Try " + TargetText + " now";

    public string ExportTip => "Export " + TargetText;

    public event EventHandler? CloseRequested;

    /// <summary>Reads the whole list, page by page (the dialog opens and after each server round).</summary>
    public async Task LoadAsync()
    {
        if (_loading is not null)
        {
            await _loading.CancelAsync().ConfigureAwait(true);
        }

        using var cts = new CancellationTokenSource();
        _loading = cts;
        ErrorText = null;
        BusyText = "Reading the waiting devices";
        IsBusy = true;
        try
        {
            // Pages by cursor (address order): entries the server removes meanwhile never shift a page.
            var rows = new List<WaitingRowViewModel>();
            string? after = null;
            do
            {
                WaitingList page = await _api.ListWaitingAsync(after, PageSize, cts.Token).ConfigureAwait(true);
                rows.AddRange(page.Devices.Select(d => new WaitingRowViewModel(d)));
                after = page.NextAfter.Length > 0 && page.Devices.Count > 0 ? page.NextAfter : null;
            }
            while (after is not null);

            _all.Clear();
            _all.AddRange(rows);
            ApplySearch();
        }
        catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
        {
            // A newer read replaced this one (Try now, a round started or ended): not an error.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = "Reading the waiting devices failed: " + Message(ex);
        }
        finally
        {
            if (ReferenceEquals(_loading, cts))
            {
                _loading = null;
                IsBusy = false;
            }
        }
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        Selected.ReplaceAll([]);
        Rows.ReplaceAll(_all.Where(r => r.Matches(SearchText)).ToList());
        CountText = Rows.Count == _all.Count
            ? string.Create(CultureInfo.CurrentCulture, $"{_all.Count:N0} devices")
            : string.Create(CultureInfo.CurrentCulture, $"{Rows.Count:N0} of {_all.Count:N0} devices");
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(TryNowTip));
        OnPropertyChanged(nameof(ExportTip));
        TryNowCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The rows an action applies to: the selection, else every row the search shows.</summary>
    private List<WaitingRowViewModel> Targets() => Selected.Count > 0 ? [.. Selected.OfType<WaitingRowViewModel>()] : [.. Rows];

    /// <summary>Whether the targets are the whole list (one call with "all" instead of 30,000 ids).</summary>
    private bool TargetsAll => Selected.Count == 0 && Rows.Count == _all.Count;

    private bool HasTargets() => !IsBusy && (Selected.Count > 0 || Rows.Count > 0);

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task TryNowAsync()
    {
        await RunAsync("Asking the server to try them now", async ct =>
        {
            WaitingSummary summary = await _api.TryWaitingNowAsync(TargetsAll ? [] : Targets().Select(r => r.Id).ToList(), TargetsAll, ct).ConfigureAwait(true);
            State.Apply(summary);
        }).ConfigureAwait(true);
        OnPropertyChanged(nameof(ScheduleText));
        await LoadAsync().ConfigureAwait(true); // the rows asked for say "Checking..." now
    }

    private bool CanRemove() => !IsBusy && Selected.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        List<WaitingRowViewModel> targets = [.. Selected.OfType<WaitingRowViewModel>()];
        string what = targets.Count == 1 ? targets[0].Address : string.Create(CultureInfo.CurrentCulture, $"{targets.Count:N0} devices");
        if (!await _dialogs.ConfirmAsync("Waiting devices", $"Remove {what} from the waiting list? OADM stops trying to add them.", "Remove").ConfigureAwait(true))
        {
            return;
        }

        bool ok = await RunAsync("Removing", async ct =>
        {
            WaitingSummary summary = await _api.RemoveWaitingAsync(targets.Select(r => r.Id).ToList(), all: false, ct).ConfigureAwait(true);
            State.Apply(summary);
        }).ConfigureAwait(true);
        if (ok)
        {
            var gone = targets.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            _all.RemoveAll(r => gone.Contains(r.Id));
            ApplySearch();
        }
    }

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task ExportAsync()
    {
        byte[] content = WriteCsv(Targets());
        try
        {
            await _dialogs.SaveFileAsync("Export waiting devices", string.Create(CultureInfo.InvariantCulture, $"oadm-waiting-{DateTime.Now:yyyy-MM-dd}.csv"),
                DevicesViewModel.CsvFile, content).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _dialogs.ShowMessageAsync("Export waiting devices", "The file could not be saved: " + ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>The import's format (Address, Tags, Site; MAC address and the last result for reading). Never a password.</summary>
    public static byte[] WriteCsv(IEnumerable<WaitingRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        Csv.AppendRecord(sb, ["Address", "Tags", "Site", "MAC address", "Last try", "Result"]);
        foreach (WaitingRowViewModel row in rows)
        {
            Csv.AppendRecord(sb, [row.Address, row.TagsText, row.SiteName, row.MacAddress, row.Device.LastTry is null ? "" : row.LastTryText, row.ResultText]);
        }

        string text = sb.ToString();
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] data = new byte[bom.Length + Encoding.UTF8.GetByteCount(text)];
        bom.CopyTo(data, 0);
        Encoding.UTF8.GetBytes(text, 0, text.Length, data, bom.Length);
        return data;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>A round started: the list again, its rows say "Checking...".</summary>
    public void OnRoundStarted() => _ = LoadAsync();

    /// <summary>After a round of the server: the list again.</summary>
    public void OnRoundEnded()
    {
        OnPropertyChanged(nameof(ScheduleText));
        _ = LoadAsync();
    }

    /// <summary>The state changed (trying, next try).</summary>
    public void OnStateChanged() => OnPropertyChanged(nameof(ScheduleText));

    private async Task<bool> RunAsync(string text, Func<CancellationToken, Task> call)
    {
        ErrorText = null;
        BusyText = text;
        IsBusy = true;
        try
        {
            await call(CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = Message(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Message(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
}
