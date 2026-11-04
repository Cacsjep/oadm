using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The speed of recognition (docs/spec/ocr.md "GPU" speed targets) on the machine that runs the tests, CPU and, when there is
/// one, the graphics card: one Search reader over a full HD picture and one fixed-place area, one picture after the other
/// like a watching reader. The measured times go to the output; the budgets only catch a hang or a large slowdown.
/// </summary>
[Trait("Category", "Perf")]
public sealed class RecognitionSpeedTests(ITestOutputHelper output)
{
    private const int Rounds = 10;

    [Fact]
    public async Task Search_and_fixed_place_on_the_cpu()
    {
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = false });
        var (search, fixedPlace) = await MeasureAsync(recognizer, RecognitionOptions.Default);
        var (accurate, _) = await MeasureAsync(recognizer, new RecognitionOptions { Accurate = true });
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Accurate on the CPU: Search {accurate:0} ms"));

        Assert.True(search < 20_000, $"{search} ms");
        Assert.True(fixedPlace < 5_000, $"{fixedPlace} ms");
    }

    [Fact]
    public async Task Search_and_fixed_place_on_the_graphics_card()
    {
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = true });
        await recognizer.CardTry;
        if (!recognizer.RunsOn.IsGraphicsCard)
        {
            output.WriteLine("No graphics card: " + recognizer.RunsOn.Text);
            return;
        }

        var (search, fixedPlace) = await MeasureAsync(recognizer, RecognitionOptions.Default);

        Assert.True(search < 5_000, $"{search} ms");
        Assert.True(fixedPlace < 2_000, $"{fixedPlace} ms");
    }

    [Fact]
    public async Task Twenty_fixed_place_readers_at_once()
    {
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = true });
        await recognizer.CardTry;
        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));
        Area[] area = [new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) }];
        await recognizer.ReadAsync(jpeg, area, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            for (var i = 0; i < 4; i++)
            {
                await recognizer.ReadAsync(jpeg, area, CancellationToken.None);
            }
        }));
        var perSecond = 80 / watch.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{recognizer.RunsOn.Name}: 20 fixed-place readers, {perSecond:0.0} pictures per second in all ({perSecond / 20:0.0} per reader)"));

        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"{watch.Elapsed}");
    }

    /// <summary>Milliseconds per picture: Search over full HD (format, whole picture) and one fixed-place area.</summary>
    private async Task<(double Search, double FixedPlace)> MeasureAsync(OnnxTextRecognizer recognizer, RecognitionOptions options)
    {
        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));
        Area[] search = [new() { Name = "Intensity", Expected = ExpectedText.WithFormat("#/##") }];
        Area[] fixedPlace = [new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) }];

        // The first reading loads the sessions.
        await recognizer.ReadDetailedAsync(jpeg, search, options, CancellationToken.None);
        await recognizer.ReadDetailedAsync(jpeg, fixedPlace, options, CancellationToken.None);

        var searchMs = await TimeAsync(() => recognizer.ReadDetailedAsync(jpeg, search, options, CancellationToken.None));
        var fixedMs = await TimeAsync(() => recognizer.ReadDetailedAsync(jpeg, fixedPlace, options, CancellationToken.None));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{recognizer.RunsOn.Name}{(options.Accurate ? ", Accurate" : string.Empty)}: Search full HD {searchMs:0} ms ({1000 / searchMs:0.0} per second), fixed place {fixedMs:0} ms ({1000 / fixedMs:0.0} per second)"));
        return (searchMs, fixedMs);
    }

    private static async Task<double> TimeAsync(Func<Task> read)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Rounds; i++)
        {
            await read();
        }

        return watch.Elapsed.TotalMilliseconds / Rounds;
    }
}
