using System.Text.Json.Nodes;

namespace Oadm.Plugins.Vision.Model;

/// <summary>How a pipeline gets its pictures (docs/spec/vision-center.md "Pipelines").</summary>
public enum PipelineMode
{
    /// <summary>The camera's MJPEG stream at <see cref="Pipeline.Fps"/> (default).</summary>
    Watching = 0,

    /// <summary>One picture when a rule runs the action Run pipeline.</summary>
    OnAction = 1,
}

/// <summary>A picture size in pixels.</summary>
public sealed record Resolution(int Width, int Height)
{
    public static readonly Resolution Default = new(1280, 720);

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>A point relative to the picture: 0 to 1 on both axes, (0, 0) top left.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(RelativePointJson))]
public sealed record RelativePoint(double X, double Y);

/// <summary>A box relative to the picture: 0 to 1, (X, Y) its top left corner. Its JSON holds only X, Y, Width and Height.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(RelativeBoxJson))]
public sealed record RelativeBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>The bottom centre (feet, wheels): where Area and Line look by default.</summary>
    public RelativePoint BottomCentre => new(X + Width / 2, Y + Height);

    public RelativePoint Centre => new(X + Width / 2, Y + Height / 2);
}

/// <summary>
/// A saved pipeline: a graph of nodes on one camera (docs/spec/vision-center.md "Pipelines"). Stored as one record (JSON,
/// camelCase). The camera stays the pipeline's for good once it is saved.
/// </summary>
public sealed record Pipeline
{
    /// <summary>The format version of this JSON; a newer one keeps the pipeline stored but not run.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>32 hex digits; empty for a new pipeline.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    /// <summary>The video source, 1 for the first.</summary>
    public int VideoSource { get; init; } = 1;

    public Resolution Resolution { get; init; } = Resolution.Default;

    public PipelineMode Mode { get; init; } = PipelineMode.Watching;

    /// <summary>Frames per second while Watching, one of <see cref="VisionLimits.FpsChoices"/>.</summary>
    public int Fps { get; init; } = VisionLimits.DefaultFps;

    public IReadOnlyList<PipelineNode> Nodes { get; init; } = [];

    public IReadOnlyList<PipelineLink> Links { get; init; } = [];

    /// <summary>The device scope that made it (docs/spec/device-scope.md); never sent to the page.</summary>
    public string? OwnerScopeKey { get; init; }
}

/// <summary>A node of a pipeline (docs/spec/vision-center.md "Nodes").</summary>
public sealed record PipelineNode
{
    /// <summary>Unique in the pipeline (letters and digits, at most 32); links and the page name it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The node kind, one of <see cref="NodeKinds"/> (<c>camera</c>, <c>detect</c>, ...).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Unique in the pipeline, at most 64 characters; rules and dashboards name it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The place on the canvas (its top left corner, canvas units).</summary>
    public double X { get; init; }

    public double Y { get; init; }

    /// <summary>"Show on video" on the page.</summary>
    public bool ShowOnVideo { get; init; }

    /// <summary>
    /// The node's settings by field key (<see cref="NodeFieldInfo.Key"/> of its kind). A field without a value takes its
    /// default. Kept as JSON so a node kind added later needs no change of this format.
    /// </summary>
    public JsonObject Settings { get; init; } = [];
}

/// <summary>A link from a node's output to another node's input.</summary>
/// <param name="From">The node whose output it is.</param>
/// <param name="To">The node whose only input it is.</param>
public sealed record PipelineLink(string From, string To)
{
    /// <summary>The input it goes into: null for the node's data input, else one of its <see cref="NodeKindInfo.ControlInputs"/> ("reset", "send").</summary>
    public string? ToPort { get; init; }
}
