using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

namespace Oadm.Plugins.Vision.Tests.Models;

/// <summary>The model check of an upload (docs/spec/vision-center.md "Models", rule 4, "Tests"): refused domains and external data, the formats, the input, the class names.</summary>
public sealed class ModelCheckTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static float[] Zeros(int count) => new float[count];

    private (CheckedModel? Model, string? Refusal) Check(OnnxModel model, string? labels = null) =>
        ModelChecker.Check(model.Save(_folder.Path, Guid.NewGuid().ToString("N") + ".onnx"), labels, CancellationToken.None);

    private static string? Scan(OnnxModel model) => OnnxScan.Refusal(new MemoryStream(model.Build()));

    [Fact]
    public void A_model_of_standard_operators_passes_the_scan()
    {
        var model = OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350)).Opset("ai.onnx.ml", 3).Opset("com.microsoft", 1).NodeOf("com.microsoft", "FusedConv").NodeOf("ai.onnx");
        Assert.Null(Scan(model));
    }

    [Theory]
    [InlineData("opset")]
    [InlineData("node")]
    [InlineData("subgraph")]
    [InlineData("function")]
    public void Another_operator_domain_is_refused_wherever_it_is(string where)
    {
        var model = OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350));
        model = where switch
        {
            "opset" => model.Opset("com.example.custom", 1),
            "node" => model.NodeOf("com.example.custom"),
            "subgraph" => model.SubgraphNodeOf("com.example.custom"),
            _ => model.FunctionWithNodeOf("com.example.custom"),
        };

        Assert.Equal(ModelTexts.OtherOperators("com.example.custom"), Scan(model));
    }

    [Fact]
    public void A_refused_domain_never_reaches_onnx_runtime()
    {
        // A custom operator ONNX Runtime does not know would fail at its load: the scan refuses it with its own reason first.
        var (model, refusal) = Check(OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350)).Opset("com.example.custom", 1).NodeOf("com.example.custom"));
        Assert.Null(model);
        Assert.Equal("This model cannot run here: it uses operators of \"com.example.custom\", which OADM does not run.", refusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void External_data_is_refused(bool locationOnly)
    {
        var model = OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350)).ExternalInitializer(locationOnly);
        Assert.Equal(ModelTexts.ExternalData, Scan(model));
        Assert.Equal(ModelTexts.CannotRun(ModelTexts.ExternalData), Check(model).Refusal);
    }

    [Fact]
    public void A_file_that_is_no_model_is_refused()
    {
        Assert.Equal(ModelTexts.NotOnnx, OnnxScan.Refusal(new MemoryStream("This is no model."u8.ToArray())));
        Assert.Equal(ModelTexts.NotOnnx, OnnxScan.Refusal(new MemoryStream([])));
        var cut = OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350)).Build();
        Assert.Equal(ModelTexts.NotOnnx, OnnxScan.Refusal(new MemoryStream(cut[..(cut.Length / 2)])));
    }

    [Fact]
    public void The_scan_skips_large_weights_without_reading_them()
    {
        var model = OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350)).LargeInitializer(8 * 1024 * 1024).NodeOf("com.example.custom");
        var counting = new CountingStream(model.Build());
        Assert.Equal(ModelTexts.OtherOperators("com.example.custom"), OnnxScan.Refusal(counting));
        Assert.True(counting.BytesRead < 1024 * 1024, $"read {counting.BytesRead} bytes of {counting.Length}");
    }

    [Fact]
    public void Yolo_v8_is_recognised_with_its_classes_and_letterbox()
    {
        var (model, refusal) = Check(OnnxModel.Picture(640, 640).Output("output0", [1, 4 + 3, 50], Zeros(7 * 50)));
        Assert.Null(refusal);
        Assert.Equal(ModelFormat.Yolo, model!.Format);
        Assert.Equal(["Class 0", "Class 1", "Class 2"], model.ClassNames);
        Assert.Equal((640, 640, true), (model.InputWidth, model.InputHeight, model.FixedInput));
        Assert.Equal(ModelFit.Letterbox, OutputFormats.DefaultFit(model.Format));
        Assert.Equal(ModelScaling.ZeroToOne, OutputFormats.DefaultScaling(model.Format));
        Assert.True(model.CpuMs >= 0);
    }

    [Fact]
    public void Yolo_v5_with_objectness_is_recognised()
    {
        var (model, _) = Check(OnnxModel.Picture().Output("output", [1, 50, 5 + 2], Zeros(50 * 7)));
        Assert.Equal(ModelFormat.Yolo, model!.Format);
        Assert.Equal(2, model.ClassNames.Count);
    }

    [Fact]
    public void Yolo_with_suppression_is_recognised()
    {
        var (model, _) = Check(OnnxModel.Picture().Output("output0", [1, 300, 6], Zeros(1800)), "person\ncar\n");
        Assert.Equal(ModelFormat.YoloWithSuppression, model!.Format);
        Assert.Equal(["person", "car"], model.ClassNames);
    }

    [Fact]
    public void Detr_is_recognised_by_its_output_names()
    {
        var (model, _) = Check(OnnxModel.Picture(800, 800).Output("pred_logits", [1, 10, 6], Zeros(60)).Output("pred_boxes", [1, 10, 4], Zeros(40)));
        Assert.Equal(ModelFormat.Detr, model!.Format);
        Assert.Equal(5, model.ClassNames.Count); // the last of C + 1 is "no object"
        Assert.Equal(ModelFit.KeepShapePad, OutputFormats.DefaultFit(model.Format));
        Assert.Equal(ModelScaling.ImageNet, OutputFormats.DefaultScaling(model.Format));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rf_detr_is_recognised_with_two_or_three_outputs(bool masks)
    {
        var model = OnnxModel.Picture(560, 560).Output("dets", [1, 10, 4], Zeros(40)).Output("labels", [1, 10, 5], Zeros(50));
        if (masks)
        {
            model.Output("masks", [1, 10, 8, 8], Zeros(640));
        }

        var (found, _) = Check(model);
        Assert.Equal(ModelFormat.RfDetr, found!.Format);
        Assert.Equal(5, found.ClassNames.Count);
        Assert.Equal(ModelFit.Stretch, OutputFormats.DefaultFit(found.Format));
        Assert.Equal(ModelScaling.ImageNet, OutputFormats.DefaultScaling(found.Format));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rf_detr_is_recognised_by_its_boxes_first_also_with_the_pred_names(bool masks)
    {
        // rf-detr-onnx names its outputs like DETR, in the other order: boxes first is RF-DETR.
        var model = OnnxModel.Picture(576, 576).Output("pred_boxes", [1, 10, 4], Zeros(40)).Output("pred_logits", [1, 10, 91], Zeros(910));
        if (masks)
        {
            model.Output("pred_masks", [1, 10, 8, 8], Zeros(640));
        }

        var (found, _) = Check(model);
        Assert.Equal(ModelFormat.RfDetr, found!.Format);
        Assert.Equal(91, found.ClassNames.Count); // a sigmoid per column, no "no object" column
    }

    [Fact]
    public void Detr_with_labels_is_recognised_with_its_size_input()
    {
        var model = OnnxModel.Picture(640, 640).Input("orig_target_sizes", OnnxModel.Int64, OnnxModel.Dim(1), OnnxModel.Dim(2))
            .Output("labels", [1, 10], new long[10]).Output("boxes", [1, 10, 4], Zeros(40)).Output("scores", [1, 10], Zeros(10));
        var (found, refusal) = Check(model, "a\nb\nc");
        Assert.Null(refusal);
        Assert.Equal(ModelFormat.DetrWithLabels, found!.Format);
        Assert.Equal(["a", "b", "c"], found.ClassNames); // the outputs do not tell the count: the labels file does
    }

    [Fact]
    public void Classification_is_recognised()
    {
        var (model, _) = Check(OnnxModel.Picture(224, 224).Output("logits", [1, 4], Zeros(4)));
        Assert.Equal(ModelFormat.Classification, model!.Format);
        Assert.Equal(ModelKind.Classification, OutputFormats.KindOf(model.Format));
        Assert.Equal(4, model.ClassNames.Count);
    }

    [Fact]
    public void A_free_input_takes_the_default_size()
    {
        var (model, _) = Check(OnnxModel.Picture(null, null).Output("output0", [1, 7, 50], Zeros(350)));
        Assert.Equal((VisionLimits.DefaultInputSize, VisionLimits.DefaultInputSize, false), (model!.InputWidth, model.InputHeight, model.FixedInput));
    }

    [Fact]
    public void Outputs_of_no_known_format_are_refused()
    {
        Assert.Equal(ModelTexts.CannotRun(ModelTexts.UnknownOutputs), Check(OnnxModel.Picture().Output("x", [1, 2, 3, 4], Zeros(24))).Refusal);
    }

    [Fact]
    public void A_model_without_a_picture_input_is_refused()
    {
        var model = new OnnxModel().Input("values", OnnxModel.Float, OnnxModel.Dim(1), OnnxModel.Dim(10)).Output("logits", [1, 4], Zeros(4));
        Assert.Equal(ModelTexts.CannotRun(ModelTexts.NoPictureInput), Check(model).Refusal);
    }

    [Fact]
    public void Too_many_classes_are_refused()
    {
        Assert.Equal(ModelTexts.CannotRun(ModelTexts.TooManyClasses(1001)), Check(OnnxModel.Picture().Output("logits", [1, 1001], Zeros(1001))).Refusal);
    }

    [Fact]
    public void Class_names_come_from_the_ultralytics_metadata()
    {
        var model = OnnxModel.Picture().Output("output0", [1, 4 + 3, 50], Zeros(350)).Metadata("names", "{0: 'person', 1: 'bicycle', 2: \"it's a car\"}");
        Assert.Equal(["person", "bicycle", "it's a car"], Check(model, "x\ny\nz").Model!.ClassNames);
    }

    [Fact]
    public void A_labels_file_must_name_every_class()
    {
        var (model, refusal) = Check(OnnxModel.Picture().Output("output0", [1, 4 + 3, 50], Zeros(350)), "person\ncar");
        Assert.Null(model);
        Assert.Equal("This model cannot run here: the labels file has 2 names, the model has 3 classes.", refusal);
    }

    [Fact]
    public void Gap_names_stay_as_gaps()
    {
        var (model, _) = Check(OnnxModel.Picture().Output("pred_logits", [1, 10, 4], Zeros(40)).Output("pred_boxes", [1, 10, 4], Zeros(40)), "N/A\nperson\nN/A\n");
        Assert.Equal(["N/A", "person", "N/A"], model!.ClassNames);
    }

    [Theory]
    [InlineData("{0: 'person', 1: 'bicycle'}", new[] { "person", "bicycle" })]
    [InlineData("{0: 'a', 2: 'c'}", new[] { "a", "Class 1", "c" })]
    [InlineData("{\"0\": \"a\", \"1\": \"b\"}", new[] { "a", "b" })]
    [InlineData("[\"a\", \"b\"]", new[] { "a", "b" })]
    [InlineData("{0: 'it\\'s', 1: 'b',}", new[] { "it's", "b" })]
    public void Metadata_names_are_read(string value, string[] names) => Assert.Equal(names, ClassNames.FromMetadata(value));

    [Theory]
    [InlineData("")]
    [InlineData("person")]
    [InlineData("{0: person}")]
    [InlineData("{5000: 'a'}")]
    public void Unreadable_metadata_names_are_none(string value) => Assert.Null(ClassNames.FromMetadata(value));

    [Fact]
    public void Labels_files_are_read_line_by_line()
    {
        Assert.Equal(["person", "Class 1", "car"], ClassNames.FromLabels("person\r\n\r\ncar\r\n\r\n"));
        Assert.Null(ClassNames.FromLabels(string.Join('\n', Enumerable.Range(0, 1001).Select(i => "c" + i))));
    }

    /// <summary>A stream that counts the bytes read from it.</summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = base.Read(buffer);
            BytesRead += n;
            return n;
        }
    }
}
