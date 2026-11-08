using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;

using SkiaSharp;

namespace Oadm.Plugins.Vision.Tests.Inference;

/// <summary>
/// The fits and scalings of the model input (docs/spec/vision-center.md "Models", "Tests"): a box drawn in a known place of
/// the picture is found in the input tensor and comes back in the same place for each fit.
/// </summary>
public sealed class FitTests
{
    private const int Input = 320;

    /// <summary>A black 400 x 200 picture with a white box at 25 % / 25 %, 10 % wide, 20 % high.</summary>
    private static SKImage Picture()
    {
        using var bitmap = new SKBitmap(400, 200);
        bitmap.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(SKRect.Create(100, 50, 40, 40), white);
        }

        return SKImage.FromBitmap(bitmap);
    }

    [Theory]
    [InlineData(ModelFit.Letterbox)]
    [InlineData(ModelFit.KeepShapePad)]
    [InlineData(ModelFit.Stretch)]
    public void A_box_comes_back_where_it_was_drawn(ModelFit fit)
    {
        using var picture = Picture();
        var tensor = new float[3 * Input * Input];

        var transform = Preprocess.Write(picture, null, fit, ModelScaling.ZeroToOne, Input, Input, tensor);

        // The white pixels of the red channel are the box in input pixels.
        int x1 = Input, y1 = Input, x2 = -1, y2 = -1;
        for (var y = 0; y < Input; y++)
        {
            for (var x = 0; x < Input; x++)
            {
                if (tensor[(y * Input) + x] > 0.5f)
                {
                    (x1, y1, x2, y2) = (Math.Min(x1, x), Math.Min(y1, y), Math.Max(x2, x + 1), Math.Max(y2, y + 1));
                }
            }
        }

        var box = transform.ToPicture(x1, y1, x2, y2)!;
        const double Tolerance = 0.012; // a pixel or so of the input's scale
        Assert.True(Math.Abs(box.X - 0.25) < Tolerance && Math.Abs(box.Y - 0.25) < Tolerance && Math.Abs(box.Width - 0.1) < Tolerance && Math.Abs(box.Height - 0.2) < Tolerance, $"{fit}: {box}");
    }

    [Fact]
    public void Letterbox_centres_the_picture_between_grey_bars()
    {
        using var picture = Picture();
        var tensor = new float[3 * Input * Input];
        var transform = Preprocess.Write(picture, null, ModelFit.Letterbox, ModelScaling.ZeroToOne, Input, Input, tensor);

        Assert.Equal((0.8, 0.0, 80.0), (transform.ScaleX, transform.OffsetX, transform.OffsetY));
        Assert.Equal(114 / 255f, tensor[0], 3); // top bar
        Assert.Equal(114 / 255f, tensor[((Input - 1) * Input) + Input - 1], 3); // bottom bar
        Assert.Equal(0, tensor[(160 * Input) + 10], 3); // the black picture
    }

    [Fact]
    public void Keep_shape_and_pad_puts_the_picture_top_left_on_black()
    {
        using var picture = Picture();
        var tensor = new float[3 * Input * Input];
        var transform = Preprocess.Write(picture, null, ModelFit.KeepShapePad, ModelScaling.ImageNet, Input, Input, tensor);

        Assert.Equal((0.8, 0.0, 0.0), (transform.ScaleX, transform.OffsetX, transform.OffsetY));

        // Black after ImageNet scaling: -mean / deviation per channel.
        var bottom = ((Input - 1) * Input) + 10;
        Assert.Equal(-0.485f / 0.229f, tensor[bottom], 3);
        Assert.Equal(-0.456f / 0.224f, tensor[(Input * Input) + bottom], 3);
        Assert.Equal(-0.406f / 0.225f, tensor[(2 * Input * Input) + bottom], 3);
    }

    [Fact]
    public void Bgr_0_to_255_puts_blue_first_and_keeps_the_pixel_values()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, new SKColor(200, 100, 50));
        bitmap.SetPixel(1, 0, new SKColor(0, 0, 255));
        var tensor = new float[6];

        Preprocess.WriteTensor(bitmap, tensor, ModelScaling.ZeroTo255Bgr);

        Assert.Equal([50f, 255, 100, 0, 200, 0], tensor);
    }

    [Fact]
    public void Stretch_scales_each_axis_on_its_own()
    {
        var transform = FitTransform.Of(ModelFit.Stretch, 400, 200, Input, Input);
        Assert.Equal((0.8, 1.6, 0.0, 0.0), (transform.ScaleX, transform.ScaleY, transform.OffsetX, transform.OffsetY));
    }

    [Fact]
    public void Boxes_are_clipped_to_the_picture_and_empty_ones_dropped()
    {
        var transform = FitTransform.Of(ModelFit.Letterbox, 400, 200, Input, Input);
        var clipped = transform.ToPicture(-50, 70, 100, 120)!;
        Assert.Equal(0, clipped.X);
        Assert.Null(transform.ToPicture(10, 0, 50, 70)); // inside the top bar only
    }

    [Fact]
    public void A_region_is_cut_out_and_stretched_to_the_input()
    {
        using var picture = Picture();
        var tensor = new float[3 * 32 * 32];
        Preprocess.Write(picture, new SKRectI(105, 55, 135, 85), ModelFit.Stretch, ModelScaling.ZeroToOne, 32, 32, tensor);
        Assert.All(tensor[..(32 * 32)], v => Assert.True(v > 0.9f)); // the white box fills the input
    }
}
