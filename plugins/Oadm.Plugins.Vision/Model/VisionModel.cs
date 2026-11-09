namespace Oadm.Plugins.Vision.Model;

/// <summary>What a model gives (docs/spec/vision-center.md "Models").</summary>
public enum ModelKind
{
    /// <summary>Boxes with class and score.</summary>
    Detection = 0,

    /// <summary>One class and score per picture.</summary>
    Classification = 1,
}

/// <summary>How the outputs of a model are read (docs/spec/vision-center.md "Output formats").</summary>
public enum ModelFormat
{
    /// <summary><c>[1, 4 + C, N]</c> (YOLOv8 and later) or <c>[1, N, 5 + C]</c> with objectness (YOLOv5); the plugin suppresses.</summary>
    Yolo = 0,

    /// <summary><c>[1, N, 6]</c>: x1, y1, x2, y2, score, class (end-to-end exports).</summary>
    YoloWithSuppression = 1,

    /// <summary>Facebook DETR: <c>pred_logits [1, Q, C + 1]</c> (last = no object, softmax) and <c>pred_boxes [1, Q, 4]</c> (cx, cy, w, h, 0 to 1).</summary>
    Detr = 2,

    /// <summary>RF-DETR (rf-detr-onnx): output 0 boxes <c>[1, Q, 4]</c> (cx, cy, w, h, 0 to 1), output 1 logits <c>[1, Q, C]</c> (sigmoid); a third output (masks) is left out.</summary>
    RfDetr = 3,

    /// <summary>Boxes, scores and labels outputs (RT-DETR, D-FINE and other end-to-end exports).</summary>
    DetrWithLabels = 4,

    /// <summary><c>[1, C]</c> logits or probabilities.</summary>
    Classification = 5,

    /// <summary>YOLOX as Megvii exports it: <c>[1, N, 5 + C]</c> per grid cell of the strides 8, 16 and 32, the boxes still relative to their cell; the plugin decodes and suppresses.</summary>
    Yolox = 6,

    /// <summary>
    /// RT-DETR, D-FINE and other DETR models without a "no object" class as Hugging Face exports them: <c>logits [1, Q, C]</c>
    /// (a sigmoid per class) and <c>pred_boxes [1, Q, 4]</c> (cx, cy, w, h, 0 to 1), found by name.
    /// </summary>
    RtDetr = 7,
}

/// <summary>How the picture is fitted to the model's input.</summary>
public enum ModelFit
{
    /// <summary>Scaled with its shape kept and centred, the rest grey (YOLO).</summary>
    Letterbox = 0,

    /// <summary>Scaled to the input on both axes (RF-DETR, classification).</summary>
    Stretch = 1,

    /// <summary>Scaled with its shape kept, top left, the rest black (DETR).</summary>
    KeepShapePad = 2,
}

/// <summary>How pixel values are scaled before the model.</summary>
public enum ModelScaling
{
    /// <summary>0 to 1.</summary>
    ZeroToOne = 0,

    /// <summary>0 to 1, then ImageNet mean (0.485, 0.456, 0.406) and deviation (0.229, 0.224, 0.225).</summary>
    ImageNet = 1,

    /// <summary>0 to 255 in the order blue, green, red (YOLOX, trained on OpenCV's pictures).</summary>
    ZeroTo255Bgr = 2,
}

/// <summary>
/// A stored model (docs/spec/vision-center.md "Models"): server-wide, the file in
/// <c>plugin-data/oadm.vision/models/&lt;id&gt;.onnx</c>. Backups keep this record, never the file.
/// </summary>
public sealed record VisionModel
{
    /// <summary>32 hex digits.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>1 to 64 characters, unique.</summary>
    public string Name { get; init; } = string.Empty;

    public ModelKind Kind { get; init; }

    public ModelFormat Format { get; init; }

    public ModelFit Fit { get; init; }

    public ModelScaling Scaling { get; init; }

    /// <summary>The input size the plugin feeds (the model's own when fixed).</summary>
    public int InputWidth { get; init; }

    public int InputHeight { get; init; }

    /// <summary>The model fixes its input size (no choice on the page).</summary>
    public bool FixedInput { get; init; }

    /// <summary>
    /// The class list this model uses (<see cref="ClassList.Id"/>); null: its own names (from the model's metadata or edited
    /// by hand). <see cref="ClassNames"/> always holds the names in effect: the library refreshes it when the list changes.
    /// </summary>
    public string? ClassListId { get; init; }

    /// <summary>One name per class id; "N/A" names a gap (never shown or chosen).</summary>
    public IReadOnlyList<string> ClassNames { get; init; } = [];

    public long SizeBytes { get; init; }

    /// <summary>Lower-case hex of the file: a file with the same hash brings a Missing model back.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>One call on the CPU at the model check, ms.</summary>
    public double CpuMs { get; init; }

    /// <summary>One call on the graphics card, ms; null: not tried or not used.</summary>
    public double? GpuMs { get; init; }

    /// <summary>The file is not there (after a restore on another computer): its pipelines show "Import the model ... again."</summary>
    public bool Missing { get; init; }

    public DateTimeOffset UploadedAt { get; init; }

    /// <summary>The class names a user can see and choose: without the "N/A" gaps.</summary>
    public IEnumerable<string> ShownClasses => ClassNames.Where(n => n != VisionLimits.NoClassName);
}
