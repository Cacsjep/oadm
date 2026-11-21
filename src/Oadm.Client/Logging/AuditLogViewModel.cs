using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Oadm.Client.Logging;

/// <summary>One audit entry of the Audit tab.</summary>
public sealed class AuditRowViewModel
{
    public AuditRowViewModel(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Id = entry.Id;
        TimeText = entry.Time is null ? "" : entry.Time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        UserName = entry.UserName;
        ClientAddress = entry.ClientAddress;
        Action = entry.Action;
        Target = entry.Target;
        Detail = entry.Detail;
        IsProblem = entry.Action.Contains("failed", StringComparison.OrdinalIgnoreCase) || entry.Action.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }

    public long Id { get; }
    public string TimeText { get; }
    public string UserName { get; }
    public string ClientAddress { get; }
    public string Action { get; }
    public string Target { get; }
    public string Detail { get; }

    /// <summary>A detail longer than the cell's three lines may show (the outcome of a task run) in full; null for a short one.</summary>
    public string? DetailTip => Detail.Length > DetailTipLength ? Detail : null;

    /// <summary>From this length on the Detail cell may cut the text, so its tooltip shows it whole.</summary>
    public const int DetailTipLength = 200;

    /// <summary>Failed or locked logins: shown with the warning chip.</summary>
    public bool IsProblem { get; }
}

/// <summary>
/// Audit tab of the Logs page (Admin only): the server's audit log, newest first, <see cref="PageSize"/> entries a page
/// (<see cref="LogPager"/>). Search and date range go to the server, so every entry of the log is found; the pages follow
/// the entries' ids (before_id), so new entries never move an older page.
/// </summary>
public sealed partial class AuditLogViewModel : ObservableObject, ILogPages
{
    public const int PageSize = 200;

    private readonly IOadmApi _api;

    /// <summary>The before_id of each page shown so far: 0 for the newest, then the last id of the page before.</summary>
    private readonly List<long> _pageStarts = [0];
    private CancellationTokenSource? _load;
    private bool _clearing; // Clear resets the range without a read

    public AuditLogViewModel(IOadmApi api, TimeProvider? time = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Range = new DateRangeChoice(time);
        Range.Changed += (_, _) =>
        {
            if (!_clearing)
            {
                _ = LoadAsync(0);
            }
        };
    }

    public RangeObservableCollection<AuditRowViewModel> Entries { get; } = [];

    /// <summary>The date range left of the search (All at each start).</summary>
    public DateRangeChoice Range { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Total entries on the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial int TotalCount { get; private set; }

    /// <summary>Entries of the range and search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(OlderCommand))]
    public partial int MatchCount { get; private set; }

    /// <summary>0 = the newest page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NewerCommand), nameof(NewestCommand), nameof(OlderCommand))]
    public partial int Page { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public string StatusLine
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            return MatchCount == TotalCount
                ? Plural(TotalCount, "entry", "entries")
                : string.Create(CultureInfo.CurrentCulture, $"{MatchCount:N0} of {TotalCount:N0} entries");
        }
    }

    public string PageText => Entries.Count == 0
        ? "No entries"
        : string.Create(CultureInfo.CurrentCulture, $"Entries {Page * PageSize + 1:N0} to {Page * PageSize + Entries.Count:N0} of {MatchCount:N0}");

    IRelayCommand ILogPages.NewestCommand => NewestCommand;

    IRelayCommand ILogPages.NewerCommand => NewerCommand;

    IRelayCommand ILogPages.OlderCommand => OlderCommand;

    partial void OnSearchTextChanged(string value) => _ = LoadAsync(0, debounce: true);

    private bool CanNewer() => Page > 0;

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private Task NewestAsync() => LoadAsync(0);

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private Task NewerAsync() => LoadAsync(Page - 1);

    private bool CanOlder() => (Page + 1) * PageSize < MatchCount && Entries.Count == PageSize;

    [RelayCommand(CanExecute = nameof(CanOlder))]
    private Task OlderAsync() => LoadAsync(Page + 1);

    /// <summary>The server's last update (docs/spec/self-update.md): "Server updated to V4.9.0"; null without one.</summary>
    [ObservableProperty]
    public partial string? LastUpdate { get; private set; }

    /// <summary>"Last server update: V4.8.0 to V4.9.0, 2026-11-03 10:05" and the reason of a failure.</summary>
    [ObservableProperty]
    public partial string? LastUpdateDetail { get; private set; }

    [ObservableProperty]
    public partial bool LastUpdateOk { get; private set; }

    [ObservableProperty]
    public partial bool LastUpdateFailed { get; private set; }

    private async Task LoadLastUpdateAsync()
    {
        try
        {
            var info = await _api.GetUpdateAsync(CancellationToken.None).ConfigureAwait(true);
            if (info.Last is not { } last)
            {
                LastUpdate = null;
                return;
            }

            var outcome = Updates.UpdateOutcome.OfServer(last, info.ServerVersion, info.OlderSiteNodes);
            LastUpdate = outcome?.Title ?? Contracts.Updates.UpdateTexts.Updating(last.ToVersion);
            LastUpdateOk = outcome?.IsDone ?? false;
            LastUpdateFailed = outcome is { IsDone: false };
            LastUpdateDetail = string.Join(" · ", new[] { Updates.UpdateOutcome.LastLine(last) }.Concat(outcome?.Lines ?? []));
        }
        catch (RpcException)
        {
            LastUpdate = null; // a server without self update
        }
        catch (NotSupportedException)
        {
            LastUpdate = null;
        }
    }

    /// <summary>Reads the newest page again and the server's last update.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var lastUpdate = LoadLastUpdateAsync(); // side by side: it may wait for GitHub, the rows never do
        await LoadAsync(0).ConfigureAwait(true);
        await lastUpdate.ConfigureAwait(true);
    }

    /// <summary>The tab's filter: the date range as instants of now and the search (paging fields unset).</summary>
    public ListAuditRequest Filter()
    {
        var (from, to) = Range.Resolve();
        return new ListAuditRequest
        {
            Search = SearchText.Trim(),
            NotBefore = from is { } f ? Timestamp.FromDateTimeOffset(f) : null,
            NotAfter = to is { } t ? Timestamp.FromDateTimeOffset(t) : null,
        };
    }

    /// <summary>How many entries an export of the tab holds: the server counts the filter.</summary>
    public async Task<LogExportCount> CountForExportAsync(CancellationToken ct)
    {
        var request = Filter();
        request.Limit = 1;
        var list = await _api.ListAuditAsync(request, ct).ConfigureAwait(true);
        return LogExportCount.Of(list.MatchCount);
    }

    /// <summary>Reads one page; a newer request cancels the one before (typing in the search). Page n starts after page n - 1.</summary>
    public async Task LoadAsync(int page, bool debounce = false)
    {
        _load?.Cancel();
        using var load = new CancellationTokenSource();
        _load = load;
        try
        {
            if (debounce)
            {
                await Task.Delay(300, load.Token).ConfigureAwait(true);
            }

            page = Math.Clamp(page, 0, _pageStarts.Count - 1);
            if (page == 0)
            {
                _pageStarts.RemoveRange(1, _pageStarts.Count - 1);
            }

            IsLoading = true;
            var request = Filter();
            request.Limit = PageSize;
            request.BeforeId = _pageStarts[page];
            AuditList list = await _api.ListAuditAsync(request, load.Token).ConfigureAwait(true);
            if (load.IsCancellationRequested)
            {
                return;
            }

            Entries.ReplaceAll([.. list.Entries.Select(e => new AuditRowViewModel(e))]);
            if (_pageStarts.Count == page + 1 && Entries.Count > 0)
            {
                _pageStarts.Add(Entries[^1].Id);
            }

            Error = null;
            TotalCount = list.TotalCount;
            MatchCount = list.MatchCount;
            Page = page;
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(StatusLine));
            OlderCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && load.IsCancellationRequested)
        {
        }
        catch (RpcException ex)
        {
            Error = "The audit log cannot be read: " + ex.Status.Detail;
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                IsLoading = false;
                _load = null;
            }
        }
    }

    /// <summary>Clears the rows (logout).</summary>
    public void Clear()
    {
        _load?.Cancel();
        _clearing = true;
        try
        {
            Range.Reset();
        }
        finally
        {
            _clearing = false;
        }

        Error = null;
        _pageStarts.RemoveRange(1, _pageStarts.Count - 1);
        TotalCount = 0;
        MatchCount = 0;
        Page = 0;
        Entries.Clear();
        OnPropertyChanged(nameof(PageText));
    }

    private static string Plural(int n, string one, string many) =>
        n == 1 ? "1 " + one : string.Create(CultureInfo.CurrentCulture, $"{n:N0} {many}");
}
