using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Models;

/// <summary>
/// The built-in class lists (docs/spec/vision-center.md "Class lists"), named by the models they fit: COCO 2017 by its
/// category ids (91 ids, 0 and the 10 unused ids "N/A": DETR and RF-DETR) and COCO's 80 classes in a row (YOLO). In code,
/// never stored.
/// </summary>
public static class ClassLists
{
    public const string Coco91Id = "coco-91";

    public const string Coco80Id = "coco-80";

    /// <summary>COCO 2017 by category id: index = id.</summary>
    private static readonly string[] Coco91Names =
    [
        "N/A", "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "N/A", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse",
        "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "N/A", "backpack", "umbrella", "N/A",
        "N/A", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball", "kite", "baseball bat",
        "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle", "N/A", "wine glass", "cup", "fork", "knife",
        "spoon", "bowl", "banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza",
        "donut", "cake", "chair", "couch", "potted plant", "bed", "N/A", "dining table", "N/A", "N/A",
        "toilet", "N/A", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven",
        "toaster", "sink", "refrigerator", "N/A", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
        "toothbrush",
    ];

    public static ClassList Coco91 { get; } = new() { Id = Coco91Id, Name = "COCO for DETR and RF-DETR (91)", Names = Coco91Names, BuiltIn = true };

    /// <summary>The same classes without the gaps, in the order of their ids.</summary>
    public static ClassList Coco80 { get; } = new()
    {
        Id = Coco80Id,
        Name = "COCO for YOLO (80)",
        Names = [.. Coco91Names.Where(n => n != VisionLimits.NoClassName)],
        BuiltIn = true,
    };

    public static IReadOnlyList<ClassList> BuiltIn { get; } = [Coco91, Coco80];

    /// <summary>The built-in list of <paramref name="id"/>, or null.</summary>
    public static ClassList? BuiltInOf(string? id) => BuiltIn.FirstOrDefault(l => l.Id == id);

    /// <summary>The built-in list a model of <paramref name="classes"/> classes gets when it names none itself: 91 (DETR, RF-DETR), 80 (YOLO), else none.</summary>
    public static ClassList? ForClassCount(int classes) => classes switch
    {
        91 => Coco91,
        80 => Coco80,
        _ => null,
    };
}

/// <summary>A class list that cannot be stored as it is: the field ("name", "names") and the text below it.</summary>
public sealed class ClassListFieldException(string field, string message) : ArgumentException(message)
{
    public const string NameField = "name";

    public const string NamesField = "names";

    public string Field { get; } = field;
}
