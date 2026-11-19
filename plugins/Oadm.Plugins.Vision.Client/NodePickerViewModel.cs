using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A node kind in the shared node list (<see cref="NodePicker"/>): its icon, colour, group and line from the catalogue.</summary>
public sealed record PickerKind(NodeKindInfo Kind)
    : NodePickerItem(Kind.Label, Kind.Description, NodeLook.IconKey(Kind.Id), NodeLook.BrushKey(Kind.Id), Kind.Group);

/// <summary>
/// The node list (docs/spec/vision-center.md "Page"): Add node opens it with every kind, the + of a node with only the kinds
/// that take its output (and links the new node), the + of an empty control input with only the kinds whose output it takes
/// (a Timer for Reset; the new node links into it). A search narrows it; the groups keep the order of the catalogue. The
/// list itself is the shared <see cref="NodePicker"/>.
/// </summary>
public sealed class NodePickerViewModel : NodePickerModel
{
    /// <summary>The node whose + opened the list; null for Add node.</summary>
    public NodeViewModel? From { get; private set; }

    /// <summary>The node whose empty control input opened the list (<see cref="IntoPort"/>); the new node links into it.</summary>
    public NodeViewModel? Into { get; private set; }

    public string? IntoPort { get; private set; }

    public static string Placeholder => VisionTexts.SearchNodes;

    public static string EmptyText => VisionTexts.NoNodeMatches;

    /// <summary>
    /// Opens the list at (<paramref name="left"/>, <paramref name="top"/>): every kind of a group, or (from a node's +) only the
    /// kinds that take <paramref name="output"/>.
    /// </summary>
    public void Open(NodeViewModel? from, PortKind? output, double left, double top)
    {
        From = from;
        Into = null;
        IntoPort = null;
        Show(Rows(NodeKinds.All.Where(k => k.Group is not null && (from is null || (output is { } o && Fits(k, o))))), left, top);
    }

    /// <summary>
    /// Opens the list for the control input <paramref name="port"/> of <paramref name="into"/>: only the kinds whose output it
    /// takes (<paramref name="accepts"/>: a Timer's pulse, a yes or no).
    /// </summary>
    public void OpenInto(NodeViewModel into, string port, PortKind accepts, double left, double top)
    {
        ArgumentNullException.ThrowIfNull(into);
        Show(Rows(NodeKinds.All.Where(k => k.Group is not null && Gives(k, accepts))), left, top);
        From = null;
        Into = into;
        IntoPort = port;
    }

    public override void Close()
    {
        base.Close();
        From = null;
        Into = null;
        IntoPort = null;
    }

    /// <summary>A kind can give <paramref name="output"/>: from no input (a Timer) or from one of the inputs it takes.</summary>
    public static bool Gives(NodeKindInfo kind, PortKind output)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Accepts.Count == 0 ? kind.Output(null) == output : kind.Accepts.Any(a => kind.Output(a) == output);
    }

    /// <summary>The kinds the list shows now, in order (tests).</summary>
    public IReadOnlyList<NodeKindInfo> ShownKinds => [.. ShownItems.OfType<PickerKind>().Select(e => e.Kind)];

    /// <summary>A kind fits an output with its data input or one of its control inputs.</summary>
    private static bool Fits(NodeKindInfo kind, PortKind output) => kind.Takes(output) || kind.ControlInputs.Any(c => c.Accepts == output);

    private static IEnumerable<PickerKind> Rows(IEnumerable<NodeKindInfo> kinds) => kinds.Select(k => new PickerKind(k));
}
