using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>A picture of the fakes: its bytes are its text.</summary>
internal static class Frame
{
    public static ReadOnlyMemory<byte> Of(string text) => Encoding.UTF8.GetBytes(text);

    public static string TextOf(ReadOnlyMemory<byte> jpeg) => Encoding.UTF8.GetString(jpeg.Span);
}

internal sealed class FakeVisionFrame(ReadOnlyMemory<byte> jpeg) : IVisionFrame
{
    public int Width => 1280;

    public int Height => 720;

    public ReadOnlyMemory<byte> Jpeg { get; } = jpeg;

    public string Text => Frame.TextOf(Jpeg);

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>The inference of the fakes: decodes any picture but "too large"; the model calls are the evaluator's.</summary>
internal sealed class FakeInference : IInference
{
    private int _decoded;

    public int Decoded => Volatile.Read(ref _decoded);

    public bool HasRoom { get; set; }

    public bool Starting { get; set; }

    public TimeSpan BusyTime { get; set; }

    public int Capacity { get; set; } = 1;

    public string RunsOn { get; set; } = string.Empty;

    public bool OnGpu { get; set; }

    public bool GpuAvailable { get; set; }

    public bool UsesGpu { get; private set; } = true;

    /// <summary>While set, handing "Use GPU" on over is a card test that ends with this task.</summary>
    public Task? CardTest { get; set; }

    public ConcurrentQueue<string> Forgotten { get; } = new();

    public IVisionFrame Decode(ReadOnlyMemory<byte> jpeg)
    {
        Interlocked.Increment(ref _decoded);
        return Frame.TextOf(jpeg) == "too large" ? throw new InvalidOperationException("The picture is too large.") : new FakeVisionFrame(jpeg);
    }

    public Task<IReadOnlyList<Detection>> DetectAsync(VisionModel model, IVisionFrame frame, double minScore, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Detection>>([]);

    public Task<IReadOnlyList<ClassResult>> ClassifyAsync(VisionModel model, IVisionFrame frame, IReadOnlyList<RelativeBox> regions, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ClassResult>>([]);

    public Task SetUseGpuAsync(bool useGpu)
    {
        UsesGpu = useGpu;
        return useGpu && CardTest is { } test ? test : Task.CompletedTask;
    }

    public void Forget(string modelId) => Forgotten.Enqueue(modelId);
}

/// <summary>
/// A scripted evaluator: each frame gives every node an output whose live line is the frame's text, and the emissions
/// <see cref="FakeEvaluators.Emit"/> gives; frames wait at <see cref="FakeEvaluators.Gate"/> when set; a frame "fail" fails
/// its models, "throw" throws.
/// </summary>
internal sealed class FakeEvaluator(FakeEvaluators owner, Pipeline pipeline, PipelineMode mode, bool preview) : IPipelineEvaluator
{
    private readonly ConcurrentQueue<string> _frames = new();
    private readonly ConcurrentQueue<string> _resets = new();
    private int _running;
    private int _most;

    public Pipeline Pipeline { get; } = pipeline;

    public PipelineMode Mode { get; } = mode;

    public bool Preview { get; } = preview;

    /// <summary>The texts of the frames run, in the order they ran.</summary>
    public IReadOnlyList<string> Frames => [.. _frames];

    public IReadOnlyList<string> Resets => [.. _resets];

    /// <summary>The most frames that were in RunAsync at once.</summary>
    public int MostAtOnce => Volatile.Read(ref _most);

    public bool Cancelled { get; private set; }

    public IReadOnlyList<Emission> CurrentStates { get; set; } = [];

    public async Task<FrameResult> RunAsync(IVisionFrame frame, DateTimeOffset at, CancellationToken ct)
    {
        var text = Frame.TextOf(frame.Jpeg);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, seen) != seen)
        {
        }

        try
        {
            if (owner.Gate is { } gate)
            {
                try
                {
                    await gate.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            if (text == "throw")
            {
                throw new InvalidOperationException("The model broke.");
            }

            _frames.Enqueue(text);
            var nodes = Pipeline.Nodes.ToDictionary(
                n => n.Id,
                n => new NodeOutput { NodeId = n.Id, Kind = PortKind.Objects, Live = n.Kind == NodeKinds.Camera ? string.Empty : text, Value = text.Length });
            return new FrameResult
            {
                At = at,
                Nodes = nodes,
                Emissions = owner.Emit(Pipeline, text),
                ModelFailure = text == "fail" ? "The model failed." : null,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public void ResetCounter(string nodeId) => _resets.Enqueue(nodeId);
}

/// <summary>The evaluator factory of the fakes: every evaluator it built, in order.</summary>
internal sealed class FakeEvaluators
{
    private readonly ConcurrentQueue<FakeEvaluator> _built = new();

    public SemaphoreSlim? Gate { get; set; }

    /// <summary>The emissions of a frame (default: an Event node "ev" fires once per frame "hit").</summary>
    public Func<Pipeline, string, IReadOnlyList<Emission>> Emit { get; set; } = (_, text) => text == "hit"
        ? [new Emission("ev", VisionEventKind.LineCrossed, EmissionState.Fired, new Dictionary<string, string> { [VisionInfo.DirectionField] = "In" })]
        : [];

    public IReadOnlyList<FakeEvaluator> Built => [.. _built];

    public IReadOnlyList<FakeEvaluator> Saved => [.. _built.Where(e => !e.Preview)];

    public IReadOnlyList<FakeEvaluator> Previews => [.. _built.Where(e => e.Preview)];

    public IPipelineEvaluator Create(Pipeline pipeline, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode) => Add(pipeline, mode, false);

    public IPipelineEvaluator CreatePreview(Pipeline pipeline, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode) => Add(pipeline, mode, true);

    private FakeEvaluator Add(Pipeline pipeline, PipelineMode mode, bool preview)
    {
        var evaluator = new FakeEvaluator(this, pipeline, mode, preview);
        _built.Enqueue(evaluator);
        return evaluator;
    }

    /// <summary>The newest saved (non-preview) evaluator of <paramref name="pipelineId"/>.</summary>
    public FakeEvaluator? Of(string pipelineId) => Saved.LastOrDefault(e => e.Pipeline.Id == pipelineId);
}

/// <summary>
/// The graph check of the fakes: the input of a node is the output of the node before it (camera Picture, detect and area
/// Objects, track TrackedObjects, line Crossings, count Number); a node with a setting "error" has that error; no camera node
/// is an error of the whole graph; a detect node's model must exist.
/// </summary>
internal static class FakeCheck
{
    public static GraphCheck Check(Pipeline pipeline, Func<string?, VisionModel?> findModel)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, PortKind>(StringComparer.Ordinal);
        foreach (var node in pipeline.Nodes)
        {
            if (node.Settings.TryGetPropertyValue("error", out var error) && error is not null)
            {
                errors[node.Id] = error.GetValue<string>();
            }

            if (node.Kind == NodeKinds.Detect && findModel(PipelineRules.Text(node, NodeKinds.ModelKey)) is null)
            {
                errors[node.Id] = "Choose a model.";
            }

            if (pipeline.Links.FirstOrDefault(l => l.To == node.Id) is { } link && pipeline.Nodes.FirstOrDefault(n => n.Id == link.From) is { } from)
            {
                inputs[node.Id] = from.Kind switch
                {
                    NodeKinds.Camera => PortKind.Picture,
                    NodeKinds.Track => PortKind.TrackedObjects,
                    NodeKinds.Line => PortKind.Crossings,
                    NodeKinds.Count => PortKind.Number,
                    _ => PortKind.Objects,
                };
            }
        }

        return new GraphCheck
        {
            NodeErrors = errors,
            GraphErrors = pipeline.Nodes.Any(n => n.Kind == NodeKinds.Camera) ? [] : ["Add a camera."],
            InputKinds = inputs,
        };
    }
}

/// <summary>The stored models of the fakes.</summary>
internal sealed class FakeModels : IModelLibrary
{
    private readonly List<VisionModel> _models = [new() { Id = "m1", Name = "People", Kind = ModelKind.Detection }];

    public List<(string FileName, long Size, string? Labels)> Begun { get; } = [];

    public List<(string UploadId, long Offset, byte[] Data)> Chunks { get; } = [];

    public List<(string Id, IReadOnlyList<string> UsedBy)> Deleted { get; } = [];

    public ModelCheck Finish { get; set; } = new(new VisionModel { Id = "m2", Name = "Cars", Kind = ModelKind.Detection }, null);

    public IReadOnlyList<VisionModel> All
    {
        get
        {
            lock (_models)
            {
                return [.. _models];
            }
        }
    }

    public event EventHandler? Changed;

    public VisionModel? Find(string? id)
    {
        lock (_models)
        {
            return _models.Find(m => m.Id == id);
        }
    }

    public string? FileOf(string id) => null;

    public Task<string> BeginUploadAsync(string fileName, long size, string? labels, CancellationToken ct)
    {
        Begun.Add((fileName, size, labels));
        return Task.FromResult("u1");
    }

    public Task AppendAsync(string uploadId, long offset, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        Chunks.Add((uploadId, offset, chunk.ToArray()));
        return Task.CompletedTask;
    }

    public Task<ModelCheck> FinishUploadAsync(string uploadId, CancellationToken ct)
    {
        if (Finish.Model is { } model)
        {
            lock (_models)
            {
                _models.Add(model);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(Finish);
    }

    public Task CancelUploadAsync(string uploadId, CancellationToken ct) => Task.CompletedTask;

    public Task<VisionModel> UpdateAsync(VisionModel changed, CancellationToken ct)
    {
        lock (_models)
        {
            _models.RemoveAll(m => m.Id == changed.Id);
            _models.Add(changed);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(changed);
    }

    public Task DeleteAsync(string id, IReadOnlyList<string> usedBy, CancellationToken ct)
    {
        Deleted.Add((id, usedBy));
        if (usedBy.Count > 0)
        {
            throw new InvalidOperationException("Used by " + string.Join(", ", usedBy) + ".");
        }

        lock (_models)
        {
            _models.RemoveAll(m => m.Id == id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

/// <summary>The cameras of the fakes: single pictures answer <see cref="Single"/>; each stream gets the frames a test sends.</summary>
internal sealed class FakeFrames : IFrameSource
{
    private readonly ConcurrentDictionary<Guid, Channel<ReadOnlyMemory<byte>>> _streams = new();
    private readonly ConcurrentDictionary<Guid, Action<string?>> _problems = new();
    private int _singles;
    private int _open;
    private int _opened;
    private int _taken;

    public string Single { get; set; } = "single";

    public int Singles => Volatile.Read(ref _singles);

    public int Open => Volatile.Read(ref _open);

    public int Opened => Volatile.Read(ref _opened);

    public int Taken => Volatile.Read(ref _taken);

    public ConcurrentQueue<int> Rates { get; } = new();

    public Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _singles);
        return Task.FromResult(Frame.Of(Single));
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, CancellationToken ct)
    {
        _problems[request.DeviceId] = problem;
        return StreamAsync(request, fps, ct);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(FrameRequest request, int fps, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        _streams[request.DeviceId] = stream;
        Rates.Enqueue(fps);
        Interlocked.Increment(ref _opened);
        Interlocked.Increment(ref _open);
        try
        {
            await foreach (var frame in stream.Reader.ReadAllAsync(ct))
            {
                yield return frame;
                Interlocked.Increment(ref _taken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    public void Tell(Guid deviceId, string? problem)
    {
        Assert.True(_problems.TryGetValue(deviceId, out var report), "No stream of the camera is open.");
        report(problem);
    }

    public void Send(Guid deviceId, string text)
    {
        Assert.True(_streams.TryGetValue(deviceId, out var stream), "No stream of the camera is open.");
        stream.Writer.TryWrite(Frame.Of(text));
    }
}

/// <summary>A started Vision Center plugin over the fakes.</summary>
internal sealed class VisionHarness : IAsyncDisposable
{
    public VisionHarness(int processorCount = 8, bool startingCard = false)
    {
        Context = new FakeCoreContext { EventPublisher = Publisher };
        Inside = Context.Add(new FakeDevice { Address = "10.0.0.1" });
        Outside = Context.Add(new FakeDevice { Address = "10.0.0.2" });
        Parts = new VisionParts
        {
            Models = _ => Models,
            Inference = _ => Inference,
            Frames = _ => Frames,
            Check = FakeCheck.Check,
            Evaluator = Evaluators.Create,
            PreviewEvaluator = Evaluators.CreatePreview,
            Time = Time,
            ProcessorCount = processorCount,
        };
        if (startingCard)
        {
            Inference.CardTest = CardTest.Task;
        }

        Plugin = new VisionPlugin(Parts);
    }

    public FakeCoreContext Context { get; }

    public FakeEventPublisher Publisher { get; } = new();

    public FakeDevice Inside { get; }

    public FakeDevice Outside { get; }

    public FakeInference Inference { get; } = new();

    public FakeFrames Frames { get; } = new();

    public FakeModels Models { get; } = new();

    public FakeEvaluators Evaluators { get; } = new();

    public TaskCompletionSource CardTest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));

    public VisionParts Parts { get; }

    public VisionPlugin Plugin { get; }

    public PipelineRuntime Runtime => Plugin.Runtime ?? throw new InvalidOperationException("Not started.");

    public static async Task<VisionHarness> StartAsync(int processorCount = 8, bool startingCard = false)
    {
        var harness = new VisionHarness(processorCount, startingCard);
        await harness.Plugin.StartAsync(harness.Context, CancellationToken.None);
        return harness;
    }

    public Task<string?> CallAsync(string method, object? payload = null, FakeCallContext? caller = null) =>
        Context.InvokeAsync(Plugin, caller ?? Context.CallAsAllDevices(), method, payload is null ? "{}" : VisionJson.Serialize(payload));

    public async Task<SavePipelineReply> TrySaveAsync(Pipeline pipeline, FakeCallContext? caller = null) =>
        VisionJson.Deserialize<SavePipelineReply>(await CallAsync(VisionMethods.SavePipeline, new SavePipelineRequest { Pipeline = pipeline }, caller));

    public async Task<Pipeline> SaveAsync(Pipeline pipeline, FakeCallContext? caller = null)
    {
        var reply = await TrySaveAsync(pipeline, caller);
        return reply.Pipeline ?? throw new PipelineFieldException(reply.Errors, reply.GraphErrors);
    }

    public async Task<PipelineList> ListAsync(FakeCallContext? caller = null) =>
        VisionJson.Deserialize<PipelineList>(await CallAsync(VisionMethods.ListPipelines, caller: caller));

    public async Task<PreviewReply> PreviewAsync(Pipeline draft, string? previewId = null, FakeCallContext? caller = null) =>
        VisionJson.Deserialize<PreviewReply>(await CallAsync(VisionMethods.StartPreview, new PreviewRequest { PreviewId = previewId ?? string.Empty, Pipeline = draft }, caller));

    /// <summary>
    /// A pipeline: camera "cam" > detect "det" (model m1) > the <paramref name="tail"/> nodes, each linked to the one before;
    /// by default an Event node "ev" on the objects.
    /// </summary>
    public Pipeline NewPipeline(string name = "Gate 1", PipelineMode mode = PipelineMode.Watching, Guid? device = null, params PipelineNode[] tail)
    {
        PipelineNode[] nodes =
        [
            Node("cam", NodeKinds.Camera, "Camera"),
            Node("det", NodeKinds.Detect, "Detect objects", new JsonObject { [NodeKinds.ModelKey] = "m1" }),
            .. tail.Length > 0 ? tail : [Node("ev", NodeKinds.Event, "Gate people")],
        ];
        return new Pipeline
        {
            Name = name,
            DeviceId = device ?? Inside.Id,
            Mode = mode,
            Nodes = nodes,
            Links = [.. nodes.Zip(nodes.Skip(1), (a, b) => new PipelineLink(a.Id, b.Id))],
        };
    }

    public static PipelineNode Node(string id, string kind, string name, JsonObject? settings = null) =>
        new() { Id = id, Kind = kind, Name = name, Settings = settings ?? [] };

    /// <summary>Sends a frame to a watching pipeline's camera and waits until it was delivered.</summary>
    public async Task SendAndWaitAsync(Guid deviceId, string text)
    {
        var before = Runtime.DeliveredFrames;
        Frames.Send(deviceId, text);
        await Eventually.TrueAsync(() => Runtime.DeliveredFrames > before, "the frame was delivered");
    }

    /// <summary>Waits until the camera's stream is open.</summary>
    public Task StreamingAsync(int open = 1) => Eventually.TrueAsync(() => Frames.Open >= open, "the stream is open");

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

/// <summary>A user whose role lacks one permission.</summary>
internal sealed class FakeCallContextWithout(FakeCallContext inner, string permission) : Oadm.Sdk.Plugins.ICallContext
{
    public string UserName => inner.UserName;

    public string SessionId => inner.SessionId;

    public string DeviceScopeKey => inner.DeviceScopeKey;

    public bool HasAllDevices => inner.HasAllDevices;

    public Oadm.Sdk.Devices.IDeviceRepository Devices => inner.Devices;

    public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

    public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

    public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => inner.EventStreams;

    public bool Has(string key) => key != permission && inner.Has(key);

    public bool Contains(Oadm.Sdk.Devices.IDeviceInfo device) => inner.Contains(device);
}
