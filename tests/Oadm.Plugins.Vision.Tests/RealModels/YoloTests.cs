using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>What the model check must find out about a real YOLO-family model.</summary>
internal sealed record YoloExpected(ModelFormat Format, int Side, bool Fixed, ModelScaling Scaling, string? ClassListId)
{
    public ModelFit Fit { get; init; } = ModelFit.Letterbox;

    /// <summary>The score the photos are read with (Ultralytics' default confidence for the nano models).</summary>
    public double MinScore { get; init; } = 0.25;

    /// <summary>The COCO objects of the photos this model was measured to miss (<see cref="RealModelChecks"/>' names); the run writes them out.</summary>
    public IReadOnlyCollection<string> MayMiss { get; init; } = [];
}

/// <summary>
/// The YOLO family end to end on the CPU (docs/spec/vision-center.md "Tests", "Measured"): the model check of a chunked upload
/// through the real <see cref="ModelLibrary"/> and the real inference on the two COCO photos. Ultralytics' weights are
/// AGPL-3.0 (tested here, never shipped), YOLOX's Apache-2.0. Category RealModel: never in manage test unit or CI.
/// </summary>
internal static class YoloRuns
{
    public static async Task CheckAsync(RealModelFixture fixture, string file, ITestOutputHelper output, YoloExpected expected)
    {
        var model = RealModelChecks.Describe(await fixture.UploadAsync(file), file, output);

        Assert.Equal(expected.Format, model.Format);
        Assert.Equal(ModelKind.Detection, model.Kind);
        Assert.Equal(expected.Fixed, model.FixedInput);
        Assert.Equal((expected.Side, expected.Side), (model.InputWidth, model.InputHeight));
        Assert.Equal(expected.Fit, model.Fit);
        Assert.Equal(expected.Scaling, model.Scaling);
        Assert.Equal(80, model.ClassNames.Count);
        Assert.Equal(expected.ClassListId, model.ClassListId);
        Assert.Equal(("person", "car", "toothbrush"), (model.ClassNames[0], model.ClassNames[2], model.ClassNames[79]));
    }

    public static async Task PhotoAsync(RealModelFixture fixture, string file, string photo, ITestOutputHelper output, YoloExpected expected) =>
        await RealModelChecks.FindsTheTruthAsync(fixture, await fixture.ModelAsync(file), photo, output, expected.MinScore, expected.MayMiss);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolov8nTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolov8n;
    private static readonly YoloExpected Expected = new(ModelFormat.Yolo, 640, true, ModelScaling.ZeroToOne, null)
    {
        MayMiss = [RealModelChecks.CarBehindTheFence],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_YOLO_with_the_names_of_its_metadata() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolo11nTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolo11n;

    // Exported with a free input size: the user's size, 640 by default.
    private static readonly YoloExpected Expected = new(ModelFormat.Yolo, 640, false, ModelScaling.ZeroToOne, null)
    {
        MayMiss = [RealModelChecks.CarBehindTheFence, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_YOLO_with_a_free_input_size() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolo11nHalfPrecisionTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolo11nFp16;
    private static readonly YoloExpected Expected = new(ModelFormat.Yolo, 640, true, ModelScaling.ZeroToOne, null)
    {
        MayMiss = [RealModelChecks.CarBehindTheFence, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_takes_the_half_precision_input() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolo11nWithoutNamesTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolo11nNoNames;

    // No names in the file: 80 classes take "COCO, 80 classes".
    private static readonly YoloExpected Expected = new(ModelFormat.Yolo, 640, true, ModelScaling.ZeroToOne, ClassLists.Coco80Id)
    {
        MayMiss = [RealModelChecks.CarBehindTheFence, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_gives_the_COCO_80_list() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolov5nTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolov5n;
    private static readonly YoloExpected Expected = new(ModelFormat.Yolo, 640, true, ModelScaling.ZeroToOne, null)
    {
        MayMiss = [RealModelChecks.LeftCar, RealModelChecks.RightCar, RealModelChecks.CarBehindTheFence, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_YOLOv5_in_half_precision() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolov10nTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolov10n;

    // Its metadata names are only the ids ("0" to "79"): 80 classes take "COCO, 80 classes".
    private static readonly YoloExpected Expected = new(ModelFormat.YoloWithSuppression, 640, true, ModelScaling.ZeroToOne, ClassLists.Coco80Id)
    {
        MayMiss = [RealModelChecks.RightCar, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_the_end_to_end_output_with_the_COCO_80_list() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class Yolo26nTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.Yolo26n;
    private static readonly YoloExpected Expected = new(ModelFormat.YoloWithSuppression, 640, true, ModelScaling.ZeroToOne, null)
    {
        MayMiss = [RealModelChecks.RightCar, RealModelChecks.PersonBehind],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_the_end_to_end_output() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class YoloxNanoTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.YoloxNano;
    private static readonly YoloExpected Expected = new(ModelFormat.Yolox, 416, true, ModelScaling.ZeroTo255Bgr, ClassLists.Coco80Id)
    {
        MayMiss = [RealModelChecks.RightCar, RealModelChecks.CarBehindTheFence],

        // The smallest model guesses a person at 26 % across two at the skate bowl since the input is drawn as YOLOX's
        // training draws it (linear, no mipmaps, 2026-11-09); its real people stay above 30 %.
        MinScore = 0.3,
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_YOLOX_with_the_COCO_80_list() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}

[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class YoloxTinyTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const string File = RealModelFiles.YoloxTiny;
    private static readonly YoloExpected Expected = new(ModelFormat.Yolox, 416, true, ModelScaling.ZeroTo255Bgr, ClassLists.Coco80Id)
    {
        MayMiss = [RealModelChecks.CarBehindTheFence],
    };

    [RealModelFact(File)]
    public Task The_model_check_reads_YOLOX_with_the_COCO_80_list() => YoloRuns.CheckAsync(fixture, File, output, Expected);

    [RealModelFact(File)]
    public Task The_skaters_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.Skaters, output, Expected);

    [RealModelFact(File)]
    public Task The_people_at_the_skate_bowl_are_found_where_COCO_has_them() => YoloRuns.PhotoAsync(fixture, File, RealModelFiles.SkateBowl, output, Expected);
}
