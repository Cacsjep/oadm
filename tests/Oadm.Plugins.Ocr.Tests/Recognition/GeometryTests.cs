using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The text finder's post-processing (own C#, no OpenCV), merging boxes of overlapping tiles, the tile plan and the contrast
/// stretch (docs/spec/ocr.md "Reading").
/// </summary>
public sealed class GeometryTests
{
    /// <summary>A probability map with filled rotated rectangles (value inside, 0 outside).</summary>
    private static float[] Map(int width, int height, params (float Cx, float Cy, float W, float H, float Degrees, float Value)[] rects)
    {
        var map = new float[width * height];
        foreach (var (cx, cy, w, h, degrees, value) in rects)
        {
            var a = degrees * MathF.PI / 180;
            var c = MathF.Cos(a);
            var s = MathF.Sin(a);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    if (MathF.Abs(dx * c + dy * s) <= w / 2 && MathF.Abs(-dx * s + dy * c) <= h / 2)
                    {
                        map[y * width + x] = value;
                    }
                }
            }
        }

        return map;
    }

    [Fact]
    public void Regions_become_rotated_boxes_enlarged_on_every_side()
    {
        var map = Map(320, 160, (80, 40, 100, 20, 0, 0.9f), (220, 110, 120, 16, 15, 0.8f));

        var boxes = DbPostProcess.Boxes(map, 320, 160).OrderBy(b => b.CenterX).ToList();

        Assert.Equal(2, boxes.Count);
        var level = boxes[0];
        Assert.Equal(80, level.CenterX, 1.0);
        Assert.Equal(40, level.CenterY, 1.0);
        Assert.Equal(0, level.Angle, 0.02);
        Assert.InRange(level.Score, 0.85f, 0.91f);

        // Enlarged by area x 1.5 / perimeter on every side: 100 x 20 grows by 2 x 12.5.
        Assert.InRange(level.Width, 120, 130);
        Assert.InRange(level.Height, 40, 50);
        Assert.Equal(level.Height, level.TextHeight);

        var slanted = boxes[1];
        Assert.Equal(15 * MathF.PI / 180, slanted.Angle, 0.03);
        Assert.True(slanted.Width > slanted.Height);
    }

    [Fact]
    public void Unsure_and_tiny_regions_are_left_out()
    {
        var map = Map(200, 100, (50, 50, 60, 20, 0, 0.5f), (150, 50, 2, 2, 0, 0.9f), (150, 20, 40, 2, 0, 0.9f));

        Assert.Empty(DbPostProcess.Boxes(map, 200, 100));
    }

    [Fact]
    public void A_tall_box_keeps_its_long_side_as_height_and_an_angle_within_45_degrees()
    {
        var map = Map(200, 300, (100, 150, 120, 20, 90, 0.9f));

        var box = Assert.Single(DbPostProcess.Boxes(map, 200, 300));

        Assert.InRange(box.Angle, -MathF.PI / 4, MathF.PI / 4);
        Assert.True(box.Height > 1.5f * box.Width);
    }

    [Fact]
    public void A_line_found_in_two_tiles_becomes_one_box()
    {
        // The same line, cut at a tile edge: the left part in one tile, the whole line in the next.
        var left = new RotatedBox(300, 100, 200, 30, 0, 0.9f);
        var whole = new RotatedBox(380, 101, 360, 30, 0.01f, 0.8f);
        var below = new RotatedBox(380, 160, 360, 30, 0, 0.9f);

        var merged = BoxMerger.Merge([left, whole, below]);

        Assert.Equal(2, merged.Count);
        var line = merged.Single(b => b.CenterY < 130);
        Assert.InRange(line.Width, 355, 370);
        Assert.Equal(0.9f, line.Score);
    }

    [Fact]
    public void Pieces_of_one_row_merge_but_columns_and_lines_of_a_turned_receipt_do_not()
    {
        // Meter drums: 5 boxes in a row with small gaps.
        var drums = Enumerable.Range(0, 5).Select(i => new RotatedBox(100 + i * 44, 50, 36, 40, 0, 0.9f));
        Assert.Single(BoxMerger.Merge(drums));

        // An item and its price far apart on one line stay apart.
        Assert.Equal(2, BoxMerger.Merge([new RotatedBox(100, 50, 120, 20, 0, 0.9f), new RotatedBox(400, 50, 60, 20, 0, 0.9f)]).Count);

        // A receipt turned by 90 degrees: upright lines next to each other are separate lines.
        var upright = Enumerable.Range(0, 4).Select(i => new RotatedBox(100 + i * 30, 200, 20, 200, 0, 0.9f));
        Assert.Equal(4, BoxMerger.Merge(upright).Count);
    }

    [Theory]
    [InlineData(1920, 3, 736)]
    [InlineData(1080, 2, 608)]
    [InlineData(960, 1, 960)]
    [InlineData(500, 1, 500)]
    [InlineData(3840, 5, 896)]
    public void Tiles_cover_the_side_with_the_fewest_tiles_and_at_least_15_percent_overlap(int length, int count, int size)
    {
        var (starts, tile) = OnnxTextRecognizer.Tiles(100, length);

        Assert.Equal(count, starts.Count);
        Assert.Equal(size, tile);
        Assert.True(tile <= OnnxTextRecognizer.TileSize);
        Assert.Equal(100, starts[0]);
        Assert.Equal(100 + length, starts[^1] + tile);
        for (var i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i - 1] + tile - starts[i] >= OnnxTextRecognizer.TileOverlap * tile, "overlap");
        }
    }

    [Fact]
    public void A_dark_line_is_stretched_and_a_clear_one_left_as_it_is()
    {
        using var dark = Filled(new SKColor(20, 20, 20), new SKColor(90, 90, 90));
        using var clear = Filled(SKColors.Black, SKColors.White);

        Levels.Stretch(dark, Levels.LineContrast);
        Levels.Stretch(clear, Levels.LineContrast);

        Assert.Equal(SKColors.Black, dark.GetPixel(0, 0));
        Assert.Equal(SKColors.White, dark.GetPixel(15, 0));
        Assert.Equal(SKColors.Black, clear.GetPixel(0, 0));
        Assert.Equal(SKColors.White, clear.GetPixel(15, 0));
    }

    private static SKBitmap Filled(SKColor left, SKColor right)
    {
        var bitmap = new SKBitmap(new SKImageInfo(16, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var x = 0; x < 16; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                bitmap.SetPixel(x, y, x < 8 ? left : right);
            }
        }

        return bitmap;
    }
}
