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

    /// <inheritdoc />
    public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box)
    {
        if (!box.IsValid)
        {
            return null;
        }

        try
        {
            using var picture = SKBitmap.Decode(jpeg.Span);
            if (picture is null)
            {
                return null;
            }

            var margin = box.Height * picture.Height / 4;
            var left = Math.Clamp((int)Math.Floor(box.X * picture.Width - margin), 0, picture.Width - 1);
            var top = Math.Clamp((int)Math.Floor(box.Y * picture.Height - margin), 0, picture.Height - 1);
            var right = Math.Clamp((int)Math.Ceiling((box.X + box.Width) * picture.Width + margin), left + 1, picture.Width);
            var bottom = Math.Clamp((int)Math.Ceiling((box.Y + box.Height) * picture.Height + margin), top + 1, picture.Height);
            using var cut = new SKBitmap();
            if (!picture.ExtractSubset(cut, new SKRectI(left, top, right, bottom)))
            {
                return null;
            }

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
