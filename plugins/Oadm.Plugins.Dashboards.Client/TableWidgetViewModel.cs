using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>A row of a table widget as it shows.</summary>
public sealed record TableEntry(string TimeText, string Text);

/// <summary>
/// The rows of one table widget, newest first, and its search over the time and the text. It holds what the server sent, at
/// most <see cref="DashboardsPluginInfo.MaxTableRows"/>; a batch of rows is one change of the list.
/// </summary>
public sealed partial class TableWidgetViewModel(TableKey key) : ObservableObject
{
    private readonly List<TableEntry> _all = []; // newest first
    private string? _ring;
    private long _last;

    /// <summary>What the widget records.</summary>
    public TableKey Key { get; } = key;

    /// <summary>The rows that match the search.</summary>
    public RangeObservableCollection<TableEntry> Rows { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>The line in an empty list; null while it has rows.</summary>
    public string? EmptyText => Rows.Count > 0 ? null : _all.Count > 0 ? DashboardTexts.NoMatchingRows : DashboardTexts.NoValue;

    public bool ShowEmpty => Rows.Count == 0;

    /// <summary>How many rows the widget holds, also those the search hides.</summary>
    public int Count => _all.Count;

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>Asks for what this widget does not have yet.</summary>
    internal TableRead ReadOf(string widgetId) => new(widgetId, Key, _ring, _last);

    /// <summary>The server's new rows (oldest first); rows of another run replace what the widget has.</summary>
    internal void Apply(TableRows rows)
    {
        if (rows.Ring != _ring)
        {
            _ring = rows.Ring;
            _last = 0;
            _all.Clear();
        }
        else if (rows.Rows.Count == 0)
        {
            return;
        }

        _all.InsertRange(0, rows.Rows.Reverse().Select(Entry));
        if (_all.Count > DashboardsPluginInfo.MaxTableRows)
        {
            _all.RemoveRange(DashboardsPluginInfo.MaxTableRows, _all.Count - DashboardsPluginInfo.MaxTableRows);
        }

        _last = rows.Rows.Count > 0 ? rows.Rows[^1].Seq : _last;
        Filter();
    }

    private static TableEntry Entry(TableRow row) =>
        new(row.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), row.Text);

    private void Filter()
    {
        var search = Search.Trim();
        Rows.ReplaceAll(search.Length == 0 ? _all : [.. _all.Where(r => Matches(r, search))]);
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private static bool Matches(TableEntry row, string search) =>
        row.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || row.TimeText.Contains(search, StringComparison.OrdinalIgnoreCase);
}
