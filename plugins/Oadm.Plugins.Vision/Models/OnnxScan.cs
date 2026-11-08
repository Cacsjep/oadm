using System.Text;

namespace Oadm.Plugins.Vision.Models;

/// <summary>
/// The first part of the model check (docs/spec/vision-center.md "Models", rule 4): reads the ONNX file's protobuf itself,
/// before ONNX Runtime ever opens it, and refuses every operator domain other than the standard ones (in the opset imports
/// and in every node: the graph, its subgraphs, the functions and the training graphs) and every tensor whose data lies in
/// another file. Reads the file in a small buffer, skips the weights without reading them: a 500 MB model takes no memory.
/// </summary>
internal static class OnnxScan
{
    /// <summary>The operator domains a model may use: the ONNX operators ("" and "ai.onnx"), ONNX-ML and ONNX Runtime's own.</summary>
    public static readonly IReadOnlySet<string> AllowedDomains = new HashSet<string>(StringComparer.Ordinal) { "", "ai.onnx", "ai.onnx.ml", "com.microsoft" };

    /// <summary>Messages inside messages deeper than this are no model (a crafted file must not overflow the stack).</summary>
    private const int MaxDepth = 64;

    /// <summary>A domain longer than this is no domain any runtime knows.</summary>
    private const int MaxDomainBytes = 256;

    /// <summary>Why the model at <paramref name="path"/> is refused (a lower-case reason for <see cref="ModelTexts.CannotRun"/>), or null when it may be opened.</summary>
    public static string? Refusal(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        return Refusal(file);
    }

    /// <inheritdoc cref="Refusal(string)"/>
    public static string? Refusal(Stream model)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            var reader = new ProtoReader(model);
            var scan = new Scan(reader);
            scan.Model(reader.Length);
            return scan.Problem ?? (scan.HasGraph ? null : ModelTexts.NotOnnx);
        }
        catch (InvalidDataException)
        {
            return ModelTexts.NotOnnx;
        }
        catch (EndOfStreamException)
        {
            return ModelTexts.NotOnnx;
        }
    }

    /// <summary>Walks the messages that can hold operators or tensors; everything else is skipped.</summary>
    private sealed class Scan(ProtoReader reader)
    {
        private int _depth;

        public string? Problem { get; private set; }

        public bool HasGraph { get; private set; }

        // ModelProto: 7 graph, 8 opset_import, 20 training_info, 25 functions.
        public void Model(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                switch (field, wire)
                {
                    case (7, ProtoReader.LengthDelimited):
                        HasGraph = true;
                        Nested(Graph);
                        break;
                    case (8, ProtoReader.LengthDelimited):
                        Nested(OperatorSet);
                        break;
                    case (20, ProtoReader.LengthDelimited):
                        Nested(TrainingInfo);
                        break;
                    case (25, ProtoReader.LengthDelimited):
                        Nested(Function);
                        break;
                    default:
                        reader.Skip(wire, end);
                        break;
                }
            }
        }

        // OperatorSetIdProto: 1 domain.
        private void OperatorSet(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                if (field == 1 && wire == ProtoReader.LengthDelimited)
                {
                    Domain(reader.ReadString(end, MaxDomainBytes));
                }
                else
                {
                    reader.Skip(wire, end);
                }
            }
        }

        // GraphProto: 1 node, 5 initializer, 15 sparse_initializer.
        private void Graph(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                switch (field, wire)
                {
                    case (1, ProtoReader.LengthDelimited):
                        Nested(Node);
                        break;
                    case (5, ProtoReader.LengthDelimited):
                        Nested(Tensor);
                        break;
                    case (15, ProtoReader.LengthDelimited):
                        Nested(SparseTensor);
                        break;
                    default:
                        reader.Skip(wire, end);
                        break;
                }
            }
        }

        // NodeProto: 5 attribute, 7 domain.
        private void Node(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                switch (field, wire)
                {
                    case (5, ProtoReader.LengthDelimited):
                        Nested(Attribute);
                        break;
                    case (7, ProtoReader.LengthDelimited):
                        Domain(reader.ReadString(end, MaxDomainBytes));
                        break;
                    default:
                        reader.Skip(wire, end);
                        break;
                }
            }
        }

        // AttributeProto: 5 t, 6 g, 10 tensors, 11 graphs, 22 sparse_tensor, 23 sparse_tensors.
        private void Attribute(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                switch (field, wire)
                {
                    case (5 or 10, ProtoReader.LengthDelimited):
                        Nested(Tensor);
                        break;
                    case (6 or 11, ProtoReader.LengthDelimited):
                        Nested(Graph);
                        break;
                    case (22 or 23, ProtoReader.LengthDelimited):
                        Nested(SparseTensor);
                        break;
                    default:
                        reader.Skip(wire, end);
                        break;
                }
            }
        }

        // TensorProto: 13 external_data, 14 data_location (1 = EXTERNAL).
        private void Tensor(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                if (field == 13 && wire == ProtoReader.LengthDelimited)
                {
                    Problem = ModelTexts.ExternalData;
                }
                else if (field == 14 && wire == ProtoReader.Varint)
                {
                    if (reader.ReadVarint(end) != 0)
                    {
                        Problem = ModelTexts.ExternalData;
                    }
                }
                else
                {
                    reader.Skip(wire, end);
                }
            }
        }

        // SparseTensorProto: 1 values, 2 indices.
        private void SparseTensor(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                if (field is 1 or 2 && wire == ProtoReader.LengthDelimited)
                {
                    Nested(Tensor);
                }
                else
                {
                    reader.Skip(wire, end);
                }
            }
        }

        // FunctionProto: 7 node, 9 opset_import (its own domain, field 10, only names it; the nodes that call it say theirs).
        private void Function(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                switch (field, wire)
                {
                    case (7, ProtoReader.LengthDelimited):
                        Nested(Node);
                        break;
                    case (9, ProtoReader.LengthDelimited):
                        Nested(OperatorSet);
                        break;
                    default:
                        reader.Skip(wire, end);
                        break;
                }
            }
        }

        // TrainingInfoProto: 1 initialization, 2 algorithm.
        private void TrainingInfo(long end)
        {
            while (Problem is null && reader.TryReadTag(end, out var field, out var wire))
            {
                if (field is 1 or 2 && wire == ProtoReader.LengthDelimited)
                {
                    Nested(Graph);
                }
                else
                {
                    reader.Skip(wire, end);
                }
            }
        }

        private void Domain(string? domain)
        {
            if (domain is null || !AllowedDomains.Contains(domain))
            {
                Problem = ModelTexts.OtherOperators(domain);
            }
        }

        /// <summary>Reads the length of a nested message and walks it with <paramref name="walk"/>, then continues after it.</summary>
        private void Nested(Action<long> walk)
        {
            if (++_depth > MaxDepth)
            {
                throw new InvalidDataException("Nested too deeply.");
            }

            var end = reader.ReadLengthEnd();
            walk(end);
            if (Problem is null)
            {
                reader.SkipTo(end);
            }

            _depth--;
        }
    }
}

/// <summary>A protobuf reader over a seekable stream with its own small buffer: skips fields by seeking.</summary>
internal sealed class ProtoReader
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int Fixed32 = 5;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private long _bufferStart;
    private int _bufferLength;
    private long _position;

    public ProtoReader(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        Length = stream.Length;
        _position = 0;
    }

    /// <summary>The length of the whole message (the file).</summary>
    public long Length { get; }

    /// <summary>The next field's number and wire type, or false at <paramref name="end"/>.</summary>
    public bool TryReadTag(long end, out int field, out int wire)
    {
        if (_position >= end)
        {
            field = 0;
            wire = 0;
            return false;
        }

        var tag = ReadVarint(end);
        field = (int)(tag >> 3);
        wire = (int)(tag & 7);
        if (field <= 0)
        {
            throw new InvalidDataException("Field 0.");
        }

        return true;
    }

    public ulong ReadVarint(long end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (_position >= end)
            {
                throw new InvalidDataException("A number runs past its message.");
            }

            var b = ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("A number is too long.");
    }

    /// <summary>Reads a length and returns where its bytes end (within the file).</summary>
    public long ReadLengthEnd()
    {
        var length = ReadVarint(Length);
        if (length > (ulong)(Length - _position))
        {
            throw new InvalidDataException("A field runs past the file.");
        }

        return _position + (long)length;
    }

    /// <summary>A string field of at most <paramref name="maxBytes"/>; null when it is longer (skipped).</summary>
    public string? ReadString(long end, int maxBytes)
    {
        var stop = ReadLengthEnd();
        if (stop > end)
        {
            throw new InvalidDataException("A field runs past its message.");
        }

        var length = (int)Math.Min(stop - _position, int.MaxValue);
        if (length > maxBytes)
        {
            SkipTo(stop);
            return null;
        }

        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = ReadByte();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int wire, long end)
    {
        switch (wire)
        {
            case Varint:
                ReadVarint(end);
                break;
            case Fixed64:
                SkipTo(_position + 8, end);
                break;
            case Fixed32:
                SkipTo(_position + 4, end);
                break;
            case LengthDelimited:
                var stop = ReadLengthEnd();
                SkipTo(stop, end);
                break;
            default:
                // Groups (3, 4) are not used by ONNX; anything else is no protobuf.
                throw new InvalidDataException("Unknown wire type.");
        }
    }

    public void SkipTo(long position) => SkipTo(position, Length);

    private void SkipTo(long position, long end)
    {
        if (position > end || position < _position)
        {
            throw new InvalidDataException("A field runs past its message.");
        }

        _position = position;
    }

    private byte ReadByte()
    {
        if (_position < _bufferStart || _position >= _bufferStart + _bufferLength)
        {
            _stream.Seek(_position, SeekOrigin.Begin);
            _bufferStart = _position;
            _bufferLength = 0;
            int read;
            while (_bufferLength < _buffer.Length && (read = _stream.Read(_buffer, _bufferLength, _buffer.Length - _bufferLength)) > 0)
            {
                _bufferLength += read;
            }

            if (_bufferLength == 0)
            {
                throw new EndOfStreamException();
            }
        }

        return _buffer[_position++ - _bufferStart];
    }
}
