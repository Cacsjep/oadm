using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// Unchanged areas (docs/spec/ocr.md "Reading"): with <see cref="RecognitionOptions.ReuseUnchanged"/> an area whose pixels
/// barely changed gives its last reading again; a changed area, another reader, too many uses in a row or a reading without
/// the option read anew.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class UnchangedAreasTests(RecognizerFixture fixture)
{
    private static readonly Area[] Areas =
    [
        new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.1) },
        new() { Name = "Kind", Box = new RelativeBox(0.26, 0.63, 0.18, 0.155) },
    ];

    private static byte[] Box() => File.ReadAllBytes(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));

    private static RecognitionOptions Watching(string source) => new() { ReuseUnchanged = true, Source = source };

    [Fact]
    public async Task An_unchanged_area_gives_its_last_reading_again()
    {
        var recognizer = fixture.Recognizer;
        var source = Guid.NewGuid().ToString();
        var first = await recognizer.ReadDetailedAsync(Box(), Areas, Watching(source), CancellationToken.None);
        var before = recognizer.Unchanged.Reused;

        // The same scene encoded once more (other compression noise) still counts as unchanged.
        var second = await recognizer.ReadDetailedAsync(Reencoded(Box(), 80), Areas, Watching(source), CancellationToken.None);

        Assert.Equal(before + 2, recognizer.Unchanged.Reused);
        Assert.Equal("BARISSIMO", Assert.Single(second.Areas[0].Lines).Line.Text);
        Assert.Same(first.Areas[1], second.Areas[1]);
    }

    [Fact]
    public async Task A_changed_area_is_read_again_and_the_unchanged_one_is_not()
    {
        var recognizer = fixture.Recognizer;
        var source = Guid.NewGuid().ToString();
        await recognizer.ReadDetailedAsync(Box(), Areas, Watching(source), CancellationToken.None);
        var before = recognizer.Unchanged.Reused;

        // A hand over the brand: its area is read anew, the kind below gives its reading again.
        var covered = Covered(Box(), new SKRect(0.28f * 1920, 0.54f * 1080, 0.36f * 1920, 0.62f * 1080));
        var result = await recognizer.ReadDetailedAsync(covered, Areas, Watching(source), CancellationToken.None);

        Assert.Equal(before + 1, recognizer.Unchanged.Reused);
        Assert.DoesNotContain(result.Areas[0].Lines, l => l.Line.Text == "BARISSIMO");
    }

    [Fact]
    public async Task Other_readers_without_the_option_and_too_many_uses_read_anew()
    {
        var recognizer = fixture.Recognizer;
        var source = Guid.NewGuid().ToString();
        var jpeg = Box();
        await recognizer.ReadDetailedAsync(jpeg, Areas, Watching(source), CancellationToken.None);
        var before = recognizer.Unchanged.Reused;

        await recognizer.ReadDetailedAsync(jpeg, Areas, Watching(Guid.NewGuid().ToString()), CancellationToken.None);
        await recognizer.ReadDetailedAsync(jpeg, Areas, RecognitionOptions.Default, CancellationToken.None);
        Assert.Equal(before, recognizer.Unchanged.Reused);

        for (var i = 0; i < UnchangedAreas.MaxReuses + 1; i++)
        {
            await recognizer.ReadDetailedAsync(jpeg, Areas, Watching(source), CancellationToken.None);
        }

        // Ten uses in a row, then a real reading.
        Assert.Equal(before + 2 * UnchangedAreas.MaxReuses, recognizer.Unchanged.Reused);
    }

    [Fact]
    public void Noise_is_unchanged_a_changed_cell_is_not()
    {
        var before = Enumerable.Range(0, 64).Select(i => (float)(i * 3 % 255)).ToArray();
        var noise = before.Select((v, i) => v + (i % 2 == 0 ? 1f : -1f)).ToArray();
        var digit = (float[])before.Clone();
        digit[17] += 20;
        var brighter = before.Select(v => v + 3f).ToArray();

        Assert.True(UnchangedAreas.Unchanged(before, noise));
        Assert.False(UnchangedAreas.Unchanged(before, digit));
        Assert.False(UnchangedAreas.Unchanged(before, brighter));
        Assert.False(UnchangedAreas.Unchanged(before, before[..32]));
    }

    private static byte[] Reencoded(byte[] jpeg, int quality)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static byte[] Covered(byte[] jpeg, SKRect rect)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = new SKColor(120, 90, 70) };
            canvas.DrawRect(rect, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }
}
