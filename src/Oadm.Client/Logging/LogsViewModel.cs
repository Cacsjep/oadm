using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Logging;

/// <summary>Entry of the level filter: shows entries at or above <see cref="MinimumRank"/>.</summary>
public sealed record LogLevelOption(string Title, int MinimumRank)
{
    public override string ToString() => Title;
}

/// <summary>Logs page: live client log with level filter and search.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly ObservableCollection<LogEntry> _source;

    public LogsViewModel(LogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _source = store.Entries;
        SelectedLevel = Levels[0];
        _source.CollectionChanged += OnSourceChanged;
        Rebuild();
    }

    public static IReadOnlyList<LogLevelOption> Levels { get; } =
    [
        new("All levels", 0),
        new("Information and above", Rank("Information")),
        new("Warnings and errors", Rank("Warning")),
        new("Errors only", Rank("Error")),
    ];

    /// <summary>Entries matching the filter, newest first.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

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
        Entries.Clear();
        foreach (LogEntry entry in _source.Where(Matches))
        {
            Entries.Add(entry);
        }

        OnPropertyChanged(nameof(StatusLine));
    }
}
