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

    /// <summary>Logits of <paramref name="queries"/> rows of <paramref name="columns"/>; <paramref name="lastWins"/>: the last column the best of every row.</summary>
    private static float[] HfLogits(int queries, int columns, bool lastWins)
    {
        var values = new float[queries * columns];
        for (var q = 0; q < queries; q++)
        {
            values[(q * columns) + (lastWins ? columns - 1 : q % (columns - 1))] = 5;
        }

        return values;
    }

    [Fact]
    public void A_hugging_face_detr_export_with_no_object_winning_on_the_blank_picture_is_detr()
    {
        // Transformers.js DETR: logits [1, Q, C + 1] and pred_boxes; on a blank picture "no object" is every query's best class.
        var (model, refusal) = Check(OnnxModel.Picture(null, null).Output("logits", [1, 10, 6], HfLogits(10, 6, lastWins: true)).Output("pred_boxes", [1, 10, 4], Zeros(40)));
        Assert.Null(refusal);
        Assert.Equal(ModelFormat.Detr, model!.Format);
        Assert.Equal(5, model.ClassNames.Count);
    }

    [Fact]
    public void A_hugging_face_export_without_no_object_is_rt_detr_with_every_column_a_class()
    {
        // RT-DETR, D-FINE, Transformers.js YOLO26: the same names, a sigmoid per class; the last class is no query's favourite.
        var (model, refusal) = Check(OnnxModel.Picture(null, null).Output("logits", [1, 10, 6], HfLogits(10, 6, lastWins: false)).Output("pred_boxes", [1, 10, 4], Zeros(40)));
        Assert.Null(refusal);
        Assert.Equal(ModelFormat.RtDetr, model!.Format);
        Assert.Equal(6, model.ClassNames.Count);
        Assert.Equal(ModelFit.Stretch, OutputFormats.DefaultFit(model.Format));
        Assert.Equal(ModelScaling.ZeroToOne, OutputFormats.DefaultScaling(model.Format));
    }

    [Fact]
    public void The_original_detr_names_stay_detr_whatever_the_blank_picture_gives()
    {
        var (model, _) = Check(OnnxModel.Picture(800, 800).Output("pred_logits", [1, 10, 6], HfLogits(10, 6, lastWins: false)).Output("pred_boxes", [1, 10, 4], Zeros(40)));
        Assert.Equal(ModelFormat.Detr, model!.Format);
    }

    [Theory]
    [InlineData(new[] { 0f, 1, 9, 2, 9, 0 }, 0.5)]
    [InlineData(new[] { 9f, 1, 2, 2, 3, 0 }, 0.0)]
    public void The_last_class_share_counts_the_rows_whose_best_column_is_the_last(float[] values, double share) =>
        Assert.Equal(share, ModelChecker.LastClassShare([1, 2, 3], values));

    [Fact]
    public void The_pixel_mask_of_a_hugging_face_detr_export_is_filled_not_refused()
    {
        // Xenova/detr-resnet-50: pixel_values [-1, -1, -1, -1] and pixel_mask [-1, 64, 64] int64.
        var onnx = new OnnxModel()
            .Input("pixel_values", OnnxModel.Float, OnnxModel.Param("batch"), OnnxModel.Param("channels"), OnnxModel.Param("height"), OnnxModel.Param("width"))
            .Input("pixel_mask", OnnxModel.Int64, OnnxModel.Param("batch"), OnnxModel.Dim(64), OnnxModel.Dim(64))
            .Output("logits", [1, 10, 6], HfLogits(10, 6, lastWins: true)).Output("pred_boxes", [1, 10, 4], Zeros(40));
        var (model, refusal) = Check(onnx);
        Assert.Null(refusal);
        Assert.Equal(ModelFormat.Detr, model!.Format);
        Assert.False(model.FixedInput);
    }

    [Fact]
    public void A_classification_model_with_a_free_input_gets_224()
    {
        var (model, _) = Check(OnnxModel.Picture(null, null).Output("logits", [1, 4], Zeros(4)));
        Assert.Equal(ModelFormat.Classification, model!.Format);
        Assert.False(model.FixedInput);
        Assert.Equal((224, 224), (model.InputWidth, model.InputHeight));
    }

    [Fact]
    public void A_classification_model_takes_imagenet_scaling_unless_ultralytics_exported_it()
    {
        Assert.Equal(ModelScaling.ImageNet, Check(OnnxModel.Picture(224, 224).Output("logits", [1, 2], Zeros(2))).Model!.Scaling);
        Assert.Equal(ModelScaling.ZeroToOne, Check(OnnxModel.Picture(224, 224).Output("output0", [1, 2], Zeros(2)).Metadata("names", "{0: 'cat', 1: 'dog'}")).Model!.Scaling);
        Assert.Equal(ModelScaling.ZeroToOne, Check(OnnxModel.Picture(224, 224).Output("output0", [1, 2], Zeros(2)).Metadata("author", "Ultralytics")).Model!.Scaling);
        Assert.Null(Check(OnnxModel.Picture().Output("output0", [1, 7, 50], Zeros(350))).Model!.Scaling); // detection: the format's default
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
    public void A_half_precision_model_is_taken_like_one_of_floats()
    {
        // YOLOv5's v7.0 release and Unity's exports: input and outputs in 16-bit floats.
        var (model, refusal) = Check(OnnxModel.Picture(type: OnnxModel.Float16).OutputHalf("output0", [1, 4 + 3, 50], Zeros(7 * 50)));
        Assert.Null(refusal);
        Assert.Equal((ModelFormat.Yolo, 3, 640, true), (model!.Format, model.ClassNames.Count, model.InputWidth, model.FixedInput));
    }

    [Fact]
    public void Yolox_is_recognised_by_one_row_per_grid_cell_and_reads_bgr_0_to_255()
    {
        // 96 x 96: 12 x 12 + 6 x 6 + 3 x 3 = 189 cells of the strides 8, 16, 32 (YOLOv5 has three anchors per cell).
        var (model, refusal) = Check(OnnxModel.Picture(96, 96).Output("output", [1, 189, 5 + 80], Zeros(189 * 85)));
        Assert.Null(refusal);
        Assert.Equal((ModelFormat.Yolox, 80), (model!.Format, model.ClassNames.Count));
        Assert.Equal(ModelFit.Letterbox, OutputFormats.DefaultFit(model.Format));
        Assert.Equal(ModelScaling.ZeroTo255Bgr, OutputFormats.DefaultScaling(model.Format));
    }

    [Fact]
    public void Yolox_exported_with_its_decoding_inside_is_read_as_yolo()
    {
        // Centres in input pixels: the cells of the right half beyond half the input.
        var values = new float[84 * 7];
        for (var i = 0; i < 84; i++)
        {
            values[i * 7] = i % 8 * 8 + 4;
        }

        var (model, _) = Check(OnnxModel.Picture(64, 64).Output("output", [1, 84, 7], values));
        Assert.Equal(ModelFormat.Yolo, model!.Format);
    }

    [Fact]
    public void Metadata_names_that_are_only_the_ids_name_nothing()
    {
        // onnx-community's YOLOv10: {0: '0', 1: '1', ...}; the count still holds, so 80 classes take the COCO list.
        var ids = "{" + string.Join(", ", Enumerable.Range(0, 80).Select(i => $"{i}: '{i}'")) + "}";
        var (model, _) = Check(OnnxModel.Picture().Output("output0", [1, 300, 6], Zeros(1800)).Metadata("names", ids));
        Assert.Equal((ModelFormat.YoloWithSuppression, NamesSource.None), (model!.Format, model.NamesFrom));
        Assert.Equal(ClassNames.Defaults(80), model.ClassNames);
        Assert.Equal(ClassLists.Coco80, ClassLists.ForClassCount(model.ClassNames.Count));
        Assert.Equal(["a", "b"], ModelChecker.NamesOf(null, "{0: '0', 1: '1'}", "a\nb").Names);
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
