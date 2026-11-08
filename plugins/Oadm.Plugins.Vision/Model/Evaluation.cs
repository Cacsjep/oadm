namespace Oadm.Plugins.Vision.Model;

/// <summary>An object as it flows through the nodes: the detection plus what nodes added.</summary>
public sealed record VisionObject
{
    public required Detection Detection { get; init; }

    /// <summary>Set after a Track node: stays while the object moves (1, 2, ... per Track node).</summary>
    public int? TrackId { get; init; }

    /// <summary>The bottom centres of the last frames (at most 12), after a Track node.</summary>
    public IReadOnlyList<RelativePoint> Trail { get; init; } = [];

    /// <summary>The class a Classify node gave it.</summary>
    public ClassResult? Label { get; init; }

    /// <summary>Seconds inside the Area node it last passed; tracked objects only.</summary>
    public double? InsideSeconds { get; init; }
}

public enum CrossDirection
{
    In = 0,
    Out = 1,
}

/// <summary>A tracked object crossed a Line node's line.</summary>
public sealed record Crossing(DateTimeOffset At, CrossDirection Direction, int TrackId, string ClassName, double Score);

/// <summary>What one node did with one frame, for the page's live line, overlay and output (docs/spec/vision-center.md "Page").</summary>
public sealed record NodeOutput
{
    public required string NodeId { get; init; }

    /// <summary>What its output carries this frame; null: it did not run (an error before it, no input, Event).</summary>
    public PortKind? Kind { get; init; }

    /// <summary>The objects it gives (Detect, Classify, Filter, Track, Area).</summary>
    public IReadOnlyList<VisionObject> Objects { get; init; } = [];

    /// <summary>Filter: the objects it left out (drawn dashed).</summary>
    public IReadOnlyList<VisionObject> Dropped { get; init; } = [];

    /// <summary>Line: this frame's crossings.</summary>
    public IReadOnlyList<Crossing> Crossings { get; init; } = [];

    /// <summary>Line and Count on crossings: the totals since the last reset.</summary>
    public long In { get; init; }

    public long Out { get; init; }

    /// <summary>Count: the value (objects now, or In on crossings).</summary>
    public long? Value { get; init; }

    /// <summary>Model nodes: the time of this frame's model call, ms.</summary>
    public double? Milliseconds { get; init; }

    /// <summary>Event: on now (stateful kinds); null for stateless ones.</summary>
    public bool? On { get; init; }

    /// <summary>The live line of the node card ("5 objects · 18 ms", "In 12 · Out 7", "On", "Fired 3 s ago").</summary>
    public string Live { get; init; } = string.Empty;
}

/// <summary>What an Event node publishes (docs/spec/vision-center.md "Events").</summary>
public enum VisionEventKind
{
    /// <summary>Stateful, input Objects: on while enough objects stayed long enough.</summary>
    ObjectsPresent = 0,

    /// <summary>Stateless, input tracked Objects.</summary>
    ObjectEntered = 1,

    ObjectLeft = 2,

    /// <summary>Stateless, input Crossings: one per crossing.</summary>
    LineCrossed = 3,

    /// <summary>Stateless, input Number, at most once a second.</summary>
    CountChanged = 4,

    /// <summary>Stateful, input Number with a Limit.</summary>
    AboveLimit = 5,

    /// <summary>On action, input Objects: once per run.</summary>
    ObjectsFound = 6,

    NoObjectsFound = 7,
}

public enum EmissionState
{
    On = 0,
    Off = 1,
    Fired = 2,
}

/// <summary>One edge or shot of an Event node; the runtime publishes it (never for a preview).</summary>
/// <param name="Values">By the field names of <c>VisionInfo</c> (count, classes, direction, class, object, score, timeInside, in, out).</param>
public sealed record Emission(string NodeId, VisionEventKind Kind, EmissionState State, IReadOnlyDictionary<string, string> Values);

/// <summary>The result of one frame through a pipeline.</summary>
public sealed record FrameResult
{
    public DateTimeOffset At { get; init; }

    /// <summary>Every node that ran or was skipped, by node id.</summary>
    public IReadOnlyDictionary<string, NodeOutput> Nodes { get; init; } = new Dictionary<string, NodeOutput>();

    public IReadOnlyList<Emission> Emissions { get; init; } = [];

    /// <summary>A model call failed this frame (the runtime counts these for the problem).</summary>
    public string? ModelFailure { get; init; }
}

/// <summary>
/// One pipeline's graph at work on its frames, with the state of its nodes (tracks, counts, event edges). Built from a
/// checked graph; one frame at a time, in order. Pure apart from the inference: the time comes with the frame.
/// </summary>
public interface IPipelineEvaluator
{
    Task<FrameResult> RunAsync(IVisionFrame frame, DateTimeOffset at, CancellationToken ct);

    /// <summary>The rule action Reset counter and the daily reset: a Count node (and its Line) start again at 0.</summary>
    void ResetCounter(string nodeId);

    /// <summary>The Event nodes that are on now, with their last values: the full report after a start.</summary>
    IReadOnlyList<Emission> CurrentStates { get; }
}

/// <summary>The graph check (docs/spec/vision-center.md rule 8): errors per node and for the whole graph.</summary>
public sealed record GraphCheck
{
    /// <summary>By node id: the one error shown directly below the node.</summary>
    public IReadOnlyDictionary<string, string> NodeErrors { get; init; } = new Dictionary<string, string>();

    /// <summary>Errors of the whole graph (no camera, too many nodes); shown below the canvas.</summary>
    public IReadOnlyList<string> GraphErrors { get; init; } = [];

    /// <summary>The input kind of every node that has one (the page shows the fields of that kind).</summary>
    public IReadOnlyDictionary<string, PortKind> InputKinds { get; init; } = new Dictionary<string, PortKind>();

    public bool Ok => NodeErrors.Count == 0 && GraphErrors.Count == 0;
}
