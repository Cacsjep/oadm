using System.Text.Json;

using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Api;

/// <summary>Ids, names, permissions and event fields shared by the server part, the page and the tests (docs/spec/vision-center.md).</summary>
public static class VisionInfo
{
    public const string PluginId = "oadm.vision";
    public const string DisplayName = "Vision Center";
    public const string IconKey = "flow";

    /// <summary>The page's subtitle.</summary>
    public const string Description = "Builds detections from the cameras' pictures for the event rules.";

    public const string EmptyText = "Upload a model, then create a pipeline.";

    /// <summary>"View vision pipelines": the host's automatic show permission ("oadm.vision:show").</summary>
    public const string ViewPermissionName = "View vision pipelines";

    public const string ChangePermission = "change";

    public const string ChangePermissionName = "Change vision pipelines";

    public const string ModelsPermission = "models";

    public const string ModelsPermissionName = "Manage vision models";

    /// <summary>The first folder of the events in the rule dialog; the second is the pipeline's name.</summary>
    public const string EventGroup = DisplayName;

    /// <summary><c>oadm.vision.event.&lt;pipeline id&gt;.&lt;node id&gt;.&lt;kind&gt;</c>, stable while the node lives.</summary>
    public static string EventTypeId(string pipelineId, string nodeId, VisionEventKind kind) =>
        $"{PluginId}.event.{pipelineId}.{nodeId}.{kind.ToString().ToLowerInvariant()}";

    public const string PipelineProblemTypePrefix = PluginId + ".pipeline-problem.";

    public const string PipelineProblemName = "Pipeline problem";

    public const string AnyPipelineProblemTypeId = PluginId + ".any-pipeline-problem";

    public const string AnyPipelineProblemName = "Any pipeline problem";

    // Event fields (Emission.Values keys).
    public const string CountField = "count";
    public const string ClassesField = "classes";
    public const string ClassField = "class";
    public const string ObjectField = "object";
    public const string ScoreField = "score";
    public const string DirectionField = "direction";
    public const string TimeInsideField = "timeInside";
    public const string InField = "in";
    public const string OutField = "out";
    public const string PipelineField = "pipeline";
    public const string ProblemField = "problem";

    // Actions.
    public const string RunPipelineActionName = "Run pipeline";
    public const string ResetCounterActionName = "Reset counter";
    public const string PipelineActionField = "pipeline";
    public const string CounterActionField = "counter";
    public const string ChoosePipeline = "Choose a pipeline.";
    public const string ChooseCounter = "Choose a counter.";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class VisionMethods
{
    /// <summary>{} -> <see cref="PipelineList"/>. View.</summary>
    public const string ListPipelines = "listPipelines";

    /// <summary><see cref="PipelineRequest"/> -> <see cref="Pipeline"/>. View.</summary>
    public const string GetPipeline = "getPipeline";

    /// <summary><see cref="SavePipelineRequest"/> -> <see cref="SavePipelineReply"/>; field and node errors in the reply, nothing stored. Change, audited.</summary>
    public const string SavePipeline = "savePipeline";

    /// <summary><see cref="PipelineRequest"/> -> {}. Change, audited.</summary>
    public const string DeletePipeline = "deletePipeline";

    /// <summary><see cref="CheckGraphRequest"/> -> <see cref="GraphCheck"/>: the server's check of a draft (the page checks the same itself). View.</summary>
    public const string CheckGraph = "checkGraph";

    /// <summary><see cref="PreviewRequest"/> -> <see cref="PreviewReply"/>: starts or keeps the page's preview of a draft. View.</summary>
    public const string StartPreview = "startPreview";

    public const string StopPreview = "stopPreview";

    /// <summary><see cref="PreviewRequest"/> (id) -> <see cref="PauseReply"/>: holds the frame; the draft then runs again on it after every change. View and Live view.</summary>
    public const string PausePreview = "pausePreview";

    public const string ResumePreview = "resumePreview";

    /// <summary>Event <see cref="PreviewEvent"/>: the preview's live node outputs, at most every <see cref="VisionLimits.LiveEvery"/>, only to its user.</summary>
    public const string PreviewTopic = "preview";

    /// <summary>Event <see cref="PipelineList"/> after a pipeline was created, changed or deleted, or its state changed.</summary>
    public const string PipelinesTopic = "pipelines";

    /// <summary>{} -> <see cref="ModelList"/>. View.</summary>
    public const string ListModels = "listModels";

    /// <summary><see cref="BeginUploadRequest"/> -> <see cref="BeginUploadReply"/>. Models.</summary>
    public const string BeginUpload = "beginModelUpload";

    /// <summary><see cref="UploadChunkRequest"/> -> {}. Models.</summary>
    public const string UploadChunk = "uploadModelChunk";

    /// <summary><see cref="UploadRequest"/> -> <see cref="FinishUploadReply"/>: the model check; audited. Models.</summary>
    public const string FinishUpload = "finishModelUpload";

    public const string CancelUpload = "cancelModelUpload";

    /// <summary><see cref="UpdateModelRequest"/> -> <see cref="VisionModel"/>: rename, classes, format, fit, scaling. Models, audited.</summary>
    public const string UpdateModel = "updateModel";

    /// <summary><see cref="ModelRequest"/> -> {}: refused while pipelines use it. Models, audited.</summary>
    public const string DeleteModel = "deleteModel";

    /// <summary>{} -> <see cref="ClassListList"/>: the built-in and the user's class lists. View.</summary>
    public const string ListClassLists = "listClassLists";

    /// <summary><see cref="SaveClassListRequest"/> -> <see cref="SaveClassListReply"/>: creates (empty id) or changes a user's list; the models that use it follow. Models, audited.</summary>
    public const string SaveClassList = "saveClassList";

    /// <summary><see cref="ClassListRequest"/> -> {}: refused for a built-in list and while a model uses it. Models, audited.</summary>
    public const string DeleteClassList = "deleteClassList";

    /// <summary>Event <see cref="ModelList"/> after a model changed.</summary>
    public const string ModelsTopic = "models";

    /// <summary>{} -> <see cref="EngineInfo"/>. View.</summary>
    public const string GetEngine = "getEngine";

    /// <summary><see cref="UseGpuRequest"/> -> <see cref="EngineInfo"/>. Change, all devices, audited.</summary>
    public const string SetUseGpu = "setUseGpu";

    /// <summary><see cref="CameraOptionsRequest"/> -> <see cref="CameraOptions"/>. View.</summary>
    public const string GetCameraOptions = "getCameraOptions";

    /// <summary>{} -> <see cref="VisionStats"/>; keeps <see cref="StatsTopic"/> on for 30 s. View.</summary>
    public const string GetStats = "getStats";

    public const string StatsTopic = "stats";

    /// <summary>{} -> <see cref="EventTypeList"/>: the on and off events a Device event node may follow. View.</summary>
    public const string ListEventTypes = "listEventTypes";
}

public sealed record PipelineRequest
{
    public string PipelineId { get; init; } = string.Empty;
}

public sealed record SavePipelineRequest
{
    public Pipeline? Pipeline { get; init; }
}

public sealed record SavePipelineReply
{
    public Pipeline? Pipeline { get; init; }

    /// <summary>By field ("name", "fps", "resolution", "videoSource") or by "node:&lt;id&gt;"; nothing was stored.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();

    /// <summary>Errors of the whole graph; nothing was stored.</summary>
    public IReadOnlyList<string> GraphErrors { get; init; } = [];
}

public sealed record CheckGraphRequest
{
    public Pipeline? Pipeline { get; init; }
}

/// <summary>A pipeline in the list, with its state.</summary>
public sealed record PipelineRow
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    public PipelineMode Mode { get; init; }

    public int Fps { get; init; }

    /// <summary>Pictures per second over the last 10 s while Watching; null: not judged yet.</summary>
    public double? Reads { get; init; }

    /// <summary>Below 90 % of its rate (orange chip).</summary>
    public bool Slow { get; init; }

    /// <summary>Why it does not run (the chip's tooltip); null while it runs.</summary>
    public string? Problem { get; init; }

    /// <summary>One live summary line ("In 12 · Out 7", "9 in Lobby"); empty when there is none.</summary>
    public string Last { get; init; } = string.Empty;

    public bool Starting { get; init; }
}

public sealed record PipelineList
{
    public IReadOnlyList<PipelineRow> Pipelines { get; init; } = [];

    public int MaxWatching { get; init; }
}

public sealed record PreviewRequest
{
    /// <summary>32 hex digits made by the page, new per start.</summary>
    public string PreviewId { get; init; } = string.Empty;

    /// <summary>The page's draft (saved or not); only StartPreview reads it.</summary>
    public Pipeline? Pipeline { get; init; }
}

public sealed record PreviewReply
{
    public string PreviewId { get; init; } = string.Empty;

    /// <summary>Why the preview runs nothing now; null while it runs.</summary>
    public string? Problem { get; init; }

    public bool Paused { get; init; }
}

public sealed record PauseReply
{
    public bool Paused { get; init; }

    public string? Problem { get; init; }

    /// <summary>The held frame as the camera sent it (base64 in JSON).</summary>
    public byte[]? Picture { get; init; }

    public PreviewEvent? State { get; init; }
}

/// <summary>The preview's live state: every node's output of its latest frame and the recent lists.</summary>
public sealed record PreviewEvent
{
    public string PreviewId { get; init; } = string.Empty;

    public DateTimeOffset At { get; init; }

    /// <summary>The picture size the boxes belong to.</summary>
    public Resolution Resolution { get; init; } = Resolution.Default;

    public IReadOnlyList<NodeOutput> Nodes { get; init; } = [];

    /// <summary>Line nodes: the last <see cref="VisionLimits.RecentPerNode"/> crossings, newest first, by node id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Crossing>> RecentCrossings { get; init; } = new Dictionary<string, IReadOnlyList<Crossing>>();

    /// <summary>Event nodes: what they would have sent (a preview never publishes), newest first, by node id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RecentFire>> RecentFires { get; init; } = new Dictionary<string, IReadOnlyList<RecentFire>>();

    /// <summary>The check of the draft it runs.</summary>
    public GraphCheck? Check { get; init; }

    public bool Paused { get; init; }
}

/// <summary>An Event node's shot or edge for the page's table.</summary>
public sealed record RecentFire(DateTimeOffset At, string Text);

public sealed record ModelList
{
    public IReadOnlyList<VisionModel> Models { get; init; } = [];

    /// <summary>By model id: the names of the pipelines that use it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UsedBy { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record BeginUploadRequest
{
    public string FileName { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary>An optional labels file's text (one class name per line).</summary>
    public string? Labels { get; init; }
}

public sealed record BeginUploadReply
{
    public string UploadId { get; init; } = string.Empty;

    public int ChunkBytes { get; init; } = VisionLimits.UploadChunkBytes;
}

public sealed record UploadChunkRequest
{
    public string UploadId { get; init; } = string.Empty;

    public long Offset { get; init; }

    /// <summary>Base64 in JSON.</summary>
    public byte[] Data { get; init; } = [];
}

public sealed record UploadRequest
{
    public string UploadId { get; init; } = string.Empty;
}

public sealed record FinishUploadReply
{
    public VisionModel? Model { get; init; }

    public string? Refusal { get; init; }
}

public sealed record ModelRequest
{
    public string ModelId { get; init; } = string.Empty;
}

public sealed record UpdateModelRequest
{
    public VisionModel? Model { get; init; }
}

public sealed record EngineInfo
{
    public string RunsOn { get; init; } = string.Empty;

    public bool GpuAvailable { get; init; }

    public bool UseGpu { get; init; } = true;

    public bool GpuStarting { get; init; }

    public IReadOnlyList<int> FpsChoices { get; init; } = VisionLimits.FpsChoices;
}

public sealed record UseGpuRequest
{
    public bool UseGpu { get; init; } = true;
}

public sealed record CameraOptionsRequest
{
    public Guid DeviceId { get; init; }
}

public sealed record CameraOptions
{
    /// <summary>1 for the first.</summary>
    public IReadOnlyList<int> VideoSources { get; init; } = [1];

    public IReadOnlyList<Resolution> Resolutions { get; init; } = [];
}

/// <summary>The traffic light (as Text recognition's).</summary>
public enum LoadState
{
    Ok = 0,
    Busy = 1,
    Overloaded = 2,
    Starting = 3,
}

public sealed record VisionStats
{
    public LoadState State { get; init; }

    public string StateText { get; init; } = string.Empty;

    /// <summary>0 to 100.</summary>
    public double Load { get; init; }

    public int Watching { get; init; }

    public int MaxWatching { get; init; }

    public int Previews { get; init; }

    public int MaxPreviews { get; init; }
}

/// <summary>The JSON of the page's calls and events (web names, enums as names).</summary>
public static class VisionJson
{
    public static readonly JsonSerializerOptions Options = PluginJson.CreateOptions(PluginJsonEnums.Names);

    public static string Serialize<T>(T value) => PluginJson.Serialize(value, Options);

    public static T Deserialize<T>(string? json) => PluginJson.ParseOrEmpty<T>(json, Options);
}

public sealed record ClassListList
{
    public IReadOnlyList<ClassList> Lists { get; init; } = [];

    /// <summary>By list id: the names of the models that use it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UsedBy { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record ClassListRequest
{
    public string ClassListId { get; init; } = string.Empty;
}

public sealed record SaveClassListRequest
{
    public ClassList? ClassList { get; init; }
}

public sealed record SaveClassListReply
{
    public ClassList? ClassList { get; init; }

    /// <summary>By field ("name", "names"); nothing was stored.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();
}

/// <summary>An on and off event of the event rules a Device event node may follow.</summary>
public sealed record EventTypeChoice
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Its folders in the event tree, top first ("Devices", "Any device").</summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    /// <summary>One state per device (the node then names a device); false: one for the server.</summary>
    public bool PerDevice { get; init; }

    /// <summary>False while the plugin that sends it is turned off.</summary>
    public bool Available { get; init; } = true;
}

public sealed record EventTypeList
{
    public IReadOnlyList<EventTypeChoice> Types { get; init; } = [];
}
