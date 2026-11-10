using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

using Xunit.Abstractions;

using static Oadm.Plugins.Vision.Tests.RealModels.RealModelChecks;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// Ready ONNX exports of the DETR family (docs/spec/vision-center.md "Tests", "Measured"), uploaded as they are, on the CPU:
/// the format the model check picks with its defaults, and the people and cars COCO annotated on the two photos.
/// facebook/detr-resnet-50 (Transformers.js export, full precision and quantized), RT-DETR v2 R18 and D-FINE S
/// (onnx-community), YOLO26n as onnx-community exports it (the outputs of RT-DETR), RF-DETR small (rf-detr-onnx). RF-DETR
/// medium has its own tests (<see cref="RfDetrTests"/>). Category RealModel: never in manage test unit or CI.
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class DetrFamilyTests(RealModelFixture fixture, ITestOutputHelper output)
{
    /// <summary>The score a nano model is read with (Ultralytics' default confidence, as in <see cref="YoloExpected"/>).</summary>
    private const double Nano = 0.25;

    // DETR R50 (Hugging Face): logits [1, 100, 92] with "no object" last, pred_boxes, pixel_values of a free size and a
    // pixel_mask [1, 64, 64]; 91 classes, the COCO ids.
    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_is_recognised_with_its_mask_input_and_the_COCO_91_list() =>
        RecognisedAsync(RealModelFiles.DetrResNet50, ModelFormat.Detr, 91, ClassLists.Coco91Id, fixedSize: null);

    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.DetrResNet50, RealModelFiles.Skaters);

    // The person behind: found at x 0.900 (only the visible part), overlap 0.47 with COCO's box.
    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.DetrResNet50, RealModelFiles.SkateBowl, PersonBehind);

    // The quantized export (8-bit weights, onnxruntime.quant: DynamicQuantizeLinear, MatMulInteger) passes the scan and runs.
    [RealModelFact(RealModelFiles.DetrResNet50Quantized)]
    public Task Quantized_detr_is_recognised_like_the_full_one() =>
        RecognisedAsync(RealModelFiles.DetrResNet50Quantized, ModelFormat.Detr, 91, ClassLists.Coco91Id, fixedSize: null);

    [RealModelFact(RealModelFiles.DetrResNet50Quantized)]
    public Task Quantized_detr_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.DetrResNet50Quantized, RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.DetrResNet50Quantized)]
    public Task Quantized_detr_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.DetrResNet50Quantized, RealModelFiles.SkateBowl);

    // RT-DETR v2 (Hugging Face): logits [1, 300, 80] with a sigmoid per class, no "no object"; 80 classes in YOLO's order.
    [RealModelFact(RealModelFiles.RtDetrV2R18)]
    public Task Rt_detr_is_recognised_with_a_sigmoid_and_80_classes() =>
        RecognisedAsync(RealModelFiles.RtDetrV2R18, ModelFormat.RtDetr, 80, ClassLists.Coco80Id, fixedSize: null);

    [RealModelFact(RealModelFiles.RtDetrV2R18)]
    public Task Rt_detr_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.RtDetrV2R18, RealModelFiles.Skaters);

    // At 50 % it also finds a person at 51 % where COCO has none (beside the skater in the air): read at 60 %.
    [RealModelFact(RealModelFiles.RtDetrV2R18)]
    public Task Rt_detr_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.RtDetrV2R18, RealModelFiles.SkateBowl, 0.6);

    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_is_recognised_with_a_sigmoid_and_80_classes() =>
        RecognisedAsync(RealModelFiles.DFineSmall, ModelFormat.RtDetr, 80, ClassLists.Coco80Id, fixedSize: null);

    // The car on the right, half hidden by the skater: boxed with the skater (overlap 0.41).
    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.DFineSmall, RealModelFiles.Skaters, RightCar);

    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.DFineSmall, RealModelFiles.SkateBowl, PersonBehind);

    // YOLO26n for Transformers.js: logits [1, 300, 80] first like DETR, but a sigmoid per class; read as RT-DETR.
    [RealModelFact(RealModelFiles.Yolo26nTransformersJs)]
    public Task Yolo26_of_transformers_js_is_recognised_as_rt_detr() =>
        RecognisedAsync(RealModelFiles.Yolo26nTransformersJs, ModelFormat.RtDetr, 80, ClassLists.Coco80Id, fixedSize: 640);

    [RealModelFact(RealModelFiles.Yolo26nTransformersJs)]
    public Task Yolo26_of_transformers_js_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.Yolo26nTransformersJs, RealModelFiles.Skaters, Nano);

    [RealModelFact(RealModelFiles.Yolo26nTransformersJs)]
    public Task Yolo26_of_transformers_js_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.Yolo26nTransformersJs, RealModelFiles.SkateBowl, Nano, PersonBehind);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_is_recognised_at_512() =>
        RecognisedAsync(RealModelFiles.RfDetrSmall, ModelFormat.RfDetr, 91, ClassLists.Coco91Id, fixedSize: 512);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.RfDetrSmall, RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.RfDetrSmall, RealModelFiles.SkateBowl);

    private async Task RecognisedAsync(string file, ModelFormat format, int classes, string list, int? fixedSize)
    {
        var model = Describe(await fixture.UploadAsync(file), file, output);

        Assert.Equal(format, model.Format);
        Assert.Equal(ModelKind.Detection, model.Kind);
        Assert.Equal(OutputFormats.DefaultFit(format), model.Fit);
        Assert.Equal(OutputFormats.DefaultScaling(format), model.Scaling);
        Assert.Equal(fixedSize is not null, model.FixedInput);
        Assert.Equal(fixedSize ?? VisionLimits.DefaultInputSize, model.InputWidth);
        Assert.Equal(fixedSize ?? VisionLimits.DefaultInputSize, model.InputHeight);
        Assert.Equal(classes, model.ClassNames.Count);
        Assert.Equal(list, model.ClassListId);
        Assert.Equal("person", model.ClassNames[classes == 91 ? 1 : 0]);
        Assert.Equal("car", model.ClassNames[classes == 91 ? 3 : 2]);
        Assert.Equal("toothbrush", model.ClassNames[^1]);
    }

    private Task FindsAsync(string file, string photo, params string[] mayMiss) => FindsAsync(file, photo, 0.5, mayMiss);

    private async Task FindsAsync(string file, string photo, double minScore, params string[] mayMiss) =>
        await FindsTheTruthAsync(fixture, await fixture.ModelAsync(file), photo, output, minScore, mayMiss);
}
