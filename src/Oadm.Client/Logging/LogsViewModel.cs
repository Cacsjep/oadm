using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Logging;

/// <summary>Entry of the level filter: shows entries at or above <see cref="MinimumRank"/>.</summary>
public sealed record LogLevelOption(string Title, int MinimumRank)
{
    public override string ToString() => Title;
}

/// <summary>The tabs of the Logs page.</summary>
public enum LogsTab
{
    Server,
    Client,
    Audit,
}

/// <summary>
/// Logs page: the switch Server (the server's log files, permission "See the server log"), Client (this client's log of
/// the session) and Audit (administrators). Server and Client show 200 entries a page, newest first, with the level filter
/// and the search. The Server tab comes first when the role may see it.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject, ILogPages
{
    public const int PageSize = 200;

    private readonly ObservableCollection<LogEntry> _source;
    private readonly Shell.UserSession? _session;
    private List<LogEntry> _matching = [];
    private bool _chosen;

    public LogsViewModel(LogStore store, AuditLogViewModel? audit = null, Shell.UserSession? session = null, ServerLogViewModel? server = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _source = store.Entries;
        Audit = audit;
        Server = server;
        _session = session;
        SelectedLevel = Levels[0];
        _source.CollectionChanged += OnSourceChanged;
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Shell.UserSession.IsAdmin))
                {
                    ApplyRole();
                }
            };
            session.PermissionsChanged += (_, _) => ApplyRole();
        }

        Tab = CanSeeServer ? LogsTab.Server : LogsTab.Client;
        Rebuild();
    }

    /// <summary>The server's audit log (Admin only); null in tests without it.</summary>
    public AuditLogViewModel? Audit { get; }

    /// <summary>The server's log (permission "See the server log"); null in tests without it.</summary>
    public ServerLogViewModel? Server { get; }

    /// <summary>The Audit tab is offered to administrators.</summary>
    public bool CanSeeAudit => Audit is not null && (_session?.IsAdmin ?? false);

    /// <summary>The Server tab is offered to roles with "See the server log" and users with all devices (its text names devices).</summary>
    public bool CanSeeServer => Server is not null && (_session?.Has(Oadm.Sdk.Plugins.HostPermissionKeys.LogsServer) ?? false) && (_session?.HasAllDevices ?? false);

    /// <summary>More than the client log: the switch shows.</summary>
    public bool ShowSwitch => CanSeeServer || CanSeeAudit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerTab), nameof(IsClientTab), nameof(IsAuditTab))]
    public partial LogsTab Tab { get; private set; }

    public bool IsServerTab => Tab == LogsTab.Server;

    public bool IsClientTab => Tab == LogsTab.Client;

    public bool IsAuditTab => Tab == LogsTab.Audit;

    [RelayCommand]
    private async Task ShowServerLogAsync()
    {
        if (!CanSeeServer || Server is null)
        {
            return;
        }

        _chosen = true;
        Tab = LogsTab.Server;
        await Server.LoadAsync(0).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowClientLog()
    {
        _chosen = true;
        Tab = LogsTab.Client;
    }

    [RelayCommand]
    private async Task ShowAuditAsync()
    {
        if (!CanSeeAudit || Audit is null)
        {
            return;
        }

        _chosen = true;
        Tab = LogsTab.Audit;
        await Audit.RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>The page is shown: the server tab reads its newest page.</summary>
    public async Task OnShownAsync()
    {
        if (!_chosen && CanSeeServer)
        {
            Tab = LogsTab.Server;
        }

        if (IsServerTab && Server is not null)
        {
            await Server.LoadAsync(Server.Page).ConfigureAwait(true);
        }
    }

    private void ApplyRole()
    {
        OnPropertyChanged(nameof(CanSeeAudit));
        OnPropertyChanged(nameof(CanSeeServer));
        OnPropertyChanged(nameof(ShowSwitch));
        if (!CanSeeAudit && IsAuditTab)
        {
            Tab = LogsTab.Client;
        }

        if (!CanSeeAudit)
        {
            Audit?.Clear();
        }

        if (!CanSeeServer)
        {
            Server?.Clear();
            if (IsServerTab)
            {
                Tab = LogsTab.Client;
            }
        }
        else if (!_chosen)
        {
            Tab = LogsTab.Server;
        }
    }

    public static IReadOnlyList<LogLevelOption> Levels { get; } =
    [
        new("All levels", 0),
        new("Information and above", Rank("Information")),
        new("Warnings and errors", Rank("Warning")),
        new("Errors only", Rank("Error")),
    ];

    /// <summary>The client entries of the current page that match the filter, newest first.</summary>
    public RangeObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial LogLevelOption SelectedLevel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    public partial string SearchText { get; set; } = "";

    public bool HasSearch => !string.IsNullOrEmpty(SearchText);

    /// <summary>0 = the newest page of the client log.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewerCommand), nameof(NewestCommand), nameof(OlderCommand))]
    public partial int Page { get; private set; }

    public string StatusLine => _matching.Count == _source.Count
        ? string.Create(CultureInfo.CurrentCulture, $"{_source.Count:N0} {(_source.Count == 1 ? "entry" : "entries")} since the client started")
        : string.Create(CultureInfo.CurrentCulture, $"{_matching.Count:N0} of {_source.Count:N0} entries");

    public string PageText => _matching.Count == 0
        ? "No entries"
        : string.Create(CultureInfo.CurrentCulture, $"Entries {Page * PageSize + 1:N0} to {Page * PageSize + Entries.Count:N0} of {_matching.Count:N0}");

    IRelayCommand ILogPages.NewestCommand => NewestCommand;

    IRelayCommand ILogPages.NewerCommand => NewerCommand;

    IRelayCommand ILogPages.OlderCommand => OlderCommand;

    partial void OnSelectedLevelChanged(LogLevelOption value) => Rebuild(firstPage: true);

    partial void OnSearchTextChanged(string value) => Rebuild(firstPage: true);

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private bool CanNewer() => Page > 0;

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private void Newest()
    {
        Page = 0;
        ShowPage();
    }

    [RelayCommand(CanExecute = nameof(CanNewer))]
    private void Newer()
    {
        Page--;
        ShowPage();
    }

    private bool CanOlder() => (Page + 1) * PageSize < _matching.Count;

    [RelayCommand(CanExecute = nameof(CanOlder))]
    private void Older()
    {
        Page++;
        ShowPage();
    }

    internal static int Rank(string level) => Oadm.Contracts.Logging.LogFileReader.Rank(level);

    private bool Matches(LogEntry entry) =>
        Rank(entry.Level) >= (SelectedLevel?.MinimumRank ?? 0)
        && (string.IsNullOrWhiteSpace(SearchText)
            || entry.Message.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
            || entry.Level.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The store inserts new entries at the top and trims the oldest at the end.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && e.NewItems is { Count: 1 })
        {
            var entry = (LogEntry)e.NewItems[0]!;
            if (Matches(entry))
            {
                _matching.Insert(0, entry);
                if (Page == 0)
                {
                    Entries.Insert(0, entry);
                    if (Entries.Count > PageSize)
                    {
                        Entries.RemoveAt(Entries.Count - 1);
                    }
                }
            }

            Notify();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (LogEntry old in e.OldItems)
            {
                // Trimmed entries are the oldest; records compare by value, so match by reference.
                var index = _matching.FindLastIndex(m => ReferenceEquals(m, old));
                if (index >= 0)
                {
                    _matching.RemoveAt(index);
                }
            }

            if (Page > 0 && Page * PageSize >= _matching.Count)
            {
                Page = Math.Max(0, (_matching.Count - 1) / PageSize);
                ShowPage();
            }

            Notify();
            return;
        }

        Rebuild();
    }

    private void Rebuild(bool firstPage = false)
    {
        _matching = [.. _source.Where(Matches)];
        if (firstPage)
        {
            Page = 0;
        }

        ShowPage();
    }

    /// <summary>One Reset per page change instead of one event per entry.</summary>
    private void ShowPage()
    {
        Entries.ReplaceAll([.. _matching.Skip(Page * PageSize).Take(PageSize)]);
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(PageText));
        OlderCommand.NotifyCanExecuteChanged();
    }
}
