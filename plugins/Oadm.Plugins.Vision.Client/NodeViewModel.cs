using System.Text.Json.Nodes;

using Avalonia;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>While a link is dragged: whether a node can take it (highlighted) or not (dimmed).</summary>
public enum LinkFit
{
    None,
    Fits,
    Dimmed,
}

/// <summary>
/// One node card on the canvas (docs/spec/vision-center.md "Page"): its kind, name, place, eye, the live line, the error
/// below it and what its link carries. Settings are kept as JSON by field key (a kind added later needs no change here).
/// </summary>
public sealed partial class NodeViewModel : ObservableObject
{
    public NodeViewModel(PipelineNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        Kind = node.Kind;
        Info = NodeKinds.Find(node.Kind);
        Name = node.Name;
        X = node.X;
        Y = node.Y;
        ShowOnVideo = node.ShowOnVideo;
        Settings = (JsonObject)node.Settings.DeepClone();
    }

    public string Id { get; }

    public string Kind { get; }

    /// <summary>The kind as this version knows it; null for a kind of a newer Vision Center.</summary>
    public NodeKindInfo? Info { get; }

    public bool IsCamera => Kind == NodeKinds.Camera;

    /// <summary>An Event node: its name is the event's name in the rules, so only it shows the Name field.</summary>
    public bool IsEvent => Kind == NodeKinds.Event;

    /// <summary>The kind's name ("Detect objects").</summary>
    public string KindLabel => NodeLook.Label(Kind);

    public string IconKey => NodeLook.IconKey(Kind);

    public string BrushKey => NodeLook.BrushKey(Kind);

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    /// <summary>"Show on video": the node's result over the live video (the selected node always shows).</summary>
    [ObservableProperty]
    public partial bool ShowOnVideo { get; set; }

    /// <summary>The node's settings by field key; replaced as a whole on every change.</summary>
    [ObservableProperty]
    public partial JsonObject Settings { get; set; }

    /// <summary>The live line ("5 objects · 18 ms"); empty before the first frame.</summary>
    [ObservableProperty]
    public partial string Live { get; set; } = string.Empty;

    /// <summary>The graph check's error, shown directly below the card; null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial LinkFit Fit { get; set; }

    /// <summary>What its input carries now; null without an input (or the Camera).</summary>
    [ObservableProperty]
    public partial PortKind? InputKind { get; set; }

    /// <summary>What its output carries now; null when it has none (Event) or nothing reaches it.</summary>
    [ObservableProperty]
    public partial PortKind? OutputKind { get; set; }

    /// <summary>The card has an input dot (every node but the Camera; a kind this version does not know keeps its link).</summary>
    public bool HasInputPort => Info is null || Info.Accepts.Count > 0;

    /// <summary>The card has an output dot (every node but Event).</summary>
    public bool HasOutputPort => Info is null
        || (Info.Accepts.Count == 0 ? Info.Output(null) is not null : Info.Accepts.Any(k => Info.Output(k) is not null));

    /// <summary>The control inputs of its kind (Reset, Send now, Only while, Second): dots on the left edge with their label.</summary>
    public IReadOnlyList<ControlInput> ControlInputs => Info?.ControlInputs ?? [];

    /// <summary>The card's height: a row more per control input.</summary>
    public double Height => CanvasGeometry.HeightFor(ControlInputs.Count);

    public Rect Rect => CanvasGeometry.NodeRect(X, Y, Height);

    /// <summary>The output dot: the bottom centre of the card.</summary>
    public Point OutputPoint => CanvasGeometry.OutputPort(X, Y, Height);

    /// <summary>The dot of the control input <paramref name="port"/>; null for the data input or a port it does not have.</summary>
    public Point? ControlPortAt(string? port)
    {
        var inputs = ControlInputs;
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].Port == port)
            {
                return CanvasGeometry.ControlPort(X, Y, i);
            }
        }

        return null;
    }

    /// <summary>The card has the eye: it draws on the video.</summary>
    public bool CanShowOnVideo => Info?.Draws ?? false;

    /// <summary>The node as stored in the pipeline.</summary>
    public PipelineNode ToNode() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        X = Math.Round(X),
        Y = Math.Round(Y),
        ShowOnVideo = ShowOnVideo,
        Settings = (JsonObject)Settings.DeepClone(),
    };
}

/// <summary>
/// A link on the canvas: from a node's output to another node's data input (its only one) or to one of its control inputs
/// (<see cref="ToPort"/>), with the live number of objects on it.
/// </summary>
public sealed partial class LinkViewModel(NodeViewModel from, NodeViewModel to, string? toPort = null) : ObservableObject
{
    /// <summary>The control input it goes into ("reset", "send"); null: the data input.</summary>
    public string? ToPort { get; } = toPort;

    public bool IsControl => ToPort is not null;

    /// <summary>Where it ends on its card: the top centre, or the control input's dot on the left edge.</summary>
    public Point End => To.ControlPortAt(ToPort) ?? CanvasGeometry.InputPort(To.X, To.Y);

    public Point Start => From.OutputPoint;

    public NodeViewModel From { get; } = from ?? throw new ArgumentNullException(nameof(from));

    public NodeViewModel To { get; } = to ?? throw new ArgumentNullException(nameof(to));

    /// <summary>The objects on it this frame ("5"); null while there is no number to show.</summary>
    [ObservableProperty]
    public partial string? Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public PipelineLink ToLink() => new(From.Id, To.Id) { ToPort = ToPort };
}
