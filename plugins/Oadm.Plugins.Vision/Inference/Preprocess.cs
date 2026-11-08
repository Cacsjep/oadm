using System.Runtime.InteropServices;

using Oadm.Plugins.Vision.Model;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>
/// Where a picture lands in a model's input (docs/spec/vision-center.md "Models", Fit): each axis scaled by
/// <see cref="ScaleX"/> and <see cref="ScaleY"/>, then moved by <see cref="OffsetX"/> and <see cref="OffsetY"/> input pixels.
/// <see cref="ToPicture"/> undoes it for the model's boxes.
/// </summary>
internal readonly record struct FitTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY, int SourceWidth, int SourceHeight)
{
    /// <summary>How a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> picture is fitted to an input of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static FitTransform Of(ModelFit fit, int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        switch (fit)
        {
            case ModelFit.Stretch:
                return new FitTransform((double)width / sourceWidth, (double)height / sourceHeight, 0, 0, sourceWidth, sourceHeight);
            case ModelFit.KeepShapePad:
            {
                var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
                return new FitTransform(scale, scale, 0, 0, sourceWidth, sourceHeight);
            }

            case ModelFit.Letterbox:
            default:
            {
                var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
                var placedWidth = Math.Round(sourceWidth * scale);
                var placedHeight = Math.Round(sourceHeight * scale);
                return new FitTransform(scale, scale, Math.Floor((width - placedWidth) / 2), Math.Floor((height - placedHeight) / 2), sourceWidth, sourceHeight);
            }
        }
    }

    /// <summary>The rectangle of the input the picture covers.</summary>
    public SKRect Placed => SKRect.Create((float)OffsetX, (float)OffsetY, (float)(SourceWidth * ScaleX), (float)(SourceHeight * ScaleY));

    /// <summary>A box in input pixels (corners) as a box relative to the whole picture, clipped to it; null when nothing is left.</summary>
    public RelativeBox? ToPicture(double x1, double y1, double x2, double y2)
    {
        var left = Math.Clamp((Math.Min(x1, x2) - OffsetX) / ScaleX / SourceWidth, 0, 1);
        var right = Math.Clamp((Math.Max(x1, x2) - OffsetX) / ScaleX / SourceWidth, 0, 1);
        var top = Math.Clamp((Math.Min(y1, y2) - OffsetY) / ScaleY / SourceHeight, 0, 1);
        var bottom = Math.Clamp((Math.Max(y1, y2) - OffsetY) / ScaleY / SourceHeight, 0, 1);
        return right - left > 0 && bottom - top > 0 && double.IsFinite(left + right + top + bottom)
            ? new RelativeBox(left, top, right - left, bottom - top)
            : null;
    }
}

/// <summary>
/// Turns a picture into a model's input tensor (docs/spec/vision-center.md "Models"): drawn into the input by its fit
/// (Letterbox grey 114, Keep shape and pad black, Stretch), RGB, channels first, scaled 0 to 1 or by ImageNet's mean and
/// deviation.
/// </summary>
internal static class Preprocess
{
    /// <summary>The grey of the letterbox bars (YOLO's training value).</summary>
    public static readonly SKColor LetterboxGrey = new(114, 114, 114);

    private static readonly (float Mean, float Std)[] ImageNet = [(0.485f, 0.229f), (0.456f, 0.224f), (0.406f, 0.225f)];

    private static readonly (float Mean, float Std)[] ZeroToOne = [(0f, 1f), (0f, 1f), (0f, 1f)];

    /// <summary>
    /// Draws <paramref name="source"/> (its <paramref name="region"/>, the whole picture when null) into a
    /// <paramref name="width"/> x <paramref name="height"/> input by <paramref name="fit"/> and writes it into
    /// <paramref name="tensor"/> ([3, height, width], at least that long).
    /// </summary>
    /// <returns>Where the region landed in the input.</returns>
    public static FitTransform Write(SKImage source, SKRectI? region, ModelFit fit, ModelScaling scaling, int width, int height, float[] tensor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tensor);
        var part = region ?? new SKRectI(0, 0, source.Width, source.Height);
        var transform = FitTransform.Of(fit, part.Width, part.Height, width, height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(fit switch
            {
                ModelFit.Letterbox => LetterboxGrey,
                _ => SKColors.Black,
            });

            // Linear with mipmaps: a picture made smaller keeps its thin edges, one made larger stays smooth.
            canvas.DrawImage(source, SKRect.Create(part.Left, part.Top, part.Width, part.Height), transform.Placed, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        WriteTensor(bitmap, tensor, scaling);
        return transform;
    }

    /// <summary>The RGB values of <paramref name="bitmap"/> into <paramref name="tensor"/>, channels first.</summary>
    public static void WriteTensor(SKBitmap bitmap, float[] tensor, ModelScaling scaling)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(tensor);
        var norm = scaling == ModelScaling.ImageNet ? ImageNet : ZeroToOne;
        var (width, height) = (bitmap.Width, bitmap.Height);
        var plane = width * height;
        var pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        var rowPixels = bitmap.RowBytes / 4;
        float r0 = 1 / (255f * norm[0].Std), g0 = 1 / (255f * norm[1].Std), b0 = 1 / (255f * norm[2].Std);
        float rm = norm[0].Mean / norm[0].Std, gm = norm[1].Mean / norm[1].Std, bm = norm[2].Mean / norm[2].Std;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowPixels, width);
            var at = y * width;
            for (var x = 0; x < width; x++)
            {
                // Rgba8888 little endian: R in the lowest byte.
                var p = row[x];
                tensor[at + x] = (p & 0xFF) * r0 - rm;
                tensor[plane + at + x] = ((p >> 8) & 0xFF) * g0 - gm;
                tensor[2 * plane + at + x] = ((p >> 16) & 0xFF) * b0 - bm;
            }
        }
    }
}
