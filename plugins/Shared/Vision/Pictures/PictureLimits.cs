using SkiaSharp;

namespace Oadm.Plugins.Shared.Vision;

/// <summary>
/// The size a camera picture may have before it is decoded (docs/spec/ocr.md "Limits"): read from the picture's header, so
/// a picture that claims 65,000 x 65,000 never takes gigabytes. Both vision add-ons decode only what passes.
/// </summary>
internal static class PictureLimits
{
    /// <summary>The largest picture side.</summary>
    public const int MaxSide = 7680;

    /// <summary>The most pixels a picture may have (8K, 7680 x 4320): a larger one is never decoded (memory).</summary>
    public const long MaxPixels = 7680L * 4320;

    /// <summary>A picture of <paramref name="width"/> x <paramref name="height"/> may be decoded.</summary>
    public static bool WithinLimits(int width, int height) =>
        width is >= 1 and <= MaxSide && height is >= 1 and <= MaxSide && (long)width * height <= MaxPixels;

    /// <summary>The picture's header says a size within the limits (no pixel is decoded).</summary>
    public static bool Fits(ReadOnlyMemory<byte> data)
    {
        using var skData = SKData.CreateCopy(data.Span);
        using var codec = SKCodec.Create(skData);
        return codec is not null && WithinLimits(codec.Info.Width, codec.Info.Height);
    }
}
