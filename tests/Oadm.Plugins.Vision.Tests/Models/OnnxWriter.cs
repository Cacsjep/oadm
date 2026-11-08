using System.Text;

namespace Oadm.Plugins.Vision.Tests.Models;

/// <summary>A minimal protobuf writer: the fields the test models need (varints, strings, bytes, messages).</summary>
internal sealed class Proto
{
    private readonly MemoryStream _bytes = new();

    public Proto Varint(int field, long value)
    {
        Tag(field, 0);
        WriteVarint((ulong)value);
        return this;
    }

    public Proto String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public Proto Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        WriteVarint((ulong)value.Length);
        _bytes.Write(value);
        return this;
    }

    public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());

    public byte[] ToArray() => _bytes.ToArray();

    private void Tag(int field, int wire) => WriteVarint((ulong)((field << 3) | wire));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _bytes.WriteByte((byte)value);
    }
}

/// <summary>
/// Small ONNX models for the tests (docs/spec/vision-center.md "Tests"): a picture input (fixed or free size) whose outputs
/// are Constant tensors of known values, so every output format can be checked and decoded without a real model; plus the
/// pieces the model check refuses (another operator domain, external data).
/// </summary>
internal sealed class OnnxModel
{
    // TensorProto.DataType
    public const int Float = 1;
    public const int Int32 = 6;
    public const int Int64 = 7;
    public const int Float16 = 10;

    private readonly List<Proto> _nodes = [];
    private readonly List<Proto> _inputs = [];
    private readonly List<Proto> _outputs = [];
    private readonly List<Proto> _initializers = [];
    private readonly List<Proto> _functions = [];
    private readonly List<(string Domain, long Version)> _opsets = [("", 17)];
    private readonly List<(string Key, string Value)> _metadata = [];

    /// <summary>A model with the picture input <c>images [1, 3, H, W]</c> of <paramref name="type"/>; null sides are free.</summary>
    public static OnnxModel Picture(int? width = 640, int? height = 640, int type = Float) =>
        new OnnxModel().Input("images", type, Dim(1), Dim(3), height is { } h ? Dim(h) : Param("height"), width is { } w ? Dim(w) : Param("width"));

    public OnnxModel Input(string name, int type, params Proto[] dims)
    {
        _inputs.Add(ValueInfo(name, type, dims));
        return this;
    }

    /// <summary>A float output of fixed values.</summary>
    public OnnxModel Output(string name, long[] dims, float[] values)
    {
        var raw = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        return Constant(name, dims, Float, raw);
    }

    /// <summary>A half precision output of fixed values (a model exported with <c>--half</c>).</summary>
    public OnnxModel OutputHalf(string name, long[] dims, float[] values)
    {
        var raw = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(raw.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)values[i]));
        }

        return Constant(name, dims, Float16, raw);
    }

    /// <summary>An int64 output of fixed values (labels).</summary>
    public OnnxModel Output(string name, long[] dims, long[] values)
    {
        var raw = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        return Constant(name, dims, Int64, raw);
    }

    public OnnxModel Metadata(string key, string value)
    {
        _metadata.Add((key, value));
        return this;
    }

    public OnnxModel Opset(string domain, long version)
    {
        _opsets.Add((domain, version));
        return this;
    }

    /// <summary>A node of another operator domain (its output unused).</summary>
    public OnnxModel NodeOf(string domain, string op = "Custom")
    {
        _nodes.Add(new Proto().String(1, "images").String(2, "custom_" + _nodes.Count).String(4, op).String(7, domain));
        return this;
    }

    /// <summary>An If node whose then-branch holds a node of <paramref name="domain"/> (a subgraph).</summary>
    public OnnxModel SubgraphNodeOf(string domain)
    {
        var inner = new Proto().String(2, "branch").Message(1, new Proto().String(2, "inner_out").String(4, "Custom").String(7, domain));
        var attribute = new Proto().String(1, "then_branch").Varint(20, 5).Message(6, inner);
        _nodes.Add(new Proto().String(1, "cond").String(2, "if_out").String(4, "If").Message(5, attribute));
        return this;
    }

    /// <summary>A model-local function whose body holds a node of <paramref name="domain"/>.</summary>
    public OnnxModel FunctionWithNodeOf(string domain)
    {
        _functions.Add(new Proto().String(1, "MyFunction").String(10, "local.functions")
            .Message(7, new Proto().String(2, "f_out").String(4, "Custom").String(7, domain)));
        return this;
    }

    /// <summary>A weight whose data lies in another file (<c>data_location = EXTERNAL</c>).</summary>
    public OnnxModel ExternalInitializer(bool locationOnly = false)
    {
        var tensor = new Proto().Varint(1, 4).Varint(2, Float).String(8, "weights");
        if (!locationOnly)
        {
            tensor.Message(13, new Proto().String(1, "location").String(2, "weights.bin"));
        }

        _initializers.Add(tensor.Varint(14, 1));
        return this;
    }

    /// <summary>A large weight held in the file (the scan skips its bytes).</summary>
    public OnnxModel LargeInitializer(int bytes)
    {
        _initializers.Add(new Proto().Varint(1, bytes / 4).Varint(2, Float).String(8, "large").Bytes(9, new byte[bytes]));
        return this;
    }

    public byte[] Build()
    {
        // The weights first: the scan must skip them to reach the nodes.
        var graph = new Proto();
        foreach (var initializer in _initializers)
        {
            graph.Message(5, initializer);
        }

        foreach (var node in _nodes)
        {
            graph.Message(1, node);
        }

        graph.String(2, "test");

        foreach (var input in _inputs)
        {
            graph.Message(11, input);
        }

        foreach (var output in _outputs)
        {
            graph.Message(12, output);
        }

        var model = new Proto().Varint(1, 8).String(2, "oadm-tests").Message(7, graph);
        foreach (var (domain, version) in _opsets)
        {
            model.Message(8, new Proto().String(1, domain).Varint(2, version));
        }

        foreach (var (key, value) in _metadata)
        {
            model.Message(14, new Proto().String(1, key).String(2, value));
        }

        foreach (var function in _functions)
        {
            model.Message(25, function);
        }

        return model.ToArray();
    }

    /// <summary>Writes the model to <paramref name="folder"/> and returns its path.</summary>
    public string Save(string folder, string name = "model.onnx")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Build());
        return path;
    }

    public static Proto Dim(long value) => new Proto().Varint(1, value);

    public static Proto Param(string name) => new Proto().String(2, name);

    private OnnxModel Constant(string name, long[] dims, int type, byte[] raw)
    {
        var tensor = new Proto();
        foreach (var d in dims)
        {
            tensor.Varint(1, d);
        }

        tensor.Varint(2, type).String(8, name + "_value").Bytes(9, raw);
        var attribute = new Proto().String(1, "value").Varint(20, 4).Message(5, tensor);
        _nodes.Add(new Proto().String(2, name).String(3, name + "_node").String(4, "Constant").Message(5, attribute));
        _outputs.Add(ValueInfo(name, type, [.. dims.Select(Dim)]));
        return this;
    }

    private static Proto ValueInfo(string name, int type, Proto[] dims)
    {
        var shape = new Proto();
        foreach (var dim in dims)
        {
            shape.Message(1, dim);
        }

        var tensorType = new Proto().Varint(1, type).Message(2, shape);
        return new Proto().String(1, name).Message(2, new Proto().Message(1, tensorType));
    }
}

/// <summary>A temporary folder unique to one test, deleted after it.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oadm-vision-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
