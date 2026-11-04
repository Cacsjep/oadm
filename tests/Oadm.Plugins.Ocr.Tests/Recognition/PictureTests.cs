using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>Decoding only the part of a picture the areas need gives the same pixels as the whole decode.</summary>
public sealed class PictureTests
{
    private static byte[] Box() => File.ReadAllBytes(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));

    [Fact]
    public void A_region_decodes_the_same_pixels_as_the_whole_picture()
    {
        var jpeg = Box();
        var region = new SKRectI(501, 577, 900, 701);
        using var whole = Picture.Decode(jpeg);
        using var part = Picture.Decode(jpeg, (_, _) => region);

        Assert.Equal((1920, 1080), (part.Width, part.Height));
        Assert.True(part.Decoded.Contains(region), part.Decoded.ToString());
        Assert.True(part.Decoded.Width * part.Decoded.Height < 1920 * 1080 / 4, part.Decoded.ToString());

        // The text finder's input of the region is the same from both.
        var w = 416;
        var h = 128;
        var fromWhole = new float[3 * w * h];
        var fromPart = new float[3 * w * h];
        whole.WriteTile(fromWhole, 0, region.Left, region.Top, w, h, region, SKColors.Gray, Tensors.ImageNet);
        part.WriteTile(fromPart, 0, region.Left, region.Top, w, h, region, SKColors.Gray, Tensors.ImageNet);
        Assert.Equal(fromWhole, fromPart);
        Assert.Equal(whole.BorderColor(region), part.BorderColor(region));

        // A rendered cut-out (scaled down, which averages blocks first, and up) too, up to the rounding of the drawing's offset.
        foreach (var scale in new[] { 0.3f, 1.7f })
        {
            var matrix = SKMatrix.Concat(SKMatrix.CreateScale(scale, scale), SKMatrix.CreateTranslation(-region.Left, -region.Top));
            using var a = whole.Render(matrix, (int)(region.Width * scale), (int)(region.Height * scale), scale, SKColors.Black);
            using var b = part.Render(matrix, (int)(region.Width * scale), (int)(region.Height * scale), scale, SKColors.Black);
            var pa = a.GetPixelSpan().ToArray();
            var pb = b.GetPixelSpan().ToArray();
            Assert.Equal(pa.Length, pb.Length);
            Assert.True(pa.Zip(pb).All(p => Math.Abs(p.First - p.Second) <= 2), $"scale {scale}");
        }
    }

    [Fact]
    public void The_needed_region_covers_every_area_with_a_margin()
    {
        Area[] areas =
        [
            new() { Name = "A", Box = new RelativeBox(0.1, 0.1, 0.1, 0.05) },
            new() { Name = "B", Box = new RelativeBox(0.5, 0.6, 0.2, 0.1) },
        ];

        var region = OnnxTextRecognizer.NeededRegion(areas, 1920, 1080);

        Assert.True(region.Left < 192 && region.Top < 108 && region.Right > 1344 && region.Bottom > 756, region.ToString());
        Assert.Equal(new SKRectI(0, 0, 1920, 1080), OnnxTextRecognizer.NeededRegion([new Area { Name = "All", Box = new RelativeBox(0, 0, 1, 1) }], 1920, 1080));
    }

    [Fact]
    public void The_cut_out_is_the_box_with_its_margin_from_a_partial_decode()
    {
        var jpeg = Box();
        var cut = new PictureCutter().Cut(jpeg, new RelativeBox(0.28, 0.54, 0.16, 0.1));

        using var bitmap = SKBitmap.Decode(cut);
        using var whole = SKBitmap.Decode(jpeg);

        // 0.16 x 0.1 of full HD (537.6 to 844.8, 583.2 to 691.2) plus a quarter of the height (27 px) on each side.
        Assert.Equal((872 - 510, 719 - 556), (bitmap.Width, bitmap.Height));
        foreach (var (x, y) in new[] { (577, 623), (700, 650), (520, 700) })
        {
            var expected = whole.GetPixel(x, y);
            var actual = bitmap.GetPixel(x - 510, y - 556);
            Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 24);
        }
    }

    [Fact]
    public void A_whole_picture_region_decodes_everything()
    {
        using var picture = Picture.Decode(Box(), (w, h) => new SKRectI(0, 0, w, h));

        Assert.Equal(new SKRectI(0, 0, 1920, 1080), picture.Decoded);
    }

    [Fact]
    public void A_picture_that_claims_a_huge_size_is_refused_before_any_pixel_memory()
    {
        // A small JPEG whose header says 65,535 x 65,535 (16 GB of pixels): refused from the header, never allocated.
        using var bitmap = new SKBitmap(64, 64);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        var jpeg = data.ToArray();
        var sof = Enumerable.Range(0, jpeg.Length - 1).First(i => jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC2);
        for (var i = sof + 5; i < sof + 9; i++)
        {
            jpeg[i] = 0xFF;
        }

        Assert.Throws<InvalidDataException>(() => Picture.Decode(jpeg));
        Assert.False(Picture.Fits(jpeg)); // a page's held frame follows the same limits
        Assert.True(Picture.Fits(data.ToArray()));
        Assert.Null(new PictureCutter().Cut(jpeg, new RelativeBox(0, 0, 0.5, 0.5)));
    }
}
