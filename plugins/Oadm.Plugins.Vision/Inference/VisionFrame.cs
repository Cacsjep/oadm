using Oadm.Plugins.Vision.Model;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>
/// A camera picture decoded once per frame (SkiaSharp), shared by every model node of the frame; in memory only
/// (docs/spec/vision-center.md rule 2). Refused over the pixel limits before any pixel memory is taken. Decoded only as large
/// as the pipeline's models need (<see cref="PictureNeed"/>): <see cref="Width"/> and <see cref="Height"/> stay the
/// camera's, <see cref="Image"/> may be a half, a quarter or an eighth of it (boxes are relative to the picture either way).
/// </summary>
internal sealed class VisionFrame : IVisionFrame
{
    /// <summary>The text of an answer that is no picture, or a picture over the limits.</summary>
    public const string NotAPicture = "The camera's answer is not a picture.";

    private VisionFrame(SKImage image, ReadOnlyMemory<byte> jpeg, int width, int height)
    {
        Width = width;
        Height = height;
        Image = image;
        Jpeg = jpeg;
        Levels = new PictureLevels(image);
    }

    /// <summary>The camera picture's size (also when <see cref="Image"/> is decoded smaller).</summary>
    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Jpeg { get; }

    /// <summary>The decoded pixels (RGBA).</summary>
    public SKImage Image { get; }

    /// <summary>The picture and its halves, made once for every model call of the frame.</summary>
    public PictureLevels Levels { get; }

    /// <exception cref="InvalidDataException">Not a picture, or larger than <see cref="PictureLimits"/> allow.</exception>
    public static VisionFrame Decode(ReadOnlyMemory<byte> jpeg) => Decode(jpeg, null);

    /// <summary>Decodes only as large as <paramref name="need"/> asks (null: the whole picture).</summary>
    /// <exception cref="InvalidDataException">Not a picture, or larger than <see cref="PictureLimits"/> allow.</exception>
    public static VisionFrame Decode(ReadOnlyMemory<byte> jpeg, PictureNeed? need)
    {
        using var data = SKData.CreateCopy(jpeg.Span);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException(NotAPicture);
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (!PictureLimits.WithinLimits(width, height))
        {
            // The header's size before any pixel memory.
            throw new InvalidDataException(NotAPicture);
        }

        var (decodedWidth, decodedHeight) = (width, height);
        if (need?.ScaleFor(width, height, s => ScaledSize(codec, s)) is { } scale and < 1)
        {
            (decodedWidth, decodedHeight) = ScaledSize(codec, scale);
        }

        var info = new SKImageInfo(decodedWidth, decodedHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
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
        return new VisionFrame(image, jpeg, width, height);
    }

    /// <summary>The size the codec decodes at <paramref name="scale"/> (JPEG: exact eighths; other formats: the whole size).</summary>
    private static (int Width, int Height) ScaledSize(SKCodec codec, float scale)
    {
        var size = codec.GetScaledDimensions(scale);
        return (size.Width, size.Height);
    }

    /// <summary>A picture made in memory (tests).</summary>
    internal static VisionFrame From(SKBitmap bitmap, ReadOnlyMemory<byte> jpeg = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new VisionFrame(SKImage.FromBitmap(bitmap), jpeg, bitmap.Width, bitmap.Height);
    }

    /// <summary>A region relative to the picture as pixels, clipped, at least one pixel.</summary>
    public SKRectI PixelsOf(RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var (width, height) = (Image.Width, Image.Height);
        var left = (int)Math.Clamp(Math.Floor(box.X * width), 0, width - 1);
        var top = (int)Math.Clamp(Math.Floor(box.Y * height), 0, height - 1);
        var right = (int)Math.Clamp(Math.Ceiling(box.Right * width), left + 1, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(box.Bottom * height), top + 1, height);
        return new SKRectI(left, top, right, bottom);
    }

    public void Dispose()
    {
        Levels.Dispose();
        Image.Dispose();
    }
}
