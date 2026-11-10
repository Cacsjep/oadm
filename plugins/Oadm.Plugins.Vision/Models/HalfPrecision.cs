using System.Runtime.InteropServices;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Oadm.Plugins.Vision.Models;

/// <summary>
/// Models exported in half precision (<c>--half</c>: the v7.0 release of YOLOv5, Unity's YOLO exports): their picture input
/// and outputs are 16-bit floats. The input is written as floats and converted, the outputs are read as floats.
/// </summary>
internal static class HalfPrecision
{
    /// <summary>Whether <paramref name="type"/> holds floating point numbers the decoders read (32 or 16 bits).</summary>
    public static bool IsFloat(TensorElementType type) => type is TensorElementType.Float or TensorElementType.Float16;

    /// <summary><paramref name="from"/> as 16-bit floats into <paramref name="to"/> (at least as long).</summary>
    public static void Write(ReadOnlySpan<float> from, Span<Float16> to)
    {
        var bits = MemoryMarshal.Cast<Float16, ushort>(to);
        for (var i = 0; i < from.Length; i++)
        {
            bits[i] = BitConverter.HalfToUInt16Bits((Half)from[i]);
        }
    }

    /// <summary>The 16-bit floats of <paramref name="from"/> as floats.</summary>
    public static float[] ToFloats(ReadOnlySpan<Float16> from)
    {
        var bits = MemoryMarshal.Cast<Float16, ushort>(from);
        var values = new float[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            values[i] = (float)BitConverter.UInt16BitsToHalf(bits[i]);
        }

        return values;
    }

    /// <summary>The values of a float output of either width, as floats (a 16-bit output is converted once per call).</summary>
    public static ReadOnlySpan<float> Floats(OrtValue value, ref float[]? converted)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16
            ? converted ??= ToFloats(value.GetTensorDataAsSpan<Float16>())
            : value.GetTensorDataAsSpan<float>();
    }
}
