using System.Globalization;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using SkiaSharp;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// A receipt in the repository's Roboto (the same pixels on every platform) on a counter, in the middle of a full HD picture:
/// the articles left, their prices right in a column of their own, so the text finder gives two boxes per row.
/// </summary>
internal static class RowsReceipt
{
    /// <summary>The receipt's rows, as <c>Text read</c> gives them.</summary>
    public const string Rows =
        "CAFE CENTRAL\nHauptstrasse 12\n2x Espresso 5.80\n1x Croissant 2.90\n1x Water 1.90\nTOTAL EUR 10.60\nORDER 23F7H\nThank you!";

    private const float Cap = 22;

    /// <summary>A square around the receipt, large enough for any turn and read in one piece.</summary>
    public static RelativeBox Area { get; } = new((1920 - 760) / 2 / 1920.0, (1080 - 760) / 2 / 1080.0, 760 / 1920.0, 760 / 1080.0);

    /// <summary>The picture as JPEG, the receipt turned by <paramref name="degrees"/> clockwise.</summary>
    public static byte[] Picture(float degrees)
    {
        using var paper = Paper();
        using var scene = new SKBitmap(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(scene))
        {
            // A counter: a tone and its grain, nothing that reads as text.
            canvas.Clear(new SKColor(128, 96, 64));
            using var grain = new SKPaint { IsAntialias = true, StrokeWidth = 2 };
            for (var y = 0; y < 1080; y += 4)
            {
                grain.Color = new SKColor(0, 0, 0, (byte)(8 + ((y * 7) % 14)));
                canvas.DrawLine(0, y + (MathF.Sin(y * 0.05f) * 4), 1920, y + (MathF.Cos(y * 0.03f) * 4), grain);
            }

            canvas.Translate(960, 540);
            canvas.RotateDegrees(degrees);
            using var image = SKImage.FromBitmap(paper);
            canvas.DrawImage(image, -paper.Width / 2f, -paper.Height / 2f, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var data = scene.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static SKBitmap Paper()
    {
        var regular = AngleSweep.Typeface(AngleSweep.RobotoRegular);
        var bold = AngleSweep.Typeface(AngleSweep.RobotoBold);
        using var font = new SKFont(regular, Cap / AngleSweep.CapRatio(regular));
        using var large = new SKFont(bold, Cap * 1.3f / AngleSweep.CapRatio(bold));
        const int Width = 480;
        const float Pitch = Cap * 2.2f;
        const float PriceRight = Width - 30;
        var rows = Rows.Split('\n');
        var bitmap = new SKBitmap(Width, (int)(40 + (rows.Length * Pitch)), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(248, 247, 240));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 30, 35) };
        var y = 20 + Cap + ((Pitch - Cap) / 2);
        foreach (var row in rows)
        {
            // An article and its price: the price right-aligned in its column.
            var price = row.LastIndexOf(' ');
            if (price > 0 && char.IsDigit(row[^1]) && row[(price + 1)..].Contains('.', StringComparison.Ordinal))
            {
                canvas.DrawText(row[..price], 30, y, SKTextAlign.Left, font, paint);
                canvas.DrawText(row[(price + 1)..], PriceRight, y, SKTextAlign.Right, font, paint);
            }
            else
            {
                canvas.DrawText(row, 30, y, SKTextAlign.Left, row.StartsWith("ORDER", StringComparison.Ordinal) ? large : font, paint);
            }

            y += Pitch;
        }

        return bitmap;
    }
}

/// <summary>
/// Rows with the real models on the CPU (docs/spec/ocr.md "Several lines"): a receipt level, on its side, upside down and
/// turned by 15 degrees gives its rows in Text read, each article with its price; Text is the same on one line.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class RowsRecognitionTests(RecognizerFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(15)]
    public async Task A_receipt_gives_its_rows_in_text_read(int degrees)
    {
        var area = new Area { Name = "Receipt", Box = RowsReceipt.Area };

        var picture = await fixture.Recognizer.ReadAsync(RowsReceipt.Picture(degrees), [area], CancellationToken.None);
        var lines = picture.LinesPerArea[0];
        var match = TextMatcher.MatchAll(lines, ExpectedText.AnyText);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{degrees}°: {string.Join(" | ", lines.Select(l => $"{l.Text} {l.Confidence:0}"))}"));
        Assert.Equal(RowsReceipt.Rows, match.RawText);
        Assert.Equal(RowsReceipt.Rows.Replace('\n', ' '), match.Text);

        // The recognition hands its lines on in the same order, one per box found.
        Assert.Equal(match.Text, string.Join(' ', lines.Select(l => l.Text)));
    }
}
