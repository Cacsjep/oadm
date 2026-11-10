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

    /// <summary>The server's time zone: Timers and Time windows judge the frame times in it.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The events of the event rules that are on, for Device event nodes; null: none is ever on.</summary>
    public IEventStates? EventStates { get; init; }
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
/// One pipeline at work: the nodes that can run (no error on them or before them, their required control inputs linked
/// to nodes that run) in the order of their data and control links, each once per frame, the outputs handed by reference.
/// A node runs only when an Event, a Count or "Show on video" after it (or itself) needs it, unless
/// <see cref="EvaluatorOptions.AllNodes"/>. Event nodes run last, so a failing model call ends the frame before any event.
/// </summary>
internal sealed class Evaluator : IPipelineEvaluator
{
    private readonly IInference _inference;
    private readonly EvaluatorOptions _options;
    private readonly IReadOnlyList<PipelineNode> _all;
    private readonly Dictionary<string, NodeOutput> _skipped = new(StringComparer.Ordinal);
    private readonly List<(VisionNode Node, string? Parent)> _work = [];
    private readonly List<(EventNode Node, string Parent)> _events = [];
    private readonly Dictionary<string, VisionNode> _byId = new(StringComparer.Ordinal);

    public Evaluator(Pipeline pipeline, GraphCheck check, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode, EvaluatorOptions options)
    {
        _inference = inference;
        _options = options;
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

        // Data links (one input each) and control links (port -> the node linked into it).
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var controlsOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var after = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var link in pipeline.Links)
        {
            if (!nodes.ContainsKey(link.From) || !nodes.ContainsKey(link.To))
            {
                continue;
            }

            if (link.ToPort is { } port)
            {
                if (!controlsOf.TryGetValue(link.To, out var ports))
                {
                    controlsOf[link.To] = ports = new(StringComparer.Ordinal);
                }

                ports.TryAdd(port, link.From);
            }
            else if (!parentOf.TryAdd(link.To, link.From))
            {
                continue;
            }

            if (!after.TryGetValue(link.From, out var list))
            {
                after[link.From] = list = [];
            }

            list.Add(link.To);
        }

        // Candidates: no error, a source or a node with a known input.
        bool Candidate(PipelineNode node) =>
            !check.NodeErrors.ContainsKey(node.Id)
            && NodeKinds.Find(node.Kind) is { } kind
            && (kind.Accepts.Count == 0 || check.InputKinds.ContainsKey(node.Id));

        // The order over data and control links (Kahn, stable by the node list); runnable: its input and its required
        // control inputs come from nodes that run.
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in _all)
        {
            if (Candidate(node))
            {
                pending.TryAdd(node.Id, 0);
            }
        }

        foreach (var (from, list) in after)
        {
            if (!pending.ContainsKey(from))
            {
                continue;
            }

            foreach (var to in list)
            {
                if (pending.TryGetValue(to, out var count))
                {
                    pending[to] = count + 1;
                }
            }
        }

        var order = new List<PipelineNode>();
        var runnable = new HashSet<string>(StringComparer.Ordinal);
        var ready = new Queue<string>(_all.Where(n => pending.TryGetValue(n.Id, out var d) && d == 0).Select(n => n.Id).Distinct());
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            var node = nodes[id];
            var kind = NodeKinds.Find(node.Kind)!;
            var ok = kind.Accepts.Count == 0 || (parentOf.TryGetValue(id, out var p) && runnable.Contains(p));
            foreach (var control in kind.ControlInputs)
            {
                if (control.Required && !(controlsOf.TryGetValue(id, out var ports) && ports.TryGetValue(control.Port, out var from) && runnable.Contains(from)))
                {
                    ok = false;
                }
            }

            if (kind == NodeKinds.CameraKind && order.Exists(n => n.Kind == NodeKinds.Camera))
            {
                ok = false;
            }

            if (ok)
            {
                runnable.Add(id);
                order.Add(node);
            }

            if (after.TryGetValue(id, out var next))
            {
                foreach (var to in next)
                {
                    if (pending.TryGetValue(to, out var d))
                    {
                        pending[to] = d - 1;
                        if (d == 1)
                        {
                            ready.Enqueue(to);
                        }
                    }
                }
            }
        }

        // Needed: a Count or an Event, shown on the video, or anything before one of those (over both kinds of link).
        var needed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var need = options.AllNodes || node.ShowOnVideo || node.Kind is NodeKinds.Count or NodeKinds.Event;
            if (!need && after.TryGetValue(node.Id, out var list))
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
            PortKind? input = NodeKinds.Find(node.Kind)!.Accepts.Count == 0 ? null : inputKind;
            VisionNode? built = node.Kind switch
            {
                NodeKinds.Camera => new CameraNode(node),
                NodeKinds.Timer => new TimerNode(node),
                NodeKinds.TimeWindow => new TimeWindowNode(node),
                NodeKinds.DeviceEvent => new DeviceEventNode(node),
                NodeKinds.Detect => new DetectNode(node, input, Model(node, findModel), options.AllNodes),
                NodeKinds.Classify => new ClassifyNode(node, input, Model(node, findModel), keep),
                NodeKinds.Filter => new FilterNode(node, input),
                NodeKinds.Track => new TrackNode(node, input),
                NodeKinds.Area => new AreaNode(node, input, keep),
                NodeKinds.Line => new LineNode(node, input, keep),
                NodeKinds.Dwell => new DwellNode(node, input),
                NodeKinds.Stationary => new StationaryNode(node, input, keep),
                NodeKinds.Direction => new DirectionNode(node, input, keep),
                NodeKinds.Count => new CountNode(node, input, parent is not null ? _byId.GetValueOrDefault(parent) as LineNode : null),
                NodeKinds.Statistics => new StatisticsNode(node, input),
                NodeKinds.Compare => new CompareNode(node, input),
                NodeKinds.Logic => new LogicNode(node, input),
                NodeKinds.Hold => new HoldNode(node, input),
                NodeKinds.Throttle => new ThrottleNode(node, input),
                NodeKinds.Event => new EventNode(node, input, mode, keep,
                    input == PortKind.Number && parent is not null && check.InputKinds.GetValueOrDefault(parent) == PortKind.Crossings),
                _ => null,
            };
            if (built is null)
            {
                continue;
            }

            if (controlsOf.TryGetValue(node.Id, out var ports))
            {
                foreach (var (port, from) in ports)
                {
                    if (runnable.Contains(from))
                    {
                        built.Controls[port] = from;
                    }
                }
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

        // Crops need the whole picture; whole-picture calls only as many pixels as their inputs take.
        PictureNeed = _work.Any(w => w.Node is ClassifyNode)
            ? null
            : new PictureNeed([.. _work.Select(w => w.Node).OfType<DetectNode>().Select(d => new ModelInput(d.Model.InputWidth, d.Model.InputHeight, d.Model.Fit))]);
    }

    /// <inheritdoc />
    public PictureNeed? PictureNeed { get; }

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

    /// <inheritdoc />
    public IFramePrefetch? Prefetch(IVisionFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Dictionary<string, Task<(IReadOnlyList<Detection> Detections, double Ms)>>? calls = null;
        FrameContext? context = null;
        foreach (var (node, parent) in _work)
        {
            // Only what needs nothing but the picture: a Detect node right after the Camera always runs with the frame.
            if (node is DetectNode detect && parent is not null && _byId.GetValueOrDefault(parent) is CameraNode)
            {
                context ??= new FrameContext(frame, default, _inference, _options.Time, ct);
                calls ??= new(StringComparer.Ordinal);
                calls[node.Id] = detect.CallAsync(context);
            }
        }

        return calls is null ? null : new FramePrefetch(calls);
    }

    public Task<FrameResult> RunAsync(IVisionFrame frame, DateTimeOffset at, CancellationToken ct) => RunAsync(frame, at, null, ct);

    /// <inheritdoc />
    public async Task<FrameResult> RunAsync(IVisionFrame frame, DateTimeOffset at, IFramePrefetch? prefetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var context = new FrameContext(frame, at, _inference, _options.Time, ct)
        {
            Zone = _options.TimeZone,
            EventStates = _options.EventStates,
            Prefetched = (prefetch as FramePrefetch)?.Calls,
        };
        var outputs = context.Outputs;
        string? failure = null;
        foreach (var (node, parent) in _work)
        {
            ct.ThrowIfCancellationRequested();
            if (!Ready(node, parent, outputs, context, out var input))
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
                if (Ready(node, parent, outputs, context, out var input))
                {
                    outputs[node.Id] = await node.RunAsync(input, context).ConfigureAwait(false);
                }
            }
        }

        var result = new Dictionary<string, NodeOutput>(outputs, StringComparer.Ordinal);
        foreach (var (id, skipped) in _skipped)
        {
            result.TryAdd(id, skipped);
        }

        return new FrameResult
        {
            At = at,
            Nodes = result,
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
                break;

            case LineNode line:
                line.Reset();
                break;

            case StatisticsNode statistics:
                statistics.Reset();
                break;
        }
    }

    /// <summary>A node runs when its data input gave a result this frame and every required control input did too.</summary>
    private static bool Ready(VisionNode node, string? parent, Dictionary<string, NodeOutput> outputs, FrameContext context, out NodeOutput? input)
    {
        input = null;
        if (parent is not null && (!outputs.TryGetValue(parent, out input) || input.Kind is null))
        {
            return false;
        }

        foreach (var control in node.Kind.ControlInputs)
        {
            if (control.Required && node.Control(context, control.Port) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static VisionModel Model(PipelineNode node, Func<string?, VisionModel?> findModel) =>
        findModel(NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.ModelKey)))
        ?? throw new InvalidOperationException("A checked model node has no model.");
}
