using Oadm.Plugins.Vision.Model;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>
/// A camera picture decoded once per frame (SkiaSharp), shared by every model node of the frame; in memory only
/// (docs/spec/vision-center.md rule 2). Refused over the pixel limits before any pixel memory is taken.
/// </summary>
internal sealed class VisionFrame : IVisionFrame
{
    /// <summary>The text of an answer that is no picture, or a picture over the limits.</summary>
    public const string NotAPicture = "The camera's answer is not a picture.";

    private VisionFrame(SKImage image, ReadOnlyMemory<byte> jpeg)
    {
        Image = image;
        Jpeg = jpeg;
    }

    public int Width => Image.Width;

    public int Height => Image.Height;

    public ReadOnlyMemory<byte> Jpeg { get; }

    /// <summary>The decoded pixels (RGBA).</summary>
    public SKImage Image { get; }

    /// <exception cref="InvalidDataException">Not a picture, or larger than <see cref="PictureLimits"/> allow.</exception>
    public static VisionFrame Decode(ReadOnlyMemory<byte> jpeg)
    {
        using var data = SKData.CreateCopy(jpeg.Span);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException(NotAPicture);
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (!PictureLimits.WithinLimits(width, height))
        {
            // The header's size before any pixel memory.
            throw new InvalidDataException(NotAPicture);
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException(NotAPicture);
        }

        bitmap.SetImmutable();
        var image = SKImage.FromBitmap(bitmap);
        bitmap.Dispose(); // the image keeps the pixels
        return new VisionFrame(image, jpeg);
    }

    /// <summary>A picture made in memory (tests).</summary>
    internal static VisionFrame From(SKBitmap bitmap, ReadOnlyMemory<byte> jpeg = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new VisionFrame(SKImage.FromBitmap(bitmap), jpeg);
    }

    /// <summary>A region relative to the picture as pixels, clipped, at least one pixel.</summary>
    public SKRectI PixelsOf(RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var left = (int)Math.Clamp(Math.Floor(box.X * Width), 0, Width - 1);
        var top = (int)Math.Clamp(Math.Floor(box.Y * Height), 0, Height - 1);
        var right = (int)Math.Clamp(Math.Ceiling(box.Right * Width), left + 1, Width);
        var bottom = (int)Math.Clamp(Math.Ceiling(box.Bottom * Height), top + 1, Height);
        return new SKRectI(left, top, right, bottom);
    }

    public void Dispose() => Image.Dispose();
}
