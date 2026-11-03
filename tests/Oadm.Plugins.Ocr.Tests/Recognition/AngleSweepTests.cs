using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The measured rotation and perspective sweep (docs/spec/ocr.md "Rotation and perspective (measured 2026-11-03)"): four
/// texts in twenty variants each, turned 0 to 355°, tilted away 0 to 60°, read on the CPU through a fixed-place area and
/// through Search (only the texts with a format: Search with Any text gives every text of the whole picture). Writes a table
/// per sweep and the misses to the test output; Perf (minutes, not in <c>manage test unit</c>). The limits it found are pinned
/// by <see cref="AngleLimitsTests"/>. <see cref="What_if_rotation"/> measures two changes of the recognition through a
/// wrapped model runner (nothing in the plugin changes). Asserts only that every reading ended.
/// </summary>
[Trait("Category", "Perf")]
[Collection(RecognitionGroup.Name)]
public sealed class AngleSweepTests(RecognizerFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Rotation_in_the_picture()
    {
        var cases = from angle in AngleSweep.RotationAngles()
                    from text in AngleSweep.Texts
                    from variant in AngleSweep.Variants
                    select (text, new SweepPose(angle), variant);
        await RunAsync("Rotation, fixed-place area", cases, search: false);
    }

    [Fact]
    public async Task Perspective_tilt()
    {
        var cases = from pose in AngleSweep.PerspectivePoses()
                    from text in AngleSweep.Texts
                    from variant in AngleSweep.Variants
                    select (text, pose, variant);
        await RunAsync("Perspective, fixed-place area", cases, search: false);
    }

    [Fact]
    public async Task Rotation_with_search()
    {
        float[] angles = [0, 15, 30, 45, 60, 75, 90, 135, 180, 225, 270, 315];
        var cases = from angle in angles
                    from text in AngleSweep.Texts.Where(t => t.Format is not null)
                    from variant in AngleSweep.Variants.Where(v => v.Index % 2 == 0)
                    select (text, new SweepPose(angle), variant);
        await RunAsync("Rotation, Search over the whole picture", cases, search: true);
    }

    [Fact]
    public async Task Perspective_with_search()
    {
        var cases = from pose in AngleSweep.PerspectivePoses().Where(p => p.Rotation == 0)
                    from text in AngleSweep.Texts.Where(t => t.Format is not null)
                    from variant in AngleSweep.Variants.Where(v => v.Index % 2 == 0)
                    select (text, pose, variant);
        await RunAsync("Perspective, Search over the whole picture", cases, search: true);
    }

    /// <summary>
    /// What the rotation sweep would give with every line not read surely upright read turned by 180° too (the angle model's
    /// 0.3 skipped) and with the text finder's pixel threshold at 0.5 instead of 0.3 (lines close together stay apart), alone
    /// and together, against the same pictures read the same way without a change.
    /// </summary>
    [Fact]
    public async Task What_if_rotation()
    {
        var cases = (from angle in AngleSweep.RotationAngles()
                     from text in AngleSweep.Texts
                     from variant in AngleSweep.Variants
                     select (text, new SweepPose(angle), variant)).ToList();
        var modes = new (string Name, bool BothWays, float Threshold)[]
        {
            ("as is", false, DbPostProcess.Threshold),
            ("both ways", true, DbPostProcess.Threshold),
            ("threshold 0.5", false, 0.5f),
            ("both + 0.5", true, 0.5f),
        };

        var results = new List<(string Name, List<SweepSample> Samples)>();
        var characters = CharacterList.Load(Path.Combine(RecognizerFixture.ModelFolder, ModelFiles.RecognitionLatinCharacters));
        foreach (var (name, bothWays, threshold) in modes)
        {
            var runner = new ExperimentRunner(new CpuRunner(fixture.Recognizer.CpuWorkers, characters, false), bothWays, threshold);
            var samples = await AngleSweep.RunAsync(
                async (jpeg, area, ct) =>
                {
                    using var picture = Picture.Decode(jpeg);
                    var result = await OnnxTextRecognizer.ReadWithAsync(picture, [area], runner, ct);
                    return [.. result.Areas[0].Lines.Select(l => l.Line)];
                },
                cases, search: false, fixture.Recognizer.Workers * 2, CancellationToken.None);
            results.Add((name, samples));
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Turn | {string.Join(" | ", modes.Select(m => m.Name + " (format / any)"))} |");
        sb.Append("|---|").AppendLine(string.Concat(Enumerable.Repeat("---|", modes.Length)));
        foreach (var angle in AngleSweep.RotationAngles())
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {angle:0}° |");
            foreach (var (_, samples) in results)
            {
                sb.Append(Rates(samples.Where(s => s.Pose.Rotation == angle).ToList()));
            }

            sb.AppendLine();
        }

        sb.Append("| all |");
        foreach (var (_, samples) in results)
        {
            sb.Append(Rates(samples));
        }

        output.WriteLine("What if, rotation, fixed-place area (exact values with a format / any text):");
        output.WriteLine(sb.ToString());
        foreach (var (name, samples) in results)
        {
            output.WriteLine($"{name}:");
            output.WriteLine(AngleSweep.Table(samples));
        }

        Assert.All(results, r => Assert.Equal(cases.Count, r.Samples.Count));
    }

    private static string Rates(List<SweepSample> samples) => string.Create(CultureInfo.InvariantCulture,
        $" {AngleSweep.Rate([.. samples.Where(s => s.Text.Format is not null)]),6:P1} / {AngleSweep.Rate([.. samples.Where(s => s.Text.Format is null)]),6:P1} |");

    private async Task RunAsync(string title, IEnumerable<(SweepText, SweepPose, SweepVariant)> cases, bool search)
    {
        var watch = Stopwatch.StartNew();
        var samples = await AngleSweep.RunAsync(AngleSweep.With(fixture.Recognizer), cases, search, fixture.Recognizer.Workers * 2, CancellationToken.None);
        watch.Stop();

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{title} on {fixture.Recognizer.RunsOn.Name}: {samples.Count} readings in {watch.Elapsed.TotalSeconds:0} s (cells: exact values, character errors)"));
        output.WriteLine(AngleSweep.Table(samples));
        foreach (var miss in samples.Where(s => !s.Exact))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Miss {miss.Pose.Label} {miss.Text.Name} v{miss.Variant.Index} ({miss.Variant.Font}, {miss.Variant.CapHeight:0} px, {miss.Variant.Damage}): '{miss.Value}' ({miss.Lines})"));
        }

        Assert.All(samples, s => Assert.NotNull(s));
    }

    /// <summary>
    /// The CPU runner with two changes for <see cref="What_if_rotation"/>: every line the reader asks the angle model about
    /// counted as maybe upside down (read both ways, the surer reading wins), and the text finder's map values below the given
    /// threshold pushed under the pixel threshold (the same as a higher threshold: lines close together stay apart; the values
    /// from it on stay as they are).
    /// </summary>
    private sealed class ExperimentRunner(IModelRunner inner, bool bothWays, float threshold) : IModelRunner
    {
        public CharacterList Characters => inner.Characters;

        public ITextFinder Search => this;

        public bool SearchOnCard => false;

        public bool BatchTiles => false;

        public int DetectionSide(int pixels) => inner.DetectionSide(pixels);

        public async Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct)
        {
            var map = await inner.DetectAsync(input, count, height, width, ct);
            if (threshold > DbPostProcess.Threshold)
            {
                var values = map.Span;
                var scale = DbPostProcess.Threshold / threshold * 0.99f;
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] < threshold)
                    {
                        values[i] *= scale;
                    }
                }
            }

            return map;
        }

        public async Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) =>
            bothWays ? [.. lines.Select(_ => 1f)] : await inner.UpsideDownAsync(lines, ct);

        public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => inner.RecognizeAsync(lines, ct);
    }
}
