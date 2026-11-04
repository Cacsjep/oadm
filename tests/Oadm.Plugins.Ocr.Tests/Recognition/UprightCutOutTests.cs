using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// A row's cut-out shows the text upright, the way the reader saw it (docs/spec/ocr.md "The last 20 reads"): generated labels
/// turned in the picture, read for real on the CPU.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class UprightCutOutTests(RecognizerFixture fixture)
{
    private const string Value = "AT-4020-12";

    private const string Format = "@@-####-##";

    /// <summary>A label with <see cref="Value"/> in the middle of a picture, turned clockwise by <paramref name="degrees"/>.</summary>
    internal static byte[] Label(float degrees)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(171, 134, 90));
            canvas.Translate(640, 360);
            canvas.RotateDegrees(degrees);
            using var paper = new SKPaint { IsAntialias = true, Color = new SKColor(248, 248, 244) };
            canvas.DrawRect(-230, -60, 460, 120, paper);
            using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(24, 24, 28) };
            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var font = new SKFont(typeface, 54);
            canvas.DrawText(Value, -font.MeasureText(Value) / 2, 19, font, ink);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static readonly Area Whole = new() { Name = "Label", Box = new RelativeBox(0.05, 0.05, 0.9, 0.9), Expected = ExpectedText.WithFormat(Format) };

    /// <summary>The cut-out of the value read in <paramref name="jpeg"/>.</summary>
    private async Task<byte[]> CutOutAsync(byte[] jpeg)
    {
        var picture = await fixture.Recognizer.ReadAsync(jpeg, [Whole], CancellationToken.None);
        var value = new TextMatcher().Match(picture.LinesPerArea[0], Whole.Expected);
        Assert.Equal(Value, value.Match.Value);
        Assert.NotNull(value.MatchTurn);
        var cut = new PictureCutter().Cut(jpeg, value.MatchBox!.Value, value.MatchTurn);
        Assert.NotNull(cut);
        Assert.InRange(cut.Length, 1, OcrLimits.MaxCutoutBytes);
        return cut;
    }

    /// <summary>How far a cut-out lies from the upright one, both brought to one small size; 0 to 255.</summary>
    private static double Distance(byte[] cut, byte[] upright, bool turned)
    {
        const int Width = 96;
        const int Height = 16;
        var (a, b) = (Levels(cut, Width, Height), Levels(upright, Width, Height));
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[turned ? a.Length - 1 - i : i] - b[i]);
        }

        return sum / a.Length;
    }

    /// <summary>The brightness of a picture brought to one size.</summary>
    private static byte[] Levels(byte[] jpeg, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        using var small = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        return [.. small.Pixels.Select(p => p.Green)];
    }

    [Theory]
    [InlineData(180)]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(15)]
    [InlineData(-30)]
    [InlineData(165)]
    public async Task A_turned_label_comes_out_upright(float degrees)
    {
        var upright = await CutOutAsync(Label(0));

        var cut = await CutOutAsync(Label(degrees));

        using var bitmap = SKBitmap.Decode(cut);
        Assert.True(bitmap.Width > 2 * bitmap.Height, $"{bitmap.Width} x {bitmap.Height}: the text runs along the picture");
        Assert.True(Distance(cut, upright, turned: false) < Distance(cut, upright, turned: true), "the text is not upside down");

        // The crop holds the value's text: it reads as the value again.
        var again = await fixture.Recognizer.ReadAsync(cut, [new Area { Name = "Cut-out", Box = RelativeBox.Whole, Expected = Whole.Expected }], CancellationToken.None);
        Assert.Equal(Value, new TextMatcher().Match(again.LinesPerArea[0], Whole.Expected).Match.Value);
    }

    [Fact]
    public async Task The_overlay_box_stays_the_level_one()
    {
        var picture = await fixture.Recognizer.ReadAsync(Label(90), [Whole], CancellationToken.None);

        var box = new TextMatcher().Match(picture.LinesPerArea[0], Whole.Expected).MatchBox!.Value;

        Assert.True(box.Height * 720 > 2 * box.Width * 1280, "the box around a turned line is as tall as the line is long");
    }

    [Fact]
    public void Without_a_turn_the_cut_out_stays_level_and_a_bad_turn_never_fails()
    {
        var jpeg = Label(0);
        var box = new RelativeBox(0.3, 0.4, 0.4, 0.2);
        var cutter = new PictureCutter();

        var level = cutter.Cut(jpeg, box);

        Assert.Equal(level, cutter.Cut(jpeg, box, null));
        Assert.Equal(level, cutter.Cut(jpeg, box, new LineTurn(640, 360, 0, 0, 1)));
        Assert.NotNull(cutter.Cut(jpeg, box, new LineTurn(5000, 5000, 100, 40, 1))); // outside the picture: still a picture
        Assert.Null(cutter.Cut(jpeg, new RelativeBox(0, 0, 0, 0), null));
    }

    [Fact]
    public void A_long_turned_line_keeps_the_limits()
    {
        var cut = new PictureCutter().Cut(Label(0), RelativeBox.Whole, new LineTurn(640, 360, 1270, 300, 0.4f));

        Assert.NotNull(cut);
        Assert.InRange(cut.Length, 1, OcrLimits.MaxCutoutBytes);
        using var bitmap = SKBitmap.Decode(cut);
        Assert.InRange(Math.Max(bitmap.Width, bitmap.Height), 1, OcrLimits.MaxCutoutSide);
    }
}
