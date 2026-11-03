using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// Cuts a found value out of a frame for the page (docs/spec/ocr.md "Expected text": the matched text's cut-out next to its
/// value), with a margin of a quarter of its height around it, as JPEG. In memory only, like the frames.
/// </summary>
public sealed class PictureCutter : IPictureCutter
{
    /// <summary>The JPEG quality of the cut-outs.</summary>
    private const int Quality = 90;

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
            using var cut = picture.Render(SKMatrix.CreateTranslation(-rect.Left, -rect.Top), rect.Width, rect.Height, 1, SKColors.Black);
            using var image = SKImage.FromBitmap(cut);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
            return data?.ToArray();
        }
#pragma warning disable CA1031 // Only the page's cut-out: a picture that cannot be cut shows none.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
