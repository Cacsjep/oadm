using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>The Camera: the frame itself.</summary>
internal sealed class CameraNode(PipelineNode node) : VisionNode(node, NodeKinds.CameraKind, null)
{
    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame) =>
        ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Picture,
            Live = $"{frame.Frame.Width}x{frame.Frame.Height}",
        });
}

/// <summary>Detect objects: the boxes of a detection model above the minimum score, of the chosen classes.</summary>
internal sealed class DetectNode : VisionNode
{
    private readonly VisionModel _model;
    private readonly double _minScore;
    private readonly HashSet<string>? _classes;

    public DetectNode(PipelineNode node, PortKind? input, VisionModel model) : base(node, NodeKinds.DetectKind, input)
    {
        _model = model;
        _minScore = (NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinScoreKey)) ?? 50) / 100;
        _classes = NodeKindsSettings.ClassSet(node);
    }

    public override async ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        var (detections, ms) = await MeasureAsync(frame, _model,
            () => frame.Inference.DetectAsync(_model, frame.Frame, _minScore, frame.Ct)).ConfigureAwait(false);
        var objects = new List<VisionObject>(Math.Min(detections.Count, VisionLimits.MaxObjects));
        foreach (var d in detections)
        {
            if (objects.Count >= VisionLimits.MaxObjects)
            {
                break;
            }

            if (d.Score >= _minScore && (_classes is null || _classes.Contains(d.ClassName)))
            {
                objects.Add(new VisionObject { Detection = d });
            }
        }

        return new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Objects,
            Objects = objects,
            Milliseconds = ms,
            Live = LiveText.Objects(objects.Count) + LiveText.Ms(ms),
        };
    }
}

/// <summary>
/// Classify: on a picture the class of the whole picture or of its area; on objects the class of each box (with its
/// margin), at most <see cref="VisionLimits.MaxClassifiedPerPicture"/> per picture, tracked ones once and again every
/// <see cref="VisionLimits.ReclassifyEvery"/>.
/// </summary>
internal sealed class ClassifyNode : VisionNode
{
    private static readonly RelativeBox Whole = new(0, 0, 1, 1);

    private readonly VisionModel _model;
    private readonly RelativeBox _region;
    private readonly TimeSpan _keep;
    private readonly Dictionary<int, Cached> _cache = [];

    public ClassifyNode(PipelineNode node, PortKind? input, VisionModel model, TimeSpan keepLost) : base(node, NodeKinds.ClassifyKind, input)
    {
        _model = model;
        _keep = keepLost;
        var points = NodeSettings.Points(VisionGraph.Setting(node, NodeKinds.PointsKey));
        _region = points is { Count: >= 3 } ? Bounds(points) : Whole;
    }

    public override async ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Input == PortKind.Picture)
        {
            var (labels, pictureMs) = await MeasureAsync(frame, _model,
                () => frame.Inference.ClassifyAsync(_model, frame.Frame, [_region], frame.Ct)).ConfigureAwait(false);
            var label = labels.Count > 0 ? labels[0] : null;
            return new NodeOutput
            {
                NodeId = Id,
                Kind = PortKind.Picture,
                Label = label,
                Milliseconds = pictureMs,
                Live = (label is null ? "No class" : $"{label.ClassName} {LiveText.Percent(label.Score)}") + LiveText.Ms(pictureMs),
            };
        }

        var objects = input.Objects;
        var tracked = Input == PortKind.TrackedObjects;
        var regions = new List<RelativeBox>();
        var asked = new List<int>();
        for (var i = 0; i < objects.Count && regions.Count < VisionLimits.MaxClassifiedPerPicture; i++)
        {
            var o = objects[i];
            if (tracked && o.TrackId is { } id && _cache.TryGetValue(id, out var c) && frame.At - c.At < VisionLimits.ReclassifyEvery)
            {
                continue;
            }

            regions.Add(WithMargin(o.Detection.Box));
            asked.Add(i);
        }

        IReadOnlyList<ClassResult> results = [];
        double? ms = null;
        if (regions.Count > 0)
        {
            (results, var took) = await MeasureAsync(frame, _model,
                () => frame.Inference.ClassifyAsync(_model, frame.Frame, regions, frame.Ct)).ConfigureAwait(false);
            ms = took;
        }

        var labelOf = new ClassResult?[objects.Count];
        for (var k = 0; k < asked.Count && k < results.Count; k++)
        {
            labelOf[asked[k]] = results[k];
        }

        var output = new List<VisionObject>(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            var label = labelOf[i];
            if (tracked && o.TrackId is { } id)
            {
                if (label is not null)
                {
                    _cache[id] = new Cached(label, frame.At, frame.At);
                }
                else if (_cache.TryGetValue(id, out var c))
                {
                    label = c.Label;
                    _cache[id] = c with { LastSeen = frame.At };
                }
            }

            output.Add(label is null ? o : o with { Label = label });
        }

        if (tracked)
        {
            Forget(frame.At);
        }

        return new NodeOutput
        {
            NodeId = Id,
            Kind = Input,
            Objects = output,
            Milliseconds = ms,
            Live = LiveText.Objects(output.Count) + (ms is { } m ? LiveText.Ms(m) : string.Empty),
        };
    }

    private void Forget(DateTimeOffset at)
    {
        List<int>? gone = null;
        foreach (var (id, c) in _cache)
        {
            if (at - c.LastSeen > _keep)
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                _cache.Remove(id);
            }
        }
    }

    internal static RelativeBox WithMargin(RelativeBox b)
    {
        var mx = b.Width * VisionLimits.ClassifyMargin;
        var my = b.Height * VisionLimits.ClassifyMargin;
        var x1 = Math.Clamp(b.X - mx, 0, 1);
        var y1 = Math.Clamp(b.Y - my, 0, 1);
        var x2 = Math.Clamp(b.Right + mx, 0, 1);
        var y2 = Math.Clamp(b.Bottom + my, 0, 1);
        return new RelativeBox(x1, y1, x2 - x1, y2 - y1);
    }

    private static RelativeBox Bounds(IReadOnlyList<RelativePoint> points)
    {
        double x1 = 1, y1 = 1, x2 = 0, y2 = 0;
        foreach (var p in points)
        {
            x1 = Math.Min(x1, p.X);
            y1 = Math.Min(y1, p.Y);
            x2 = Math.Max(x2, p.X);
            y2 = Math.Max(y2, p.Y);
        }

        return x2 > x1 && y2 > y1 ? new RelativeBox(x1, y1, x2 - x1, y2 - y1) : Whole;
    }

    private sealed record Cached(ClassResult Label, DateTimeOffset At, DateTimeOffset LastSeen);
}

/// <summary>Settings shared by several nodes.</summary>
internal static class NodeKindsSettings
{
    /// <summary>The chosen classes; null: every class.</summary>
    public static HashSet<string>? ClassSet(PipelineNode node) =>
        NodeSettings.Texts(VisionGraph.Setting(node, NodeKinds.ClassesKey)) is { Count: > 0 } list
            ? new HashSet<string>(list, StringComparer.Ordinal)
            : null;
}
