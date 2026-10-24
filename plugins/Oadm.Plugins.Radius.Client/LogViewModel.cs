using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One authentication of the Log tab.</summary>
public sealed class LogRowViewModel(LogRow source)
{
    public LogRow Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    public string Time => RadiusTexts.Time(Source.TimeUtc);

    public string Switch => Source.SwitchName;

    public string Port => Source.Port;

    public string Mac => Source.Mac;

    public string Identity => Source.Identity ?? string.Empty;

    public string Device => Source.DeviceName;

    public string IpAddress => Source.IpAddress;

    /// <summary>"Authenticated" or "Failed".</summary>
    public string Result => Source.Result == LogResults.Authenticated ? "Authenticated" : "Failed";

    public bool IsOk => Source.Result == LogResults.Authenticated && !HasWarning;

    /// <summary>Passed with a warning (e.g. the MAC does not match the certificate): amber.</summary>
    public bool IsWarning => Source.Result == LogResults.Authenticated && HasWarning;

    public bool IsError => Source.Result == LogResults.Failed;

    public bool HasWarning => !string.IsNullOrEmpty(Source.Warning);

    /// <summary>The refusal's reason, or the warning of a session that passed.</summary>
    public string Reason => Source.Result == LogResults.Failed ? Source.Reason ?? string.Empty : Source.Warning ?? string.Empty;

    public string Vlan => Source.Vlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The device sent a certificate: View certificate shows it.</summary>
    public bool HasCertificate => Source.HasCertificate;

    /// <summary>A row of requests from a sender that is no switch: Add switch fills in its address.</summary>
    public bool IsFromUnknownSender => Source.Sender is { Length: > 0 };

    /// <summary>The search text is in any column (one pass for the live rows).</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Time.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Switch.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Port.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Mac.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Identity.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Device.Contains(search, StringComparison.OrdinalIgnoreCase)
        || IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Result.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Vlan.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the log asks of its view: the shared certificate window.</summary>
public interface ILogDialogs
{
    Task ShowCertificateAsync(CertificateDetails certificate);
}

/// <summary>
/// The Log tab (like the EST enrollments): the authentications, newest first, read from the server 500 at a time with the
/// search over every column (the server searches; one grid reset per page); new rows arrive live on the first page; View
/// certificate (the shared certificate window); Delete all (red, confirmed, users with all devices). Rows that arrive while a
/// page is read are added after it (once each); the chosen row stays chosen over live rows; a log read before the page was
/// left is read again when the tab opens (<see cref="MarkStale"/>).
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly Func<bool> _mayManage;
    private CancellationTokenSource? _searchDelay;
    private int _readVersion;
    private bool _loadedOnce;
    private bool _stale;
    private List<LogRow>? _pending; // live rows that came in while a page was read

    public LogViewModel(ICorePluginClientContext ctx, Func<bool> mayManage, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The certificate window (the page view).</summary>
    public ILogDialogs? Dialogs { get; set; }

    /// <summary>Wait after typing in the search before the server is asked.</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The time zone the page shows the times in (sent with every read so the search matches them).</summary>
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;

    /// <summary>The entries the server keeps (the saved setting): the live rows never count beyond it.</summary>
    public int Kept { get; set; } = RadiusLimits.DefaultLogKept;

    public RangeObservableCollection<LogRowViewModel> Rows { get; } = [];

    public bool IsLoaded => _loadedOnce;

    /// <summary>The tab must be read when it opens: never read, or read before the page was left (no events meanwhile).</summary>
    public bool NeedsLoad => !_loadedOnce || _stale;

    /// <summary>The page was left: no events came in since, the log is read again when the tab opens.</summary>
    public void MarkStale() => _stale = _loadedOnce;

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewCertificateCommand))]
    public partial LogRowViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(EmptyText), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand), nameof(DeleteAllCommand))]
    public partial int Total { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Offset { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool MayManage => _mayManage();

    /// <summary>"1-500 of 12,345"; empty without rows.</summary>
    public string PageText => Total == 0 ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{Offset + 1:N0}-{Math.Min(Offset + RadiusLimits.MaxLogPage, Total):N0} of {Total:N0}");

    public bool HasPages => Total > RadiusLimits.MaxLogPage;

    /// <summary>One line without rows: what to do first, or that nothing matches.</summary>
    public string? EmptyText => Total > 0 || !_loadedOnce ? null
        : !string.IsNullOrWhiteSpace(Search) ? "No entry matches the search."
        : "No authentications yet. Add a switch and turn on 802.1X on its ports.";

    /// <summary>Reads the page at <see cref="Offset"/> with the search (the overlay the first time, then the progress row).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var version = Interlocked.Increment(ref _readVersion);
        if (_loadedOnce)
        {
            IsReloading = true;
        }
        else
        {
            IsLoading = true;
        }

        _pending ??= [];
        try
        {
            // The server searches the time also as this page shows it (local time).
            var query = new LogQuery(string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), Offset, RadiusLimits.MaxLogPage) { TimeZoneId = TimeZoneId };
            var page = RadiusJson.Deserialize<LogPage>(await _ctx.InvokeAsync(RadiusMethods.ListLog, RadiusJson.Serialize(query), ct).ConfigureAwait(true));
            if (version != Volatile.Read(ref _readVersion))
            {
                return;
            }

            var selected = Selected?.Source.Id;
            Rows.ReplaceAll(page.Rows.Select(r => new LogRowViewModel(r)));
            _loadedOnce = true;
            _stale = false;
            Total = page.Total;
            Error = null;
            Selected = selected is { } id ? Rows.FirstOrDefault(r => r.Source.Id == id) : null;
            var pending = _pending;
            _pending = null;
            if (pending is { Count: > 0 })
            {
                var shown = Rows.Select(r => r.Source.Id).ToHashSet();
                Add([.. pending.Where(r => !shown.Contains(r.Id)).DistinctBy(r => r.Id)]); // the page may hold them already
            }

            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(IsLoaded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                Error = "Cannot read the log: " + ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                _pending = null;
                IsLoading = false;
                IsReloading = false;
            }
        }
    }

    /// <summary>
    /// New rows of the <c>log</c> event: on top of the first page (those that match the search), in one step; while a page is
    /// read they wait for it. The chosen row stays chosen.
    /// </summary>
    public void Add(IReadOnlyList<LogRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        if (_pending is { } pending)
        {
            pending.AddRange(rows);
            return;
        }

        if (!_loadedOnce)
        {
            return;
        }

        var search = Search?.Trim() ?? string.Empty;
        var added = rows.Select(r => new LogRowViewModel(r)).Where(r => r.Matches(search)).OrderByDescending(r => r.Source.TimeUtc).ToList();
        if (added.Count == 0)
        {
            return;
        }

        if (Offset == 0)
        {
            if (Rows.Count + added.Count > RadiusLimits.MaxLogPage)
            {
                var selected = Selected;
                Rows.ReplaceAll([.. added.Concat(Rows).Take(RadiusLimits.MaxLogPage)]); // one reset, the page stays 500 rows
                if (selected is not null)
                {
                    // The reset clears the grid's choice: the same row again while it is still on the page.
                    Selected = Rows.Contains(selected) ? selected : null;
                }
            }
            else
            {
                Rows.InsertRange(0, added);
            }
        }

        Total = Math.Min(Total + added.Count, Math.Max(Kept, Total));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>
    /// The <c>logCleared</c> event: every page is empty. A read still running is dropped (its answer may hold the deleted
    /// rows) and so are the live rows that waited for it.
    /// </summary>
    public void Cleared()
    {
        Interlocked.Increment(ref _readVersion);
        _pending = null;
        IsLoading = false;
        IsReloading = false;
        _loadedOnce = true;
        _stale = false;
        Error = null;
        Rows.ReplaceAll([]);
        Offset = 0;
        Total = 0;
        Selected = null;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsLoaded));
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        DeleteAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        var delay = _searchDelay = new CancellationTokenSource();
        _ = SearchLaterAsync(delay.Token);
    }

    private async Task SearchLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDelay, _time, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // typed again
        }

        if (_loadedOnce || IsLoading)
        {
            Offset = 0;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private bool CanPrevious() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync()
    {
        Offset = Math.Max(0, Offset - RadiusLimits.MaxLogPage);
        return LoadAsync();
    }

    private bool CanNext() => Offset + RadiusLimits.MaxLogPage < Total;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync()
    {
        Offset += RadiusLimits.MaxLogPage;
        return LoadAsync();
    }

    private bool CanView() => Selected is { HasCertificate: true };

    /// <summary>View certificate (toolbar, double click): the client certificate of the row, described here from its DER.</summary>
    [RelayCommand(CanExecute = nameof(CanView))]
    private async Task ViewCertificateAsync()
    {
        if (Selected is not { HasCertificate: true } row)
        {
            return;
        }

        try
        {
            var reply = RadiusJson.Deserialize<LogCertificate>(await _ctx.InvokeAsync(RadiusMethods.GetLogCertificate, RadiusJson.Serialize(new LogEntryRef(row.Source.Id)), CancellationToken.None).ConfigureAwait(true));
            var description = CertificateDescriber.Describe(Convert.FromBase64String(reply.Der));
            var name = row.Device.Length > 0 ? row.Device : row.Mac.Length > 0 ? row.Mac : row.Identity;
            var details = new CertificateDetails(description, $"Certificate · {name} · IEEE 802.1X", _time.GetUtcNow().UtcDateTime);
            if (Dialogs is { } dialogs)
            {
                await dialogs.ShowCertificateAsync(details).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("View certificate", ex is FormatException ? "The certificate could not be read." : ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    private bool CanDeleteAll() => MayManage && Total > 0;

    /// <summary>Delete all (red): every entry of the log, confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteAll))]
    private async Task DeleteAllAsync()
    {
        if (!await _ctx.ConfirmAsync("Delete all", "Delete every entry of the log? Other clients see the empty log too.", "Delete all").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(RadiusMethods.DeleteLog, null, CancellationToken.None).ConfigureAwait(true);
            Cleared();
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
    }
}
