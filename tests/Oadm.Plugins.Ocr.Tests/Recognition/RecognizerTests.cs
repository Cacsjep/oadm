using Microsoft.Extensions.Logging;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>One CPU recognizer with the real models for every test of the collection (loading the models takes a moment).</summary>
public sealed class RecognizerFixture : IDisposable
{
    public RecognizerFixture()
    {
        Recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = ModelFolder, UseGraphicsCard = false });
    }

    /// <summary>The models copied next to the tests (the plugin's Models folder).</summary>
    public static string ModelFolder => Path.Combine(AppContext.BaseDirectory, "Models");

    internal OnnxTextRecognizer Recognizer { get; }

    public void Dispose() => Recognizer.Dispose();
}

[CollectionDefinition(Name)]
public sealed class RecognitionGroup : ICollectionFixture<RecognizerFixture>
{
    public const string Name = "Recognition";
}

/// <summary>
/// Reading with the real models on the CPU (docs/spec/ocr.md "Reading" and "Several lines"): the coffee box recorded read-only
/// from 10.0.0.48, lines found, their text and order, several areas, Search with a format, a slanted and a turned line, text
/// too small, and what runs the recognition.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class RecognizerTests(RecognizerFixture fixture)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AccuracySet.Folder, name));

    [Fact]
    public async Task The_lines_of_the_coffee_box_are_read_top_to_bottom()
    {
        var area = new Area { Name = "Box", Box = new RelativeBox(0.2, 0.45, 0.3, 0.55) };

        var result = await fixture.Recognizer.ReadDetailedAsync(Fixture("box-1920x1080.jpg"), [area], RecognitionOptions.Default, None);

        Assert.Equal(new Resolution(1920, 1080), result.Resolution);
        var lines = Assert.Single(result.Areas).Lines;
        var texts = lines.Select(l => l.Line.Text).ToList();
        Assert.True(texts.IndexOf("BARISSIMO") >= 0, string.Join(" | ", texts));
        Assert.True(texts.IndexOf("BARISSIMO") < texts.IndexOf("DECAF-"), string.Join(" | ", texts));
        Assert.True(texts.IndexOf("DECAF-") < texts.IndexOf("FEINATO"), string.Join(" | ", texts));
        Assert.Contains("INTENSITY", texts);
        Assert.Contains("4/12", texts);
        Assert.All(lines, l =>
        {
            Assert.InRange(l.Line.Confidence, 0, 100);
            Assert.True(l.MeanConfidence >= OcrLimits.MinConfidence);
            Assert.True(l.Line.Box.IsValid);
            Assert.InRange(l.Line.Box.X, 0.19, 0.51);
            Assert.Equal(l.Line.Text, string.Concat(l.Characters.Select(c => c.Text)));
        });
    }

    [Fact]
    public async Task Each_area_gets_its_own_lines_in_the_order_asked()
    {
        Area[] areas =
        [
            new() { Name = "Strength", Box = new RelativeBox(0.352, 0.805, 0.085, 0.105) },
            new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) },
            new() { Name = "Window", Box = new RelativeBox(0.0, 0.0, 0.2, 0.3) },
        ];

        var picture = await fixture.Recognizer.ReadAsync(Fixture("box-1280x720.jpg"), areas, None);

        Assert.Equal(3, picture.LinesPerArea.Count);
        Assert.Equal(["INTENSITY", "4/12"], picture.LinesPerArea[0].Select(l => l.Text));
        Assert.Equal(["BARISSIMO"], picture.LinesPerArea[1].Select(l => l.Text));
        Assert.Empty(picture.LinesPerArea[2]);
    }

    [Fact]
    public async Task An_area_over_the_whole_picture_reads_it_all()
    {
        var picture = await fixture.Recognizer.ReadAsync(Fixture("box-1280x720.jpg"), [new Area { Name = "All", Box = RelativeBox.Whole }], None);

        var lines = Assert.Single(picture.LinesPerArea);
        Assert.Contains(lines, l => l.Text == "BARISSIMO");
        Assert.InRange(lines.Count, 1, OcrLimits.MaxTextBoxes);
    }

    [Fact]
    public async Task Search_over_the_whole_picture_finds_the_format()
    {
        var area = new Area { Name = "Intensity", Expected = ExpectedText.WithFormat("#/##") };

        var result = await fixture.Recognizer.ReadDetailedAsync(Fixture("box-1920x1080.jpg"), [area], RecognitionOptions.Default, None);

        var match = Assert.Single(result.Areas[0].Lines, l => l.FormatMatch is not null);
        Assert.Equal("4/12", match.FormatMatch);
        Assert.True(match.Line.Confidence > 90, match.Line.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(-9)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_slanted_or_turned_line_is_read_level(int degrees)
    {
        var jpeg = TurnedSign(degrees, 1f);

        var picture = await fixture.Recognizer.ReadAsync(jpeg, [new Area { Name = "All", Box = RelativeBox.Whole }], None);

        Assert.Contains(picture.LinesPerArea[0], l => l.Text == "LOADING DOCK 3");
    }

    [Fact]
    public async Task Text_too_small_is_left_out_with_the_hint()
    {
        // The sign at an eighth of its size (letters about 6 pixels high), a fixed-place area around it.
        var jpeg = TurnedSign(0, 0.125f);
        var around = new Area { Name = "Sign", Box = new RelativeBox(0.44, 0.46, 0.12, 0.08) };

        var result = await fixture.Recognizer.ReadDetailedAsync(jpeg, [around], RecognitionOptions.Default, None);

        var area = Assert.Single(result.Areas);
        Assert.True(area.Lines.Count == 0, string.Join(" | ", area.Lines.Select(l => l.Line.Text)));
        Assert.True(area.TooSmall > 0);
        Assert.Equal("Text too small. Draw a smaller area or choose a higher resolution.", area.Hint);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_picture_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Recognizer.ReadAsync("<html>401</html>"u8.ToArray(), [new Area { Name = "All" }], None));
    }

    [Fact]
    public async Task Without_areas_nothing_is_read_and_the_picture_is_not_decoded()
    {
        // Text is read only inside areas: not even an answer that is no picture is looked at.
        var picture = await fixture.Recognizer.ReadAsync("<html>401</html>"u8.ToArray(), [], None);

        Assert.Empty(picture.LinesPerArea);
        Assert.False(picture.TooManyTexts);
    }

    [Fact]
    public void At_most_30_text_boxes_are_read_the_strongest_first()
    {
        // 40 boxes over two areas with rising scores: the 30 strongest stay, in their order, and the reading says so.
        var boxes = Enumerable.Range(0, 40).Select(i => (i % 2, new RotatedBox(10 + i, 10, 50, 20, 0, 0.5f + (i / 100f)))).ToList();

        var (kept, dropped) = OnnxTextRecognizer.Strongest(boxes, OcrLimits.MaxTextBoxes);

        Assert.True(dropped);
        Assert.Equal(30, kept.Count);
        Assert.Equal(Enumerable.Range(10, 30).Select(i => 10f + i), kept.Select(k => k.Box.CenterX));
        var (all, none) = OnnxTextRecognizer.Strongest(boxes.Take(30).ToList(), OcrLimits.MaxTextBoxes);
        Assert.False(none);
        Assert.Equal(30, all.Count);
        Assert.True(new RecognitionResult(new Resolution(1920, 1080), []) { TooManyTexts = true }.ToPicture().TooManyTexts);
    }

    [Fact]
    public async Task The_reader_has_a_class_for_every_entry_of_its_character_list()
    {
        using var set = new ModelSet(RecognizerFixture.ModelFolder, ModelChoice.Mobile, 1);
        using var line = new SKBitmap(new SKImageInfo(100, ModelSet.LineHeight, SKColorType.Rgba8888, SKAlphaType.Premul));

        var scores = Assert.Single(await set.RecognizeAsync([line], None));

        Assert.Equal(set.Characters.ClassCount, scores.Classes);
        Assert.Equal(838, scores.Classes);
        Assert.Equal(1f, scores.Frame(0).ToArray().Sum(), 2);
    }

    /// <summary>The card as a cancelled reading meets it: the reading goes on, its lines stay blank (<see cref="CardModels"/>).</summary>
    private sealed class CancelledOnTheCard(IModelRunner inner, CancellationTokenSource cancel) : IModelRunner
    {
        public CharacterList Characters => inner.Characters;

        public ITextFinder Search => this;

        public bool SearchOnCard => false;

        public bool BatchTiles => false;

        public int DetectionSide(int pixels) => inner.DetectionSide(pixels);

        public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
            inner.DetectAsync(input, count, height, width, CancellationToken.None);

        public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => Task.FromResult(new float[lines.Count]);

        public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
        {
            var read = await inner.RecognizeAsync(lines, CancellationToken.None);
            await cancel.CancelAsync();
            return [.. read.Select(s => new LineScores(new float[s.Probabilities.Length], s.Frames, s.Classes))];
        }
    }

    [Fact]
    public async Task A_reading_cancelled_on_the_graphics_card_is_no_empty_reading()
    {
        var area = new Area { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) };
        var characters = CharacterList.Load(Path.Combine(RecognizerFixture.ModelFolder, ModelFiles.RecognitionLatinCharacters));
        using var cancel = new CancellationTokenSource();
        var runner = new CancelledOnTheCard(new CpuRunner(fixture.Recognizer.CpuWorkers, characters, false), cancel);
        using var picture = Picture.Decode(Fixture("box-1920x1080.jpg"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OnnxTextRecognizer.ReadWithAsync(picture, [area], runner, cancel.Token));
    }

    [Fact]
    public async Task With_the_setting_off_the_graphics_card_is_tried_only_when_it_is_turned_on()
    {
        var tries = 0;
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions
        {
            ModelDirectory = RecognizerFixture.ModelFolder,
            UseGraphicsCard = false,
            ProcessorCount = 2,
            FindGraphicsCard = (_, _) =>
            {
                tries++;
                return null; // no real card in a test
            },
        });

        Assert.Equal(0, tries);
        await recognizer.SetUseGpu(false);
        await recognizer.CardTry;
        Assert.Equal(0, tries);

        await recognizer.SetUseGpu(true);
        Assert.Equal(1, tries);
        Assert.False(recognizer.GpuAvailable);
        Assert.False(recognizer.OnGpu);
    }

    [Fact]
    public void Without_the_graphics_card_the_cpu_runs_with_half_the_cores()
    {
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = false, ProcessorCount = 8 });

        Assert.False(recognizer.RunsOn.IsGraphicsCard);
        Assert.Equal("CPU (8 cores)", recognizer.RunsOn.Name);
        Assert.Equal("Runs on: CPU (8 cores)", recognizer.RunsOn.Text);
        Assert.Equal(4, recognizer.Workers);
        Assert.Equal(4, recognizer.CpuWorkers.Slots);
    }

    [Fact]
    public async Task A_graphics_card_that_fails_falls_back_to_the_cpu_with_one_log_line()
    {
        var logger = new ListLogger();
        var broken = Path.Combine(Path.GetTempPath(), $"oadm-ocr-{Guid.NewGuid():N}.onnx");
        await File.WriteAllTextAsync(broken, "not a model");
        try
        {
            using var recognizer = new OnnxTextRecognizer(
                new OcrEngineOptions
                {
                    ModelDirectory = RecognizerFixture.ModelFolder,
                    UseGraphicsCard = false,
                    ProcessorCount = 2,
                    CpuModels = ModelChoice.Mobile with { Detection = broken },
                    FindGraphicsCard = (_, _) => ExecutionTarget.Cpu, // no real card in a test: the same path with a model that does not load
                },
                logger);
            var before = logger.Lines.Count;

            await recognizer.SetUseGpu(true);

            Assert.False(recognizer.GpuAvailable);
            var line = Assert.Single(logger.Lines.Skip(before));
            Assert.StartsWith("Text recognition runs on the CPU", line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    /// <summary>The "LOADING DOCK 3" sign of the accuracy set, cut out, scaled and turned onto a grey 1280 x 720 picture.</summary>
    private static byte[] TurnedSign(int degrees, float scale)
    {
        using var source = SKBitmap.Decode(Fixture("sign-01.jpg"));
        var set = AccuracySet.Load().Single(p => p.File == "sign-01.jpg");
        var box = set.Areas[0].Box;
        var cut = SKRect.Create((float)box[0] * source.Width, (float)box[1] * source.Height, (float)box[2] * source.Width, (float)box[3] * source.Height);
        using var target = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(new SKColor(128, 128, 128));
            canvas.Translate(640, 360);
            canvas.RotateDegrees(degrees);
            canvas.Scale(scale);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, cut, SKRect.Create(-cut.Width / 2, -cut.Height / 2, cut.Width, cut.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var data = target.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    internal sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
