using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>The example of the approved draft: models, the pipeline "Gate 1 people", its live outputs and a drawn gate.</summary>
internal static class PageSamples
{
    public static readonly TestDevice Gate = new("10.0.0.48", "AXIS Q1798-LE") { Id = Guid.Parse("11111111-0000-0000-0000-000000000048") };

    public static readonly TestDevice Dock = new("10.0.0.202", "AXIS P1468-LE") { Id = Guid.Parse("11111111-0000-0000-0000-000000000202") };

    public static readonly DateTimeOffset Time = new(2026, 11, 8, 14, 32, 5, TimeSpan.Zero);

    public static readonly string[] Coco = ["person", "bicycle", "car", "motorcycle", "bus", "truck"];

    public static VisionModel RfDetr => new()
    {
        Id = "m1", Name = "RF-DETR medium", Kind = ModelKind.Detection, Format = ModelFormat.RfDetr, InputWidth = 576, InputHeight = 576,
        FixedInput = true, ClassNames = Coco, ClassListId = "gate6", SizeBytes = 129L * 1024 * 1024, Sha256 = "ab", CpuMs = 120, GpuMs = 14,
    };

    public static VisionModel Detr => new()
    {
        Id = "m2", Name = "DETR R50", Kind = ModelKind.Detection, Format = ModelFormat.Detr, InputWidth = 800, InputHeight = 800,
        ClassNames = Coco, SizeBytes = 159L * 1024 * 1024, Sha256 = "cd", CpuMs = 900,
    };

    public static VisionModel Helmet => new()
    {
        Id = "m3", Name = "Helmet check", Kind = ModelKind.Classification, Format = ModelFormat.Classification, InputWidth = 224, InputHeight = 224,
        ClassNames = ["helmet", "no helmet"], SizeBytes = 9L * 1024 * 1024, Sha256 = "ef", CpuMs = 3,
    };

    public static IReadOnlyList<VisionModel> Models => [RfDetr, Detr, Helmet];

    public static ClassList Coco91 => new()
    {
        Id = "coco-91", Name = "COCO for DETR (91)", BuiltIn = true,
        Names = [.. Enumerable.Range(0, 91).Select(i => i < Coco.Length ? Coco[i] : i % 12 == 0 ? VisionLimits.NoClassName : $"class {i}")],
    };

    public static ClassList Coco80 => new()
    {
        Id = "coco-80", Name = "COCO for YOLO (80)", BuiltIn = true,
        Names = [.. Enumerable.Range(0, 80).Select(i => i < Coco.Length ? Coco[i] : $"class {i}")],
    };

    public static ClassList Coco90 => new()
    {
        Id = "coco-90", Name = "COCO for RF-DETR (90)", BuiltIn = true,
        Names = [.. Enumerable.Range(0, 90).Select(i => i < Coco.Length ? Coco[i] : $"class {i}")],
    };

    /// <summary>The user's own list of six names: it fits the sample detection models.</summary>
    public static ClassList GateClasses => new() { Id = "gate6", Name = "Gate classes", Names = ["person", "bicycle", "car", "motorbike", "bus", "lorry"] };

    public static ClassListList ClassLists => new()
    {
        Lists = [Coco91, Coco90, Coco80, GateClasses],
        UsedBy = new Dictionary<string, IReadOnlyList<string>> { ["gate6"] = ["RF-DETR medium"] },
    };

    public static ModelList ModelList => new()
    {
        Models = Models,
        UsedBy = new Dictionary<string, IReadOnlyList<string>> { ["m1"] = ["Gate 1 people"], ["m2"] = ["Dock 4 trucks", "Lobby count"] },
    };

    /// <summary>The real graph check with the sample models.</summary>
    public static GraphCheck Check(Pipeline pipeline) => VisionGraph.Check(pipeline, id => Models.FirstOrDefault(m => m.Id == id));

    private static JsonArray Points(params (double X, double Y)[] points) => NodeValues.ToJson(points.Select(p => new Point(p.X, p.Y)));

    private static PipelineNode Node(string id, string kind, string name, double x, double y, JsonObject? settings = null, bool eye = false) =>
        new() { Id = id, Kind = kind, Name = name, X = x, Y = y, Settings = settings ?? [], ShowOnVideo = eye };

    /// <summary>Camera > Detect objects > People > Track > Gate line > Gate count > Gate count changed, and Track > Waiting zone > People waiting.</summary>
    public static Pipeline GatePeople => new()
    {
        Id = "p1",
        Name = "Gate 1 people",
        DeviceId = Gate.Id,
        Mode = PipelineMode.Watching,
        Fps = 8,
        Nodes =
        [
            Node("a", NodeKinds.Camera, "Camera", 160, 40),
            Node("b", NodeKinds.Detect, "Detect objects", 160, 170, new JsonObject { ["model"] = "m1" }),
            Node("c", NodeKinds.Filter, "People", 160, 300, new JsonObject { ["classes"] = new JsonArray("person"), ["minScore"] = 60 }),
            Node("d", NodeKinds.Track, "Track", 160, 430, eye: true),
            Node("e", NodeKinds.Line, "Gate line", 40, 560, new JsonObject { ["points"] = Points((0.46, 0.55), (0.55, 0.98)) }, eye: true),
            Node("f", NodeKinds.Count, "Gate count", 40, 690, new JsonObject { ["resetAt"] = "06:00" }),
            Node("g", NodeKinds.Event, "Gate count changed", 40, 820),
            Node("h", NodeKinds.Area, "Waiting zone", 280, 560,
                new JsonObject { ["points"] = Points((0.65, 0.62), (0.91, 0.62), (0.96, 0.97), (0.62, 0.97)) }, eye: true),
            Node("i", NodeKinds.Event, "People waiting", 280, 690, new JsonObject { ["minObjects"] = 3, ["atLeastSeconds"] = 2 }),
        ],
        Links =
        [
            new("a", "b"), new("b", "c"), new("c", "d"), new("d", "e"), new("e", "f"), new("f", "g"), new("d", "h"), new("h", "i"),
        ],
    };

    /// <summary>"Dock 4 trucks" On action: a Line without Track, and Track and Line refused On action.</summary>
    public static Pipeline DockTrucks => new()
    {
        Id = "p2",
        Name = "Dock 4 trucks",
        DeviceId = Dock.Id,
        Mode = PipelineMode.OnAction,
        Nodes =
        [
            Node("a", NodeKinds.Camera, "Camera", 40, 40),
            Node("b", NodeKinds.Detect, "Detect objects", 40, 170, new JsonObject { ["model"] = "m2" }, eye: true),
            Node("c", NodeKinds.Filter, "Trucks", 40, 300, new JsonObject { ["classes"] = new JsonArray("truck") }),
            Node("d", NodeKinds.Line, "Dock entry", 40, 430, new JsonObject { ["points"] = Points((0.5, 0.5), (0.5, 0.98)) }),
            Node("e", NodeKinds.Event, "Truck arrived", 40, 600),
        ],
        Links = [new("a", "b"), new("b", "c"), new("c", "d"), new("d", "e")],
    };

    /// <summary>
    /// "Lobby count": Camera > Detect objects > Lobby > People in lobby (Count) > Lobby full (Compare: at least 10) > Lobby full
    /// event; the Timer Midnight (every day at 00:00) into the Count's Reset, the Timer Every hour into the Hourly report's Send now.
    /// </summary>
    public static Pipeline LobbyCount => new()
    {
        Id = "p3",
        Name = "Lobby count",
        DeviceId = Dock.Id,
        Mode = PipelineMode.Watching,
        Fps = 4,
        Nodes =
        [
            Node("a", NodeKinds.Camera, "Camera", 400, 40),
            Node("b", NodeKinds.Detect, "Detect objects", 400, 170, new JsonObject { ["model"] = "m1", ["classes"] = new JsonArray("person") }),
            Node("c", NodeKinds.Area, "Lobby", 400, 300, new JsonObject { ["points"] = Points((0.1, 0.5), (0.9, 0.5), (0.95, 0.98), (0.05, 0.98)) }, eye: true),
            Node("d", NodeKinds.Count, "People in lobby", 400, 430),
            Node("e", NodeKinds.Compare, "Lobby full", 280, 560, new JsonObject { ["operator"] = "atLeast", ["value"] = 10 }),
            Node("f", NodeKinds.Event, "Lobby full event", 280, 690),
            Node("g", NodeKinds.Event, "Hourly report", 760, 560),
            Node("t1", NodeKinds.Timer, "Midnight", 140, 300, new JsonObject { ["every"] = "day", ["at"] = "00:00" }),
            Node("t2", NodeKinds.Timer, "Every hour", 640, 430, new JsonObject { ["every"] = "hour" }),
        ],
        Links =
        [
            new("a", "b"), new("b", "c"), new("c", "d"), new("d", "e"), new("e", "f"), new("d", "g"),
            new("t1", "d") { ToPort = NodeKinds.ResetPort },
            new("t2", "g") { ToPort = NodeKinds.SendPort },
        ],
    };

    /// <summary>Lobby count's live outputs: 9 people, the condition not met, the timers' next pulses.</summary>
    public static PreviewEvent LobbyPreview(string previewId) => new()
    {
        PreviewId = previewId,
        At = Time,
        Nodes =
        [
            new() { NodeId = "a", Kind = PortKind.Picture, Live = "4 pictures per second" },
            new() { NodeId = "b", Kind = PortKind.Objects, Live = "9 objects · 21 ms" },
            new() { NodeId = "c", Kind = PortKind.Objects, Live = "9 inside" },
            new() { NodeId = "d", Kind = PortKind.Number, Value = 9, Live = "9 now" },
            new() { NodeId = "e", Kind = PortKind.Condition, Live = "9 ≥ 10 · No" },
            new() { NodeId = "f", On = false, Live = "Off" },
            new() { NodeId = "g", Live = "Fired 12 min ago" },
            new() { NodeId = "t1", Kind = PortKind.Pulse, Live = "Next 00:00" },
            new() { NodeId = "t2", Kind = PortKind.Pulse, Live = "Next 15:00" },
        ],
    };

    /// <summary>
    /// The graph check of the samples while the graph stage does not know control links yet: the real check on the pipeline
    /// without its Timers and the links into control inputs.
    /// </summary>
    public static GraphCheck CheckData(Pipeline pipeline) => Check(pipeline with
    {
        Nodes = [.. pipeline.Nodes.Where(n => n.Kind != NodeKinds.Timer)],
        Links = [.. pipeline.Links.Where(l => l.ToPort is null)],
    });

    public static PipelineList List => new()
    {
        Pipelines =
        [
            new() { Id = "p1", Name = "Gate 1 people", DeviceId = Gate.Id, Mode = PipelineMode.Watching, Fps = 8, Reads = 8, Last = "In 12 · Out 7" },
            new() { Id = "p2", Name = "Dock 4 trucks", DeviceId = Dock.Id, Mode = PipelineMode.OnAction, Last = "Last run 14:02:11" },
            new() { Id = "p3", Name = "Lobby count", DeviceId = Dock.Id, Mode = PipelineMode.Watching, Fps = 15, Reads = 11.2, Slow = true, Last = "9 in Lobby" },
        ],
        MaxWatching = 24,
    };

    private static VisionObject Person(double x, double y, double score, int? id = null, double? inside = null) => new()
    {
        Detection = new Detection(0, "person", score, new RelativeBox(x, y, 0.035, 0.2)),
        TrackId = id,
        Trail = id is null ? [] : [new(x - 0.05, y + 0.2), new(x - 0.03, y + 0.2), new(x, y + 0.2), new(x + 0.0175, y + 0.2)],
        InsideSeconds = inside,
    };

    private static readonly VisionObject Car = new() { Detection = new Detection(2, "car", 0.91, new RelativeBox(0.08, 0.5, 0.15, 0.1)) };

    /// <summary>The frame of the draft's example: 4 people and a car, 3 people tracked, 2 waiting, the gate count at 12.</summary>
    public static PreviewEvent Preview(string previewId)
    {
        VisionObject[] people = [Person(0.30, 0.66, 0.88), Person(0.52, 0.70, 0.81), Person(0.72, 0.74, 0.93), Person(0.82, 0.75, 0.77)];
        VisionObject[] tracked = [Person(0.52, 0.70, 0.81, 3), Person(0.72, 0.74, 0.93, 5, 4.2), Person(0.82, 0.75, 0.77, 6, 1.4)];
        var crossing = new Crossing(Time, CrossDirection.In, 3, "person", 0.81);
        return new PreviewEvent
        {
            PreviewId = previewId,
            At = Time,
            Resolution = new Resolution(1280, 720),
            Nodes =
            [
                new() { NodeId = "a", Kind = PortKind.Picture, Live = "8 pictures per second" },
                new() { NodeId = "b", Kind = PortKind.Objects, Objects = [.. people, Car], Milliseconds = 18, Live = "5 objects · 18 ms" },
                new() { NodeId = "c", Kind = PortKind.Objects, Objects = people, Dropped = [Car], Live = "4 kept · 1 left out" },
                new() { NodeId = "d", Kind = PortKind.TrackedObjects, Objects = tracked, Live = "3 tracked" },
                new() { NodeId = "e", Kind = PortKind.Crossings, Crossings = [crossing], In = 12, Out = 7, Live = "In 12 · Out 7" },
                new() { NodeId = "f", Kind = PortKind.Number, Value = 12, In = 12, Out = 7, Live = "In 12 · Out 7 · Net 5" },
                new() { NodeId = "g", Live = "Fired 3 s ago" },
                new() { NodeId = "h", Kind = PortKind.TrackedObjects, Objects = tracked[1..], Live = "2 inside" },
                new() { NodeId = "i", On = false, Live = "Off" },
            ],
            RecentCrossings = new Dictionary<string, IReadOnlyList<Crossing>>
            {
                ["e"] = [crossing, crossing with { At = Time.AddSeconds(-9), Direction = CrossDirection.Out, TrackId = 2 }],
            },
            RecentFires = new Dictionary<string, IReadOnlyList<RecentFire>> { ["g"] = [new(Time.AddSeconds(-3), "Count 12 · In 12 · Out 7")] },
        };
    }

    /// <summary>The fake server: the lists, each pipeline, the engine, the camera, the preview.</summary>
    public static FakePageContext Context()
    {
        var ctx = new FakePageContext();
        ctx.DeviceList.AddRange([Gate, Dock]);
        ctx.Answers[VisionMethods.ListPipelines] = _ => VisionJson.Serialize(List);
        ctx.Answers[VisionMethods.ListModels] = _ => VisionJson.Serialize(ModelList);
        ctx.Answers[VisionMethods.ListClassLists] = _ => VisionJson.Serialize(ClassLists);
        ctx.Answers[VisionMethods.GetPipeline] = json => VisionJson.Deserialize<PipelineRequest>(json).PipelineId switch
        {
            "p2" => VisionJson.Serialize(DockTrucks),
            "p3" => VisionJson.Serialize(LobbyCount),
            _ => VisionJson.Serialize(GatePeople),
        };
        ctx.Answers[VisionMethods.GetEngine] = _ => VisionJson.Serialize(new EngineInfo { RunsOn = "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti", GpuAvailable = true });
        ctx.Answers[VisionMethods.GetStats] = _ => VisionJson.Serialize(new VisionStats { State = LoadState.Ok, StateText = "All pipelines keep up", Load = 38 });
        ctx.Answers[VisionMethods.GetCameraOptions] = _ => VisionJson.Serialize(new CameraOptions { VideoSources = [1, 2], Resolutions = [new(1920, 1080), new(1280, 720)] });
        ctx.Answers[VisionMethods.StartPreview] = json => VisionJson.Serialize(new PreviewReply { PreviewId = VisionJson.Deserialize<PreviewRequest>(json).PreviewId });
        ctx.Answers[VisionMethods.StopPreview] = _ => "{}";
        ctx.Answers[VisionMethods.SavePipeline] = json =>
        {
            var p = VisionJson.Deserialize<SavePipelineRequest>(json).Pipeline!;
            return VisionJson.Serialize(new SavePipelineReply { Pipeline = p with { Id = p.Id.Length == 0 ? "p9" : p.Id } });
        };
        return ctx;
    }

    /// <summary>Decodes in place (no thread pool in the tests).</summary>
    public static Task<Bitmap?> Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Task.FromResult<Bitmap?>(new Bitmap(stream));
    }

    /// <summary>A drawn gate with people (1280x720) as PNG bytes; needs the headless session.</summary>
    public static byte[] GatePicture()
    {
        using var target = new RenderTargetBitmap(new PixelSize(1280, 720));
        using (var dc = target.CreateDrawingContext())
        {
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#1e2328")), new Rect(0, 0, 1280, 720));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#2b3540")), new Rect(0, 0, 1280, 330));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#3d4650")), new Rect(520, 120, 240, 210));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#232a31")), new Rect(560, 160, 160, 170));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#262b30")), new Rect(0, 330, 1280, 390));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#2f3439")), new Rect(0, 330, 1280, 95));
            var body = new SolidColorBrush(Color.Parse("#9aa3ad"));
            foreach (var (x, y) in new[] { (0.30, 0.66), (0.52, 0.70), (0.72, 0.74), (0.82, 0.75) })
            {
                dc.DrawEllipse(body, null, new Point((x + 0.0175) * 1280, (y + 0.025) * 720), 11, 11);
                dc.FillRectangle(body, new Rect((x + 0.005) * 1280, (y + 0.05) * 720, 30, 100));
            }

            dc.FillRectangle(new SolidColorBrush(Color.Parse("#6f7b88")), new Rect(0.08 * 1280, 0.52 * 720, 0.15 * 1280, 0.07 * 720), 8);
        }

        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
}

/// <summary>A model file in memory for the upload tests.</summary>
internal sealed class MemoryModelFile(string name, byte[] bytes) : IModelFile
{
    public string Name => name;

    public long Length => bytes.Length;

    public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
}
