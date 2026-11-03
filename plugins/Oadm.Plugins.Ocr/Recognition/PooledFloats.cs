using System.Buffers;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// A float buffer rented from <see cref="ArrayPool{T}.Shared"/>: the model inputs and the text finder's maps (megabytes for a
/// full HD picture) are reused instead of new large objects per picture. Returned once on <see cref="Dispose"/>.
/// </summary>
internal sealed class PooledFloats : IDisposable
{
    private float[]? _array;

    private PooledFloats(float[] array, int length)
    {
        _array = array;
        Length = length;
    }

    /// <summary>The usable length (the rented array may be longer).</summary>
    public int Length { get; }

    /// <summary>The rented array; only its first <see cref="Length"/> values belong to this buffer.</summary>
    public float[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledFloats));

    /// <summary>The usable part.</summary>
    public Span<float> Span => Array.AsSpan(0, Length);

    /// <summary>A buffer of <paramref name="length"/> values, zeroed when <paramref name="clear"/> (padding read as 0).</summary>
    public static PooledFloats Rent(int length, bool clear)
    {
        var array = ArrayPool<float>.Shared.Rent(length);
        if (clear)
        {
            System.Array.Clear(array, 0, length);
        }

        return new PooledFloats(array, length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _array, null) is { } array)
        {
            ArrayPool<float>.Shared.Return(array);
        }
    }
}
