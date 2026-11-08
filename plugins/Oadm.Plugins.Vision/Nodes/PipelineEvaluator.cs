using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>How an evaluator runs.</summary>
public sealed record EvaluatorOptions
{
    public static readonly EvaluatorOptions Default = new();

    /// <summary>
    /// Every node runs, not only those an Event, a Count or "Show on video" needs: the page's preview shows every node's
    /// output.
    /// </summary>
    public bool AllNodes { get; init; }

    /// <summary>Measures the model calls (their ms); never the frame's time, which comes with the frame.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>Builds the evaluator of a checked pipeline (docs/spec/vision-center.md "Running" and "Events").</summary>
public static class PipelineEvaluator
{
    /// <summary>
    /// The evaluator of <paramref name="pipeline"/> (checked: <see cref="Graph.VisionGraph.Check"/> passed). Nodes after an
    /// error never run. <paramref name="mode"/> decides Objects found / No objects found against the stateful kinds.
    /// </summary>
    public static IPipelineEvaluator Create(Pipeline pipeline, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode) =>
        Create(pipeline, findModel, inference, mode, EvaluatorOptions.Default);

    /// <summary>As <see cref="Create(Pipeline, Func{string?, VisionModel?}, IInference, PipelineMode)"/>, with <paramref name="options"/> (the preview: every node).</summary>
    public static IPipelineEvaluator Create(
        Pipeline pipeline, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode, EvaluatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(findModel);
        ArgumentNullException.ThrowIfNull(inference);
        ArgumentNullException.ThrowIfNull(options);
        var check = VisionGraph.Check(pipeline with { Mode = mode }, findModel);
        return new Evaluator(pipeline, check, findModel, inference, mode, options);
    }
}

/// <summary>
/// One pipeline at work: the nodes that can run (no error on them or before them) in the order from the camera, each once
/// per frame, the outputs handed by reference. A node runs only when an Event, a Count or "Show on video" after it (or
/// itself) needs it, unless <see cref="EvaluatorOptions.AllNodes"/>. Event nodes run last, so a failing model call ends
/// the frame before any event.
/// </summary>
internal sealed class Evaluator : IPipelineEvaluator
{
    private readonly IInference _inference;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<PipelineNode> _all;
    private readonly Dictionary<string, NodeOutput> _skipped = new(StringComparer.Ordinal);
    private readonly List<(VisionNode Node, string? Parent)> _work = [];
    private readonly List<(EventNode Node, string Parent)> _events = [];
    private readonly Dictionary<string, VisionNode> _byId = new(StringComparer.Ordinal);

    public Evaluator(Pipeline pipeline, GraphCheck check, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode, EvaluatorOptions options)
    {
        _inference = inference;
        _time = options.Time;
        _all = pipeline.Nodes;
        foreach (var node in _all)
        {
            _skipped.TryAdd(node.Id, new NodeOutput { NodeId = node.Id });
        }

        if (check.GraphErrors.Count > 0)
        {
            return;
        }

        var nodes = new Dictionary<string, PipelineNode>(StringComparer.Ordinal);
        foreach (var node in _all)
        {
            nodes.TryAdd(node.Id, node);
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in pipeline.Links)
        {
            if (!nodes.ContainsKey(link.From) || !nodes.ContainsKey(link.To) || !parentOf.TryAdd(link.To, link.From))
            {
                continue;
            }

            if (!children.TryGetValue(link.From, out var list))
            {
                children[link.From] = list = [];
            }

            list.Add(link.To);
        }

        var camera = _all.FirstOrDefault(n => n.Kind == NodeKinds.Camera);
        if (camera is null || check.NodeErrors.ContainsKey(camera.Id))
        {
            return;
        }

        // The runnable nodes from the camera on, parents first (a node with an error stops its branch).
        var order = new List<PipelineNode>();
        var stack = new Stack<PipelineNode>();
        stack.Push(camera);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            if (!children.TryGetValue(node.Id, out var list))
            {
                continue;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var child = nodes[list[i]];
                if (!check.NodeErrors.ContainsKey(child.Id) && check.InputKinds.ContainsKey(child.Id))
                {
                    stack.Push(child);
                }
            }
        }

        // Needed: a Count or an Event, shown on the video, or anything before one of those.
        var needed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var need = options.AllNodes || node.ShowOnVideo || node.Kind is NodeKinds.Count or NodeKinds.Event;
            if (!need && children.TryGetValue(node.Id, out var list))
            {
                foreach (var c in list)
                {
                    need |= needed.Contains(c);
                }
            }

            if (need)
            {
                needed.Add(node.Id);
            }
        }

        // The Track node before each node: how long its lost objects keep their ids.
        var keepLost = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var node in order)
        {
            if (!needed.Contains(node.Id))
            {
                continue;
            }

            var parent = parentOf.GetValueOrDefault(node.Id);
            var keep = parent is not null && keepLost.TryGetValue(parent, out var k) ? k : TimeSpan.Zero;
            if (parent is not null && nodes[parent].Kind == NodeKinds.Track)
            {
                keep = TrackNode.SettingsOf(nodes[parent]).KeepLost;
            }

            keepLost[node.Id] = keep;
            check.InputKinds.TryGetValue(node.Id, out var inputKind);
            PortKind? input = node.Kind == NodeKinds.Camera ? null : inputKind;
            VisionNode? built = node.Kind switch
            {
                NodeKinds.Camera => new CameraNode(node),
                NodeKinds.Detect => new DetectNode(node, input, Model(node, findModel)),
                NodeKinds.Classify => new ClassifyNode(node, input, Model(node, findModel), keep),
                NodeKinds.Filter => new FilterNode(node, input),
                NodeKinds.Track => new TrackNode(node, input),
                NodeKinds.Area => new AreaNode(node, input, keep),
                NodeKinds.Line => new LineNode(node, input, keep),
                NodeKinds.Count => new CountNode(node, input, parent is not null ? _byId.GetValueOrDefault(parent) as LineNode : null),
                NodeKinds.Event => new EventNode(node, input, mode, keep,
                    input == PortKind.Number && parent is not null && check.InputKinds.GetValueOrDefault(parent) == PortKind.Crossings),
                _ => null,
            };
            if (built is null)
            {
                continue;
            }

            _byId[node.Id] = built;
            if (built is EventNode e)
            {
                _events.Add((e, parent!));
            }
            else
            {
                _work.Add((built, parent));
            }
        }
    }

    public IReadOnlyList<Emission> CurrentStates
    {
        get
        {
            var list = new List<Emission>();
            foreach (var (e, _) in _events)
            {
                list.AddRange(e.OnStates());
            }

            return list;
        }
    }

    public async Task<FrameResult> RunAsync(IVisionFrame frame, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var context = new FrameContext(frame, at, _inference, _time, ct);
        var outputs = new Dictionary<string, NodeOutput>(_all.Count, StringComparer.Ordinal);
        string? failure = null;
        foreach (var (node, parent) in _work)
        {
            ct.ThrowIfCancellationRequested();
            NodeOutput? input = null;
            if (parent is not null && (!outputs.TryGetValue(parent, out input) || input.Kind is null))
            {
                continue;
            }

            try
            {
                outputs[node.Id] = await node.RunAsync(input, context).ConfigureAwait(false);
            }
            catch (ModelFailedException ex)
            {
                failure = ex.Message;
                break;
            }
        }

        if (failure is null)
        {
            foreach (var (node, parent) in _events)
            {
                if (outputs.TryGetValue(parent, out var input) && input.Kind is not null)
                {
                    outputs[node.Id] = await node.RunAsync(input, context).ConfigureAwait(false);
                }
            }
        }

        foreach (var (id, skipped) in _skipped)
        {
            outputs.TryAdd(id, skipped);
        }

        return new FrameResult
        {
            At = at,
            Nodes = outputs,
            Emissions = failure is null ? context.Emissions : [],
            ModelFailure = failure,
        };
    }

    public void ResetCounter(string nodeId)
    {
        switch (_byId.GetValueOrDefault(nodeId))
        {
            case CountNode count:
                count.Reset();
                count.Line?.Reset();
                break;

            case LineNode line:
                line.Reset();
                break;
        }
    }

    private static VisionModel Model(PipelineNode node, Func<string?, VisionModel?> findModel) =>
        findModel(NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.ModelKey)))
        ?? throw new InvalidOperationException("A checked model node has no model.");
}
