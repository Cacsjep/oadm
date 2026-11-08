using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Nodes;
using Oadm.Plugins.Vision.Tests.Nodes;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// RF-DETR medium (rf-detr-onnx, Apache-2.0) end to end on the CPU (docs/spec/vision-center.md "Tests"): the model check of a
/// chunked upload, the inference on two COCO val2017 photos against their COCO boxes, and a pipeline Camera > Detect objects >
/// Filter (person) > Count through the real evaluator. Category RealModel: never in manage test unit or CI; the files come
/// from RealModels.targets (manage test models).
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class RfDetrTests(RealModelFixture fixture, ITestOutputHelper output)
{
    /// <summary>The COCO val2017 boxes (x, y, width, height, relative) of the people and cars of each photo (instances_val2017.json).</summary>
    private static readonly Dictionary<string, (string Class, RelativeBox Box)[]> Truth = new()
    {
        [RealModelFiles.Skaters] =
        [
            ("person", new RelativeBox(0.147, 0.240, 0.159, 0.363)),
            ("person", new RelativeBox(0.563, 0.245, 0.235, 0.454)),
            ("car", new RelativeBox(0.013, 0.281, 0.479, 0.285)),
            ("car", new RelativeBox(0.709, 0.306, 0.171, 0.248)),
        ],
        [RealModelFiles.SkateBowl] =
        [
            ("person", new RelativeBox(0.346, 0.097, 0.294, 0.516)),
            ("person", new RelativeBox(0.723, 0.072, 0.265, 0.552)),
            ("person", new RelativeBox(0.826, 0.168, 0.169, 0.438)),
            ("car", new RelativeBox(0.084, 0.003, 0.332, 0.198)),
        ],
    };

    [RealModelFact]
    public async Task The_model_check_recognises_RF_DETR_with_the_COCO_91_list()
    {
        var check = await fixture.UploadAsync();

        Assert.Null(check.Refusal);
        var model = check.Model!;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{model.Format}, {model.Kind}, input {model.InputWidth} x {model.InputHeight} (fixed {model.FixedInput}), fit {model.Fit}, scaling {model.Scaling}, {model.ClassNames.Count} classes, list {model.ClassListId}, CPU {model.CpuMs} ms, {model.SizeBytes:N0} bytes, SHA-256 {model.Sha256}"));
        using (var session = SessionFactory.Create(RealModelFiles.PathOf(RealModelFiles.RfDetrMedium), ExecutionTarget.Cpu, threads: 1))
        {
            foreach (var (name, meta) in session.InputMetadata.Concat(session.OutputMetadata))
            {
                output.WriteLine($"  {name}: [{string.Join(", ", meta.Dimensions)}] {meta.ElementDataType}");
            }
        }

        Assert.Equal(ModelFormat.RfDetr, model.Format);
        Assert.Equal(ModelKind.Detection, model.Kind);
        Assert.True(model.FixedInput);
        Assert.Equal(576, model.InputWidth);
        Assert.Equal(576, model.InputHeight);
        Assert.Equal(ModelFit.Stretch, model.Fit);
        Assert.Equal(ModelScaling.ImageNet, model.Scaling);
        Assert.Equal(91, model.ClassNames.Count);
        Assert.Equal(ClassLists.Coco91Id, model.ClassListId);
        Assert.Equal(("person", "car", "toothbrush"), (model.ClassNames[1], model.ClassNames[3], model.ClassNames[90]));
        Assert.Equal(RealModelFiles.Sha256Of(RealModelFiles.RfDetrMedium), model.Sha256);
        Assert.True(model.CpuMs > 0);
    }

    [RealModelFact]
    public Task Two_skaters_and_two_cars_are_found_where_COCO_has_them() => FindsTheTruthAsync(RealModelFiles.Skaters);

    [RealModelFact]
    public Task Three_people_and_the_car_behind_the_fence_are_found_where_COCO_has_them() => FindsTheTruthAsync(RealModelFiles.SkateBowl);

    [RealModelFact]
    public async Task A_pipeline_counts_the_two_skaters()
    {
        var model = await fixture.ModelAsync();
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, new JsonObject { [NodeKinds.ModelKey] = model.Id, [NodeKinds.MinScoreKey] = 50 });
        var filter = b.After(detect, NodeKinds.Filter, new JsonObject { [NodeKinds.ClassesKey] = new JsonArray("person") });
        var count = b.After(filter, NodeKinds.Count);
        var pipeline = b.Build();
        var check = VisionGraph.Check(pipeline, fixture.Library.Find);
        Assert.True(check.Ok, string.Join("; ", check.GraphErrors.Concat(check.NodeErrors.Select(e => e.Key + ": " + e.Value))));

        var evaluator = PipelineEvaluator.Create(pipeline, fixture.Library.Find, fixture.Inference, PipelineMode.OnAction);
        var frames = new PhotoFrames(RealModelFiles.PathOf(RealModelFiles.Skaters));
        using var frame = fixture.Inference.Decode(await frames.SingleAsync(new FrameRequest(Guid.Empty, 1, Resolution.Default), CancellationToken.None));
        var result = await evaluator.RunAsync(frame, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Null(result.ModelFailure);
        var people = result.Nodes[filter].Objects;
        output.WriteLine($"Count {result.Nodes[count].Value}; " + string.Join("; ", people.Select(p => Describe(p.Detection))));
        // Two: COCO's annotation of the photo has two people (no crowd region) and the photo shows no one else.
        Assert.Equal(2, result.Nodes[count].Value);
        Assert.All(people, p => Assert.Equal("person", p.Detection.ClassName));
    }

    private async Task FindsTheTruthAsync(string photo)
    {
        var model = await fixture.ModelAsync();
        var jpeg = await File.ReadAllBytesAsync(RealModelFiles.PathOf(photo));
        using var frame = fixture.Inference.Decode(jpeg);

        // One call warms the session; then the time per picture, one CPU thread per call as the workers run it.
        var found = await fixture.Inference.DetectAsync(model, frame, 0.5, CancellationToken.None);
        const int Runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            found = await fixture.Inference.DetectAsync(model, frame, 0.5, CancellationToken.None);
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{photo} ({frame.Width} x {frame.Height}): {ms:F0} ms per picture on the CPU (one thread per call; {Environment.ProcessorCount} cores, {fixture.Inference.Capacity} workers); {found.Count} objects at 50 % or more"));
        foreach (var d in found)
        {
            output.WriteLine("  " + Describe(d));
        }

        Assert.False(fixture.Inference.OnGpu);
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, d => d.ClassName == VisionLimits.NoClassName);
        Assert.All(found, d => Assert.InRange(d.Score, 0.5, 1));
        foreach (var (name, box) in Truth[photo])
        {
            var best = found.Where(d => d.ClassName == name).Select(d => IoU(d.Box, box)).DefaultIfEmpty(0).Max();
            Assert.True(best >= 0.5, string.Create(CultureInfo.InvariantCulture, $"No {name} found at {Describe(box)}: best overlap {best:F2}."));
        }

        // Every person found is one of COCO's people (no ghost people).
        var truePeople = Truth[photo].Where(t => t.Class == "person").Select(t => t.Box).ToList();
        Assert.All(found.Where(d => d.ClassName == "person"), d => Assert.True(truePeople.Max(t => IoU(d.Box, t)) >= 0.5, "A person where COCO has none: " + Describe(d)));
    }

    private static double IoU(RelativeBox a, RelativeBox b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        return inter / ((a.Width * a.Height) + (b.Width * b.Height) - inter);
    }

    private static string Describe(RelativeBox b) =>
        string.Create(CultureInfo.InvariantCulture, $"x {b.X:F3} y {b.Y:F3} w {b.Width:F3} h {b.Height:F3}");

    private static string Describe(Detection d) =>
        string.Create(CultureInfo.InvariantCulture, $"{d.ClassName} (id {d.ClassId}) {d.Score:P0} at {Describe(d.Box)}");

    /// <summary>A camera whose every picture is the photo.</summary>
    private sealed class PhotoFrames(string path) : IFrameSource
    {
        public async Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct) => await File.ReadAllBytesAsync(path, ct);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return await File.ReadAllBytesAsync(path, ct);
        }
    }
}
