using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// A model family of the Import model wizard (docs/spec/vision-center.md "Import model"): where a model comes from, and the
/// format, fit and scaling it is read with. The wizard preselects the family the model check found.
/// </summary>
public sealed record ModelFamily(string Name, string Examples, ModelKind Kind, ModelFormat Format, ModelFit Fit, ModelScaling Scaling)
{
    public override string ToString() => Name;
}

/// <summary>The families of the wizard's select: detection, then classification.</summary>
public static class ModelFamilies
{
    public static IReadOnlyList<ModelFamily> All { get; } =
    [
        new("YOLOv8, YOLO11, YOLO12", "Ultralytics detection exports, also YOLOv5.", ModelKind.Detection, ModelFormat.Yolo, ModelFit.Letterbox, ModelScaling.ZeroToOne),
        new("YOLOv10, YOLO26 end to end", "Exports that sort out overlapping boxes themselves.", ModelKind.Detection, ModelFormat.YoloWithSuppression, ModelFit.Letterbox, ModelScaling.ZeroToOne),
        new("YOLOX", "Megvii's YOLOX releases.", ModelKind.Detection, ModelFormat.Yolox, ModelFit.Letterbox, ModelScaling.ZeroTo255Bgr),
        new("RF-DETR", "Roboflow's RF-DETR.", ModelKind.Detection, ModelFormat.RfDetr, ModelFit.Stretch, ModelScaling.ImageNet),
        new("DETR", "Facebook's DETR, also Hugging Face exports.", ModelKind.Detection, ModelFormat.Detr, ModelFit.KeepShapePad, ModelScaling.ImageNet),
        new("RT-DETR, D-FINE", "Hugging Face exports.", ModelKind.Detection, ModelFormat.RtDetr, ModelFit.Stretch, ModelScaling.ZeroToOne),
        new("DETR with labels", "Exports with boxes, scores and labels apart.", ModelKind.Detection, ModelFormat.DetrWithLabels, ModelFit.Stretch, ModelScaling.ZeroToOne),
        new("ImageNet classifier", "ResNet, MobileNet, EfficientNet and others.", ModelKind.Classification, ModelFormat.Classification, ModelFit.Stretch, ModelScaling.ImageNet),
        new("YOLO classifier", "Ultralytics classification exports.", ModelKind.Classification, ModelFormat.Classification, ModelFit.Stretch, ModelScaling.ZeroToOne),
    ];

    /// <summary>The family the model check found: by the format, a classifier by its scaling (Ultralytics' take 0 to 1).</summary>
    public static ModelFamily Of(VisionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Kind == ModelKind.Classification
            ? All.First(f => f.Kind == ModelKind.Classification && f.Scaling == (model.Scaling == ModelScaling.ZeroToOne ? ModelScaling.ZeroToOne : ModelScaling.ImageNet))
            : All.FirstOrDefault(f => f.Kind == ModelKind.Detection && f.Format == model.Format) ?? All[0];
    }
}
