using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// Stretches the contrast of a dark or flat cut-out (docs/spec/ocr.md "Accuracy": read from worse pictures): the darkest and
/// brightest percent of its brightness become black and white. A cut-out that already has contrast is left as it is.
/// </summary>
internal static class Levels
{
    /// <summary>
    /// A line whose brightness spans at least this much (0 to 255) is left alone, so coloured print keeps its colours. (The text
    /// finder gets the areas as they are: stretched areas lost small marks such as the registered sign.)
    /// </summary>
    public const int LineContrast = 100;

    /// <summary>The largest gain (a very flat cut-out is not turned into noise).</summary>
    private const float MaxGain = 4f;

    /// <summary>The gain and offset for <paramref name="pixels"/> (RGBA), or (1, 0) when it has contrast enough.</summary>
    public static (float Gain, float Offset) Measure(ReadOnlySpan<byte> pixels, int enough, int step = 1)
    {
        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4 * step)
        {
            histogram[(pixels[i] + 2 * pixels[i + 1] + pixels[i + 2]) >> 2]++;
            count++;
        }

        if (count == 0)
        {
            return (1, 0);
        }

        var cut = Math.Max(1, count / 100);
        int lo = 0, hi = 255, sum = 0;
        for (var v = 0; v < 256; v++)
        {
            sum += histogram[v];
            if (sum >= cut)
            {
                lo = v;
                break;
            }
        }

        sum = 0;
        for (var v = 255; v >= 0; v--)
        {
            sum += histogram[v];
            if (sum >= cut)
            {
                hi = v;
                break;
            }
        }

        if (hi - lo >= enough || hi <= lo)
        {
            return (1, 0);
        }

        var gain = Math.Min(MaxGain, 255f / (hi - lo));
        return (gain, -lo * gain);
    }

    /// <summary>Stretches <paramref name="bitmap"/> (RGBA) in place when it is dark or flat.</summary>
    public static void Stretch(SKBitmap bitmap, int enough)
    {
        var pixels = bitmap.GetPixelSpan();
        var (gain, offset) = Measure(pixels, enough);
        if (gain == 1 && offset == 0)
        {
            return;
        }

        Span<byte> map = stackalloc byte[256];
        for (var v = 0; v < 256; v++)
        {
            map[v] = (byte)Math.Clamp((int)MathF.Round(v * gain + offset), 0, 255);
        }

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = map[pixels[i]];
            pixels[i + 1] = map[pixels[i + 1]];
            pixels[i + 2] = map[pixels[i + 2]];
        }
    }
}
