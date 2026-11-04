using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The cost of one picture per case (docs/spec/ocr.md "GPU", measured speeds), on the CPU and, when there is one, with the
/// graphics card (Search on the card, the rest on the CPU): a fixed-place area, a reader with 4 areas, 20 fixed-place readers
/// at once and a Search over the whole picture in full HD, QHD and 4K (the box picture scaled up). The times go to the test
/// output as one table with the card's estimated session memory after each case (sample the card's real memory next to it:
/// each case logs its start time); the budgets only catch a hang or a large slowdown. The accuracy set on the card's route
/// goes to the output too.
/// </summary>
[Trait("Category", "Perf")]
public sealed class RecognitionBenchmarkTests(ITestOutputHelper output)
{
    private static readonly Area[] FixedPlace = [new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) }];

    private static readonly Area[] FourAreas =
    [
        new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.1) },
        new() { Name = "Kind", Box = new RelativeBox(0.26, 0.63, 0.18, 0.155) },
        new() { Name = "Machine", Box = new RelativeBox(0.245, 0.8, 0.105, 0.095) },
        new() { Name = "Strength", Box = new RelativeBox(0.352, 0.805, 0.085, 0.105) },
    ];

    private static readonly Area[] Search = [new() { Name = "Intensity", Box = new RelativeBox(0, 0, 1, 1), Expected = ExpectedText.WithFormat("#/##") }];

    [Fact]
    public Task Cpu() => RunAsync(useGraphicsCard: false);

    [Fact]
    public Task Graphics_card() => RunAsync(useGraphicsCard: true);

    private async Task RunAsync(bool useGraphicsCard)
    {
        await using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = useGraphicsCard });
        await recognizer.CardTry;
        if (useGraphicsCard && !recognizer.RunsOn.IsGraphicsCard)
        {
            output.WriteLine("No graphics card: " + recognizer.RunsOn.Text);
            return;
        }

        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));
        var qhd = Scaled(jpeg, 2560, 1440);
        var uhd = Scaled(jpeg, 3840, 2160);
        var rows = new List<(string Case, double Ms, double PerSecond)>();
        var cardBytes = new List<(string Case, long Bytes, int Sessions, int Evicted)>();
        void Card(string name)
        {
            if (recognizer.GpuBudget is { } budget)
            {
                cardBytes.Add((name, budget.Bytes, budget.Count, budget.Evicted));
            }
        }

        void Started(string name) => output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} start {name}"));
        var stages = new List<(string Case, Dictionary<Stage, double> Ms)>();

        var decode = Time(20, () => DecodeOnce(jpeg));
        rows.Add(("Decode full HD", decode, 1000 / decode));

        foreach (var (name, picture, areas, rounds) in new[]
        {
            ("Fixed place, 1 area", jpeg, FixedPlace, 30),
            ("Reader with 4 areas", jpeg, FourAreas, 20),
            ("Search, full HD", jpeg, Search, 8),
            ("Search, QHD", qhd, Search, 6),
            ("Search, 4K", uhd, Search, 4),
        })
        {
            Started(name);

            // The first readings load the sessions (and on DirectML each input size).
            for (var i = 0; i < 2; i++)
            {
                await recognizer.ReadDetailedAsync(picture, areas, RecognitionOptions.Default, CancellationToken.None);
            }

            StageClock.Reset();
            var ms = await TimeAsync(rounds, () => recognizer.ReadDetailedAsync(picture, areas, RecognitionOptions.Default, CancellationToken.None));
            rows.Add((name, ms, 1000 / ms));
            stages.Add((name, StageClock.Snapshot().ToDictionary(e => e.Key, e => e.Value / rounds)));
            Card(name);
        }

        Started("20 fixed-place readers at once");
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            for (var i = 0; i < 4; i++)
            {
                await recognizer.ReadAsync(jpeg, FixedPlace, CancellationToken.None);
            }
        }));
        var twenty = watch.Elapsed.TotalMilliseconds / 80;
        rows.Add(("20 fixed-place readers at once", twenty, 1000 / twenty));
        Card("20 fixed-place readers at once");
        Started("unchanged");

        // Watching a still scene: the areas' last readings used again (every 11th picture read anew).
        foreach (var (name, areas) in new[] { ("Fixed place, unchanged", FixedPlace), ("4 areas, unchanged", FourAreas) })
        {
            var options = new RecognitionOptions { ReuseUnchanged = true, Source = name };
            var ms = await TimeAsync(33, () => recognizer.ReadDetailedAsync(jpeg, areas, options, CancellationToken.None));
            rows.Add((name, ms, 1000 / ms));
        }

        watch.Restart();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async reader =>
        {
            var options = new RecognitionOptions { ReuseUnchanged = true, Source = "reader " + reader };
            for (var i = 0; i < 11; i++)
            {
                await recognizer.ReadDetailedAsync(jpeg, FixedPlace, options, CancellationToken.None);
            }
        }));
        var twentyUnchanged = watch.Elapsed.TotalMilliseconds / 220;
        rows.Add(("20 readers, unchanged", twentyUnchanged, 1000 / twentyUnchanged));

        output.WriteLine($"Runs on {recognizer.RunsOn.Name}");
        output.WriteLine($"{"Case",-32} {"ms per picture",15} {"pictures/s",11}");
        foreach (var (name, ms, perSecond) in rows)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-32} {ms,15:0.0} {perSecond,11:0.0}"));
        }

        output.WriteLine("Per stage, ms per picture (summed over threads):");
        foreach (var (name, ms) in stages)
        {
            output.WriteLine($"{name,-32} " + string.Join(", ", ms.Where(e => e.Key != Stage.Count && e.Value >= 0.05).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key} {e.Value:0.0}"))));
        }

        if (cardBytes.Count > 0)
        {
            output.WriteLine("The card's sessions after each case (estimated):");
            foreach (var (name, bytes, sessions, evicted) in cardBytes)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-32} {bytes / (1024.0 * 1024 * 1024),5:0.00} GB in {sessions} sessions, {evicted} freed by the cap"));
            }

            Assert.All(cardBytes, c => Assert.True(c.Bytes <= GpuSessionBudget.MaxBytes, $"{c.Case}: {c.Bytes}"));
        }

        Assert.All(rows, r => Assert.True(r.Ms < 20_000, $"{r.Case}: {r.Ms} ms"));
    }

    [Fact]
    public async Task Accuracy_set_with_the_graphics_card()
    {
        await using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = true });
        await recognizer.CardTry;
        if (!recognizer.RunsOn.IsGraphicsCard)
        {
            output.WriteLine("No graphics card: " + recognizer.RunsOn.Text);
            return;
        }

        var pictures = AccuracySet.Load();
        var watch = Stopwatch.StartNew();
        var (scores, misses) = await AccuracySet.ScoreAsync(recognizer, pictures, CancellationToken.None);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Accuracy set on {recognizer.RunsOn.Name}, {pictures.Count} pictures in {watch.Elapsed.TotalSeconds:0.0} s:"));
        output.WriteLine(AccuracySet.Table(scores));
        foreach (var miss in misses)
        {
            output.WriteLine("Miss: " + miss);
        }

        if (recognizer.GpuBudget is { } budget)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The card's sessions: {budget.Bytes / (1024.0 * 1024 * 1024):0.00} GB in {budget.Count}, {budget.Evicted} freed by the cap"));
            Assert.True(budget.Bytes <= GpuSessionBudget.MaxBytes);
        }

        // Search reads with the server text finder on the card, the rest as on the CPU: never below the CPU's baseline (the same
        // slack of one value per group as AccuracySetTests).
        var baseline = AccuracySet.Baseline();
        Assert.Equal(baseline.Count, scores.Count);
        foreach (var score in scores)
        {
            var floor = baseline[score.Group];
            var slack = 1.0 / score.Values;
            Assert.True(score.ExactRate >= floor.Exact - slack - 1e-9, $"{score.Group}: {score.ExactRate:P1} exact, baseline {floor.Exact:P1}");
            Assert.True(score.CharacterErrorRate <= floor.Cer + slack + 1e-9, $"{score.Group}: {score.CharacterErrorRate:P2} character errors, baseline {floor.Cer:P2}");
        }
    }

    /// <summary>The picture scaled to <paramref name="width"/> x <paramref name="height"/> (a camera of a higher resolution).</summary>
    private static byte[] Scaled(byte[] jpeg, int width, int height)
    {
        using var source = SKBitmap.Decode(jpeg);
        using var scaled = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void DecodeOnce(byte[] jpeg)
    {
        using var picture = Picture.Decode(jpeg);
    }

    private static double Time(int rounds, Action work)
    {
        work();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            work();
        }

        return watch.Elapsed.TotalMilliseconds / rounds;
    }

    private static async Task<double> TimeAsync(int rounds, Func<Task> work)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            await work();
        }

        return watch.Elapsed.TotalMilliseconds / rounds;
    }
}
