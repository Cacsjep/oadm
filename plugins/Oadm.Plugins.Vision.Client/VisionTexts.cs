using System.Globalization;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>Every text of the Vision Center page (CLAUDE.md "Wording": short, plain, one phrase per concept).</summary>
public static class VisionTexts
{
    public const string Pipelines = "Pipelines";
    public const string Models = "Models";
    public const string SearchPipelines = "Search pipelines";
    public const string NoMatch = "No pipeline matches the search.";
    public const string ReadingPipelines = "Reading the pipelines";
    public const string ReadingPipeline = "Reading the pipeline";
    public const string NewTip = "Start from a camera";
    public const string ChooseCamera = "Choose a camera";
    public const string Watching = "Watching";
    public const string OnAction = "On action";
    public const string Saved = "Saved";
    public const string NotSaved = "Not saved";
    public const string Save = "Save";

    // The canvas.
    public const string AddNode = "Add node";
    public const string Undo = "Undo";
    public const string Redo = "Redo";
    public const string FitView = "Fit view";
    public const string TidyUp = "Tidy up";
    public const string Delete = "Delete";
    public const string UndoTip = "Ctrl+Z";
    public const string RedoTip = "Ctrl+Y";
    public const string DeleteTip = "Delete the selected node or link";
    public const string ShowOnVideo = "Show on video";
    public const string AddAfter = "Add a node after this one";
    public const string DragToLink = "Drag to another node";
    public const string SearchNodes = "Search nodes";
    public const string NoNodeMatches = "No node matches.";
    public const string NotRunning = "Not running";

    // The right column.
    public const string LiveOutput = "Live output";
    public const string NothingYet = "Nothing yet.";
    public const string LinkInputFirst = "Link an input first.";
    public const string FixErrorFirst = "Nothing runs until the error is fixed.";
    public const string PreviewSendsNoEvents = "A preview sends no events. This is what it would send.";
    public const string LinkSelected = "Delete removes the selected link.";
    public const string SelectNode = "Select a node to see its settings and what it finds.";
    public const string Camera = "Camera";
    public const string Stream = "Stream";
    public const string Name = "Name";
    public const string Mode = "Mode";
    public const string LastCrossings = "Last crossings";
    public const string LastFires = "Last events";
    public const string Count = "Count";
    public const string On = "On";
    public const string Off = "Off";
    public const string ObjectsNow = "Objects now";
    public const string Draw = "Draw";
    public const string DrawAreaHint = "Drag on the video to draw the area. Esc stops.";
    public const string DrawLineHint = "Drag on the video from one end of the line to the other. Esc stops.";
    public const string PausedTip = "Saved pipelines keep running.";
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string EveryClass = "Every class";
    public const string ChooseModel = "Choose a model";
    public const string Optional = "Optional";

    // Models.
    public const string UploadModel = "Upload model";
    public const string CheckingModel = "Checking the model";
    public const string Cancel = "Cancel";
    public const string Dismiss = "Dismiss";
    public const string Rename = "Rename";
    public const string Classes = "Classes";
    public const string RenameModel = "Rename model";
    public const string ModelClasses = "Class names";
    public const string ChooseModelFile = "Choose a model file";
    public const string Missing = "Missing";
    public const string MissingTip = "Upload the file again to use it.";
    public const string DeleteModel = "Delete model";
    public const string DeletePipeline = "Delete pipeline";
    public const string ClassesHint = "One name per line.";

    // Errors of the page's own checks, the same as the server's (docs/spec/vision-center.md "Pipelines").
    public const string PipelineNameTaken = "Another pipeline has this name.";
    public const string ModelNameTaken = "Another model has this name.";
    public const string WrongClassCount = "Enter one name per class.";

    public static string WatchingAt(int fps) => string.Create(CultureInfo.InvariantCulture, $"{Watching} · {fps} fps");

    public static string WatchingReads(int fps, double reads) => string.Create(CultureInfo.InvariantCulture, $"{WatchingAt(fps)} (reads {reads:0.0})");

    public static string RunsOn(string what) => "Runs on: " + what;

    /// <summary>"CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti" gives "CPU 24 cores · RTX 5070 Ti" (as Text recognition).</summary>
    public static string RunsOnShort(string what) => (what ?? string.Empty)
        .Replace("CPU (", "CPU ", StringComparison.Ordinal)
        .Replace(" cores)", " cores", StringComparison.Ordinal)
        .Replace("NVIDIA ", string.Empty, StringComparison.Ordinal)
        .Replace("GeForce ", string.Empty, StringComparison.Ordinal)
        .Replace("AMD ", string.Empty, StringComparison.Ordinal);

    public static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(value, 0, 100))} %");

    public static string Zoom(double zoom) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(zoom * 100)} %");

    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Save's tooltip while nodes have errors.</summary>
    public static string FixErrors(int count) => count == 1 ? "Fix the error on the node first." : string.Create(CultureInfo.InvariantCulture, $"Fix the {count} errors on the nodes first.");

    /// <summary>"Configure · 1280x720 @ 8 fps" (no frame rate On action).</summary>
    public static string StreamSummary(Resolution resolution, int? fps) =>
        fps is { } f ? string.Create(CultureInfo.InvariantCulture, $"Configure · {resolution} @ {f} fps") : "Configure · " + resolution;

    /// <summary>The Camera node's live line: "AXIS Q1798-LE · 1280x720 @ 8 fps".</summary>
    public static string CameraSummary(string model, Resolution resolution, int? fps) =>
        fps is { } f ? string.Create(CultureInfo.InvariantCulture, $"{model} · {resolution} @ {f} fps") : $"{model} · {resolution}";

    public static string VideoSource(int number) => string.Create(CultureInfo.InvariantCulture, $"Video source {number}");

    public static string NewPipelineName(int number) => string.Create(CultureInfo.InvariantCulture, $"Pipeline {number}");

    /// <summary>"Upload 42 %".</summary>
    public static string Uploading(double fraction) => string.Create(CultureInfo.InvariantCulture, $"Upload {Math.Round(Math.Clamp(fraction, 0, 1) * 100)} %");

    /// <summary>"Detection · 576x576 · 91 classes · 129 MB · 2 pipelines".</summary>
    public static string ModelLine(VisionModel model, int usedBy)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kind = model.Kind == ModelKind.Detection ? "Detection" : "Classification";
        var classes = model.ShownClasses.Count();
        var text = string.Create(CultureInfo.InvariantCulture, $"{kind} · {model.InputWidth}x{model.InputHeight} · {classes} {(classes == 1 ? "class" : "classes")} · {Size(model.SizeBytes)}");
        return usedBy switch
        {
            0 => text,
            1 => text + " · 1 pipeline",
            _ => text + string.Create(CultureInfo.InvariantCulture, $" · {usedBy} pipelines"),
        };
    }

    /// <summary>The model line's tooltip: the line, the pipelines that use it, the time of one call, the SHA-256.</summary>
    public static string ModelDetails(VisionModel model, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usedBy);
        var lines = new List<string> { ModelLine(model, usedBy.Count) };
        if (usedBy.Count > 0)
        {
            lines.Add(string.Join(", ", usedBy));
        }

        var call = string.Create(CultureInfo.InvariantCulture, $"One call: {Math.Round(model.CpuMs)} ms on the CPU");
        if (model.GpuMs is { } gpu)
        {
            call += string.Create(CultureInfo.InvariantCulture, $", {Math.Round(gpu)} ms on the graphics card");
        }

        lines.Add(call);
        lines.Add("SHA-256 " + model.Sha256);
        return string.Join(Environment.NewLine, lines);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };

    public static string Objects(int count) => count == 1 ? "1 object" : string.Create(CultureInfo.InvariantCulture, $"{count} objects");

    /// <summary>The live output's first line: "5 in this picture · 18 ms per picture · 2 left out".</summary>
    public static string InThisPicture(int count, double? ms, int? dropped)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{count} in this picture");
        if (ms is { } m)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {Math.Round(m)} ms per picture");
        }

        if (dropped is { } d)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {d} left out");
        }

        return text;
    }

    public static string InOut(long @in, long @out) => string.Create(CultureInfo.InvariantCulture, $"In {@in} · Out {@out}");

    public static string Score(double score) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(score * 100)} %");

    public static string Seconds(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s");

    public static string ObjectId(int id) => string.Create(CultureInfo.InvariantCulture, $"#{id}");

    public static string Direction(CrossDirection direction) => direction == CrossDirection.In ? "In" : "Out";

    public static string DeletePipelineQuestion(string name) => $"Delete \"{name}\"? Rules that use it get no more events.";

    public static string DeleteModelQuestion(string name) => $"Delete \"{name}\"?";

    public static string DiscardQuestion(string name) => $"Discard the changes of \"{name}\"?";
}
