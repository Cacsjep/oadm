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
    public const string WhatTheModelSees = "What the model sees";
    public const string ModeInfo = "Watching runs all the time. On action: when a rule runs it.";
    public const string RefreshModelView = "Refresh";

    /// <summary>Below "What the model sees": "576x576 · Stretch".</summary>
    public static string ModelViewCaption(int width, int height, ModelFit fit) =>
        string.Create(CultureInfo.InvariantCulture, $"{width}x{height} · {ModelSettingsViewModel.Fits.First(f => f.Value == fit).Name}");
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
    public const string ImportModel = "Import model";
    public const string SizeTip = "Height, share of the picture's";
    public const string LabelsFile = "Labels file";
    public const string ModelFamily = "Model family";
    public const string ChooseModelFileFirst = "Choose the model file.";
    public const string StepModel = "Model";
    public const string StepSettings = "Settings";
    public const string StepLabels = "Labels";
    public const string LoadLabels = "Load labels file";
    public const string ImportIntro = "Choose your ONNX model. OADM checks it, finds out what it is and sets it up.";
    public const string LabelsStepHint = "The names shown on the boxes and offered in the nodes. A labels file has one name per line.";
    public const string Back = "Back";
    public const string Next = "Next";
    public const string Import = "Import";
    public const string ModelDetected = "Model detected";
    public const string FamilyInfo = "Sets format, fit and scaling for the models of this family.";
    public const string FormatInfo = "How the model's answer is read. It must fit how the model was made.";
    public const string FitInfo = "How the camera's wide picture goes into the model's input.";
    public const string ScalingInfo = "What the model gets for each pixel.";
    public const string AddInto = "Add a node into this input";
    public const string CheckingModel = "Checking the model";
    public const string Cancel = "Cancel";
    public const string Dismiss = "Dismiss";
    public const string Rename = "Rename";
    public const string Classes = "Classes";
    public const string RenameModel = "Rename model";
    public const string ModelClasses = "Class names";
    public const string ChooseModelFile = "Choose a model file";
    public const string Missing = "Missing";
    public const string MissingTip = "Import the file again to use it.";
    public const string DeleteModel = "Delete model";
    public const string DeletePipeline = "Delete pipeline";
    public const string ClassesHint = "One name per line.";
    public const string ClassLists = "Class lists";
    public const string ModelSettings = "Settings";
    public const string ClassListTitle = "Class list";
    public const string OwnNames = "Own names";
    public const string NewClassList = "New class list";
    public const string EditClassList = "Edit class list";
    public const string DeleteClassList = "Delete class list";
    public const string View = "View";
    public const string Edit = "Edit";
    public const string New = "New";
    public const string Create = "Create";
    public const string Close = "Close";
    public const string BuiltIn = "Built-in";
    public const string ClassListNameTaken = "Another class list has this name.";
    public const string EnterNames = "Enter the names, one per line.";
    public const string ChooseEvent = "Choose event";
    public const string CopyToCameras = "Copy to cameras";
    public const string SaveFirst = "Save the pipeline first.";
    public const string CopyToCamerasSaveFirst = "Copy to cameras (save first)";
    public const string ChooseCameras = "Choose cameras";

    /// <summary>The most cameras one copy goes to.</summary>
    public const int MaxCopies = 100;

    public static string TooManyCameras => string.Create(CultureInfo.InvariantCulture, $"Choose at most {MaxCopies} cameras.");

    /// <summary>One line per refused camera: "AXIS M3215-LVE · 10.0.0.202: At most 50 pipelines."</summary>
    public static string CopyRefusals(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
    public const string TurnedOff = "turned off";
    public const string SearchEvents = "Search events";
    public const string NoEventMatches = "No event matches the search.";
    public const string ReadingEvents = "Reading the events";
    public const string NoEvents = "No event to follow. Turn on the plugins that send events.";
    public const string ChooseDevice = "Choose a device";
    public const string ChooseAnEvent = "Choose an event.";

    /// <summary>Below the Class list select when no list fits the model.</summary>
    public static string NoFittingList(int classes) =>
        string.Create(CultureInfo.InvariantCulture, $"No class list has {classes} names. Create one or use own names.");

    public static string TooManyNames => string.Create(CultureInfo.InvariantCulture, $"Enter at most {VisionLimits.MaxClasses:N0} names.");

    public static string NameCount(int count) => count == 1 ? "1 name" : string.Create(CultureInfo.InvariantCulture, $"{count} names");

    /// <summary>"91 names · Built-in", "80 names · 2 models".</summary>
    public static string ClassListLine(ClassList list, int usedBy)
    {
        ArgumentNullException.ThrowIfNull(list);
        var text = NameCount(list.Names.Count) + (list.BuiltIn ? " · " + BuiltIn : string.Empty);
        return usedBy switch
        {
            0 => text,
            1 => text + " · 1 model",
            _ => text + string.Create(CultureInfo.InvariantCulture, $" · {usedBy} models"),
        };
    }


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
    /// <summary>What a fit does, below its select in the Import model wizard.</summary>
    public static string FitMeaning(ModelFit fit) => fit switch
    {
        ModelFit.Stretch => "The whole picture, squeezed into the input. For RF-DETR, RT-DETR and classifiers.",
        ModelFit.KeepShapePad => "The whole picture at the top, the rest filled. For DETR.",
        _ => "The whole picture, scaled down with bars above and below. For YOLO and YOLOX.",
    };

    /// <summary>What a scaling does, below its select in the Import model wizard.</summary>
    public static string ScalingMeaning(ModelScaling scaling) => scaling switch
    {
        ModelScaling.ImageNet => "Values shifted by the average of the ImageNet pictures. For DETR, RF-DETR and ImageNet classifiers.",
        ModelScaling.ZeroTo255Bgr => "The raw values with blue first. For YOLOX.",
        _ => "Each colour from 0 to 1. For YOLO and most other exports.",
    };

    /// <summary>What the model check found, below "Model detected" ("Detection · 640x640 · 80 classes").</summary>
    public static string Found(VisionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kind = model.Kind == ModelKind.Classification ? "Classification" : "Detection";
        var classes = model.ClassNames.Count;
        return string.Create(CultureInfo.InvariantCulture, $"{kind} · {model.InputWidth}x{model.InputHeight} · {classes:N0} {(classes == 1 ? "class" : "classes")}");
    }

    /// <summary>Below a select of Import model whose value the user changed: "The check found RF-DETR."</summary>
    public static string CheckFound(string name) => "The check found " + name + ".";

    public static string Uploading(double fraction) => string.Create(CultureInfo.InvariantCulture, $"Upload {Math.Round(Math.Clamp(fraction, 0, 1) * 100)} %");

    /// <summary>"Detection · 576x576 · 91 classes · 129 MB · 2 pipelines".</summary>
    public static string ModelLine(VisionModel model, int usedBy, string? listName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var kind = model.Kind == ModelKind.Detection ? "Detection" : "Classification";
        var classes = model.ShownClasses.Count();
        var list = listName is { Length: > 0 } && listName != OwnNames ? " · " + listName : string.Empty;
        var text = string.Create(CultureInfo.InvariantCulture, $"{kind} · {model.InputWidth}x{model.InputHeight} · {classes} {(classes == 1 ? "class" : "classes")}{list} · {Size(model.SizeBytes)}");
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

    /// <summary>An object's height as a share of the picture's: "9 %" (what Minimum size and Maximum size of a Filter count).</summary>
    public static string Size(double height) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(height, 0, 1) * 100)} %");

    /// <summary>A Filter's live output: the sizes of the objects that came in, " · Sizes 6 to 12 %" (empty without objects).</summary>
    public static string Sizes(IReadOnlyList<double> heights)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Count == 0)
        {
            return string.Empty;
        }

        var low = Math.Round(Math.Clamp(heights.Min(), 0, 1) * 100);
        var high = Math.Round(Math.Clamp(heights.Max(), 0, 1) * 100);
        return low == high
            ? string.Create(CultureInfo.InvariantCulture, $" · Size {low} %")
            : string.Create(CultureInfo.InvariantCulture, $" · Sizes {low} to {high} %");
    }

    /// <summary>The live output's first line: "5 in this picture · 3 below 50 % · 18 ms per picture · 2 left out".</summary>
    public static string InThisPicture(int count, double? ms, int? dropped, int? below = null, int? minScore = null)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{count} in this picture");
        if (below is { } b && minScore is { } min)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {b} below {min} %");
        }

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
