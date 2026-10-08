using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Logging;

/// <summary>Entry of the level filter: shows entries at or above <see cref="MinimumRank"/>.</summary>
public sealed record LogLevelOption(string Title, int MinimumRank)
{
    public override string ToString() => Title;
}

/// <summary>Logs page: live client log with level filter and search; administrators also get the server's Audit tab.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly ObservableCollection<LogEntry> _source;
    private readonly Shell.UserSession? _session;

    public LogsViewModel(LogStore store, AuditLogViewModel? audit = null, Shell.UserSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _source = store.Entries;
        Audit = audit;
        _session = session;
        SelectedLevel = Levels[0];
        _source.CollectionChanged += OnSourceChanged;
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Shell.UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(CanSeeAudit));
                    if (!CanSeeAudit)
                    {
                        IsAuditTab = false;
                        Audit?.Clear();
                    }
                }
            };
        }

        Rebuild();
    }

    /// <summary>The server's audit log (Admin only); null in tests without it.</summary>
    public AuditLogViewModel? Audit { get; }

    /// <summary>The Audit tab is offered to administrators.</summary>
    public bool CanSeeAudit => Audit is not null && (_session?.IsAdmin ?? false);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientTab))]
    public partial bool IsAuditTab { get; private set; }

    public bool IsClientTab => !IsAuditTab;

    [RelayCommand]
    private void ShowClientLog() => IsAuditTab = false;

    [RelayCommand]
    private async Task ShowAuditAsync()
    {
        if (!CanSeeAudit || Audit is null)
        {
            return;
        }

        IsAuditTab = true;
        await Audit.RefreshAsync().ConfigureAwait(true);
    }

    public static IReadOnlyList<LogLevelOption> Levels { get; } =
    [
        new("All levels", 0),
        new("Information and above", Rank("Information")),
        new("Warnings and errors", Rank("Warning")),
        new("Errors only", Rank("Error")),
    ];

    /// <summary>Entries matching the filter, newest first.</summary>
    public RangeObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial LogLevelOption SelectedLevel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    public partial string SearchText { get; set; } = "";

    public bool HasSearch => !string.IsNullOrEmpty(SearchText);

    public string StatusLine => Entries.Count == _source.Count
        ? $"{_source.Count} entries"
        : $"{Entries.Count} of {_source.Count} entries";

    partial void OnSelectedLevelChanged(LogLevelOption value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    internal static int Rank(string level) => level switch
    {
        "Verbose" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        "Fatal" => 5,
        _ => 2,
    };

    private bool Matches(LogEntry entry) =>
        Rank(entry.Level) >= (SelectedLevel?.MinimumRank ?? 0)
        && (string.IsNullOrWhiteSpace(SearchText)
            || entry.Message.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
            || entry.Level.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The store only inserts new entries at the top and trims the oldest; mirror that cheaply.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && e.NewItems is { Count: 1 })
        {
            if (Matches((LogEntry)e.NewItems[0]!))
            {
                Entries.Insert(0, (LogEntry)e.NewItems[0]!);
            }

            OnPropertyChanged(nameof(StatusLine));
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (LogEntry old in e.OldItems)
            {
                // Trimmed entries are the oldest, at the end; records compare by value, so match by reference.
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(Entries[i], old))
                    {
                        Entries.RemoveAt(i);
                        break;
                    }
                }
            }

            OnPropertyChanged(nameof(StatusLine));
            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        // One Reset per filter change instead of one event per entry.
        Entries.ReplaceAll(_source.Where(Matches).ToList());
        OnPropertyChanged(nameof(StatusLine));
    }
}
