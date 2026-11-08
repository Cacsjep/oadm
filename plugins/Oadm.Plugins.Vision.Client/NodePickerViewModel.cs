using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A row of the node list: a group heading, or a node kind with its line.</summary>
public abstract record PickerEntry;

/// <summary>"Models", "Objects", "Geometry", "Output".</summary>
public sealed record PickerGroup(string Name) : PickerEntry;

public sealed record PickerKind(NodeKindInfo Kind) : PickerEntry
{
    public string Label => Kind.Label;

    public string Description => Kind.Description;

    public string IconKey => NodeLook.IconKey(Kind.Id);

    public string BrushKey => NodeLook.BrushKey(Kind.Id);
}

/// <summary>
/// The node list (docs/spec/vision-center.md "Page"): Add node opens it with every kind, the + of a node with only the kinds
/// that take its output (and links the new node). A search narrows it; the groups keep the order of the catalogue.
/// </summary>
public sealed partial class NodePickerViewModel : ObservableObject
{
    private IReadOnlyList<NodeKindInfo> _kinds = [];

    /// <summary>The list shows.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The node whose + opened the list; null for Add node.</summary>
    public NodeViewModel? From { get; private set; }

    /// <summary>Where the list sits on the canvas (screen pixels of the canvas control).</summary>
    [ObservableProperty]
    public partial double Left { get; private set; }

    [ObservableProperty]
    public partial double Top { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public RangeObservableCollection<PickerEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public static string Placeholder => VisionTexts.SearchNodes;

    public static string EmptyText => VisionTexts.NoNodeMatches;

    /// <summary>
    /// Opens the list at (<paramref name="left"/>, <paramref name="top"/>): every kind of a group, or (from a node's +) only the
    /// kinds that take <paramref name="output"/>.
    /// </summary>
    public void Open(NodeViewModel? from, PortKind? output, double left, double top)
    {
        From = from;
        _kinds = [.. NodeKinds.All.Where(k => k.Group is not null && (from is null || (output is { } o && Fits(k, o))))];
        Left = left;
        Top = top;
        SearchText = string.Empty;
        Fill();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        From = null;
    }

    /// <summary>A kind fits an output with its data input or one of its control inputs.</summary>
    private static bool Fits(NodeKindInfo kind, PortKind output) => kind.Takes(output) || kind.ControlInputs.Any(c => c.Accepts == output);

    /// <summary>The kinds the list shows now, in order (tests).</summary>
    public IReadOnlyList<NodeKindInfo> ShownKinds => [.. Entries.OfType<PickerKind>().Select(e => e.Kind)];

    partial void OnSearchTextChanged(string value) => Fill();

    private void Fill()
    {
        var query = SearchText.Trim();
        var rows = new List<PickerEntry>();
        foreach (var group in _kinds.Where(k => query.Length == 0 || k.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).GroupBy(k => k.Group!))
        {
            rows.Add(new PickerGroup(group.Key));
            rows.AddRange(group.Select(k => new PickerKind(k)));
        }

        Entries.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
    }
}
