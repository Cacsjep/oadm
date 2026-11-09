using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.OnnxRuntime;

using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Tests.Models;
using Oadm.Sdk.Testing;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Tests.Inference;

/// <summary>
/// The inference end to end on the CPU (docs/spec/vision-center.md "Models", "Load", "Tests"): generated models whose outputs
/// are known values, decoded per format, the fit undone, suppression, scores, the workers, the shared sessions, the card's
/// rules with the CPU standing in for a card (no graphics card is ever touched).
/// </summary>
public sealed class InferenceTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly Dictionary<string, string> _files = [];
    private readonly FakeTimeProvider _time = new();
    private OnnxInference? _inference;

    public void Dispose()
    {
        _inference?.Dispose();
        _folder.Dispose();
    }

    private OnnxInference Inference(int cores = 4, Func<InferenceOptions, InferenceOptions>? change = null)
    {
        var options = new InferenceOptions
        {
            FileOf = id => _files.GetValueOrDefault(id),
            ProcessorCount = cores,
            Time = _time,
            FindGraphicsCard = _ => throw new InvalidOperationException("No card in tests."),
        };
        return _inference = new OnnxInference(change?.Invoke(options) ?? options);
    }

    private VisionModel Model(OnnxModel onnx, ModelFormat format, string[] classes, ModelFit? fit = null, ModelScaling scaling = ModelScaling.ZeroToOne, int width = 640, int height = 640)
    {
        var id = Guid.NewGuid().ToString("N");
        _files[id] = onnx.Save(_folder.Path, id + ".onnx");
        return new VisionModel
        {
            Id = id,
            Name = "Test",
            Kind = OutputFormats.KindOf(format),
            Format = format,
            Fit = fit ?? OutputFormats.DefaultFit(format),
            Scaling = scaling,
            InputWidth = width,
            InputHeight = height,
            FixedInput = true,
            ClassNames = classes,
        };
    }

    /// <summary>A grey picture of <paramref name="width"/> x <paramref name="height"/> as a JPEG.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(90, 90, 90));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static void Near(RelativeBox expected, RelativeBox actual)
    {
        const double Tolerance = 0.002;
        Assert.True(Math.Abs(expected.X - actual.X) < Tolerance && Math.Abs(expected.Y - actual.Y) < Tolerance
            && Math.Abs(expected.Width - actual.Width) < Tolerance && Math.Abs(expected.Height - actual.Height) < Tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>The N of the YOLOv8 outputs: more boxes than values per box, as in every real export (the rest are empty).</summary>
    private const int YoloBoxes = 16;

    /// <summary>YOLOv8 <c>[1, 4 + C, N]</c> of the given boxes (cx, cy, w, h in input pixels) with one class score each.</summary>
    private static float[] YoloV8(int classes, params (float Cx, float Cy, float W, float H, int Class, float Score)[] boxes)
    {
        const int n = YoloBoxes;
        var data = new float[(4 + classes) * n];
        for (var i = 0; i < boxes.Length; i++)
        {
            var b = boxes[i];
            data[i] = b.Cx;
            data[n + i] = b.Cy;
            data[(2 * n) + i] = b.W;
            data[(3 * n) + i] = b.H;
            data[((4 + b.Class) * n) + i] = b.Score;
        }

        return data;
    }

    [Fact]
    public async Task Yolo_boxes_come_back_on_the_whole_picture_through_the_letterbox()
    {
        // 1280 x 640 into 640 x 640: half the size, 160 grey rows above and below.
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], YoloV8(2, (320, 320, 64, 32, 1, 0.9f), (100, 300, 20, 20, 0, 0.2f))), ModelFormat.Yolo, ["person", "car"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(1280, 640));

        var found = await inference.DetectAsync(model, frame, 0.5, CancellationToken.None);

        var car = Assert.Single(found);
        Assert.Equal((1, "car", 0.9), (car.ClassId, car.ClassName, Math.Round(car.Score, 2)));
        Near(new RelativeBox(0.45, 0.45, 0.1, 0.1), car.Box);
    }

    [Fact]
    public async Task Yolo_suppresses_overlapping_boxes_of_one_class_only()
    {
        var model = Model(
            OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], YoloV8(2,
                (100, 100, 50, 50, 0, 0.9f),
                (102, 101, 50, 50, 0, 0.8f), // the same object again: suppressed
                (101, 100, 50, 50, 1, 0.7f), // another class at the same place: kept
                (400, 400, 50, 50, 0, 0.6f))),
            ModelFormat.Yolo,
            ["a", "b"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));

        var found = await inference.DetectAsync(model, frame, 0.25, CancellationToken.None);

        Assert.Equal([0.9, 0.7, 0.6], found.Select(d => Math.Round(d.Score, 2)));
        Assert.Equal(["a", "b", "a"], found.Select(d => d.ClassName));
    }

    [Fact]
    public async Task Yolo_v5_multiplies_the_objectness()
    {
        // [1, N, 5 + C]: cx, cy, w, h, objectness, class scores.
        var data = new float[YoloBoxes * 7];
        new float[] { 320, 320, 64, 64, 0.5f, 0.2f, 0.9f, 100, 100, 10, 10, 0.1f, 1, 0 }.CopyTo(data, 0);
        var model = Model(OnnxModel.Picture().Output("output", [1, YoloBoxes, 7], data), ModelFormat.Yolo, ["a", "b"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));

        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.3, CancellationToken.None));
        Assert.Equal(("b", 0.45), (found.ClassName, Math.Round(found.Score, 2)));
        Near(new RelativeBox(0.45, 0.45, 0.1, 0.1), found.Box);
    }

    [Fact]
    public async Task Half_precision_inputs_and_outputs_are_converted()
    {
        var onnx = OnnxModel.Picture(type: OnnxModel.Float16).OutputHalf("output0", [1, 6, YoloBoxes], YoloV8(2, (320, 320, 64, 32, 1, 0.9f)));
        var model = Model(onnx, ModelFormat.Yolo, ["person", "car"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(1280, 640));

        var car = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Equal(("car", 0.9), (car.ClassName, Math.Round(car.Score, 2)));
        Near(new RelativeBox(0.45, 0.45, 0.1, 0.1), car.Box);
    }

    [Fact]
    public async Task Yolox_boxes_are_decoded_from_their_grid_cell()
    {
        // 64 x 64: rows 0 to 63 stride 8, 64 to 79 stride 16, 80 to 83 stride 32. Row 73 = stride 16, column 1, row 2:
        // offsets 0.5 and log size 0 give a 16 x 16 box at (24, 40); score = objectness x class.
        var data = new float[84 * 7];
        new float[] { 0.5f, 0.5f, 0, 0, 0.9f, 0.1f, 0.8f }.CopyTo(data, 73 * 7);
        new float[] { 0.5f, 0.5f, 0, 0, 0.2f, 0.9f, 0 }.CopyTo(data, 3 * 7); // below the score
        var model = Model(OnnxModel.Picture(64, 64).Output("output", [1, 84, 7], data), ModelFormat.Yolox, ["a", "b"], width: 64, height: 64);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(64, 64));

        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Equal(("b", 0.72), (found.ClassName, Math.Round(found.Score, 2)));
        Near(new RelativeBox(0.25, 0.5, 0.25, 0.25), found.Box);
    }

    [Fact]
    public async Task Yolo_with_suppression_keeps_every_box_and_at_most_300()
    {
        // 400 boxes in a grid, x1 y1 x2 y2 score class.
        var data = new float[400 * 6];
        for (var i = 0; i < 400; i++)
        {
            var (x, y) = (i % 20 * 32f, i / 20 * 32f);
            new[] { x, y, x + 16, y + 16, 0.5f + (i / 1000f), i % 2 }.CopyTo(data, i * 6);
        }

        var model = Model(OnnxModel.Picture().Output("output0", [1, 400, 6], data), ModelFormat.YoloWithSuppression, ["a", "b"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));

        var found = await inference.DetectAsync(model, frame, 0.1, CancellationToken.None);

        Assert.Equal(VisionLimits.MaxObjects, found.Count);
        Assert.Equal(found.OrderByDescending(d => d.Score).Select(d => d.Score), found.Select(d => d.Score));
        Assert.Equal(0.899, Math.Round(found[0].Score, 3));
    }

    [Fact]
    public async Task Detr_takes_the_softmax_and_leaves_no_object_out()
    {
        // 2 classes + "no object". Query 0 a sure class 1, query 1 sure "no object".
        var logits = new float[] { 0, 5, 0, 0, 0, 8 };
        var boxes = new float[] { 0.5f, 0.25f, 0.1f, 0.1f, 0.5f, 0.5f, 0.2f, 0.2f };
        var model = Model(OnnxModel.Picture().Output("pred_logits", [1, 2, 3], logits).Output("pred_boxes", [1, 2, 4], boxes), ModelFormat.Detr, ["a", "b"], scaling: ModelScaling.ImageNet);
        var inference = Inference();

        // 1280 x 640 into 640 x 640 with its shape kept, top left: half the size, the lower half black.
        using var frame = inference.Decode(Jpeg(1280, 640));
        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Equal("b", found.ClassName);
        Assert.Equal(Math.Round(Math.Exp(5) / (Math.Exp(5) + 2), 4), found.Score, 4);
        Near(new RelativeBox(0.45, 0.4, 0.1, 0.2), found.Box);
    }

    [Fact]
    public async Task Rt_detr_takes_a_sigmoid_per_class_and_finds_its_outputs_by_name()
    {
        // logits [1, 2, 3] first (every column a class), pred_boxes second. Query 0 class 2 at sigmoid(3); query 1 unsure.
        var logits = new float[] { -4, -4, 3, -1, -2, -3 };
        var boxes = new float[] { 0.5f, 0.5f, 0.2f, 0.4f, 0.1f, 0.1f, 0.1f, 0.1f };
        var model = Model(OnnxModel.Picture().Output("logits", [1, 2, 3], logits).Output("pred_boxes", [1, 2, 4], boxes), ModelFormat.RtDetr, ["a", "b", "c"]);
        var inference = Inference();

        // Stretched: the box is relative to the whole picture.
        using var frame = inference.Decode(Jpeg(1280, 640));
        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Equal("c", found.ClassName);
        Assert.Equal(Math.Round(1 / (1 + Math.Exp(-3)), 4), found.Score, 4);
        Near(new RelativeBox(0.4, 0.3, 0.2, 0.4), found.Box);
    }

    [Fact]
    public async Task Detr_never_shows_a_gap_class()
    {
        var logits = new float[] { 9, 0, 0 };
        var model = Model(OnnxModel.Picture().Output("pred_logits", [1, 1, 3], logits).Output("pred_boxes", [1, 1, 4], [0.5f, 0.5f, 0.1f, 0.1f]), ModelFormat.Detr, ["N/A", "person"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));
        Assert.Empty(await inference.DetectAsync(model, frame, 0.1, CancellationToken.None));
    }

    [Fact]
    public async Task Rf_detr_takes_a_sigmoid_per_class_and_leaves_the_masks_out()
    {
        var boxes = new float[] { 0.5f, 0.5f, 0.2f, 0.4f, 0.1f, 0.1f, 0.05f, 0.05f };
        var logits = new float[] { -3, 2, -3, -2, -2, -2 }; // query 0: class 1 at sigmoid(2); query 1: nothing sure
        var onnx = OnnxModel.Picture(560, 560).Output("dets", [1, 2, 4], boxes).Output("labels", [1, 2, 3], logits).Output("masks", [1, 2, 4, 4], new float[32]);
        var model = Model(onnx, ModelFormat.RfDetr, ["a", "b", "c"], scaling: ModelScaling.ImageNet, width: 560, height: 560);
        var inference = Inference();

        // Stretched: the box stays where it is relative to the picture whatever its shape.
        using var frame = inference.Decode(Jpeg(1280, 720));
        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Equal("b", found.ClassName);
        Assert.Equal(Math.Round(1 / (1 + Math.Exp(-2)), 4), found.Score, 4);
        Near(new RelativeBox(0.4, 0.3, 0.2, 0.4), found.Box);
    }

    [Fact]
    public async Task Detr_with_labels_reads_its_three_outputs_with_the_size_input()
    {
        var onnx = OnnxModel.Picture().Input("orig_target_sizes", OnnxModel.Int64, OnnxModel.Dim(1), OnnxModel.Dim(2))
            .Output("labels", [1, 2], new long[] { 2, 0 })
            .Output("boxes", [1, 2, 4], [64f, 64, 128, 192, 0, 0, 10, 10])
            .Output("scores", [1, 2], [0.8f, 0.1f]);
        var model = Model(onnx, ModelFormat.DetrWithLabels, ["a", "b", "c"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));

        var found = Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));
        Assert.Equal((2, "c"), (found.ClassId, found.ClassName));
        Near(new RelativeBox(0.1, 0.1, 0.1, 0.2), found.Box);
    }

    [Fact]
    public async Task A_class_without_a_name_is_named_by_its_number()
    {
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], YoloV8(2, (320, 320, 64, 64, 1, 0.9f))), ModelFormat.Yolo, []);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(640, 640));
        Assert.Equal("Class 1", Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None)).ClassName);
    }

    [Theory]
    [InlineData(new float[] { 1, 3, 0 }, 1)]
    [InlineData(new float[] { 0.1f, 0.2f, 0.7f }, 2)]
    public async Task Classification_gives_the_best_class_of_each_region(float[] values, int best)
    {
        var model = Model(OnnxModel.Picture(224, 224).Output("logits", [1, 3], values), ModelFormat.Classification, ["cat", "dog", "bird"], width: 224, height: 224);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(1280, 720));

        var results = await inference.ClassifyAsync(model, frame, [new RelativeBox(0, 0, 0.5, 0.5), new RelativeBox(0.9, 0.9, 0.2, 0.2)], CancellationToken.None);

        Assert.Equal(2, results.Count);
        var probabilities = values.Sum() is > 0.99f and < 1.01f ? values.Select(v => (double)v).ToArray() : Softmax(values);
        Assert.All(results, r => Assert.Equal((best, model.ClassNames[best]), (r.ClassId, r.ClassName)));
        Assert.All(results, r => Assert.Equal(probabilities[best], r.Score, 4));
    }

    private static double[] Softmax(float[] values)
    {
        var e = values.Select(v => Math.Exp(v)).ToArray();
        return [.. e.Select(v => v / e.Sum())];
    }

    [Fact]
    public void A_picture_over_the_limits_is_refused_before_its_pixels()
    {
        var inference = Inference();
        Assert.Throws<InvalidDataException>(() => inference.Decode("no picture"u8.ToArray()));
        using var wide = new SKBitmap(PictureLimits.MaxSide + 1, 1);
        using var image = SKImage.FromBitmap(wide);
        Assert.Throws<InvalidDataException>(() => inference.Decode(image.Encode(SKEncodedImageFormat.Png, 100).ToArray()));
    }

    [Fact]
    public async Task A_missing_model_file_says_to_upload_it_again()
    {
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], new float[6 * YoloBoxes]), ModelFormat.Yolo, ["a", "b"]) with { Name = "Gate people" };
        _files.Remove(model.Id);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(64, 64));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => inference.DetectAsync(model, frame, 0.5, CancellationToken.None));
        Assert.Equal("Import the model Gate people again.", error.Message);
    }

    [Fact]
    public async Task The_callers_share_one_session_per_model_and_size_until_it_idles()
    {
        var model = Model(OnnxModel.Picture(null, null).Output("output0", [1, 6, YoloBoxes], YoloV8(2, (32, 32, 8, 8, 0, 0.9f))), ModelFormat.Yolo, ["a", "b"]);
        var inference = Inference();
        using var frame = inference.Decode(Jpeg(64, 64));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => inference.DetectAsync(model, frame, 0.5, CancellationToken.None)));
        Assert.Equal(1, inference.Sessions);

        await inference.DetectAsync(model with { InputWidth = 320, InputHeight = 320 }, frame, 0.5, CancellationToken.None);
        Assert.Equal(2, inference.Sessions); // a free input: one session per size

        _time.Advance(OnnxInference.IdleTimeout + OnnxInference.IdleTimeout);
        Assert.Equal(0, inference.Sessions);

        await inference.DetectAsync(model, frame, 0.5, CancellationToken.None);
        inference.Forget(model.Id);
        Assert.Equal(0, inference.Sessions);
    }

    [Fact]
    public async Task Model_calls_take_a_bounded_number_of_workers()
    {
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], new float[6 * YoloBoxes]), ModelFormat.Yolo, ["a", "b"]);
        var inference = Inference(cores: 4);
        Assert.Equal(VisionLimits.Workers(4), inference.Capacity);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inference.BeforeCpuCall = () => gate.Task;
        using var frame = inference.Decode(Jpeg(64, 64));
        Assert.True(inference.HasRoom);

        var calls = Enumerable.Range(0, 3).Select(_ => inference.DetectAsync(model, frame, 0.5, CancellationToken.None)).ToList();

        await Eventually.TrueAsync(() => inference.BusyWorkers == 2, "two workers hold calls");
        Assert.False(inference.HasRoom);
        Assert.DoesNotContain(calls, c => c.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(inference.HasRoom);
        Assert.True(inference.BusyTime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Use_gpu_off_never_looks_for_a_card()
    {
        var looked = 0;
        var inference = Inference(change: o => o with { FindGraphicsCard = _ => { looked++; return null; } });
        await inference.SetUseGpuAsync(false);
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], new float[6 * YoloBoxes]), ModelFormat.Yolo, ["a", "b"]);
        using var frame = inference.Decode(Jpeg(64, 64));
        await inference.DetectAsync(model, frame, 0.5, CancellationToken.None);

        Assert.Equal(0, looked);
        Assert.Equal("CPU (4 cores)", inference.RunsOn);
        Assert.False(inference.OnGpu || inference.GpuAvailable || inference.Starting);

        await inference.SetUseGpuAsync(true);
        Assert.Equal(1, looked); // no provider for this platform: the CPU
        Assert.False(inference.GpuAvailable);
    }

    [Fact]
    public async Task A_model_the_card_cannot_run_stays_on_the_cpu()
    {
        var made = new List<ExecutionTarget>();
        var inference = Inference(change: o => o with
        {
            FindGraphicsCard = _ => ExecutionTarget.DirectMl,
            CreateSession = (path, target) =>
            {
                lock (made)
                {
                    made.Add(target);
                }

                // The CPU always; the "card" refuses: no provider is ever loaded in a test.
                return target == ExecutionTarget.Cpu ? new InferenceSession(path) : throw new InvalidOperationException("no card here");
            },
        });
        await inference.SetUseGpuAsync(true);
        Assert.True(inference.GpuAvailable);

        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], YoloV8(2, (320, 320, 64, 64, 0, 0.9f))), ModelFormat.Yolo, ["a", "b"]);
        using var frame = inference.Decode(Jpeg(640, 640));
        Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));

        Assert.Contains(ExecutionTarget.DirectMl, made);
        Assert.False(inference.OnGpu);
        Assert.Null(inference.GpuMsOf(model.Id));
        Assert.Equal("CPU (4 cores)", inference.RunsOn);
    }

    [Fact]
    public async Task A_card_that_fails_during_a_run_sends_the_calls_to_the_cpu()
    {
        var failing = false;
        var inference = Inference(change: o => o with
        {
            FindGraphicsCard = _ => ExecutionTarget.DirectMl,
            // The "card" is a CPU session: the routing is tested, never a provider.
            CreateSession = (path, _) => new InferenceSession(path),
            CardName = _ => "Test card",
            CardWorth = (_, _) => true,
        });
        inference.BeforeCardCall = () =>
        {
            if (failing)
            {
                throw new InvalidOperationException("the driver reset");
            }
        };
        await inference.SetUseGpuAsync(true);
        var model = Model(OnnxModel.Picture().Output("output0", [1, 6, YoloBoxes], YoloV8(2, (320, 320, 64, 64, 0, 0.9f))), ModelFormat.Yolo, ["a", "b"]);
        using var frame = inference.Decode(Jpeg(640, 640));

        Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None));
        Assert.True(inference.OnGpu);
        Assert.Equal("CPU (4 cores) · Test card", inference.RunsOn);
        Assert.NotNull(inference.GpuMsOf(model.Id));

        failing = true;
        Assert.Single(await inference.DetectAsync(model, frame, 0.5, CancellationToken.None)); // this call on the CPU
        Assert.False(inference.OnGpu);
        Assert.False(inference.GpuAvailable);
        Assert.Equal("CPU (4 cores)", inference.RunsOn);
    }
}
