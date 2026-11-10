using System.Diagnostics;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tracking;

using SkiaSharp;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// The time of each stage of a frame on the CPU (docs/spec/vision-center.md "Measured"): the JPEG decode of a 720p and a
/// 1080p camera picture, the picture into a model's input, one call through <see cref="OnnxInference"/> against ONNX
/// Runtime alone (the difference is our work around the model), and the tracker. Writes the times; budgets only catch a
/// stage that became many times slower. RealModel: needs the cached files, never in CI.
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", RealModelFiles.Category)]
public sealed class StageTimesTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const int Runs = 30;

    /// <summary>A camera picture of <paramref name="width"/> x <paramref name="height"/> made from a COCO photo, as the camera's JPEG.</summary>
    private static byte[] CameraJpeg(int width, int height)
    {
        using var photo = SKBitmap.Decode(RealModelFiles.PathOf(RealModelFiles.TwoCars));
        using var scaled = photo.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private double Time(string what, Action run)
    {
        run(); // warm up
        run();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            run();
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine($"{what,-48} {ms,8:0.00} ms");
        return ms;
    }

    private async Task<double> TimeAsync(string what, Func<Task> run)
    {
        await run();
        await run();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            await run();
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine($"{what,-48} {ms,8:0.00} ms");
        return ms;
    }

    [RealModelFact(RealModelFiles.Yolo11n, RealModelFiles.RfDetrMedium)]
    public async Task Each_stage_of_a_frame_is_measured()
    {
        var hd = CameraJpeg(1280, 720);
        var fullHd = CameraJpeg(1920, 1080);
        output.WriteLine($"JPEG 1280x720 {hd.Length / 1024} KB, 1920x1080 {fullHd.Length / 1024} KB");

        var decode720 = Time("decode 1280x720", () => VisionFrame.Decode(hd).Dispose());
        var decode1080 = Time("decode 1920x1080", () => VisionFrame.Decode(fullHd).Dispose());

        foreach (var (name, jpeg) in new[] { ("720p", hd), ("1080p", fullHd) })
        {
            Time($"  decode {name} at 1/2 (libjpeg)", () =>
            {
                using var codec = SKCodec.Create(SKData.CreateCopy(jpeg));
                var size = codec.GetScaledDimensions(0.5f);
                using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            });
            Time($"  per frame: decode {name} + input 640 letterbox", () =>
            {
                using var f = VisionFrame.Decode(jpeg);
                Preprocess.Write(f.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, new float[3 * 640 * 640]);
            });
            Time($"  per frame: decode {name} for a 640 model + input", () =>
            {
                using var f = VisionFrame.Decode(jpeg, new PictureNeed([new ModelInput(640, 640, ModelFit.Letterbox)]));
                Preprocess.Write(f.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, new float[3 * 640 * 640]);
            });
        }

        using var frame720 = VisionFrame.Decode(hd);
        using var frame1080 = VisionFrame.Decode(fullHd);
        var tensor640 = new float[3 * 640 * 640];
        var tensor576 = new float[3 * 576 * 576];
        var letterbox = Time("input 720p -> 640 letterbox, 0 to 1", () => Preprocess.Write(frame720.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, tensor640));
        Time("input 1080p -> 640 letterbox, 0 to 1", () => Preprocess.Write(frame1080.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, tensor640));
        var stretch = Time("input 720p -> 576 stretch, ImageNet", () => Preprocess.Write(frame720.Levels, null, ModelFit.Stretch, ModelScaling.ImageNet, 576, 576, tensor576));
        // The parts of the input and the sampling choices (Skia rebuilt the mipmaps on every call before PictureLevels).
        using (var target = new SKBitmap(new SKImageInfo(640, 640, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            Time("  draw, linear + mipmaps", () => { using var c = new SKCanvas(target); c.DrawImage(frame720.Image, SKRect.Create(0, 0, 1280, 720), SKRect.Create(0, 140, 640, 360), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)); });
            Time("  draw, linear", () => { using var c = new SKCanvas(target); c.DrawImage(frame720.Image, SKRect.Create(0, 0, 1280, 720), SKRect.Create(0, 140, 640, 360), new SKSamplingOptions(SKFilterMode.Linear)); });
            Time("  draw, cubic", () => { using var c = new SKCanvas(target); c.DrawImage(frame720.Image, SKRect.Create(0, 0, 1280, 720), SKRect.Create(0, 140, 640, 360), new SKSamplingOptions(SKCubicResampler.Mitchell)); });
            Time("  tensor write 640", () => Preprocess.WriteTensor(target, tensor640, ModelScaling.ZeroToOne));
            Time("  bitmap alloc 640", () => new SKBitmap(new SKImageInfo(640, 640, SKColorType.Rgba8888, SKAlphaType.Premul)).Dispose());
        }

        Time("input 720p crop 120x200 -> 224 stretch", () => Preprocess.Write(frame720.Levels, new SKRectI(500, 300, 620, 500), ModelFit.Stretch, ModelScaling.ImageNet, 224, 224, new float[3 * 224 * 224]));

        foreach (var file in new[] { RealModelFiles.Yolo11n, RealModelFiles.RfDetrMedium })
        {
            var model = await fixture.ModelAsync(file);
            var whole = await TimeAsync($"{file}: detect (input, model, outputs)", () => fixture.Inference.DetectAsync(model, frame720, 0.25, CancellationToken.None));

            // ONNX Runtime alone on the same input: one thread as the fixture's sessions.
            using var session = Oadm.Plugins.Shared.Vision.SessionFactory.Create(RealModelFiles.PathOf(file), Oadm.Plugins.Shared.Vision.ExecutionTarget.Cpu, threads: 1);
            var input = session.InputMetadata.First(i => i.Value.Dimensions.Length == 4).Key;
            var values = new float[3 * model.InputWidth * model.InputHeight];
            Preprocess.Write(frame720.Levels, null, model.Fit, model.Scaling, model.InputWidth, model.InputHeight, values);
            var tensor = new DenseTensor<float>(values, [1, 3, model.InputHeight, model.InputWidth]);
            var bare = Time($"{file}: ONNX Runtime alone", () =>
            {
                using var results = session.Run([NamedOnnxValue.CreateFromTensor(input, tensor)]);
            });
            output.WriteLine($"{file}: our work around the model {whole - bare,8:0.00} ms");
            Assert.True(whole - bare < 200, $"{whole - bare:0} ms around the model");
        }

        // A pipeline's frames per second: one frame at a time, against 4 in work whose Detect starts at decoding.
        {
            var yolo = await fixture.ModelAsync(RealModelFiles.Yolo11n);
            var b = new Oadm.Plugins.Vision.Tests.Nodes.PipelineBuilder();
            var cam = b.Add(NodeKinds.Camera);
            var det = b.After(cam, NodeKinds.Detect, Oadm.Plugins.Vision.Tests.Nodes.PipelineBuilder.DetectSettings(25));
            b.After(b.After(b.After(det, NodeKinds.Track), NodeKinds.Count), NodeKinds.Event);
            var pipeline = b.Build();
            const int Frames = 24;
            var serial = Oadm.Plugins.Vision.Nodes.PipelineEvaluator.Create(pipeline, _ => yolo, fixture.Inference, PipelineMode.Watching);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Frames; i++)
            {
                using var f = fixture.Inference.Decode(hd, serial.PictureNeed);
                await serial.RunAsync(f, DateTimeOffset.UnixEpoch.AddSeconds(i), CancellationToken.None);
            }

            var oneAtATime = Frames / watch.Elapsed.TotalSeconds;
            var pipelined = Oadm.Plugins.Vision.Nodes.PipelineEvaluator.Create(pipeline, _ => yolo, fixture.Inference, PipelineMode.Watching);
            watch.Restart();
            var inWork = new Queue<(IVisionFrame Frame, IFramePrefetch? Prefetch)>();
            for (var i = 0; i < Frames; i++)
            {
                var f = fixture.Inference.Decode(hd, pipelined.PictureNeed);
                inWork.Enqueue((f, pipelined.Prefetch(f, CancellationToken.None)));
                if (inWork.Count == VisionLimits.MaxFramesInWork || i == Frames - 1)
                {
                    while (inWork.TryDequeue(out var w))
                    {
                        await pipelined.RunAsync(w.Frame, DateTimeOffset.UnixEpoch.AddSeconds(i), w.Prefetch, CancellationToken.None);
                        w.Frame.Dispose();
                    }
                }
            }

            var fourInWork = Frames / watch.Elapsed.TotalSeconds;
            output.WriteLine($"yolo11n pipeline, one thread a call: {oneAtATime,5:0.0} fps one frame at a time, {fourInWork,5:0.0} fps with 4 in work");
        }

        // The tracker with 60 objects moving on a belt.
        var tracker = new ObjectTracker(TrackerSettings.Default);
        var objects = Enumerable.Range(0, 60).Select(i => new VisionObject
        {
            Detection = new Detection(49, "orange", 0.25, new RelativeBox(0.02 + (i % 10) * 0.095, 0.05 + (i / 10) * 0.15, 0.06, 0.1)),
        }).ToArray();
        var at = DateTimeOffset.UnixEpoch;
        var step = 0;
        var track = Time("track 60 objects", () =>
        {
            step++;
            at += TimeSpan.FromMilliseconds(66);
            var moved = objects.Select(o => o with { Detection = o.Detection with { Box = o.Detection.Box with { X = o.Detection.Box.X + (step * 0.002 % 0.5) } } }).ToArray();
            tracker.Update(moved, at);
        });

        Assert.True(decode720 < 200 && decode1080 < 400 && letterbox < 200 && stretch < 200 && track < 50, "a stage many times slower than measured");
    }
}
