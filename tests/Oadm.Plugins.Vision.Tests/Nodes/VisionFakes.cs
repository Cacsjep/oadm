using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Nodes;

/// <summary>A decoded picture without pixels.</summary>
internal sealed class FakeFrame(int width = 1280, int height = 720) : IVisionFrame
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public ReadOnlyMemory<byte> Jpeg => ReadOnlyMemory<byte>.Empty;

    public void Dispose()
    {
    }
}

/// <summary>An inference that answers from a script: the detections of the next frame, the classes of the regions.</summary>
internal sealed class FakeInference : IInference
{
    private readonly Queue<IReadOnlyList<Detection>> _detections = new();

    /// <summary>Detections of every call when the queue is empty.</summary>
    public IReadOnlyList<Detection> Always { get; set; } = [];

    /// <summary>The class of a region.</summary>
    public Func<RelativeBox, ClassResult> Classify { get; set; } = _ => new ClassResult(0, "red", 0.9);

    /// <summary>The next model call fails with this.</summary>
    public Exception? FailNext { get; set; }

    /// <summary>Advanced by <see cref="CallTime"/> in every model call (the measured ms).</summary>
    public FakeTimeProvider? Clock { get; set; }

    public TimeSpan CallTime { get; set; } = TimeSpan.FromMilliseconds(18);

    public int DetectCalls { get; private set; }

    public int ClassifyCalls { get; private set; }

    public List<int> ClassifiedRegions { get; } = [];

    public List<double> MinScores { get; } = [];

    public void Enqueue(params Detection[] detections) => _detections.Enqueue(detections);

    public IVisionFrame Decode(ReadOnlyMemory<byte> jpeg) => new FakeFrame();

    public Task<IReadOnlyList<Detection>> DetectAsync(VisionModel model, IVisionFrame frame, double minScore, CancellationToken ct)
    {
        DetectCalls++;
        MinScores.Add(minScore);
        Clock?.Advance(CallTime);
        if (FailNext is { } ex)
        {
            FailNext = null;
            return Task.FromException<IReadOnlyList<Detection>>(ex);
        }

        var list = _detections.Count > 0 ? _detections.Dequeue() : Always;
        return Task.FromResult<IReadOnlyList<Detection>>(list.Where(d => d.Score >= minScore).ToList());
    }

    public Task<IReadOnlyList<ClassResult>> ClassifyAsync(VisionModel model, IVisionFrame frame, IReadOnlyList<RelativeBox> regions, CancellationToken ct)
    {
        ClassifyCalls++;
        ClassifiedRegions.Add(regions.Count);
        Clock?.Advance(CallTime);
        if (FailNext is { } ex)
        {
            FailNext = null;
            return Task.FromException<IReadOnlyList<ClassResult>>(ex);
        }

        return Task.FromResult<IReadOnlyList<ClassResult>>(regions.Select(Classify).ToList());
    }

    public TimeSpan BusyTime => TimeSpan.Zero;

    public int Capacity => 1;

    public bool HasRoom => true;

    public string RunsOn => "CPU";

    public bool OnGpu => false;

    public bool GpuAvailable => false;

    public bool Starting => false;

    public Task SetUseGpuAsync(bool useGpu) => Task.CompletedTask;

    public void Forget(string modelId)
    {
    }
}

/// <summary>Builds pipelines for the tests: nodes in a chain or with explicit links.</summary>
internal sealed class PipelineBuilder
{
    public static readonly VisionModel Detector = new()
    {
        Id = "0123456789abcdef0123456789abcdef", Name = "People", Kind = ModelKind.Detection,
        ClassNames = ["person", "bicycle", "car", VisionLimits.NoClassName],
    };

    public static readonly VisionModel Classifier = new()
    {
        Id = "fedcba9876543210fedcba9876543210", Name = "Colours", Kind = ModelKind.Classification, ClassNames = ["red", "blue"],
    };

    private readonly List<PipelineNode> _nodes = [];
    private readonly List<PipelineLink> _links = [];

    public PipelineMode Mode { get; set; } = PipelineMode.Watching;

    public int Version { get; set; } = Pipeline.CurrentVersion;

    public static VisionModel? Find(string? id) =>
        id == Detector.Id ? Detector : id == Classifier.Id ? Classifier : null;

    /// <summary>Adds a node (id = name without spaces unless given) and returns its id.</summary>
    public string Add(string kind, string? name = null, JsonObject? settings = null, string? id = null, bool show = false)
    {
        name ??= NodeKinds.Find(kind)?.Label ?? kind;
        name = Unique(name);
        id ??= new string(name.Where(char.IsAsciiLetterOrDigit).ToArray());
        _nodes.Add(new PipelineNode { Id = id, Kind = kind, Name = name, Settings = settings ?? [], ShowOnVideo = show });
        return id;
    }

    /// <summary>Adds a node linked from <paramref name="from"/>.</summary>
    public string After(string from, string kind, JsonObject? settings = null, string? name = null, bool show = false)
    {
        var id = Add(kind, name, settings, show: show);
        Link(from, id);
        return id;
    }

    public PipelineBuilder Link(string from, string to)
    {
        _links.Add(new PipelineLink(from, to));
        return this;
    }

    /// <summary>A link into the control input <paramref name="port"/> of <paramref name="to"/>.</summary>
    public PipelineBuilder Control(string from, string to, string port)
    {
        _links.Add(new PipelineLink(from, to) { ToPort = port });
        return this;
    }

    public Pipeline Build() => new()
    {
        Id = "00000000000000000000000000000001",
        Name = "Gate",
        Version = Version,
        Mode = Mode,
        DeviceId = Guid.Empty,
        Nodes = [.. _nodes],
        Links = [.. _links],
    };

    public static JsonObject DetectSettings(double minScore = 50, params string[] classes)
    {
        var o = new JsonObject { [NodeKinds.ModelKey] = Detector.Id, [NodeKinds.MinScoreKey] = minScore };
        if (classes.Length > 0)
        {
            o[NodeKinds.ClassesKey] = new JsonArray(classes.Select(c => (JsonNode?)c).ToArray());
        }

        return o;
    }

    public static JsonObject Shape(params (double X, double Y)[] points) =>
        new() { [NodeKinds.PointsKey] = NodeSettings.ToJson(points.Select(p => new RelativePoint(p.X, p.Y))) };

    private string Unique(string name)
    {
        if (_nodes.All(n => n.Name != name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (_nodes.All(n => n.Name != candidate))
            {
                return candidate;
            }
        }
    }
}

internal static class Det
{
    /// <summary>A person at its bottom centre (<paramref name="cx"/>, <paramref name="bottom"/>).</summary>
    public static Detection Person(double cx, double bottom, double score = 0.9, double w = 0.1, double h = 0.3) =>
        new(0, "person", score, new RelativeBox(cx - w / 2, bottom - h, w, h));

    public static Detection Car(double cx, double bottom, double score = 0.9) =>
        new(2, "car", score, new RelativeBox(cx - 0.1, bottom - 0.15, 0.2, 0.15));
}
