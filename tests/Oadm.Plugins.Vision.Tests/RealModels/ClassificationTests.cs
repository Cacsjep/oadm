using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// Ready ImageNet classification exports (docs/spec/vision-center.md "Tests", "Measured"), uploaded with an ImageNet labels
/// file (the models carry no names), on the CPU: the format and input the model check picks, and the class of the cars of
/// the photo with the two skaters (COCO's boxes as the regions). ResNet-50 (transformers.js export of microsoft/resnet-50),
/// MobileNetV2 (ONNX Model Zoo), YOLO11n-cls (Ultralytics, the ONNX file of mobilint's repository).
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
        864, // tow truck
    ];

    /// <summary>The black car and the blue car behind the skaters (COCO's boxes).</summary>
    private static readonly RelativeBox[] Cars = [.. CocoPhotos.Truth[RealModelFiles.Skaters].Where(t => t.Class == "car").Select(t => t.Box)];

    // Hugging Face ResNet-50: pixel_values of a free size, logits [1, 1000]; trained at 224 with ImageNet mean and deviation.
    [RealModelFact(RealModelFiles.ResNet50, RealModelFiles.ResNet50Config)]
    public Task ResNet50_names_both_cars() => ClassifiesAsync(RealModelFiles.ResNet50, fixedInput: false);

    [RealModelFact(RealModelFiles.MobileNetV2, RealModelFiles.ResNet50Config)]
    public Task MobileNetV2_names_both_cars() => ClassifiesAsync(RealModelFiles.MobileNetV2, fixedInput: true);

    [RealModelFact(RealModelFiles.Yolo11Cls, RealModelFiles.ResNet50Config)]
    public Task Yolo11_cls_names_both_cars() => ClassifiesAsync(RealModelFiles.Yolo11Cls, fixedInput: true);

    /// <summary>The ImageNet names of the ResNet-50 export's config.json (<c>id2label</c>) as a labels file: one name per line.</summary>
    internal static string ImageNetLabels()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(RealModelFiles.PathOf(RealModelFiles.ResNet50Config)));
        var names = config.RootElement.GetProperty("id2label").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);
        Assert.Equal(1000, names.Count);
        return string.Join('\n', Enumerable.Range(0, names.Count).Select(i => names[i]));
    }

    private async Task ClassifiesAsync(string file, bool fixedInput)
    {
        var check = await fixture.UploadAsync(file, ImageNetLabels());
        Assert.Null(check.Refusal);
        var model = check.Model!;
        CocoPhotos.WriteFacts(model, file, output);

        Assert.Equal(ModelFormat.Classification, model.Format);
        Assert.Equal(ModelKind.Classification, model.Kind);
        Assert.Equal(fixedInput, model.FixedInput);
        Assert.Equal((224, 224), (model.InputWidth, model.InputHeight));
        Assert.Equal(1000, model.ClassNames.Count);
        Assert.Null(model.ClassListId);
        Assert.StartsWith("tench", model.ClassNames[0], StringComparison.Ordinal);

        var jpeg = await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.Skaters));
        using var frame = fixture.Inference.Decode(jpeg);
        foreach (var scaling in new[] { model.Scaling, model.Scaling == ModelScaling.ImageNet ? ModelScaling.ZeroToOne : ModelScaling.ImageNet })
        {
            var run = model with { Scaling = scaling };
            var found = await fixture.Inference.ClassifyAsync(run, frame, Cars, CancellationToken.None);
            const int Runs = 5;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Runs; i++)
            {
                found = await fixture.Inference.ClassifyAsync(run, frame, Cars[..1], CancellationToken.None);
            }

            var ms = watch.Elapsed.TotalMilliseconds / Runs;
            found = await fixture.Inference.ClassifyAsync(run, frame, Cars, CancellationToken.None);
            var classes = string.Join("; ", found.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.ClassName} (id {f.ClassId}) {f.Score:P0}")));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{model.Name} scaling {scaling}: {ms:F1} ms per picture on the CPU; {classes}"));
        }

        var results = await fixture.Inference.ClassifyAsync(model, frame, Cars, CancellationToken.None);
        Assert.False(fixture.Inference.OnGpu);
        Assert.All(results, r => Assert.True(CarClasses.Contains(r.ClassId), $"Not a car: {r.ClassName} (id {r.ClassId}) {r.Score:P0}"));
        Assert.All(results, r => Assert.InRange(r.Score, 0.05, 1));
    }
}
