namespace Oadm.Plugins.Vision.Model;

// The contracts between the parts (docs/spec/vision-center.md "Parts and contract"). The runtime (Pipelines/) works only
// against these; the plugin builds the real parts once per start (VisionParts.Real), the tests hand fakes.

/// <summary>One object a detection model found.</summary>
/// <param name="ClassId">The model's class id.</param>
/// <param name="ClassName">Its name from the model's class names ("Class 7" without one).</param>
/// <param name="Score">0 to 1.</param>
/// <param name="Box">Relative to the whole picture, clipped to it.</param>
public sealed record Detection(int ClassId, string ClassName, double Score, RelativeBox Box);

/// <summary>The best class of a classification model for one picture or box.</summary>
public sealed record ClassResult(int ClassId, string ClassName, double Score);

/// <summary>Which pictures a pipeline asks a camera for.</summary>
public sealed record FrameRequest(Guid DeviceId, int VideoSource, Resolution Resolution)
{
    public static FrameRequest Of(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return new FrameRequest(pipeline.DeviceId, pipeline.VideoSource, pipeline.Resolution);
    }
}

/// <summary>
/// The pictures of a camera as JPEG through the device's own route (sites work): single pictures and the MJPEG stream
/// (the shape of Text recognition's frame source, shared code in plugins/Shared/Vision).
/// </summary>
public interface IFrameSource
{
    Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct);

    /// <summary>
    /// The pictures at <paramref name="fps"/> until <paramref name="ct"/> ends; a dropped stream is opened again. <paramref name="problem"/>
    /// tells why no pictures come now (the page's text) and null once they flow again.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, CancellationToken ct);
}

/// <summary>A decoded picture, shared by every model node of one frame. Disposed by the runtime after the frame.</summary>
public interface IVisionFrame : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>The JPEG it came from (the page's paused frame).</summary>
    ReadOnlyMemory<byte> Jpeg { get; }
}

/// <summary>
/// Runs the stored models (docs/spec/vision-center.md "Load"): bounded CPU workers, the graphics card when there is one,
/// one session per model and input size shared by every pipeline. Calls wait for a free worker.
/// </summary>
public interface IInference
{
    /// <summary>Decodes a JPEG once per frame; refuses pictures over the pixel limits before any pixel memory is taken.</summary>
    IVisionFrame Decode(ReadOnlyMemory<byte> jpeg);

    /// <summary>The objects of <paramref name="model"/> in the whole frame, at most <see cref="VisionLimits.MaxObjects"/>, highest score first; scores below <paramref name="minScore"/> (0 to 1) are left out.</summary>
    Task<IReadOnlyList<Detection>> DetectAsync(VisionModel model, IVisionFrame frame, double minScore, CancellationToken ct);

    /// <summary>The best class of each region (a box with its margin, or a polygon's bounding box) of <paramref name="frame"/>, in their order.</summary>
    Task<IReadOnlyList<ClassResult>> ClassifyAsync(VisionModel model, IVisionFrame frame, IReadOnlyList<RelativeBox> regions, CancellationToken ct);

    /// <summary>The load: busy time of all model calls since the start, the calls that fit at once, and whether one is free now.</summary>
    TimeSpan BusyTime { get; }

    int Capacity { get; }

    bool HasRoom { get; }

    /// <summary>What runs the models: "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti".</summary>
    string RunsOn { get; }

    bool OnGpu { get; }

    bool GpuAvailable { get; }

    /// <summary>True while the card's test of a start runs: pipelines wait for it.</summary>
    bool Starting { get; }

    /// <summary>Applies "Use GPU" and returns at once; the task ends when the card's try ended.</summary>
    Task SetUseGpuAsync(bool useGpu);

    /// <summary>Frees the sessions of <paramref name="modelId"/> (deleted or replaced model).</summary>
    void Forget(string modelId);

    /// <summary>The time of one call of <paramref name="modelId"/> on the graphics card, ms, once it was tested there; null before or without a card (the page's "on the card").</summary>
    double? GpuMsOf(string modelId) => null;
}

/// <summary>The outcome of the model check of an upload.</summary>
/// <param name="Model">The model as it would be stored (format, input, classes, times); null when refused.</param>
/// <param name="Refusal">"This model cannot run here: ..." when refused.</param>
public sealed record ModelCheck(VisionModel? Model, string? Refusal);

/// <summary>
/// The stored models (docs/spec/vision-center.md "Models"): the list in the plugin's records, the files in its data
/// folder, uploads in chunks hashed on the way, the model check, Missing after a restore.
/// </summary>
public interface IModelLibrary
{
    IReadOnlyList<VisionModel> All { get; }

    VisionModel? Find(string? id);

    /// <summary>The file of a stored model; null when Missing.</summary>
    string? FileOf(string id);

    /// <summary>Starts an upload; refuses over the limits. <paramref name="labels"/>: an optional labels file's text.</summary>
    Task<string> BeginUploadAsync(string fileName, long size, string? labels, CancellationToken ct);

    Task AppendAsync(string uploadId, long offset, ReadOnlyMemory<byte> chunk, CancellationToken ct);

    /// <summary>Checks the whole file and stores the model, or refuses it (nothing kept).</summary>
    Task<ModelCheck> FinishUploadAsync(string uploadId, CancellationToken ct);

    Task CancelUploadAsync(string uploadId, CancellationToken ct);

    /// <summary>Name, format, fit, scaling, input size, class names: what the user may change after the check.</summary>
    Task<VisionModel> UpdateAsync(VisionModel changed, CancellationToken ct);

    /// <summary>Refused while <paramref name="usedBy"/> names pipelines.</summary>
    Task DeleteAsync(string id, IReadOnlyList<string> usedBy, CancellationToken ct);

    event EventHandler? Changed;

    /// <summary>Reads the stored list and marks models whose file is gone as Missing; once per start, before any other call.</summary>
    Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>The built-in class lists, then the user's (docs/spec/vision-center.md "Class lists").</summary>
    IReadOnlyList<ClassList> ClassLists => Models.ClassLists.BuiltIn;

    /// <summary>The names of the models that use the class list <paramref name="id"/>.</summary>
    IReadOnlyList<string> ModelsUsing(string? id) => [.. All.Where(m => m.ClassListId is not null && m.ClassListId == id).Select(m => m.Name)];

    /// <summary>Creates (empty id) or changes a user's class list; its models take the new names. Field errors: <see cref="Models.ClassListFieldException"/>.</summary>
    Task<ClassList> SaveClassListAsync(ClassList incoming, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Deletes a user's class list; refused for a built-in one and while a model uses it.</summary>
    Task DeleteClassListAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
