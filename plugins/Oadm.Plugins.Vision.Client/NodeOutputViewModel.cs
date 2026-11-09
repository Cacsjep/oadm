using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// An object of a node's output in this frame: Object (the tracked id), Class, Score, Size (its height as a share of the
/// picture's, as the Filter's sizes count it), Time inside.
/// </summary>
public sealed record ObjectRow(string ObjectId, string Class, string Score, string Size, string TimeInside);

/// <summary>A crossing of a Line node: Time, Direction, Class, Object.</summary>
public sealed record CrossingRow(string Time, string Direction, string Class, string ObjectId);

/// <summary>A fire of an Event node the preview would have sent: Time and its values.</summary>
public sealed record FireRow(string Time, string Text);

/// <summary>What the live output of a node shows (docs/spec/vision-center.md "Page").</summary>
public enum OutputShape
{
    /// <summary>A line of text only (Camera, a node without input or with an error).</summary>
    Note,

    /// <summary>Detect, Classify, Filter, Track, Area: the objects of this frame.</summary>
    Objects,

    /// <summary>Line: In and Out, the last crossings.</summary>
    Crossings,

    /// <summary>Count: the value.</summary>
    Number,

    /// <summary>Event: on or off, the last fires.</summary>
    Event,
}

/// <summary>
/// The selected node's live output, the feedback the user tunes with: objects of this frame with the node's time per picture,
/// the last 20 crossings, the count, an Event node's state and the last times it fired (a preview never publishes). Rows are
/// replaced once per preview event (at most twice a second).
/// </summary>
public sealed partial class NodeOutputViewModel : ObservableObject
{
    /// <summary>The objects table shows the first ones; the line above it says how many there are.</summary>
    public const int MaxRows = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNote), nameof(IsObjects), nameof(IsCrossings), nameof(IsNumber), nameof(IsEvent))]
    public partial OutputShape Shape { get; private set; }

    public bool IsNote => Shape == OutputShape.Note;

    public bool IsObjects => Shape == OutputShape.Objects;

    public bool IsCrossings => Shape == OutputShape.Crossings;

    public bool IsNumber => Shape == OutputShape.Number;

    public bool IsEvent => Shape == OutputShape.Event;

    /// <summary>The line above the table ("5 in this picture · 18 ms per picture"), or the note.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Area with tracked objects: the Time inside column.</summary>
    [ObservableProperty]
    public partial bool ShowTimeInside { get; private set; }

    public RangeObservableCollection<ObjectRow> Objects { get; } = [];

    public RangeObservableCollection<CrossingRow> Crossings { get; } = [];

    public RangeObservableCollection<FireRow> Fires { get; } = [];

    [ObservableProperty]
    public partial string In { get; private set; } = "0";

    [ObservableProperty]
    public partial string Out { get; private set; } = "0";

    /// <summary>Count: the value, big.</summary>
    [ObservableProperty]
    public partial string Value { get; private set; } = "0";

    /// <summary>Event: the state chip (stateful kinds); null for stateless ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsOn), nameof(StateText))]
    public partial bool? On { get; private set; }

    public bool HasState => On is not null;

    public bool IsOn => On == true;

    public string StateText => On == true ? VisionTexts.On : VisionTexts.Off;

    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    /// <summary>Shows <paramref name="node"/>'s part of <paramref name="preview"/> (null: no frame yet).</summary>
    public void Apply(NodeViewModel node, PreviewEvent? preview)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.HasError)
        {
            Note(VisionTexts.FixErrorFirst);
            return;
        }

        var output = preview?.Nodes.LastOrDefault(o => o.NodeId == node.Id);
        if (node.IsCamera)
        {
            Note(output?.Live ?? node.Live);
            return;
        }

        if (node.InputKind is null && node.Info is not { Accepts.Count: 0 })
        {
            Note(VisionTexts.LinkInputFirst);
            return;
        }

        switch (node.Kind)
        {
            case NodeKinds.Compare or NodeKinds.Timer or NodeKinds.TimeWindow or NodeKinds.DeviceEvent or NodeKinds.Hold
                or NodeKinds.Throttle or NodeKinds.Logic or NodeKinds.Statistics:
                Note(string.IsNullOrEmpty(output?.Live) ? VisionTexts.NothingYet : output.Live);
                break;
            case NodeKinds.Line:
                Shape = OutputShape.Crossings;
                In = (output?.In ?? 0).ToString(CultureInfo.InvariantCulture);
                Out = (output?.Out ?? 0).ToString(CultureInfo.InvariantCulture);
                var crossings = preview?.RecentCrossings.GetValueOrDefault(node.Id) ?? output?.Crossings ?? [];
                Crossings.ReplaceAll(crossings.Take(VisionLimits.RecentPerNode).Select(c =>
                    new CrossingRow(VisionTexts.Time(c.At), VisionTexts.Direction(c.Direction), c.ClassName, VisionTexts.ObjectId(c.TrackId))));
                HasRows = Crossings.Count > 0;
                Summary = string.Empty;
                break;
            case NodeKinds.Count:
                Shape = OutputShape.Number;
                Value = (output?.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                Summary = node.InputKind == PortKind.Crossings
                    ? VisionTexts.InOut(output?.In ?? 0, output?.Out ?? 0) + string.Create(CultureInfo.InvariantCulture, $" · Net {(output?.In ?? 0) - (output?.Out ?? 0)}")
                    : VisionTexts.ObjectsNow;
                break;
            case NodeKinds.Event:
                Shape = OutputShape.Event;
                On = output?.On;
                var fires = preview?.RecentFires.GetValueOrDefault(node.Id) ?? [];
                Fires.ReplaceAll(fires.Take(VisionLimits.RecentPerNode).Select(f => new FireRow(VisionTexts.Time(f.At), f.Text)));
                HasRows = Fires.Count > 0;
                Summary = VisionTexts.PreviewSendsNoEvents;
                break;
            default:
                Shape = OutputShape.Objects;
                var objects = output?.Objects ?? [];
                var tracked = objects.Any(o => o.TrackId is not null);
                ShowTimeInside = node.Kind == NodeKinds.Area && tracked;
                Objects.ReplaceAll(objects.Take(MaxRows).Select(o => new ObjectRow(
                    o.TrackId is { } id ? VisionTexts.ObjectId(id) : "—",
                    o.Label?.ClassName ?? o.Detection.ClassName,
                    VisionTexts.Score(o.Label?.Score ?? o.Detection.Score),
                    VisionTexts.Size(o.Detection.Box.Height),
                    o.InsideSeconds is { } s ? VisionTexts.Seconds(s) : string.Empty)));
                HasRows = Objects.Count > 0;
                Summary = output is null ? VisionTexts.NothingYet
                    : output.Label is { } label && objects.Count == 0 ? $"{label.ClassName} {VisionTexts.Score(label.Score)}"
                    : VisionTexts.InThisPicture(objects.Count, output.Milliseconds, node.Kind == NodeKinds.Filter ? output.Dropped.Count : null)
                        + (node.Kind == NodeKinds.Filter ? VisionTexts.Sizes([.. objects.Concat(output.Dropped).Select(o => o.Detection.Box.Height)]) : string.Empty);
                break;
        }
    }

    private void Note(string text)
    {
        Shape = OutputShape.Note;
        Summary = text;
        HasRows = false;
    }
}
