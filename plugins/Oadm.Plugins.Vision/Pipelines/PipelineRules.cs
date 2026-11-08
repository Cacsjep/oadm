using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>The keys of the save's field errors (<see cref="Api.SavePipelineReply.Errors"/>).</summary>
public static class PipelineFields
{
    public const string Name = "name";
    public const string Camera = "camera";
    public const string VideoSource = "videoSource";
    public const string Resolution = "resolution";
    public const string Fps = "fps";
    public const string Mode = "mode";

    /// <summary>"node:&lt;id&gt;": the error of one node (the graph check's).</summary>
    public static string Node(string nodeId) => "node:" + nodeId;
}

/// <summary>A save refused for its fields: nothing was stored.</summary>
public sealed class PipelineFieldException : Exception
{
    public PipelineFieldException(IReadOnlyDictionary<string, string> errors, IReadOnlyList<string>? graphErrors = null)
        : base(errors.Values.Concat(graphErrors ?? []).FirstOrDefault() ?? "The pipeline cannot be saved.")
    {
        Errors = errors;
        GraphErrors = graphErrors ?? [];
    }

    public PipelineFieldException(string field, string error)
        : this(new Dictionary<string, string>(StringComparer.Ordinal) { [field] = error })
    {
    }

    public PipelineFieldException()
        : this(new Dictionary<string, string>())
    {
    }

    public PipelineFieldException(string message)
        : base(message)
    {
        Errors = new Dictionary<string, string>();
        GraphErrors = [];
    }

    public PipelineFieldException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = new Dictionary<string, string>();
        GraphErrors = [];
    }

    public IReadOnlyDictionary<string, string> Errors { get; }

    public IReadOnlyList<string> GraphErrors { get; }
}

/// <summary>The texts and rules of pipelines (docs/spec/vision-center.md "Pipelines", "Load").</summary>
public static class PipelineRules
{
    /// <summary>The smallest and largest side of a picture a pipeline asks for.</summary>
    public const int MinSide = 160;

    public const int MaxSide = 7680;

    /// <summary>The highest video source a camera can have.</summary>
    public const int MaxVideoSource = 16;

    public const string NameMissing = "Enter a name.";

    public static readonly string NameTooLong = string.Create(CultureInfo.InvariantCulture, $"Use at most {VisionLimits.MaxNameLength} characters.");

    public const string CameraMissing = "Choose a camera.";

    public const string CameraFixed = "A saved pipeline keeps its camera. Create a new pipeline for another camera.";

    public const string SourceMissing = "Choose a video source.";

    public const string ResolutionMissing = "Choose a resolution.";

    public const string ModeMissing = "Choose Watching or On action.";

    public const string Gone = "The pipeline no longer exists.";

    public const string Off = "The pipeline's camera is no longer in its device group.";

    public const string NotRunning = "The Vision Center plugin is not running.";

    public const string Starting = "Starting: testing the graphics card.";

    /// <summary>The models fail <see cref="VisionLimits.FailedFramesForProblem"/> frames in a row.</summary>
    public const string ModelsFail = "The models fail on the pictures of this pipeline. The server log has the details.";

    /// <summary>A newer pipeline (a restore on an older OADM): stored, never run.</summary>
    public const string NewerVersion = "This pipeline needs a newer Vision Center.";

    /// <summary>The action Run pipeline while another run of the same On action pipeline runs (nothing is queued).</summary>
    public const string AlreadyRunning = "Already running";

    public static readonly string TooManyPipelines = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {VisionLimits.MaxPipelines} pipelines. Delete one first.");

    /// <summary>A copy beyond the pipeline limit (the others were made).</summary>
    public static readonly string CopyLimit = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {VisionLimits.MaxPipelines} pipelines.");

    /// <summary>A copy to the pipeline's own camera.</summary>
    public const string OwnCamera = "This is the pipeline's own camera.";

    /// <summary>The most cameras one copy call takes.</summary>
    public const int MaxCopyCameras = 100;

    public static readonly string TooManyCopyCameras = string.Create(CultureInfo.InvariantCulture, $"Copy to at most {MaxCopyCameras} cameras at once.");

    /// <summary>A camera outside the caller's devices (the same answer as for an unknown one).</summary>
    public static readonly string CameraNotAvailable = Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1);

    public static string NameTaken(string name) => $"A pipeline named {name} exists already.";

    public static string FpsNotAllowed()
    {
        var choices = VisionLimits.FpsChoices;
        var list = string.Join(", ", choices.Take(choices.Count - 1).Select(c => c.ToString(CultureInfo.InvariantCulture)));
        return string.Create(CultureInfo.InvariantCulture, $"Choose {list} or {choices[^1]} frames per second.");
    }

    private const string LimitStart = "At most ";

    private const string WatchLimit = " pipelines can watch at once";

    /// <summary>"At most 8 pipelines can watch at once. Set another pipeline to On action first."</summary>
    public static string TooManyWatching(int maxWatching) => string.Create(
        CultureInfo.InvariantCulture,
        $"{LimitStart}{maxWatching}{WatchLimit}. Set another pipeline to On action first.");

    /// <summary>"At most 4 pipelines can watch at once at the site Building B. Set another pipeline there to On action first."</summary>
    public static string TooManyWatchingAtSite(string? siteName) => string.Create(
        CultureInfo.InvariantCulture,
        $"{LimitStart}{VisionLimits.MaxWatchingPerSite}{WatchLimit} at the site {(string.IsNullOrWhiteSpace(siteName) ? "of this camera" : siteName)}. Set another pipeline there to On action first.");

    /// <summary>A problem that is one of the watching limits (the page's chip "Limit reached").</summary>
    public static bool IsWatchingLimit(string? problem) =>
        problem is not null && problem.StartsWith(LimitStart, StringComparison.Ordinal) && problem.Contains(WatchLimit, StringComparison.Ordinal);

    public static readonly string TooManyPreviews = string.Create(
        CultureInfo.InvariantCulture,
        $"At most {VisionLimits.MaxPreviews} pipelines can be shown live at once. Try again when another page closes.");

    public const string PreviewBusy = "No live results now: OADM already watches as many cameras as it can.";

    public const string NothingToPause = "No picture to pause yet.";

    public const string PictureTooLarge = "This picture is too large to pause. Choose a lower resolution.";

    public const string PauseNeedsLiveView = "Your role does not allow: Live view.";

    // The traffic light (docs/spec/ocr.md "Runtime stats", adapted).
    public const string KeepUp = "All pipelines keep up";

    public const string Overloaded = "Overloaded: lower the frames per second or the number of pipelines";

    public static string BusySlow(int pipelines) => pipelines == 1
        ? "Busy: 1 pipeline runs slower than set"
        : string.Create(CultureInfo.InvariantCulture, $"Busy: {pipelines} pipelines run slower than set");

    public static string BusyLoad(double load) =>
        string.Create(CultureInfo.InvariantCulture, $"Busy: the models are at {Math.Round(Math.Clamp(load, 0, 100))} %");

    public static string CpuText(int cores) => string.Create(CultureInfo.InvariantCulture, $"CPU ({cores} {(cores == 1 ? "core" : "cores")})");

    /// <summary>The pipeline as it is stored: names trimmed, the current format version, no owner from the page.</summary>
    public static Pipeline Normalize(Pipeline pipeline)
    {
        var stored = Stored(pipeline);
        return stored with
        {
            Version = Pipeline.CurrentVersion,
            Name = stored.Name.Trim(),
            Fps = stored.Mode == PipelineMode.OnAction && !VisionLimits.FpsChoices.Contains(stored.Fps) ? VisionLimits.DefaultFps : stored.Fps,
            OwnerScopeKey = null,
        };
    }

    /// <summary>A stored pipeline as the runtime reads it: lists and settings that are never null (older or hand-made records).</summary>
    public static Pipeline Stored(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline with
        {
            Name = pipeline.Name ?? string.Empty,
            Resolution = pipeline.Resolution ?? Resolution.Default,
            Nodes = [.. (pipeline.Nodes ?? []).OfType<PipelineNode>().Select(n => n with
            {
                Id = n.Id ?? string.Empty,
                Kind = n.Kind ?? string.Empty,
                Name = n.Name?.Trim() ?? string.Empty,
                Settings = n.Settings ?? [],
            })],
            Links = [.. (pipeline.Links ?? []).OfType<PipelineLink>()],
        };
    }

    /// <summary>The field problems of a normalized pipeline, at most one per field; the graph is the check's.</summary>
    public static Dictionary<string, string> Problems(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pipeline.Name.Length == 0)
        {
            problems[PipelineFields.Name] = NameMissing;
        }
        else if (pipeline.Name.Length > VisionLimits.MaxNameLength)
        {
            problems[PipelineFields.Name] = NameTooLong;
        }

        if (pipeline.DeviceId == Guid.Empty)
        {
            problems[PipelineFields.Camera] = CameraMissing;
        }

        if (pipeline.VideoSource is < 1 or > MaxVideoSource)
        {
            problems[PipelineFields.VideoSource] = SourceMissing;
        }

        if (!ResolutionFits(pipeline.Resolution))
        {
            problems[PipelineFields.Resolution] = ResolutionMissing;
        }

        if (!Enum.IsDefined(pipeline.Mode))
        {
            problems[PipelineFields.Mode] = ModeMissing;
        }
        else if (pipeline.Mode == PipelineMode.Watching && !VisionLimits.FpsChoices.Contains(pipeline.Fps))
        {
            problems[PipelineFields.Fps] = FpsNotAllowed();
        }

        return problems;
    }

    public static bool ResolutionFits(Resolution? resolution) =>
        resolution is { Width: >= MinSide and <= MaxSide, Height: >= MinSide and <= MaxSide };

    /// <summary>The frame rate a pipeline's stream runs at: its own while Watching, 1 for a preview of an On action pipeline.</summary>
    public static int StreamFps(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.Mode != PipelineMode.Watching)
        {
            return 1;
        }

        if (VisionLimits.FpsChoices.Contains(pipeline.Fps))
        {
            return pipeline.Fps;
        }

        var lower = VisionLimits.FpsChoices.Where(c => c < pipeline.Fps).ToList();
        return lower.Count > 0 ? lower.Max() : VisionLimits.DefaultFps;
    }

    /// <summary>
    /// The value of a node's setting, or its field's default (null: none). Read here, not through the graph's helper, so the
    /// runtime needs only the stored JSON.
    /// </summary>
    public static JsonNode? Setting(PipelineNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Settings.TryGetPropertyValue(key, out var value) && value is not null)
        {
            return value;
        }

        var field = NodeKinds.Find(node.Kind)?.Fields.FirstOrDefault(f => f.Key == key);
        return field?.Default is { } json ? JsonNode.Parse(json) : null;
    }

    /// <summary>A setting as text; null when it is missing or not a string.</summary>
    public static string? Text(PipelineNode node, string key) =>
        Setting(node, key) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A setting as a number; null when it is missing or not a number.</summary>
    public static double? Number(PipelineNode node, string key) =>
        Setting(node, key) is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>The ids of the models the nodes of <paramref name="pipeline"/> name.</summary>
    public static IReadOnlyList<string> ModelsOf(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var ids = new List<string>();
        foreach (var node in pipeline.Nodes)
        {
            foreach (var field in NodeKinds.Find(node.Kind)?.Fields ?? [])
            {
                if (field.Type == NodeFieldType.Model && Text(node, field.Key) is { Length: > 0 } id && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The first error of a check (a node's or the graph's); null when it passed.</summary>
    public static string? FirstError(GraphCheck check, Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (check.GraphErrors.Count > 0)
        {
            return check.GraphErrors[0];
        }

        foreach (var node in pipeline.Nodes)
        {
            if (check.NodeErrors.TryGetValue(node.Id, out var error))
            {
                return error;
            }
        }

        return check.NodeErrors.Values.FirstOrDefault();
    }

    /// <summary>A pipeline id or a preview id: 32 hex digits.</summary>
    public static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);
}
