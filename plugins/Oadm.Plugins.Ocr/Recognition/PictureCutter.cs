using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// Cuts a found value out of a frame for the page (docs/spec/ocr.md "Expected text": the matched text's cut-out next to its
/// value), with a margin of a quarter of its height around it, as JPEG: upright as the reader saw it when its line's turn is
/// known, else level. In memory only, like the frames.
/// </summary>
public sealed class PictureCutter : IPictureCutter
{
    /// <summary>The JPEG quality of the cut-outs, the lower ones for one that would be too long.</summary>
    private static readonly int[] Qualities = [90, 75, 50];

    /// <summary>The cut-out in picture pixels: the box with a margin of a quarter of its height, inside the picture.</summary>
    private static SKRectI Rect(RelativeBox box, int width, int height)
    {
        var margin = box.Height * height / 4;
        var left = Math.Clamp((int)Math.Floor(box.X * width - margin), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(box.Y * height - margin), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((box.X + box.Width) * width + margin), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((box.Y + box.Height) * height + margin), top + 1, height);
        return new SKRectI(left, top, right, bottom);
    }

    /// <inheritdoc />
    public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box)
    {
        if (!box.IsValid)
        {
            return null;
        }

        try
        {
            // Only the cut-out's rows and columns are decoded (a full HD frame per area per reading cost about 16 ms).
            using var picture = Picture.Decode(jpeg, (w, h) => Rect(box, w, h));
            var rect = Rect(box, picture.Width, picture.Height);
            return Fitted(picture, SKMatrix.CreateTranslation(-rect.Left, -rect.Top), rect.Width, rect.Height);
        }
#pragma warning disable CA1031 // Only the page's cut-out: a picture that cannot be cut shows none.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <inheritdoc />
    public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box, LineTurn? turn)
    {
        if (turn is not { Width: > 0, Height: > 0 } line)
        {
            return Cut(jpeg, box);
        }

        try
        {
            // The line's own box turned upright, the same margin around it: only the part it needs is decoded.
            var margin = line.Height / 4;
            var width = (int)MathF.Ceiling(line.Width + (2 * margin));
            var height = (int)MathF.Ceiling(line.Height + (2 * margin));
            using var picture = Picture.Decode(jpeg, (w, h) => Bounds(line, margin, w, h));
            var matrix = SKMatrix.CreateTranslation(width / 2f, height / 2f)
                .PreConcat(SKMatrix.CreateRotation(-line.Angle))
                .PreConcat(SKMatrix.CreateTranslation(-line.CenterX, -line.CenterY));
            return Fitted(picture, matrix, width, height) ?? Cut(jpeg, box);
        }
#pragma warning disable CA1031 // A turn that cannot be cut never costs the row its picture.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Cut(jpeg, box);
        }
    }

    /// <summary>The picture's pixels the turned box with its margin touches.</summary>
    private static SKRectI Bounds(LineTurn line, float margin, int width, int height)
    {
        var (cos, sin) = (MathF.Abs(MathF.Cos(line.Angle)), MathF.Abs(MathF.Sin(line.Angle)));
        var (w, h) = (line.Width + (2 * margin), line.Height + (2 * margin));
        var halfX = ((w * cos) + (h * sin)) / 2;
        var halfY = ((w * sin) + (h * cos)) / 2;
        return SKRectI.Intersect(
            new SKRectI((int)MathF.Floor(line.CenterX - halfX) - 2, (int)MathF.Floor(line.CenterY - halfY) - 2, (int)MathF.Ceiling(line.CenterX + halfX) + 2, (int)MathF.Ceiling(line.CenterY + halfY) + 2),
            new SKRectI(0, 0, width, height));
    }

    /// <summary>
    /// The <paramref name="width"/> x <paramref name="height"/> cut-out of <paramref name="toCut"/> at most
    /// <see cref="OcrLimits.MaxCutoutSide"/> on its longer side and <see cref="OcrLimits.MaxCutoutBytes"/> long; null when it stays longer.
    /// </summary>
    private static byte[]? Fitted(Picture picture, SKMatrix toCut, int width, int height)
    {
        var scale = Math.Min(1f, (float)OcrLimits.MaxCutoutSide / Math.Max(width, height));
        return Encoded(picture, toCut, width, height, scale) ?? Encoded(picture, toCut, width, height, scale / 2);
    }

    /// <summary>The cut-out at <paramref name="scale"/> as JPEG within <see cref="OcrLimits.MaxCutoutBytes"/>; null when it stays longer.</summary>
    private static byte[]? Encoded(Picture picture, SKMatrix toCut, int width, int height, float scale)
    {
        var matrix = toCut.PostConcat(SKMatrix.CreateScale(scale, scale));
        using var cut = picture.Render(matrix, Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)), scale, SKColors.Black);
        using var image = SKImage.FromBitmap(cut);
        foreach (var quality in Qualities)
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is not null && data.Size <= OcrLimits.MaxCutoutBytes)
            {
                return data.ToArray();
            }
        }

        return null;
    }
}
