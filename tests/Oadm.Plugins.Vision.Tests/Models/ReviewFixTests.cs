using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Tests.Models;

/// <summary>
/// The model check and the upload against crafted files and failing disks: the scan reads the wire format as ONNX Runtime's
/// protobuf does or refuses, a function's default attributes are scanned, a mask never takes gigabytes, a failed write ends
/// its upload, the limits hold for concurrent uploads, a record's key never becomes a path.
/// </summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly MemoryPluginRecords _records = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _data.Dispose();

    private static byte[] Base() => OnnxModel.Picture().Output("output0", [1, 7, 50], new float[350]).Build();

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }

    /// <summary>A graph whose only node is of another operator domain.</summary>
    private static byte[] CustomGraph() =>
        new Proto().Message(1, new Proto().String(2, "x").String(4, "Custom").String(7, "com.example.custom")).ToArray();

    private static string? Scan(byte[] model) => OnnxScan.Refusal(new MemoryStream(model));

    [Fact]
    public void A_tag_longer_than_32_bits_is_refused()
    {
        // Field 7 (graph) with bit 32 set: ONNX Runtime's protobuf drops the high bits and would merge it as the graph.
        var graph = CustomGraph();
        byte[] model = [.. Base(), .. Varint(((ulong)1 << 32) | (7 << 3) | 2), .. Varint((ulong)graph.Length), .. graph];
        Assert.Equal(ModelTexts.NotOnnx, Scan(model));

        // The same graph under its real tag is refused for its domain: the scan reads it.
        byte[] plain = [.. Base(), .. Varint((7 << 3) | 2), .. Varint((ulong)graph.Length), .. graph];
        Assert.Equal(ModelTexts.OtherOperators("com.example.custom"), Scan(plain));
    }

    [Fact]
    public void A_tag_written_with_extra_zero_bytes_reads_as_its_field()
    {
        // Non-minimal but within 32 bits: protobuf reads field 7, so the scan does too.
        var graph = CustomGraph();
        byte[] tag = [(7 << 3) | 2 | 0x80, 0x80, 0x80, 0x00];
        byte[] model = [.. Base(), .. tag, .. Varint((ulong)graph.Length), .. graph];
        Assert.Equal(ModelTexts.OtherOperators("com.example.custom"), Scan(model));
    }

    [Fact]
    public void A_number_of_more_than_64_bits_is_refused()
    {
        // data_location of an initializer as a ten byte varint whose last byte holds more than the 64th bit.
        byte[] location = [(14 << 3) | 0, 0x81, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        byte[] tensor = [.. new Proto().Varint(2, 1).String(8, "w").ToArray(), .. location];
        var graph = new Proto().Bytes(5, tensor).ToArray();
        byte[] model = [.. Base(), .. Varint((7 << 3) | 2), .. Varint((ulong)graph.Length), .. graph];
        Assert.Equal(ModelTexts.NotOnnx, Scan(model));
    }

    [Fact]
    public void A_message_that_runs_past_its_parent_is_refused()
    {
        // A node claims more bytes than its graph holds; the bytes after the graph are a node of another domain.
        var node = new Proto().String(2, "x").String(4, "Custom").String(7, "com.example.custom").ToArray();
        byte[] graph = [.. Varint((1 << 3) | 2), .. Varint((ulong)node.Length)];
        byte[] model = [.. Base(), .. Varint((7 << 3) | 2), .. Varint((ulong)graph.Length), .. graph, .. node];
        Assert.Equal(ModelTexts.NotOnnx, Scan(model));
    }

    [Fact]
    public void Groups_are_refused()
    {
        byte[] model = [.. Base(), .. Varint((30 << 3) | 3), .. Varint((30 << 3) | 4)];
        Assert.Equal(ModelTexts.NotOnnx, Scan(model));
    }

    [Fact]
    public void A_function_s_default_attributes_are_scanned()
    {
        var external = new Proto().String(1, "w").Varint(20, 4).Message(5, new Proto().Varint(2, 1).String(8, "w").Varint(14, 1));
        var withTensor = new Proto().String(1, "F").String(10, "local").Message(11, external);
        Assert.Equal(ModelTexts.ExternalData, Scan([.. Base(), .. new Proto().Message(25, withTensor).ToArray()]));

        var graph = new Proto().String(1, "body").Varint(20, 5).Bytes(6, CustomGraph());
        var withGraph = new Proto().String(1, "F").String(10, "local").Message(11, graph);
        Assert.Equal(ModelTexts.OtherOperators("com.example.custom"), Scan([.. Base(), .. new Proto().Message(25, withGraph).ToArray()]));
    }

    [Fact]
    public void A_huge_mask_input_is_refused_without_its_memory()
    {
        var model = OnnxModel.Picture().Input("pixel_mask", OnnxModel.Int64, OnnxModel.Dim(1), OnnxModel.Dim(40000), OnnxModel.Dim(40000))
            .Output("pred_logits", [1, 10, 6], new float[60]).Output("pred_boxes", [1, 10, 4], new float[40]);
        var (found, refusal) = ModelChecker.Check(model.Save(_data.Path), null, CancellationToken.None);
        Assert.Null(found);
        Assert.StartsWith("This model cannot run here: ", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Whatever_the_check_throws_becomes_a_refusal()
    {
        var (found, refusal) = ModelChecker.Check(Path.Combine(_data.Path, "not-there.onnx"), null, CancellationToken.None);
        Assert.Null(found);
        Assert.StartsWith("This model cannot run here: it does not load", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_write_ends_the_upload()
    {
        using var library = new ModelLibrary(_records, _data.Path, _time) { WriteHook = (_, _, _) => throw new IOException("disk full") };
        var file = Base();
        var id = await library.BeginUploadAsync("m.onnx", file.Length, null, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => library.AppendAsync(id, 0, file, CancellationToken.None));
        Assert.Equal(ModelTexts.UploadNotWritten, error.Message);
        Assert.Equal(0, library.Uploads);
        Assert.Empty(Directory.GetFiles(Path.Combine(_data.Path, ModelLibrary.FolderName)));
    }

    [Fact]
    public async Task A_failed_flush_ends_the_upload()
    {
        using var library = new ModelLibrary(_records, _data.Path, _time) { FlushHook = (_, _) => throw new IOException("disk full") };
        var file = Base();
        var id = await library.BeginUploadAsync("m.onnx", file.Length, null, CancellationToken.None);
        await library.AppendAsync(id, 0, file, CancellationToken.None);
        Assert.Equal(ModelTexts.UploadNotWritten, (await library.FinishUploadAsync(id, CancellationToken.None)).Refusal);
        Assert.Equal(0, library.Uploads);
        Assert.Empty(Directory.GetFiles(Path.Combine(_data.Path, ModelLibrary.FolderName)));
        Assert.Empty(library.All);
    }

    [Fact]
    public async Task An_upload_being_checked_keeps_its_place_in_the_limits()
    {
        var gate = new ManualResetEventSlim();
        using var library = new ModelLibrary(_records, _data.Path, _time, null, (path, labels, ct) =>
        {
            gate.Wait(ct);
            return ModelChecker.Check(path, labels, ct);
        });
        var file = Base();
        var first = await library.BeginUploadAsync("first.onnx", file.Length, null, CancellationToken.None);
        await library.AppendAsync(first, 0, file, CancellationToken.None);
        var checking = library.FinishUploadAsync(first, CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => library.Uploads == 0, "the first upload is being checked");

        // 4 GB less the first file, in 500 MB uploads and the rest: one byte more is refused.
        for (var i = 0; i < 8; i++)
        {
            await library.BeginUploadAsync("big.onnx", VisionLimits.MaxModelBytes, null, CancellationToken.None);
        }

        var rest = VisionLimits.MaxAllModelsBytes - (8 * VisionLimits.MaxModelBytes) - file.Length;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => library.BeginUploadAsync("rest.onnx", rest + 1, null, CancellationToken.None));
        Assert.Equal(ModelTexts.TooMuchTogether, error.Message);

        // The count: the one being checked, the 8 above and 11 more make 20.
        for (var i = 0; i < VisionLimits.MaxModels - 9; i++)
        {
            await library.BeginUploadAsync("small.onnx", 1, null, CancellationToken.None);
        }

        Assert.Equal(ModelTexts.TooMany, (await Assert.ThrowsAsync<ArgumentException>(() => library.BeginUploadAsync("one more.onnx", 1, null, CancellationToken.None))).Message);

        gate.Set();
        Assert.NotNull((await checking.WaitAsync(TimeSpan.FromSeconds(30))).Model);
        Assert.Equal(ModelTexts.TooMany, (await Assert.ThrowsAsync<ArgumentException>(() => library.BeginUploadAsync("one more.onnx", 1, null, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_record_key_that_is_no_model_id_is_left_out()
    {
        var model = new VisionModel { Name = "evil", Kind = ModelKind.Detection, Format = ModelFormat.Yolo, InputWidth = 640, InputHeight = 640, SizeBytes = 3 };
        await _records.SetManyAsync(ModelLibrary.Collection, [new("..\\..\\outside", Oadm.Plugins.Vision.Api.VisionJson.Serialize(model)), new("0123456789ABCDEF0123456789ABCDEF", Oadm.Plugins.Vision.Api.VisionJson.Serialize(model))], CancellationToken.None);
        using var library = new ModelLibrary(_records, _data.Path, _time);
        await library.LoadAsync(CancellationToken.None);
        Assert.Empty(library.All);
        Assert.True(ModelLibrary.IsModelId("0123456789abcdef0123456789abcdef"));
    }
}
