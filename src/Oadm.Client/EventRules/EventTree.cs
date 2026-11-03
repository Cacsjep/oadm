using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.EventRules;

/// <summary>A row of the event tree: a folder or an event, indented by its depth.</summary>
public abstract class EventTreeRow : ObservableObject
{
    private protected EventTreeRow(string text, int depth)
    {
        Text = text;
        Depth = depth;
    }

    /// <summary>What the row shows: the folder's name, or the event's name in its folder.</summary>
    public string Text { get; }

    /// <summary>0 for a top folder.</summary>
    public int Depth { get; }
}

/// <summary>A folder of the event tree ("Devices", "Any device"); a click opens or closes it.</summary>
public sealed partial class EventTreeFolder : EventTreeRow
{
    internal EventTreeFolder(string name, string key, int depth) : base(name, depth) => Key = key;

    /// <summary>The folder's path, its names joined by " › " (unique in the tree).</summary>
    public string Key { get; }

    internal List<EventTreeFolder> Folders { get; } = [];

    internal List<EventTreeEvent> Events { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Shows its content now: opened, or while searching (the chevron points down).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; internal set; }
}

/// <summary>An event of the event tree; a click chooses it.</summary>
public sealed class EventTreeEvent : EventTreeRow
{
    internal EventTreeEvent(EventTypeOption option, string text, int depth, string searchText) : base(text, depth)
    {
        Option = option;
        SearchText = searchText;
    }

    public EventTypeOption Option { get; }

    /// <summary>The event's folders and name in lower case: what the search looks in.</summary>
    internal string SearchText { get; }

    /// <summary>What the event means (the bottom line of the picker), empty when the source gives nothing.</summary>
    public string Description => Option.Entry.Description;
}

/// <summary>
/// The events of the rule dialog as a tree (the folders each event declares, <c>EventTypeInfo.Folders</c>) in one flat,
/// virtualized list of rows: folders in the catalog's order, a folder's subfolders before its events. The search shows
/// only the events whose name or folders contain the text, their folders open. Building and filtering are O(n).
/// </summary>
public sealed partial class EventTree : ObservableObject
{
    public const string Separator = " › ";

    private readonly List<EventTreeFolder> _roots = [];
    private readonly Dictionary<string, EventTreeFolder> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<EventTypeOption, EventTreeEvent> _events = [];

    public EventTree(IEnumerable<EventTypeOption> options)
    {
        foreach (var option in options)
        {
            var path = Folders(option);
            EventTreeFolder? parent = null;
            for (var i = 0; i < path.Count; i++)
            {
                var key = string.Join(Separator, path.Take(i + 1));
                if (!_folders.TryGetValue(key, out var folder))
                {
                    folder = new EventTreeFolder(path[i], key, i) { IsExpanded = i == 0 }; // the top folders start open
                    _folders[key] = folder;
                    (parent?.Folders ?? _roots).Add(folder);
                }

                parent = folder;
            }

            var text = TextIn(option, path[^1]);
            var leaf = new EventTreeEvent(option, text, path.Count, (string.Join(" ", path) + " " + text).ToLower(CultureInfo.CurrentCulture));
            parent!.Events.Add(leaf);
            _events[option] = leaf;
        }

        Sort(_roots);
        Refresh();
    }

    /// <summary>
    /// The order of the folders: Devices first, then the other areas; in an area Selected devices, then Any device, then the
    /// rest; each by name otherwise. The events of a folder keep the catalog's order (by name).
    /// </summary>
    internal static int Rank(string folder) => folder switch
    {
        "Devices" or "Selected devices" => 0,
        "Any device" => 1,
        _ => 2,
    };

    private static void Sort(List<EventTreeFolder> folders)
    {
        folders.Sort((a, b) => Rank(a.Text) != Rank(b.Text) ? Rank(a.Text).CompareTo(Rank(b.Text)) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Text, b.Text));
        foreach (var folder in folders)
        {
            Sort(folder.Folders);
        }
    }

    /// <summary>The rows to show now (one Reset per change).</summary>
    public RangeObservableCollection<EventTreeRow> Rows { get; } = [];

    /// <summary>The search text; empty shows the folders as opened.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>The search found nothing.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The event's folders, top first; an older server sends none: its group.</summary>
    public static IReadOnlyList<string> Folders(EventTypeOption option) =>
        option.Entry.Path.Count > 0 ? option.Entry.Path : [option.Entry.Group];

    /// <summary>The event's folders and name as the closed picker shows them ("Devices › Selected devices › Device unreachable").</summary>
    public static string PathText(EventTypeOption option) =>
        string.Join(Separator, Folders(option).Append(TextIn(option, Folders(option)[^1])));

    /// <summary>
    /// The event's name in its folder: without the folder's name it starts with ("Any device: unreachable" in "Any device"
    /// is "Unreachable"), "(turned off)" when its plugin is off.
    /// </summary>
    public static string TextIn(EventTypeOption option, string folder)
    {
        var name = option.Entry.DisplayName;
        var prefix = folder + ": ";
        if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
        {
            name = name[prefix.Length..];
            name = char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
        }

        return option.Entry.Available ? name : name + " (turned off)";
    }

    /// <summary>Opens or closes a folder (no effect while searching: every match shows).</summary>
    public void Toggle(EventTreeFolder folder)
    {
        if (Query.Length > 0)
        {
            return;
        }

        folder.IsExpanded = !folder.IsExpanded;
        Refresh();
    }

    /// <summary>Opens the folders of an event (the chosen one, when the picker opens) and returns its row.</summary>
    public EventTreeEvent? Reveal(EventTypeOption? option)
    {
        if (option is null || !_events.TryGetValue(option, out var leaf))
        {
            return null;
        }

        var path = Folders(option);
        var changed = false;
        for (var i = 0; i < path.Count; i++)
        {
            var folder = _folders[string.Join(Separator, path.Take(i + 1))];
            changed |= !folder.IsExpanded;
            folder.IsExpanded = true;
        }

        if (changed)
        {
            Refresh();
        }

        return leaf;
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        var query = Query.Trim().ToLower(CultureInfo.CurrentCulture);
        var rows = new List<EventTreeRow>();
        foreach (var root in _roots)
        {
            Add(root, query, rows);
        }

        Rows.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
    }

    // Adds the folder and what shows of it; while searching only folders with a match, open.
    private static bool Add(EventTreeFolder folder, string query, List<EventTreeRow> rows)
    {
        var at = rows.Count;
        rows.Add(folder);
        folder.IsOpen = query.Length > 0 || folder.IsExpanded;
        if (query.Length == 0 && !folder.IsExpanded)
        {
            return true;
        }

        var any = false;
        foreach (var sub in folder.Folders)
        {
            any |= Add(sub, query, rows);
        }

        foreach (var leaf in folder.Events)
        {
            if (query.Length == 0 || leaf.SearchText.Contains(query, StringComparison.Ordinal))
            {
                rows.Add(leaf);
                any = true;
            }
        }

        if (query.Length > 0 && !any)
        {
            rows.RemoveAt(at);
        }

        return query.Length == 0 || any;
    }
}
