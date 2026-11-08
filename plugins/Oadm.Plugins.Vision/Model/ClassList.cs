namespace Oadm.Plugins.Vision.Model;

/// <summary>
/// A class list profile (docs/spec/vision-center.md "Class lists"): the names of a model's class ids, chosen on the model.
/// Built-in lists (COCO with 91 ids for DETR and RF-DETR, COCO with 80 ids for YOLO) cannot be changed or deleted; the
/// user's own lists are server-wide like models. "N/A" names a gap.
/// </summary>
public sealed record ClassList
{
    /// <summary>"coco-91", "coco-90" and "coco-80" for the built-in ones, else 32 hex digits.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>1 to 64 characters, unique ("COCO for DETR (91)").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One name per class id, at most <see cref="VisionLimits.MaxClasses"/>.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    public bool BuiltIn { get; init; }
}
