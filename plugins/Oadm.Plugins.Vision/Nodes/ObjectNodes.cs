using Oadm.Plugins.Vision.Geometry;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tracking;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>Filter: keeps the objects that pass every check (class, score, size as a share of the picture's height, the class of Classify).</summary>
internal sealed class FilterNode : VisionNode
{
    private readonly HashSet<string>? _classes;
    private readonly double _minScore;
    private readonly double? _minSize;
    private readonly double? _maxSize;
    private readonly string? _label;

    public FilterNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.FilterKind, input)
    {
        _classes = NodeKindsSettings.ClassSet(node);
        _minScore = (NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinScoreKey)) ?? 50) / 100;
        _minSize = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinSizeKey)) / 100;
        _maxSize = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MaxSizeKey)) / 100;
        _label = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.LabelKey)) is { Length: > 0 } l ? l : null;
    }

    public bool Keeps(VisionObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = o.Detection;
        return (_classes is null || _classes.Contains(d.ClassName))
            && d.Score >= _minScore
            && (_minSize is not { } min || d.Box.Height >= min)
            && (_maxSize is not { } max || d.Box.Height <= max)
            && (_label is null || string.Equals(o.Label?.ClassName, _label, StringComparison.OrdinalIgnoreCase));
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var kept = new List<VisionObject>(input.Objects.Count);
        List<VisionObject>? dropped = null;
        foreach (var o in input.Objects)
        {
            if (Keeps(o))
            {
                kept.Add(o);
            }
            else
            {
                (dropped ??= []).Add(o);
            }
        }

        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = Input,
            Objects = kept,
            Dropped = (IReadOnlyList<VisionObject>?)dropped ?? [],
            Live = LiveText.Objects(kept.Count),
        });
    }
}

/// <summary>Track: an id per object that stays while it moves.</summary>
internal sealed class TrackNode : VisionNode
{
    private readonly ObjectTracker _tracker;

    public TrackNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.TrackKind, input)
    {
        Settings = SettingsOf(node);
        _tracker = new ObjectTracker(Settings);
    }

    public TrackerSettings Settings { get; }

    public static TrackerSettings SettingsOf(PipelineNode node)
    {
        var confirm = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.ConfirmKey)) ?? 3;
        var keep = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.KeepLostKey)) ?? 1;
        return new TrackerSettings((int)Math.Max(1, Math.Round(confirm)), TimeSpan.FromSeconds(Math.Max(0, keep)));
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tracked = _tracker.Update(input.Objects, frame.At);
        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.TrackedObjects,
            Objects = tracked,
            Live = $"{LiveText.Number(tracked.Count)} tracked",
        });
    }
}

/// <summary>Area: the objects inside a polygon, by their bottom centre, centre or any overlap; tracked ones with their time inside.</summary>
internal sealed class AreaNode : VisionNode
{
    private readonly IReadOnlyList<RelativePoint> _polygon;
    private readonly string _anchor;
    private readonly TimeSpan _keep;
    private readonly Dictionary<int, Inside> _inside = [];
    private readonly HashSet<int> _present = [];

    public AreaNode(PipelineNode node, PortKind? input, TimeSpan keepLost) : base(node, NodeKinds.AreaKind, input)
    {
        _polygon = NodeSettings.Points(VisionGraph.Setting(node, NodeKinds.PointsKey)) ?? [];
        _anchor = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.AnchorKey)) ?? NodeKinds.AnchorBottom;
        _keep = keepLost;
    }

    public bool Contains(RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return _anchor switch
        {
            NodeKinds.AnchorCentre => VisionGeometry.Inside(box.X + box.Width / 2, box.Y + box.Height / 2, _polygon),
            NodeKinds.AnchorOverlap => VisionGeometry.Overlaps(box, _polygon),
            _ => VisionGeometry.Inside(box.X + box.Width / 2, box.Bottom, _polygon),
        };
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var at = frame.At;
        var tracked = Input == PortKind.TrackedObjects;
        var result = new List<VisionObject>();
        _present.Clear();
        foreach (var o in input.Objects)
        {
            var id = o.TrackId;
            if (tracked && id is { } seen)
            {
                _present.Add(seen);
            }

            if (!Contains(o.Detection.Box))
            {
                if (tracked && id is { } outside)
                {
                    _inside.Remove(outside);
                }

                continue;
            }

            if (tracked && id is { } t)
            {
                var since = _inside.TryGetValue(t, out var entry) ? entry.Since : at;
                _inside[t] = new Inside(since, at);
                result.Add(o with { InsideSeconds = (at - since).TotalSeconds });
            }
            else
            {
                result.Add(o);
            }
        }

        if (tracked)
        {
            // Objects that vanished (lost by the tracker) keep their time while the tracker keeps them.
            List<int>? gone = null;
            foreach (var (id, entry) in _inside)
            {
                if (!_present.Contains(id) && at - entry.LastSeen > _keep)
                {
                    (gone ??= []).Add(id);
                }
            }

            if (gone is not null)
            {
                foreach (var id in gone)
                {
                    _inside.Remove(id);
                }
            }
        }

        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = Input,
            Objects = result,
            Live = $"{LiveText.Number(result.Count)} inside",
        });
    }

    private readonly record struct Inside(DateTimeOffset Since, DateTimeOffset LastSeen);
}

/// <summary>
/// Line: a crossing when a tracked object's bottom centre steps over the polyline. In is from the left of the line's
/// drawn direction to its right (<see cref="VisionGeometry.Side"/>; a line drawn from left to right: downwards on the
/// picture), Out back. One object never counts the same direction twice in a row.
/// </summary>
internal sealed class LineNode : VisionNode
{
    private readonly IReadOnlyList<RelativePoint> _line;
    private readonly TimeSpan _keep;
    private readonly Dictionary<int, Step> _last = [];

    public LineNode(PipelineNode node, PortKind? input, TimeSpan keepLost) : base(node, NodeKinds.LineKind, input)
    {
        _line = NodeSettings.Points(VisionGraph.Setting(node, NodeKinds.PointsKey)) ?? [];
        Directions = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.DirectionsKey)) ?? NodeKinds.DirectionsBoth;
        _keep = keepLost;
    }

    /// <summary><see cref="NodeKinds.DirectionsBoth"/>, In or Out.</summary>
    public string Directions { get; }

    public long In { get; private set; }

    public long Out { get; private set; }

    public void Reset()
    {
        In = 0;
        Out = 0;
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var at = frame.At;
        List<Crossing>? crossings = null;
        foreach (var o in input.Objects)
        {
            if (o.TrackId is not { } id)
            {
                continue;
            }

            var p = o.Detection.Box.BottomCentre;
            if (_last.TryGetValue(id, out var step))
            {
                if (VisionGeometry.Crossing(step.Point, p, _line) is { } dir && dir != step.Counted)
                {
                    step = step with { Counted = dir };
                    if (Counts(dir))
                    {
                        if (dir == CrossDirection.In)
                        {
                            In++;
                        }
                        else
                        {
                            Out++;
                        }

                        (crossings ??= []).Add(new Crossing(at, dir, id, o.Detection.ClassName, o.Detection.Score));
                    }
                }

                _last[id] = step with { Point = p, LastSeen = at };
            }
            else
            {
                _last[id] = new Step(p, at, null);
            }
        }

        List<int>? gone = null;
        foreach (var (id, step) in _last)
        {
            if (at - step.LastSeen > _keep)
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                _last.Remove(id);
            }
        }

        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Crossings,
            Crossings = (IReadOnlyList<Crossing>?)crossings ?? [],
            In = In,
            Out = Out,
            Live = InOut(Directions, In, Out),
        });
    }

    /// <summary>"In 12 · Out 7", "In 12", "Out 7".</summary>
    public static string InOut(string directions, long @in, long @out) => directions switch
    {
        NodeKinds.DirectionsIn => $"In {LiveText.Number(@in)}",
        NodeKinds.DirectionsOut => $"Out {LiveText.Number(@out)}",
        _ => $"In {LiveText.Number(@in)} · Out {LiveText.Number(@out)}",
    };

    private bool Counts(CrossDirection dir) => Directions switch
    {
        NodeKinds.DirectionsIn => dir == CrossDirection.In,
        NodeKinds.DirectionsOut => dir == CrossDirection.Out,
        _ => true,
    };

    private readonly record struct Step(RelativePoint Point, DateTimeOffset LastSeen, CrossDirection? Counted);
}

/// <summary>
/// Count: the objects there now, or the crossings In and Out since the last reset. Its value on crossings is In, Out for a
/// line that counts Out only. Only while no: it counts nothing and keeps its value. A pulse into Reset starts it (and its
/// Line) again at 0 after this frame's output, so a Report of the same pulse still sees the old value.
/// </summary>
internal sealed class CountNode(PipelineNode node, PortKind? input, LineNode? line) : VisionNode(node, NodeKinds.CountKind, input)
{
    private long _objects;

    public LineNode? Line { get; } = line;

    public long In { get; private set; }

    public long Out { get; private set; }

    public void Reset()
    {
        In = 0;
        Out = 0;
        Line?.Reset();
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var counting = While(frame);
        NodeOutput output;
        if (Input != PortKind.Crossings)
        {
            if (counting)
            {
                _objects = input.Objects.Count;
            }

            output = new NodeOutput { NodeId = Id, Kind = PortKind.Number, Value = _objects, Live = LiveText.Number(_objects) };
        }
        else
        {
            if (counting)
            {
                foreach (var c in input.Crossings)
                {
                    if (c.Direction == CrossDirection.In)
                    {
                        In++;
                    }
                    else
                    {
                        Out++;
                    }
                }
            }

            var value = Line?.Directions == NodeKinds.DirectionsOut ? Out : In;
            output = new NodeOutput { NodeId = Id, Kind = PortKind.Number, In = In, Out = Out, Value = value, Live = LiveText.Number(value) };
        }

        if (Pulsed(frame, NodeKinds.ResetPort))
        {
            Reset();
        }

        return ValueTask.FromResult(output);
    }
}
