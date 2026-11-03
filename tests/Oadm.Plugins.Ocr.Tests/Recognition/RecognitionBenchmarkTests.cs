using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The cost of one picture per case (docs/spec/ocr.md "GPU", measured speeds), on the CPU and, when there is one, the graphics
/// card: a fixed-place area, a reader with 4 areas, 20 fixed-place readers at once and a Search over the whole full HD
/// picture. The times go to the test output as one table; the budgets only catch a hang or a large slowdown.
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
        using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = useGraphicsCard });
        if (useGraphicsCard && !recognizer.RunsOn.IsGraphicsCard)
        {
            output.WriteLine("No graphics card: " + recognizer.RunsOn.Text);
            return;
        }

        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));
        var rows = new List<(string Case, double Ms, double PerSecond)>();
        var stages = new List<(string Case, Dictionary<Stage, double> Ms)>();

        var decode = Time(20, () => DecodeOnce(jpeg));
        rows.Add(("Decode full HD", decode, 1000 / decode));

        foreach (var (name, areas, rounds) in new[] { ("Fixed place, 1 area", FixedPlace, 30), ("Reader with 4 areas", FourAreas, 20), ("Search, full HD", Search, 8) })
        {
            // The first readings load the sessions (and on DirectML each input size).
            for (var i = 0; i < 2; i++)
            {
                await recognizer.ReadDetailedAsync(jpeg, areas, RecognitionOptions.Default, CancellationToken.None);
            }

            StageClock.Reset();
            var ms = await TimeAsync(rounds, () => recognizer.ReadDetailedAsync(jpeg, areas, RecognitionOptions.Default, CancellationToken.None));
            rows.Add((name, ms, 1000 / ms));
            stages.Add((name, StageClock.Snapshot().ToDictionary(e => e.Key, e => e.Value / rounds)));
        }

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

        Assert.All(rows, r => Assert.True(r.Ms < 20_000, $"{r.Case}: {r.Ms} ms"));
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
