using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Vision.Model;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// Ready ImageNet classification exports (docs/spec/vision-center.md "Tests", "Measured"), uploaded with an ImageNet labels
/// file (none of them carries names), on the CPU: the format, input and scaling the model check picks, and the class of the
/// two cars of COCO val2017 97679 (COCO's boxes as the regions, as Classify does with a detection's box). ResNet-50
/// (Transformers.js export of microsoft/resnet-50), MobileNetV2 (ONNX Model Zoo), YOLO11n-cls (Ultralytics, the ONNX file
/// of mobilint's repository). Category RealModel: never in manage test unit or CI.
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class ClassificationTests(RealModelFixture fixture, ITestOutputHelper output)
{
    /// <summary>The ImageNet classes of a car or a part of one (0-based ids of the 1,000 classes).</summary>
    private static readonly HashSet<int> CarClasses =
    [
        407, // ambulance
        436, // beach wagon, station wagon
        468, // cab
        479, // car wheel
        511, // convertible
        581, // grille
        609, // jeep
        627, // limousine
        656, // minivan
        661, // Model T
        717, // pickup
        751, // racer
        817, // sports car
    ];

    /// <summary>The silver sedan and the white station wagon (COCO's boxes, instances_val2017.json).</summary>
    private static readonly RelativeBox[] Cars = [new(0.162, 0.332, 0.653, 0.451), new(0.560, 0.332, 0.429, 0.356)];

    // Hugging Face ResNet-50: pixel_values of a free size (224 x 224 chosen), logits [1, 1000]; ImageNet mean and deviation.
    [RealModelFact(RealModelFiles.ResNet50, RealModelFiles.ResNet50Config, RealModelFiles.TwoCars)]
    public Task ResNet50_names_both_cars() => ClassifiesAsync(RealModelFiles.ResNet50, fixedInput: false, ModelScaling.ImageNet);

    [RealModelFact(RealModelFiles.MobileNetV2, RealModelFiles.ResNet50Config, RealModelFiles.TwoCars)]
    public Task MobileNetV2_names_both_cars() => ClassifiesAsync(RealModelFiles.MobileNetV2, fixedInput: true, ModelScaling.ImageNet);

    // Ultralytics' classifiers take 0 to 1 (with ImageNet scaling YOLO11n-cls answers "hourglass" at 100 % for anything).
    // This file has no metadata, so it gets ImageNet like any classifier: the model's Settings set 0 to 1 (updateModel).
    [RealModelFact(RealModelFiles.Yolo11Cls, RealModelFiles.ResNet50Config, RealModelFiles.TwoCars)]
    public Task Yolo11_cls_names_both_cars_once_set_to_0_to_1() =>
        ClassifiesAsync(RealModelFiles.Yolo11Cls, fixedInput: true, ModelScaling.ImageNet, setTo: ModelScaling.ZeroToOne);

    /// <summary>The ImageNet names of the ResNet-50 export's config.json (<c>id2label</c>) as a labels file: one name per line.</summary>
    internal static string ImageNetLabels()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(RealModelFiles.PathOf(RealModelFiles.ResNet50Config)));
        var names = config.RootElement.GetProperty("id2label").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);
        Assert.Equal(1000, names.Count);
        return string.Join('\n', Enumerable.Range(0, names.Count).Select(i => names[i]));
    }

    private async Task ClassifiesAsync(string file, bool fixedInput, ModelScaling scaling, ModelScaling? setTo = null)
    {
        var model = RealModelChecks.Describe(await fixture.UploadAsync(file, ImageNetLabels()), file, output);

        Assert.Equal(ModelFormat.Classification, model.Format);
        Assert.Equal(ModelKind.Classification, model.Kind);
        Assert.Equal(fixedInput, model.FixedInput);
        Assert.Equal((224, 224), (model.InputWidth, model.InputHeight));
        Assert.Equal(ModelFit.Stretch, model.Fit);
        Assert.Equal(1000, model.ClassNames.Count);
        Assert.Null(model.ClassListId);
        Assert.Equal(("tench, Tinca tinca", "sports car, sport car"), (model.ClassNames[0], model.ClassNames[817]));
        Assert.Equal(scaling, model.Scaling);
        if (setTo is { } changed)
        {
            model = await fixture.LibraryOf(file).UpdateAsync(model with { Scaling = changed }, CancellationToken.None);
            Assert.Equal(changed, model.Scaling);
        }

        var jpeg = await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.TwoCars));
        using var frame = fixture.Inference.Decode(jpeg);
        var found = await fixture.Inference.ClassifyAsync(model, frame, Cars, CancellationToken.None);
        const int Runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            await fixture.Inference.ClassifyAsync(model, frame, Cars[..1], CancellationToken.None);
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        var other = await fixture.Inference.ClassifyAsync(model with { Scaling = model.Scaling == ModelScaling.ImageNet ? ModelScaling.ZeroToOne : ModelScaling.ImageNet }, frame, Cars, CancellationToken.None);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{model.Name} on {RealModelFiles.TwoCars}: {ms:F1} ms per picture on the CPU (one thread per call); {Names(found)}; with the other scaling: {Names(other)}"));

        Assert.False(fixture.Inference.OnGpu);
        Assert.Equal(2, found.Count);
        Assert.All(found, r => Assert.True(CarClasses.Contains(r.ClassId), "Not a car: " + Names([r])));
        Assert.All(found, r => Assert.InRange(r.Score, 0.2, 1));
    }

    private static string Names(IEnumerable<ClassResult> results) =>
        string.Join("; ", results.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.ClassName} (id {r.ClassId}) {r.Score:P0}")));
}
