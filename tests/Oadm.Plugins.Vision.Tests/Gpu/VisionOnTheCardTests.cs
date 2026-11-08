using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.RealModels;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Gpu;

/// <summary>
/// The real <see cref="OnnxInference"/> with Use GPU on and the real provider (docs/spec/vision-center.md "Load", "Tests"):
/// each model passes its card test, <see cref="OnnxInference.OnGpu"/> and <see cref="OnnxInference.RunsOn"/> name the
/// card, the card's detections are the CPU's (same classes, overlap of 0.9 or more, scores within 5 points), the time per
/// picture on the card and on one CPU thread (median of warm runs, written out), Use GPU off moves the calls back to the
/// CPU, on again to the card. Category Gpu: only on purpose (<c>manage test gpu</c>), one test process at a time.
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", GpuCard.Category)]
public sealed class VisionOnTheCardTests(RealModelFixture fixture, ITestOutputHelper output)
{
    private const int Runs = 9;

    /// <summary>The silver sedan and the white station wagon of COCO val2017 97679 (COCO's boxes).</summary>
    private static readonly RelativeBox[] Cars = [new(0.162, 0.332, 0.653, 0.451), new(0.560, 0.332, 0.429, 0.356)];

    // YOLO11n at opset 19 (Kalray's export, 640 x 640); the 16-bit export the same way.
    [GpuFact(RealModelFiles.Yolo11nNoNames)]
    public Task Yolo11n_detects_on_the_card_as_on_the_CPU() => DetectsAsync(RealModelFiles.Yolo11nNoNames, 0.25);

    [GpuFact(RealModelFiles.Yolo11nFp16)]
    public Task Yolo11n_in_16_bit_floats_detects_on_the_card_as_on_the_CPU() => DetectsAsync(RealModelFiles.Yolo11nFp16, 0.25);

    /// <summary>
    /// Ultralytics' own YOLO11n export (v8.4.0, free input) is opset 22: ONNX Runtime 1.24's DirectML has no Conv and MaxPool
    /// of opset 22, so 91 of its nodes run on the CPU between the card's and its card test takes about 150 to 165 ms against
    /// about 105 on a CPU thread, at the edge of the rule (1.5 times): it runs where the rule says from the logged times,
    /// with the CPU's detections either way. The same model at opset 19 takes 3 ms on the card.
    /// </summary>
    [GpuFact(RealModelFiles.Yolo11n)]
    public async Task Yolo11n_of_opset_22_runs_where_its_card_test_says()
    {
        var model = await fixture.ModelAsync(RealModelFiles.Yolo11n);
        var log = new List<string>();
        using var card = Card(RealModelFiles.Yolo11n, model, out _, log);
        await card.SetUseGpuAsync(true);
        using var frame = card.Decode(await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.Skaters)));

        var found = Plain(await card.DetectAsync(model, frame, 0.25, CancellationToken.None));
        var onCpu = Plain(await fixture.Inference.DetectAsync(model, frame, 0.25, CancellationToken.None));

        Assert.True(GpuSupport.Compare(onCpu, found, 0.25).Same);
        string line;
        lock (log)
        {
            line = log.Single(l => l.Contains("the model yolo11n ", StringComparison.Ordinal));
        }

        var times = System.Text.RegularExpressions.Regex.Matches(line, @"([\d.]+) ms").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{model.Name}: {line}; runs on {card.RunsOn}"));
        Assert.Equal(2, times.Length);
        Assert.Equal(EngineRules.Worth(times[0], times[1]), card.OnGpu);
        if (SessionFactory.GraphicsCardTarget() == ExecutionTarget.DirectMl)
        {
            Assert.True(times[0] > times[1], "With DirectML the opset 22 export is slower on the card than on one CPU thread.");
        }
    }

    [GpuFact(RealModelFiles.Yolo26n)]
    public Task Yolo26n_detects_on_the_card_as_on_the_CPU() => DetectsAsync(RealModelFiles.Yolo26n, 0.25);

    [GpuFact(RealModelFiles.RfDetrMedium)]
    public Task RfDetr_medium_detects_on_the_card_as_on_the_CPU() => DetectsAsync(RealModelFiles.RfDetrMedium, 0.5);

    [GpuFact(RealModelFiles.ResNet50, RealModelFiles.ResNet50Config, RealModelFiles.TwoCars)]
    public async Task ResNet50_classifies_on_the_card_as_on_the_CPU()
    {
        var model = await fixture.ModelAsync(RealModelFiles.ResNet50, ClassificationTests.ImageNetLabels());
        using var card = Card(RealModelFiles.ResNet50, model, out var calls);
        await card.SetUseGpuAsync(true);
        Assert.True(card.GpuAvailable, "The graphics card's provider was not found.");
        using var frame = card.Decode(await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.TwoCars)));

        var onCard = await card.ClassifyAsync(model, frame, Cars, CancellationToken.None);
        AssertOnTheCard(card, model);
        var onCpu = await fixture.Inference.ClassifyAsync(model, frame, Cars, CancellationToken.None);
        output.WriteLine("CPU: " + Names(onCpu) + "; card: " + Names(onCard));
        Assert.Equal(onCpu.Select(r => r.ClassId), onCard.Select(r => r.ClassId));
        Assert.All(onCpu.Zip(onCard), p => Assert.InRange(Math.Abs(p.First.Score - p.Second.Score), 0, GpuSupport.MaxScoreDiff));

        var cpuAtProbe = calls.Cpu;
        var cardMs = await MedianAsync(() => card.ClassifyAsync(model, frame, Cars[..1], CancellationToken.None));
        var cpuMs = await MedianAsync(() => fixture.Inference.ClassifyAsync(model, frame, Cars[..1], CancellationToken.None));
        Assert.Equal(cpuAtProbe, calls.Cpu);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{model.Name}: {cardMs:F1} ms per region on the card, {cpuMs:F1} ms on one CPU thread (median of {Runs}); card test {card.GpuMsOf(model.Id)} ms"));

        await BackToTheCpuAsync(card, calls, () => card.ClassifyAsync(model, frame, Cars, CancellationToken.None), found => Assert.Equal(onCpu.Select(r => r.ClassId), found.Select(r => r.ClassId)));
    }

    [GpuFact(RealModelFiles.Yolo11n)]
    public async Task A_fresh_process_with_Use_GPU_off_never_loads_DirectML()
    {
        var (exit, result) = await GpuSupport.RunChildAsync(output, TimeSpan.FromMinutes(3), "cpu-only");

        Assert.Equal(0, exit);
        Assert.NotNull(result);
        var r = result.RootElement;
        Assert.False(r.GetProperty("onGpu").GetBoolean());
        Assert.False(r.GetProperty("gpuAvailable").GetBoolean());
        Assert.True(r.GetProperty("found").GetInt32() >= 4, "YOLO11n found less than the two skaters and two cars.");
        var modules = r.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Contains(modules, m => Path.GetFileName(m).Contains("onnxruntime", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modules, m => Path.GetFileName(m).Contains("DirectML", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modules, m => Path.GetFileName(m).Contains("providers_cuda", StringComparison.OrdinalIgnoreCase));
    }

    private async Task DetectsAsync(string file, double minScore)
    {
        var model = await fixture.ModelAsync(file);
        using var card = Card(file, model, out var calls);
        Assert.False(card.GpuAvailable);
        await card.SetUseGpuAsync(true);
        Assert.True(card.GpuAvailable, "The graphics card's provider was not found.");

        foreach (var photo in new[] { RealModelFiles.Skaters, RealModelFiles.SkateBowl })
        {
            using var frame = card.Decode(await File.ReadAllBytesAsync(RealModelFiles.PathOf(photo)));
            var onCard = Plain(await card.DetectAsync(model, frame, minScore, CancellationToken.None));
            AssertOnTheCard(card, model);
            var onCpu = Plain(await fixture.Inference.DetectAsync(model, frame, minScore, CancellationToken.None));
            var comparison = GpuSupport.Compare(onCpu, onCard, minScore);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{model.Name} on {photo}: CPU {onCpu.Count}, card {onCard.Count} objects; {comparison.Pairs} pairs, smallest overlap {comparison.MinIoU:F4}, largest score difference {comparison.MaxScoreDiff:F4}"));
            foreach (var (c, g) in onCpu.OrderByDescending(d => d.Score).Zip(onCard.OrderByDescending(d => d.Score)))
            {
                output.WriteLine($"  CPU {c} | card {g}");
            }

            Assert.True(comparison.Same, comparison.Diff);
            Assert.Equal(onCpu.Where(d => d.Score >= minScore + GpuSupport.NearMinimum).Select(d => d.ClassName).Order(), onCard.Where(d => d.Score >= minScore + GpuSupport.NearMinimum).Select(d => d.ClassName).Order());

            var cpuAtProbe = calls.Cpu;
            var cardMs = await MedianAsync(() => card.DetectAsync(model, frame, minScore, CancellationToken.None));
            var cpuMs = await MedianAsync(() => fixture.Inference.DetectAsync(model, frame, minScore, CancellationToken.None));
            Assert.Equal(cpuAtProbe, calls.Cpu); // no call of the card's inference went to the CPU
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{model.Name} ({model.InputWidth} x {model.InputHeight}) on {photo}: {cardMs:F1} ms per picture on the card, {cpuMs:F1} ms on one CPU thread (median of {Runs} warm runs); card test {card.GpuMsOf(model.Id)} ms, model check CPU {model.CpuMs} ms"));
        }

        using var again = card.Decode(await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.Skaters)));
        var reference = Plain(await fixture.Inference.DetectAsync(model, again, minScore, CancellationToken.None));
        await BackToTheCpuAsync(card, calls, () => card.DetectAsync(model, again, minScore, CancellationToken.None), found => Assert.True(GpuSupport.Compare(reference, Plain(found), minScore).Same));
    }

    /// <summary>Use GPU off: the next call runs on a CPU worker, RunsOn says the CPU; on again: the card.</summary>
    private async Task BackToTheCpuAsync<T>(OnnxInference card, Calls calls, Func<Task<T>> call, Action<T> check)
    {
        await card.SetUseGpuAsync(false);
        Assert.False(card.OnGpu);
        Assert.Equal(EngineRules.CpuText(Environment.ProcessorCount), card.RunsOn);
        var (cpu, onCard) = (calls.Cpu, calls.Card);
        check(await call());
        Assert.Equal(onCard, calls.Card);
        Assert.True(calls.Cpu > cpu, "With Use GPU off the call did not run on a CPU worker.");

        await card.SetUseGpuAsync(true);
        (cpu, onCard) = (calls.Cpu, calls.Card);
        check(await call());
        Assert.True(card.OnGpu);
        Assert.Equal(cpu, calls.Cpu);
        Assert.True(calls.Card > onCard, "With Use GPU on again the call did not run on the card.");
        output.WriteLine("Use GPU off: the call ran on the CPU; on again: on the card.");
    }

    private void AssertOnTheCard(OnnxInference card, VisionModel model)
    {
        Assert.True(card.OnGpu, $"{model.Name} did not pass its test on the card (see the log lines).");
        Assert.NotNull(card.GpuMsOf(model.Id));
        var name = SessionFactory.GraphicsCardName(SessionFactory.GraphicsCardTarget()!.Value);
        Assert.Equal(EngineRules.CardText(Environment.ProcessorCount, name), card.RunsOn);
        if (GpuCard.Name is { } system)
        {
            Assert.Contains(system, card.RunsOn, StringComparison.OrdinalIgnoreCase);
        }

        output.WriteLine("Runs on: " + card.RunsOn);
    }

    /// <summary>An inference with Use GPU off at the start and the real provider; counts its calls on the card and on the CPU.</summary>
    private OnnxInference Card(string file, VisionModel model, out Calls calls, List<string>? log = null)
    {
        var path = fixture.LibraryOf(file).FileOf(model.Id) ?? throw new InvalidOperationException("No file.");
        var counted = new Calls();
        calls = counted;
        return new OnnxInference(new InferenceOptions { FileOf = id => id == model.Id ? path : null }, new TestOutputLog(output, "vision", log))
        {
            BeforeCardCall = () => Interlocked.Increment(ref counted.CardCalls),
            BeforeCpuCall = () =>
            {
                Interlocked.Increment(ref counted.CpuCalls);
                return Task.CompletedTask;
            },
        };
    }

    private static async Task<double> MedianAsync<T>(Func<Task<T>> call)
    {
        for (var i = 0; i < 3; i++)
        {
            await call(); // warm, the card's clock up
        }

        var times = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await call();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return GpuSupport.Median(times);
    }

    private static List<Found> Plain(IReadOnlyList<Detection> found) =>
        [.. found.Select(d => new Found(d.ClassName, d.Score, d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height))];

    private static string Names(IEnumerable<ClassResult> results) =>
        string.Join("; ", results.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.ClassName} (id {r.ClassId}) {r.Score:P1}")));

    private sealed class Calls
    {
        public int CardCalls;
        public int CpuCalls;

        public int Card => Volatile.Read(ref CardCalls);

        public int Cpu => Volatile.Read(ref CpuCalls);
    }
}
