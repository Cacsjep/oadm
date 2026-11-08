using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>The checks every real detection model goes through: the model check's facts written out, and the two COCO photos against their COCO boxes.</summary>
internal static class RealModelChecks
{
    public const string LeftSkater = "left skater";
    public const string RightSkater = "right skater";
    public const string LeftCar = "large car on the left";
    public const string RightCar = "car on the right";
    public const string SkaterInTheAir = "skater in the air";
    public const string SkaterOnTheEdge = "skater on the edge";
    public const string PersonBehind = "person behind, partly hidden";
    public const string CarBehindTheFence = "car behind the fence";

    /// <summary>The COCO val2017 boxes (x, y, width, height, relative) of the people and cars of each photo (instances_val2017.json).</summary>
    public static readonly Dictionary<string, (string Name, string Class, RelativeBox Box)[]> Truth = new()
    {
        [RealModelFiles.Skaters] =
        [
            (LeftSkater, "person", new RelativeBox(0.147, 0.240, 0.159, 0.363)),
            (RightSkater, "person", new RelativeBox(0.563, 0.245, 0.235, 0.454)),
            (LeftCar, "car", new RelativeBox(0.013, 0.281, 0.479, 0.285)),
            (RightCar, "car", new RelativeBox(0.709, 0.306, 0.171, 0.248)),
        ],
        [RealModelFiles.SkateBowl] =
        [
            (SkaterInTheAir, "person", new RelativeBox(0.346, 0.097, 0.294, 0.516)),
            (SkaterOnTheEdge, "person", new RelativeBox(0.723, 0.072, 0.265, 0.552)),
            (PersonBehind, "person", new RelativeBox(0.826, 0.168, 0.169, 0.438)),
            (CarBehindTheFence, "car", new RelativeBox(0.084, 0.003, 0.332, 0.198)),
        ],
    };

    /// <summary>Writes the model's facts and the file's inputs and outputs to the test output; the check must have passed.</summary>
    public static VisionModel Describe(ModelCheck check, string file, ITestOutputHelper output)
    {
        Assert.True(check.Refusal is null, check.Refusal);
        var model = check.Model!;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file}: {model.Format}, {model.Kind}, input {model.InputWidth} x {model.InputHeight} (fixed {model.FixedInput}), fit {model.Fit}, scaling {model.Scaling}, {model.ClassNames.Count} classes, list {model.ClassListId ?? "own names"}, CPU {model.CpuMs} ms, {model.SizeBytes:N0} bytes, SHA-256 {model.Sha256}"));
        using (var session = SessionFactory.Create(RealModelFiles.PathOf(file), ExecutionTarget.Cpu, threads: 1))
        {
            foreach (var (name, meta) in session.InputMetadata.Concat(session.OutputMetadata))
            {
                output.WriteLine($"  {name}: [{string.Join(", ", meta.Dimensions)}] {meta.ElementDataType}");
            }
        }

        Assert.Equal(RealModelFiles.Sha256Of(file), model.Sha256);
        Assert.True(model.CpuMs > 0);
        return model;
    }

    /// <summary>
    /// The model on <paramref name="photo"/>: every COCO person and car found at an overlap of 50 % or more with a score of at
    /// least <paramref name="minScore"/> (except the objects of <paramref name="mayMiss"/>, which a small model was measured to
    /// miss: written out with the best overlap at any score), no "N/A", no person where COCO has none; the time per picture
    /// written out.
    /// </summary>
    public static async Task<IReadOnlyList<Detection>> FindsTheTruthAsync(RealModelFixture fixture, VisionModel model, string photo, ITestOutputHelper output, double minScore = 0.5, IReadOnlyCollection<string>? mayMiss = null)
    {
        var jpeg = await File.ReadAllBytesAsync(RealModelFiles.PathOf(photo));
        using var frame = fixture.Inference.Decode(jpeg);

        // One call warms the session; then the time per picture, one CPU thread per call as the workers run it.
        var found = await fixture.Inference.DetectAsync(model, frame, minScore, CancellationToken.None);
        const int Runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            found = await fixture.Inference.DetectAsync(model, frame, minScore, CancellationToken.None);
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{model.Name} on {photo} ({frame.Width} x {frame.Height}): {ms:F0} ms per picture on the CPU (one thread per call; {Environment.ProcessorCount} cores, {fixture.Inference.Capacity} workers); {found.Count} objects at {minScore:P0} or more"));
        foreach (var d in found)
        {
            output.WriteLine("  " + Describe(d));
        }

        Assert.False(fixture.Inference.OnGpu);
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, d => d.ClassName == VisionLimits.NoClassName);
        Assert.All(found, d => Assert.InRange(d.Score, minScore, 1));
        Assert.All(found, d => Assert.True(d.Box.Width * d.Box.Height < 0.95, "A box over the whole picture: " + Describe(d)));
        var missed = new List<string>();
        var unexpected = new List<string>();
        var low = (IReadOnlyList<Detection>?)null;
        foreach (var (name, cls, box) in Truth[photo])
        {
            var best = found.Where(d => d.ClassName == cls).Select(d => IoU(d.Box, box)).DefaultIfEmpty(0).Max();
            if (best >= 0.5)
            {
                continue;
            }

            low ??= await fixture.Inference.DetectAsync(model, frame, 0.05, CancellationToken.None);
            var closest = low.Where(d => d.ClassName == cls).OrderByDescending(d => IoU(d.Box, box)).FirstOrDefault();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{name} ({cls} at {Describe(box)}): best overlap {best:F2}; closest at 5 % or more: {(closest is null ? "none" : $"{closest.Score:P0}, overlap {IoU(closest.Box, box):F2}")}");
            missed.Add(line);
            if (mayMiss?.Contains(name) != true)
            {
                unexpected.Add(line);
            }
        }

        foreach (var line in missed)
        {
            output.WriteLine("  missed: " + line);
        }

        Assert.True(unexpected.Count == 0, "Not found: " + string.Join("; ", unexpected));

        // Every person found is one of COCO's people (no ghost people; a partly hidden one found smaller than COCO's box
        // still overlaps it by more than 30 %).
        var truePeople = Truth[photo].Where(t => t.Class == "person").Select(t => t.Box).ToList();

        Assert.All(found.Where(d => d.ClassName == "person"), d => Assert.True(truePeople.Max(t => IoU(d.Box, t)) >= 0.3, "A person where COCO has none: " + Describe(d)));
        return found;
    }

    public static double IoU(RelativeBox a, RelativeBox b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        return inter / ((a.Width * a.Height) + (b.Width * b.Height) - inter);
    }

    public static string Describe(RelativeBox b) =>
        string.Create(CultureInfo.InvariantCulture, $"x {b.X:F3} y {b.Y:F3} w {b.Width:F3} h {b.Height:F3}");

    public static string Describe(Detection d) =>
        string.Create(CultureInfo.InvariantCulture, $"{d.ClassName} (id {d.ClassId}) {d.Score:P0} at {Describe(d.Box)}");
}
