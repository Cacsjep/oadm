using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// A decoded camera picture (SkiaSharp, RGBA) and the cut-outs recognition needs: an area or tile for the text finder, a
/// line turned level and scaled to the reader's height. Scaling up is bicubic; scaling down by more than half averages
/// blocks of pixels first, so thin strokes are not lost. Lives in memory only (docs/spec/ocr.md rule 2).
/// <para>
/// Only the part of the picture the areas need is decoded (<see cref="Decode"/> with a region: the rows above it skipped, the
/// rows below never read, the columns outside cut by the JPEG decoder), so a small area costs a fraction of a full decode.
/// Coordinates stay those of the whole picture; pixels outside the decoded region read as black.
/// </para>
/// </summary>
internal sealed class Picture : IDisposable
{
    private readonly SKBitmap _bitmap;
    private readonly SKImage _image;
    private readonly int _left;
    private readonly int _top;

    private Picture(SKBitmap bitmap, int width, int height, int left, int top)
    {
        _bitmap = bitmap;
        _image = SKImage.FromBitmap(bitmap);
        Width = width;
        Height = height;
        _left = left;
        _top = top;
    }

    /// <summary>Width of the whole picture in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the whole picture in pixels.</summary>
    public int Height { get; }

    /// <summary>The decoded part, in picture pixels (the whole picture without a region).</summary>
    public SKRectI Decoded => new(_left, _top, _left + _bitmap.Width, _top + _bitmap.Height);

    /// <summary>
    /// Decodes a JPEG (or any picture SkiaSharp reads): the whole picture, or only <paramref name="region"/> of it (given the
    /// picture's width and height; null or empty: the whole picture).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a picture.</exception>
    public static Picture Decode(ReadOnlyMemory<byte> data, Func<int, int, SKRectI?>? region = null)
    {
        using var skData = SKData.CreateCopy(data.Span);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException("The camera's answer is not a picture.");
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        var full = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var part = region?.Invoke(width, height) is { } r ? SKRectI.Intersect(r, new SKRectI(0, 0, width, height)) : SKRectI.Empty;
        if (!part.IsEmpty && part.Width * (long)part.Height < width * (long)height * 9 / 10 && DecodePart(codec, full, part) is { } partial)
        {
            return new Picture(partial, width, height, part.Left, part.Top);
        }

        var bitmap = new SKBitmap(full);
        var result = codec.GetPixels(full, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException("The camera's answer is not a picture.");
        }

        return new Picture(bitmap, width, height, 0, 0);
    }

    /// <summary>The rows and columns of <paramref name="part"/> through the line decoder, or null when the codec cannot.</summary>
    private static SKBitmap? DecodePart(SKCodec codec, SKImageInfo full, SKRectI part)
    {
        // The line decoder cuts columns only (the JPEG decoder skips the blocks left and right); rows are skipped above and
        // never read below.
        var options = new SKCodecOptions(new SKRectI(part.Left, 0, part.Right, full.Height));
        if (codec.StartScanlineDecode(full, options) != SKCodecResult.Success || codec.ScanlineOrder != SKCodecScanlineOrder.TopDown)
        {
            return null;
        }

        var bitmap = new SKBitmap(new SKImageInfo(part.Width, part.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (part.Top > 0 && !codec.SkipScanlines(part.Top))
        {
            bitmap.Dispose();
            return null;
        }

        var rows = codec.GetScanlines(bitmap.GetPixels(), part.Height, bitmap.RowBytes);
        if (rows <= 0)
        {
            bitmap.Dispose();
            return null;
        }

        if (rows < part.Height)
        {
            // A cut-off picture: the rows that never came stay black, as in a full decode of an incomplete JPEG.
            bitmap.GetPixelSpan()[(rows * bitmap.RowBytes)..].Clear();
        }

        return bitmap;
    }

    /// <summary>
    /// Renders the picture through <paramref name="pictureToTarget"/> into a new <paramref name="width"/> x
    /// <paramref name="height"/> bitmap filled with <paramref name="fill"/>; only <paramref name="clip"/> (target pixels) gets
    /// picture content when given. <paramref name="scale"/> is the matrix's scale (target pixels per picture pixel).
    /// </summary>
    public SKBitmap Render(SKMatrix pictureToTarget, int width, int height, float scale, SKColor fill, SKRect? clip = null)
    {
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(fill);
        if (clip is { } c)
        {
            canvas.ClipRect(c);
        }

        if (scale < 0.5f)
        {
            // Average k x k blocks of the part of the picture that is drawn, then draw that with a scale of 0.5 to 1.
            var k = Math.Max(2, (int)MathF.Floor(1f / scale));
            if (!pictureToTarget.TryInvert(out var inverse))
            {
                return target;
            }

            // In the decoded bitmap's pixels.
            var bounds = inverse.MapRect(new SKRect(0, 0, width, height));
            var x0 = Math.Clamp((int)MathF.Floor(bounds.Left) - _left, 0, _bitmap.Width);
            var y0 = Math.Clamp((int)MathF.Floor(bounds.Top) - _top, 0, _bitmap.Height);
            var x1 = Math.Clamp((int)MathF.Ceiling(bounds.Right) - _left, 0, _bitmap.Width);
            var y1 = Math.Clamp((int)MathF.Ceiling(bounds.Bottom) - _top, 0, _bitmap.Height);
            if (x1 - x0 < k || y1 - y0 < k)
            {
                return target;
            }

            using var reduced = Reduce(x0, y0, x1 - x0, y1 - y0, k);
            using var reducedImage = SKImage.FromBitmap(reduced);
            var m = SKMatrix.Concat(pictureToTarget, SKMatrix.Concat(SKMatrix.CreateTranslation(x0 + _left, y0 + _top), SKMatrix.CreateScale(k, k)));
            canvas.SetMatrix(m);
            canvas.DrawImage(reducedImage, 0, 0, Sampling(scale * k));
        }
        else
        {
            canvas.SetMatrix(pictureToTarget);
            canvas.DrawImage(_image, _left, _top, Sampling(scale));
        }

        canvas.Flush();
        return target;
    }

    /// <summary>
    /// Writes the <paramref name="width"/> x <paramref name="height"/> tile at (<paramref name="x"/>, <paramref name="y"/>) at
    /// full resolution into item <paramref name="item"/> of a [N, 3, height, width] tensor (blue first): pixels outside
    /// <paramref name="area"/> or the picture get <paramref name="fill"/>. No resampling, so it is a plain copy.
    /// </summary>
    public void WriteTile(float[] tensor, int item, int x, int y, int width, int height, SKRectI area, SKColor fill, (float Mean, float Std)[] norm)
    {
        var plane = width * height;
        var offset = item * 3 * plane;
        var pixels = _bitmap.GetPixelSpan();
        var stride = _bitmap.RowBytes;
        var s0 = 1f / (255f * norm[0].Std);
        var s1 = 1f / (255f * norm[1].Std);
        var s2 = 1f / (255f * norm[2].Std);
        var m0 = norm[0].Mean / norm[0].Std;
        var m1 = norm[1].Mean / norm[1].Std;
        var m2 = norm[2].Mean / norm[2].Std;
        var f0 = fill.Blue * s0 - m0;
        var f1 = fill.Green * s1 - m1;
        var f2 = fill.Red * s2 - m2;
        var left = Math.Max(area.Left, _left);
        var right = Math.Min(area.Right, _left + _bitmap.Width);
        var top = Math.Max(area.Top, _top);
        var bottom = Math.Min(area.Bottom, _top + _bitmap.Height);
        for (var row = 0; row < height; row++)
        {
            var py = y + row;
            var o = offset + row * width;
            var rowInside = py >= top && py < bottom;
            for (var col = 0; col < width; col++)
            {
                var px = x + col;
                if (rowInside && px >= left && px < right)
                {
                    var i = (py - _top) * stride + (px - _left) * 4;
                    tensor[o + col] = pixels[i + 2] * s0 - m0;
                    tensor[o + plane + col] = pixels[i + 1] * s1 - m1;
                    tensor[o + 2 * plane + col] = pixels[i] * s2 - m2;
                }
                else
                {
                    tensor[o + col] = f0;
                    tensor[o + plane + col] = f1;
                    tensor[o + 2 * plane + col] = f2;
                }
            }
        }
    }

    /// <summary>The mean colour of the border pixels of a rectangle (the fill around a cut-out area).</summary>
    public SKColor BorderColor(SKRectI rect)
    {
        rect = SKRectI.Intersect(rect, Decoded);
        if (rect.IsEmpty)
        {
            return SKColors.Gray;
        }

        long r = 0, g = 0, b = 0, n = 0;
        var pixels = _bitmap.GetPixelSpan();
        var stride = _bitmap.RowBytes;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var edge = y == rect.Top || y == rect.Bottom - 1;
            for (var x = rect.Left; x < rect.Right; x += edge ? 1 : Math.Max(1, rect.Width - 1))
            {
                var i = (y - _top) * stride + (x - _left) * 4;
                r += pixels[i];
                g += pixels[i + 1];
                b += pixels[i + 2];
                n++;
            }
        }

        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static SKSamplingOptions Sampling(float scale) =>
        scale >= 0.99f ? new SKSamplingOptions(SKCubicResampler.CatmullRom) : new SKSamplingOptions(SKCubicResampler.Mitchell);

    private SKBitmap Reduce(int x0, int y0, int width, int height, int k)
    {
        var w = width / k;
        var h = height / k;
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var src = _bitmap.GetPixelSpan();
        var srcStride = _bitmap.RowBytes;
        var dst = result.GetPixelSpan();
        var dstStride = result.RowBytes;
        var count = k * k;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0;
                for (var dy = 0; dy < k; dy++)
                {
                    var row = (y0 + y * k + dy) * srcStride + (x0 + x * k) * 4;
                    for (var dx = 0; dx < k; dx++)
                    {
                        var i = row + dx * 4;
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                    }
                }

                var o = y * dstStride + x * 4;
                dst[o] = (byte)(r / count);
                dst[o + 1] = (byte)(g / count);
                dst[o + 2] = (byte)(b / count);
                dst[o + 3] = 255;
            }
        }

        return result;
    }

    public void Dispose()
    {
        _image.Dispose();
        _bitmap.Dispose();
    }
}

/// <summary>Model input tensors from rendered bitmaps (channel order blue, green, red, like the PaddleOCR models were trained).</summary>
internal static class Tensors
{
    /// <summary>The ImageNet normalisation of the text finder and the angle model, per tensor channel as PaddleOCR applies it.</summary>
    public static readonly (float Mean, float Std)[] ImageNet = [(0.485f, 0.229f), (0.456f, 0.224f), (0.406f, 0.225f)];

    /// <summary>The recognition model's normalisation: -1 to 1.</summary>
    public static readonly (float Mean, float Std)[] Symmetric = [(0.5f, 0.5f), (0.5f, 0.5f), (0.5f, 0.5f)];

    /// <summary>
    /// Writes <paramref name="bitmap"/> into one item of a [N, 3, height, width] tensor at <paramref name="item"/>, blue
    /// first (or red first with <paramref name="rgb"/>); columns right of the bitmap stay as they are (0: the padding).
    /// </summary>
    public static void Write(SKBitmap bitmap, float[] tensor, int item, int height, int width, (float Mean, float Std)[] norm, bool rgb = false, bool flip = false)
    {
        var plane = height * width;
        var offset = item * 3 * plane;
        var pixels = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        var w = Math.Min(width, bitmap.Width);
        var h = Math.Min(height, bitmap.Height);
        Span<float> scale = [1f / (255f * norm[0].Std), 1f / (255f * norm[1].Std), 1f / (255f * norm[2].Std)];
        Span<float> shift = [norm[0].Mean / norm[0].Std, norm[1].Mean / norm[1].Std, norm[2].Mean / norm[2].Std];
        for (var y = 0; y < h; y++)
        {
            var sy = flip ? h - 1 - y : y;
            var row = sy * stride;
            var o = offset + y * width;
            for (var x = 0; x < w; x++)
            {
                var sx = flip ? w - 1 - x : x;
                var i = row + sx * 4;
                var r = pixels[i];
                var g = pixels[i + 1];
                var b = pixels[i + 2];
                var c0 = rgb ? r : b;
                var c2 = rgb ? b : r;
                tensor[o + x] = c0 * scale[0] - shift[0];
                tensor[o + plane + x] = g * scale[1] - shift[1];
                tensor[o + 2 * plane + x] = c2 * scale[2] - shift[2];
            }
        }
    }
}
