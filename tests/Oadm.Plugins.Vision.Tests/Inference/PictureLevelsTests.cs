using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Nodes;
using Oadm.Plugins.Vision.Tests.Nodes;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Tests.Inference;

/// <summary>
/// The work around a model call (docs/spec/vision-center.md "Measured"): the picture's halves made once per frame, a JPEG
/// decoded only as large as the pipeline's models need, and what each pipeline needs.
/// </summary>
public sealed class PictureLevelsTests
{
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(SKRect.Create(width / 4f, height / 4f, width / 10f, height / 5f), white);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public void A_half_is_the_rounded_mean_of_each_two_by_two()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, new SKColor(255, 0, 0));
        bitmap.SetPixel(1, 0, new SKColor(0, 0, 0));
        bitmap.SetPixel(0, 1, new SKColor(255, 0, 0));
        bitmap.SetPixel(1, 1, new SKColor(0, 0, 255));
        for (var x = 2; x < 4; x++)
        {
            bitmap.SetPixel(x, 0, new SKColor(10, 20, 30));
            bitmap.SetPixel(x, 1, new SKColor(11, 21, 31));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var half = PictureLevels.Half(image);
        Assert.Equal((2, 1), (half.Width, half.Height));
        using var pixels = SKBitmap.FromImage(half);
        Assert.Equal(new SKColor(128, 0, 64), pixels.GetPixel(0, 0)); // (510 + 2) / 4, (255 + 2) / 4
        Assert.Equal(new SKColor(11, 21, 31), pixels.GetPixel(1, 0));
    }

    [Fact]
    public void An_input_is_drawn_from_the_smallest_level_still_large_enough_and_the_levels_are_made_once()
    {
        using var bitmap = new SKBitmap(1280, 720);
        using var image = SKImage.FromBitmap(bitmap);
        using var levels = new PictureLevels(image);
        var whole = new SKRectI(0, 0, 1280, 720);

        Assert.Same(image, levels.For(whole, 1).Image); // no smaller
        Assert.Same(image, levels.For(whole, 0.6).Image); // a half would be scaled up
        var (half, region) = levels.For(whole, 0.5);
        Assert.Equal((640, 360), (half.Width, half.Height));
        Assert.Equal(SKRect.Create(0, 0, 640, 360), region);
        Assert.Equal(320, levels.For(whole, 0.25).Image.Width);
        Assert.Same(half, levels.For(whole, 0.5).Image); // made once
        Assert.Equal(SKRect.Create(50, 25, 60, 30), levels.For(new SKRectI(100, 50, 220, 110), 0.5).Region);
    }

    [Theory]
    [InlineData(640, 640, ModelFit.Letterbox, 1280, 720, 0.5f)] // 640 x 360 is where the picture lands
    [InlineData(640, 640, ModelFit.Letterbox, 1920, 1080, 0.5f)] // 960 x 540: a quarter (480) would be too small
    [InlineData(320, 320, ModelFit.Letterbox, 1920, 1080, 0.25f)] // 480 x 270 for 320 x 180 (an eighth, 240, is too small)
    [InlineData(320, 320, ModelFit.Letterbox, 2560, 1440, 0.125f)] // 320 x 180 is an eighth
    [InlineData(576, 576, ModelFit.Stretch, 1280, 720, 1f)] // 576 rows of 720: a half (360) would be scaled up
    [InlineData(1024, 1024, ModelFit.Letterbox, 800, 600, 1f)] // a model larger than the picture
    public void The_scale_keeps_every_input_as_fine_as_the_whole_picture(int inputWidth, int inputHeight, ModelFit fit, int width, int height, float scale)
    {
        var need = new PictureNeed([new ModelInput(inputWidth, inputHeight, fit)]);
        Assert.Equal(scale, need.ScaleFor(width, height, s => ((int)Math.Ceiling(width * s), (int)Math.Ceiling(height * s))));

        // Several models: the largest need decides; none (no model): the smallest.
        var two = new PictureNeed([new ModelInput(320, 320, ModelFit.Letterbox), new ModelInput(inputWidth, inputHeight, fit)]);
        Assert.Equal(scale, two.ScaleFor(width, height, s => ((int)Math.Ceiling(width * s), (int)Math.Ceiling(height * s))));
        Assert.Equal(0.125f, new PictureNeed([]).ScaleFor(width, height, s => ((int)Math.Ceiling(width * s), (int)Math.Ceiling(height * s))));
    }

    [Fact]
    public void A_frame_decoded_smaller_keeps_the_cameras_size_and_its_box_lands_in_the_same_place()
    {
        var jpeg = Jpeg(1280, 720);
        using var frame = VisionFrame.Decode(jpeg, new PictureNeed([new ModelInput(640, 640, ModelFit.Letterbox)]));
        Assert.Equal((1280, 720), (frame.Width, frame.Height));
        Assert.Equal((640, 360), (frame.Image.Width, frame.Image.Height));
        Assert.Equal(new SKRectI(160, 90, 224, 162), frame.PixelsOf(new RelativeBox(0.25, 0.25, 0.1, 0.2))); // in the decoded half

        using var whole = VisionFrame.Decode(jpeg, null);
        Assert.Equal((1280, 720), (whole.Image.Width, whole.Image.Height));

        // The same input from both: the box is where it was (small differences of the filters only).
        var a = new float[3 * 640 * 640];
        var b = new float[3 * 640 * 640];
        Preprocess.Write(frame.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, a);
        Preprocess.Write(whole.Levels, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, 640, 640, b);
        var inside = (266 * 640) + 192; // the box's middle in the input: x 160 to 224, y 140 + 90 to 140 + 162
        Assert.True(a[inside] > 0.9 && b[inside] > 0.9);
        var differ = a.Zip(b).Count(p => Math.Abs(p.First - p.Second) > 0.1f);
        Assert.True(differ < a.Length / 100, $"{differ} values differ");
    }

    [Fact]
    public async Task A_detect_after_the_camera_starts_early_and_the_frame_takes_that_call()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var filter = b.After(detect, NodeKinds.Filter, show: true);
        b.After(b.After(filter, NodeKinds.Count), NodeKinds.Event);
        var h = new EvaluatorHarness(b.Build());
        h.Inference.Enqueue(Det.Person(0.1, 0.1, score: 0.9));

        using var frame = new FakeFrame();
        var prefetch = Assert.IsAssignableFrom<IFramePrefetch>(h.Evaluator.Prefetch(frame, CancellationToken.None));
        Assert.Equal(1, h.Inference.DetectCalls);
        var result = await h.Evaluator.RunAsync(frame, EvaluatorHarness.T0, prefetch, CancellationToken.None);
        await prefetch.WhenDoneAsync();
        Assert.Equal(1, h.Inference.DetectCalls); // the frame took the early call, no second one
        Assert.Single(result.Nodes[detect].Objects);

        // Without early calls the frame makes its own.
        await h.Evaluator.RunAsync(frame, EvaluatorHarness.T0.AddSeconds(1), CancellationToken.None);
        Assert.Equal(2, h.Inference.DetectCalls);

        // A failed early call fails the frame as a failed call does, and its end is still awaited without a throw.
        h.Inference.FailNext = new InvalidOperationException("broken");
        var failing = h.Evaluator.Prefetch(frame, CancellationToken.None)!;
        await failing.WhenDoneAsync();
        var failed = await h.Evaluator.RunAsync(frame, EvaluatorHarness.T0.AddSeconds(2), failing, CancellationToken.None);
        Assert.Contains("broken", failed.ModelFailure);
    }

    [Fact]
    public void A_pipeline_with_a_classifier_needs_the_whole_picture_one_with_detection_its_inputs()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        b.After(b.After(detect, NodeKinds.Count), NodeKinds.Event);
        var detecting = new EvaluatorHarness(b.Build());
        var need = Assert.IsType<PictureNeed>(detecting.Evaluator.PictureNeed);
        Assert.Single(need.Inputs);

        var c = new PipelineBuilder();
        var cam = c.Add(NodeKinds.Camera);
        var det = c.After(cam, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var classify = c.After(det, NodeKinds.Classify, new JsonObject { [NodeKinds.ModelKey] = PipelineBuilder.Classifier.Id });
        c.After(c.After(classify, NodeKinds.Count), NodeKinds.Event);
        Assert.Null(new EvaluatorHarness(c.Build()).Evaluator.PictureNeed);
    }
}
