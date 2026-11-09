using System.Runtime.InteropServices;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>
/// A picture and its halves, each made once from the one before with a 2 x 2 box filter (what mipmaps are), and kept for
/// the frame (docs/spec/vision-center.md "Measured": Skia built the mipmaps of the whole picture again on every model
/// call, 5 ms of a 720p picture). A model's input is drawn with linear sampling from the smallest level that is still at
/// least as large as the input needs, so no level is scaled down by more than half. Thread-safe: the model calls of one
/// frame share it. Level 0 belongs to the caller; the halves are freed with this.
/// </summary>
internal sealed class PictureLevels(SKImage full) : IDisposable
{
    private readonly List<SKImage> _levels = [full ?? throw new ArgumentNullException(nameof(full))];
    private readonly Lock _lock = new();
    private bool _disposed;

    public SKImage Full => _levels[0];

    /// <summary>
    /// The level to draw <paramref name="region"/> of the full picture from, scaled by <paramref name="scale"/> (the smaller
    /// of both axes' scale), and the region in that level's pixels.
    /// </summary>
    public (SKImage Image, SKRect Region) For(SKRectI region, double scale)
    {
        var level = 0;
        var factor = 1.0;
        while (scale * factor * 2 <= 1 && region.Width / (factor * 2) >= 1 && region.Height / (factor * 2) >= 1)
        {
            level++;
            factor *= 2;
        }

        var image = Level(level);
        if (level == 0)
        {
            return (image, SKRect.Create(region.Left, region.Top, region.Width, region.Height));
        }

        // A level's size is the floor of the halves: its pixels cover the picture's pixels by this exact ratio.
        var sx = (double)image.Width / Full.Width;
        var sy = (double)image.Height / Full.Height;
        return (image, SKRect.Create((float)(region.Left * sx), (float)(region.Top * sy), (float)(region.Width * sx), (float)(region.Height * sy)));
    }

    /// <summary>Level <paramref name="index"/> (0: the picture), made with the ones before it when first asked; it stops at 1 x 1.</summary>
    public SKImage Level(int index)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_levels.Count <= index)
            {
                var last = _levels[^1];
                if (last.Width < 2 || last.Height < 2)
                {
                    return last;
                }

                _levels.Add(Half(last));
            }

            return _levels[index];
        }
    }

    /// <summary>The picture at half its size, each pixel the rounded mean of 2 x 2 (premultiplied RGBA, so the mean is right).</summary>
    internal static SKImage Half(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var pixmap = source.PeekPixels() ?? throw new InvalidOperationException("The picture has no pixels in memory.");
        if (pixmap.ColorType != SKColorType.Rgba8888 && pixmap.ColorType != SKColorType.Bgra8888)
        {
            throw new InvalidOperationException("A picture of 4 bytes per pixel is needed.");
        }

        var (width, height) = (source.Width / 2, source.Height / 2);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, pixmap.ColorType, pixmap.AlphaType));
        var from = MemoryMarshal.Cast<byte, uint>(pixmap.GetPixelSpan());
        var to = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        var fromRow = pixmap.RowBytes / 4;
        var toRow = bitmap.RowBytes / 4;
        const uint Low = 0x00FF00FF;
        const uint Round = 0x00020002;
        for (var y = 0; y < height; y++)
        {
            var top = from.Slice(2 * y * fromRow, 2 * width);
            var bottom = from.Slice(((2 * y) + 1) * fromRow, 2 * width);
            var row = to.Slice(y * toRow, width);
            for (var x = 0; x < row.Length; x++)
            {
                uint a = top[2 * x], b = top[(2 * x) + 1], c = bottom[2 * x], d = bottom[(2 * x) + 1];

                // Two bytes of each pixel per 16-bit lane: four of them sum to at most 1,020, no carry into the next lane.
                var even = (a & Low) + (b & Low) + (c & Low) + (d & Low);
                var odd = ((a >> 8) & Low) + ((b >> 8) & Low) + ((c >> 8) & Low) + ((d >> 8) & Low);
                row[x] = (((even + Round) >> 2) & Low) | ((((odd + Round) >> 2) & Low) << 8);
            }
        }

        bitmap.SetImmutable();
        var image = SKImage.FromBitmap(bitmap);
        bitmap.Dispose(); // the image keeps the pixels
        return image;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var level in _levels.Skip(1))
            {
                level.Dispose();
            }

            _levels.RemoveRange(1, _levels.Count - 1);
        }
    }
}
