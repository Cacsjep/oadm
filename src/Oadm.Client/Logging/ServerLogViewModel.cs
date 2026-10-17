using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Logging;

/// <summary>Paging of a log tab: newest page first, 200 entries a page (<see cref="LogPager"/>).</summary>
public interface ILogPages
{
    /// <summary>"Entries 1 to 200", "Entries 201 to 400 of 1,234".</summary>
    string PageText { get; }

    IRelayCommand NewestCommand { get; }

    IRelayCommand NewerCommand { get; }

    IRelayCommand OlderCommand { get; }
}

/// <summary>One server log entry of the grid.</summary>
public sealed record ServerLogRow(DateTimeOffset Time, string Level, string Source, string Message, string Exception)
{
    public string TimeText => Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>The class that logged it without its namespace: "ActionRunner".</summary>
    public string SourceText => Source.Length == 0 ? string.Empty : Source[(Source.LastIndexOf('.') + 1)..];

    public bool IsError => Level is "Error" or "Fatal";

    public bool IsWarning => Level == "Warning";

    public bool IsInfo => Level == "Information";

    /// <summary>The whole entry for the tooltip: source, message and exception.</summary>
    public string DetailText => string.Join("\n", new[] { Source, Message, Exception }.Where(t => t.Length > 0));

    public bool HasException => Exception.Length > 0;
}

/// <summary>
/// The Server tab of the Logs page: the server's log files, 200 entries a page, newest first
/// (ServerLogService.List, permission "See the server log"). Level filter and search go to the server; Refresh and
/// Newest read the first page again.
/// </summary>
public sealed partial class ServerLogViewModel(IOadmApi api) : ObservableObject, ILogPages
{
    public const int PageSize = 200;

    private CancellationTokenSource? _load;

    public RangeObservableCollection<ServerLogRow> Entries { get; } = [];

    [ObservableProperty]
    public partial LogLevelOption SelectedLevel { get; set; } = LogsViewModel.Levels[0];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>0 = the newest page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NewerCommand), nameof(NewestCommand))]
    public partial int Page { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(OlderCommand))]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public string PageText => Entries.Count == 0
        ? (Page == 0 ? "No entries" : "No more entries")
        : string.Create(CultureInfo.CurrentCulture, $"Entries {Page * PageSize + 1:N0} to {Page * PageSize + Entries.Count:N0}");

    IRelayCommand ILogPages.NewestCommand => NewestCommand;

    IRelayCommand ILogPages.NewerCommand => NewerCommand;

    IRelayCommand ILogPages.OlderCommand => OlderCommand;

    partial void OnSelectedLevelChanged(LogLevelOption value) => _ = LoadAsync(0);

    partial void OnSearchTextChanged(string value) => _ = LoadAsync(0, debounce: true);

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(Page);

    private bool CanNewer() => Page > 0;

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private Task NewestAsync() => LoadAsync(0);

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private Task NewerAsync() => LoadAsync(Page - 1);

    private bool CanOlder() => HasMore;

    [RelayCommand(CanExecute = nameof(CanOlder))]
    private Task OlderAsync() => LoadAsync(Page + 1);

    /// <summary>Reads one page; a newer request cancels the one before (typing in the search).</summary>
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

            IsLoading = true;
            var reply = await api.ListServerLogAsync(
                Math.Max(0, page) * PageSize, PageSize, LevelName(SelectedLevel.MinimumRank), SearchText.Trim(), load.Token).ConfigureAwait(true);
            if (load.IsCancellationRequested)
            {
                return;
            }

            Entries.ReplaceAll([.. reply.Entries.Select(ToRow)]);
            Page = Math.Max(0, page);
            HasMore = reply.HasMore;
            Error = null;
            OnPropertyChanged(nameof(PageText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Cancelled && load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = "The server log could not be read: " + (ex is Grpc.Core.RpcException rpc && rpc.Status.Detail.Length > 0 ? rpc.Status.Detail : ex.Message);
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

    /// <summary>Forgets the entries (the role lost the permission).</summary>
    public void Clear()
    {
        _load?.Cancel();
        Entries.ReplaceAll([]);
        Page = 0;
        HasMore = false;
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>The lowest level of a filter for the server: "Information", "Warning", "Error"; empty = all.</summary>
    private static string LevelName(int rank) => rank switch
    {
        <= 1 => string.Empty,
        2 => "Information",
        3 => "Warning",
        _ => "Error",
    };

    private static ServerLogRow ToRow(ServerLogEntry entry) => new(
        DateTimeOffset.TryParse(entry.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DateTimeOffset.MinValue,
        entry.Level,
        entry.Source,
        entry.Message,
        entry.Exception);
}
