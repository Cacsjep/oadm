using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>Per tracked object state that ends once the object was gone longer than the Track node keeps it.</summary>
internal sealed class TrackState<T>(TimeSpan keep)
{
    private readonly Dictionary<int, (T Value, DateTimeOffset LastSeen)> _items = [];

    public bool TryGet(int id, out T value)
    {
        if (_items.TryGetValue(id, out var item))
        {
            value = item.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(int id, T value, DateTimeOffset at) => _items[id] = (value, at);

    /// <summary>Drops the objects not seen for longer than the Track node keeps them.</summary>
    public void Forget(DateTimeOffset at)
    {
        List<int>? gone = null;
        foreach (var (id, item) in _items)
        {
            if (at - item.LastSeen > keep)
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                _items.Remove(id);
            }
        }
    }
}

/// <summary>Dwell: the tracked objects that have been inside the Area before it longer than Longer than.</summary>
internal sealed class DwellNode : VisionNode
{
    private readonly double _seconds;

    public DwellNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.DwellKind, input) =>
        _seconds = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinSecondsKey)) ?? 30;

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var kept = new List<VisionObject>();
        foreach (var o in input.Objects)
        {
            if (o.InsideSeconds is { } s && s > _seconds)
            {
                kept.Add(o);
            }
        }

        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = Input, Objects = kept, Live = LiveText.Objects(kept.Count) });
    }
}

/// <summary>
/// Stationary: the tracked objects whose bottom centre stayed within Movement allowed (a share of the picture) of where
/// it settled, for Still for. A larger move makes the object settle again where it is.
/// </summary>
internal sealed class StationaryNode : VisionNode
{
    private readonly TimeSpan _still;
    private readonly double _tolerance;
    private readonly TrackState<(RelativePoint Anchor, DateTimeOffset Since)> _state;

    public StationaryNode(PipelineNode node, PortKind? input, TimeSpan keepLost) : base(node, NodeKinds.StationaryKind, input)
    {
        _still = TimeSpan.FromSeconds(NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.StillSecondsKey)) ?? 60);
        _tolerance = (NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.ToleranceKey)) ?? 3) / 100;
        _state = new(keepLost);
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        var at = frame.At;
        var kept = new List<VisionObject>();
        foreach (var o in input.Objects)
        {
            if (o.TrackId is not { } id)
            {
                continue;
            }

            var p = o.Detection.Box.BottomCentre;
            if (!_state.TryGet(id, out var s) || Distance(s.Anchor, p) > _tolerance)
            {
                s = (p, at);
            }

            _state.Set(id, s, at);
            if (at - s.Since >= _still)
            {
                kept.Add(o);
            }
        }

        _state.Forget(at);
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = Input, Objects = kept, Live = LiveText.Objects(kept.Count) });
    }

    private static double Distance(RelativePoint a, RelativePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Direction: the tracked objects whose bottom centre moved over about the last second within Tolerance degrees of the
/// drawn arrow, and at least Minimum movement (a share of the picture's height). Angles in picture pixels.
/// </summary>
internal sealed class DirectionNode : VisionNode
{
    /// <summary>The movement is judged over this much time.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly RelativePoint _from;
    private readonly RelativePoint _to;
    private readonly double _cosTolerance;
    private readonly double _minMove;
    private readonly TrackState<Queue<(DateTimeOffset At, RelativePoint Point)>> _paths;

    public DirectionNode(PipelineNode node, PortKind? input, TimeSpan keepLost) : base(node, NodeKinds.DirectionKind, input)
    {
        var points = NodeSettings.Points(VisionGraph.Setting(node, NodeKinds.PointsKey)) ?? [];
        _from = points.Count > 0 ? points[0] : new RelativePoint(0, 0);
        _to = points.Count > 1 ? points[1] : new RelativePoint(1, 0);
        _cosTolerance = Math.Cos((NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.AngleKey)) ?? 45) * Math.PI / 180);
        _minMove = (NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinMoveKey)) ?? 5) / 100;
        _paths = new(keepLost);
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        var at = frame.At;
        double w = Math.Max(1, frame.Frame.Width), h = Math.Max(1, frame.Frame.Height);
        var ax = (_to.X - _from.X) * w;
        var ay = (_to.Y - _from.Y) * h;
        var arrow = Math.Sqrt(ax * ax + ay * ay);
        var kept = new List<VisionObject>();
        foreach (var o in input.Objects)
        {
            if (o.TrackId is not { } id)
            {
                continue;
            }

            if (!_paths.TryGet(id, out var path))
            {
                path = new Queue<(DateTimeOffset, RelativePoint)>();
            }

            var p = o.Detection.Box.BottomCentre;
            path.Enqueue((at, p));
            while (path.Count > 1 && path.ElementAt(1).At <= at - Window)
            {
                path.Dequeue();
            }

            _paths.Set(id, path, at);
            var start = path.Peek().Point;
            var dx = (p.X - start.X) * w;
            var dy = (p.Y - start.Y) * h;
            var moved = Math.Sqrt(dx * dx + dy * dy);
            if (arrow > 0 && moved > 0 && moved >= _minMove * h && (dx * ax + dy * ay) / (moved * arrow) >= _cosTolerance)
            {
                kept.Add(o);
            }
        }

        _paths.Forget(at);
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = Input, Objects = kept, Live = LiveText.Objects(kept.Count) });
    }
}
