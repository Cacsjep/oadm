using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// Ready ONNX exports of the DETR family (docs/spec/vision-center.md "Tests", "Measured"), uploaded as they are, on the CPU:
/// the format the model check picks with its defaults, and every person and car COCO annotated on the two photos.
/// facebook/detr-resnet-50 (transformers.js export, full precision and quantized), RT-DETR v2 R18 and D-FINE S
/// (onnx-community), RF-DETR small (rf-detr-onnx). RF-DETR medium has its own tests (<see cref="RfDetrTests"/>).
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class DetrFamilyTests(RealModelFixture fixture, ITestOutputHelper output)
{
    // DETR R50 (Hugging Face): logits [1, Q, 92] with "no object" last, pred_boxes, pixel_values of a free size and a
    // pixel_mask [1, 64, 64]; 91 classes, the COCO ids.
    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_is_recognised_with_its_mask_input_and_the_COCO_91_list() =>
        RecognisedAsync(RealModelFiles.DetrResNet50, ModelFormat.Detr, 91, ClassLists.Coco91Id, fixedSize: null);

    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.DetrResNet50, RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.DetrResNet50)]
    public Task Detr_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.DetrResNet50, RealModelFiles.SkateBowl);

    // The quantized export (dynamic int8: DynamicQuantizeLinear, MatMulInteger, ConvInteger) passes the scan and runs.
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

    [RealModelFact(RealModelFiles.RtDetrV2R18)]
    public Task Rt_detr_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.RtDetrV2R18, RealModelFiles.SkateBowl);

    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_is_recognised_with_a_sigmoid_and_80_classes() =>
        RecognisedAsync(RealModelFiles.DFineSmall, ModelFormat.RtDetr, 80, ClassLists.Coco80Id, fixedSize: null);

    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.DFineSmall, RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.DFineSmall)]
    public Task D_fine_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.DFineSmall, RealModelFiles.SkateBowl);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_is_recognised_at_512() =>
        RecognisedAsync(RealModelFiles.RfDetrSmall, ModelFormat.RfDetr, 91, ClassLists.Coco91Id, fixedSize: 512);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_finds_the_skaters_and_cars() => FindsAsync(RealModelFiles.RfDetrSmall, RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.RfDetrSmall)]
    public Task Rf_detr_small_finds_the_people_and_car_at_the_skate_bowl() => FindsAsync(RealModelFiles.RfDetrSmall, RealModelFiles.SkateBowl);

    private async Task RecognisedAsync(string file, ModelFormat format, int classes, string list, int? fixedSize)
    {
        var check = await fixture.UploadAsync(file);
        Assert.Null(check.Refusal);
        var model = check.Model!;
        CocoPhotos.WriteFacts(model, file, output);

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
        Assert.Equal(RealModelFiles.Sha256Of(file), model.Sha256);
        Assert.True(model.CpuMs > 0);
    }

    private async Task FindsAsync(string file, string photo) =>
        await CocoPhotos.FindsTheTruthAsync(fixture, await fixture.ModelAsync(file), photo, output);
}
