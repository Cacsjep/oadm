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
    [RealModelFact(RealModelFiles.RfDetrMedium)]
    public async Task The_model_check_recognises_RF_DETR_with_the_COCO_91_list()
    {
        var check = await fixture.UploadAsync();

        Assert.Null(check.Refusal);
        var model = check.Model!;
        CocoPhotos.WriteFacts(model, RealModelFiles.RfDetrMedium, output);

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

    [RealModelFact(RealModelFiles.RfDetrMedium)]
    public Task Two_skaters_and_two_cars_are_found_where_COCO_has_them() => FindsTheTruthAsync(RealModelFiles.Skaters);

    [RealModelFact(RealModelFiles.RfDetrMedium)]
    public Task Three_people_and_the_car_behind_the_fence_are_found_where_COCO_has_them() => FindsTheTruthAsync(RealModelFiles.SkateBowl);

    [RealModelFact(RealModelFiles.RfDetrMedium)]
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
        output.WriteLine($"Count {result.Nodes[count].Value}; " + string.Join("; ", people.Select(p => CocoPhotos.Describe(p.Detection))));
        // Two: COCO's annotation of the photo has two people (no crowd region) and the photo shows no one else.
        Assert.Equal(2, result.Nodes[count].Value);
        Assert.All(people, p => Assert.Equal("person", p.Detection.ClassName));
    }

    private async Task FindsTheTruthAsync(string photo) =>
        await CocoPhotos.FindsTheTruthAsync(fixture, await fixture.ModelAsync(), photo, output);

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
