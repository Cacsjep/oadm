using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Vision.Model;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// The two COCO val2017 photos with their COCO boxes, and the check every real detection model passes on them: every person
/// and car COCO annotated found at an overlap of 50 % or more, scores of 50 % or more, no "N/A", no person where COCO has
/// none; the time per picture on one CPU thread.
/// </summary>
internal static class CocoPhotos
{
    /// <summary>The COCO val2017 boxes (x, y, width, height, relative) of the people and cars of each photo (instances_val2017.json).</summary>
    public static readonly Dictionary<string, (string Class, RelativeBox Box)[]> Truth = new()
    {
        [RealModelFiles.Skaters] =
        [
            ("person", new RelativeBox(0.147, 0.240, 0.159, 0.363)),
            ("person", new RelativeBox(0.563, 0.245, 0.235, 0.454)),
            ("car", new RelativeBox(0.013, 0.281, 0.479, 0.285)),
            ("car", new RelativeBox(0.709, 0.306, 0.171, 0.248)),
        ],
        [RealModelFiles.SkateBowl] =
        [
            ("person", new RelativeBox(0.346, 0.097, 0.294, 0.516)),
            ("person", new RelativeBox(0.723, 0.072, 0.265, 0.552)),
            ("person", new RelativeBox(0.826, 0.168, 0.169, 0.438)),
            ("car", new RelativeBox(0.084, 0.003, 0.332, 0.198)),
        ],
    };

    /// <summary>Runs <paramref name="model"/> on <paramref name="photo"/> and checks it against COCO's boxes.</summary>
    /// <returns>The time of one call in ms (the mean of 5 after one warm-up call).</returns>
    public static async Task<double> FindsTheTruthAsync(RealModelFixture fixture, VisionModel model, string photo, ITestOutputHelper output)
    {
        var jpeg = await File.ReadAllBytesAsync(RealModelFiles.PathOf(photo));
        using var frame = fixture.Inference.Decode(jpeg);

        // One call warms the session; then the time per picture, one CPU thread per call as the workers run it.
        var found = await fixture.Inference.DetectAsync(model, frame, 0.5, CancellationToken.None);
        const int Runs = 5;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            found = await fixture.Inference.DetectAsync(model, frame, 0.5, CancellationToken.None);
        }

        var ms = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{model.Name} on {photo} ({frame.Width} x {frame.Height}): {ms:F0} ms per picture on the CPU (one thread per call; {Environment.ProcessorCount} cores, {fixture.Inference.Capacity} workers); {found.Count} objects at 50 % or more"));
        foreach (var d in found)
        {
            output.WriteLine("  " + Describe(d));
        }

        Assert.False(fixture.Inference.OnGpu);
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, d => d.ClassName == VisionLimits.NoClassName);
        Assert.All(found, d => Assert.InRange(d.Score, 0.5, 1));
        foreach (var (name, box) in Truth[photo])
        {
            var best = found.Where(d => d.ClassName == name).Select(d => IoU(d.Box, box)).DefaultIfEmpty(0).Max();
            Assert.True(best >= 0.5, string.Create(CultureInfo.InvariantCulture, $"No {name} found at {Describe(box)}: best overlap {best:F2}."));
        }

        // Every person found is one of COCO's people (no ghost people).
        var truePeople = Truth[photo].Where(t => t.Class == "person").Select(t => t.Box).ToList();
        Assert.All(found.Where(d => d.ClassName == "person"), d => Assert.True(truePeople.Max(t => IoU(d.Box, t)) >= 0.5, "A person where COCO has none: " + Describe(d)));
        return ms;
    }

    /// <summary>The model's facts and its inputs and outputs as ONNX Runtime reads them, into the test output.</summary>
    public static void WriteFacts(VisionModel model, string file, ITestOutputHelper output)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{model.Format}, {model.Kind}, input {model.InputWidth} x {model.InputHeight} (fixed {model.FixedInput}), fit {model.Fit}, scaling {model.Scaling}, {model.ClassNames.Count} classes, list {model.ClassListId}, CPU {model.CpuMs} ms, {model.SizeBytes:N0} bytes, SHA-256 {model.Sha256}"));
        using var session = SessionFactory.Create(RealModelFiles.PathOf(file), ExecutionTarget.Cpu, threads: 1);
        foreach (var (name, meta) in session.InputMetadata.Concat(session.OutputMetadata))
        {
            output.WriteLine($"  {name}: [{string.Join(", ", meta.Dimensions)}] {meta.ElementDataType}");
        }

        foreach (var (key, value) in session.ModelMetadata.CustomMetadataMap)
        {
            output.WriteLine($"  metadata {key}: {(value.Length > 200 ? value[..200] + "..." : value)}");
        }
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
