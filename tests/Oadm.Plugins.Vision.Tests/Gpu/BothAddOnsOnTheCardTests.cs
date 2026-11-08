using System.Text.Json;

using Oadm.Plugins.Vision.Tests.RealModels;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Gpu;

/// <summary>
/// Text recognition and Vision Center at the same time on the graphics card in one process (docs/spec/vision-center.md
/// "Load", "Tests"; docs/spec/add-ons.md rule 7): a fresh process (<see cref="BothAddOnsChild"/>) loads both plugin folders
/// as the server does, each in its own load context with its own ONNX Runtime and DirectML, starts both with Use GPU on in
/// each order and lets them read and detect at once for 20 s. Both must stay on the card, read what their CPU reads, and
/// no call may fail. Category Gpu: only on purpose (<c>manage test gpu</c>), one test process at a time.
/// </summary>
[Collection(RealModelGroup.Name)]
[Trait("Category", GpuCard.Category)]
public sealed class BothAddOnsOnTheCardTests(ITestOutputHelper output)
{
    private const int Seconds = 20;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GpuPluginsFact(RealModelFiles.Yolo11nNoNames, RealModelFiles.RfDetrMedium)]
    public Task Text_recognition_first_then_Vision_Center() => BothAsync("ocr-first");

    [GpuPluginsFact(RealModelFiles.Yolo11nNoNames, RealModelFiles.RfDetrMedium)]
    public Task Vision_Center_first_then_Text_recognition() => BothAsync("vision-first");

    [GpuPluginsFact(RealModelFiles.Yolo11nNoNames, RealModelFiles.RfDetrMedium)]
    public Task Both_start_at_the_same_time() => BothAsync("together");

    private async Task BothAsync(string order)
    {
        var (exit, result) = await GpuSupport.RunChildAsync(output, TimeSpan.FromMinutes(5), "both", order, Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(0, exit);
        Assert.NotNull(result);
        var r = result.RootElement;
        output.WriteLine(JsonSerializer.Serialize(r, Indented));
        var ocr = r.GetProperty("ocr");
        var vision = r.GetProperty("vision");

        Assert.True(ocr.GetProperty("onGpu").GetBoolean(), "Text recognition is not on the card: " + ocr.GetProperty("runsOn").GetString());
        Assert.True(vision.GetProperty("onGpu").GetBoolean(), "Vision Center is not on the card: " + vision.GetProperty("runsOn").GetString());
        Assert.Empty(ocr.GetProperty("errors").EnumerateArray());
        Assert.Empty(vision.GetProperty("errors").EnumerateArray());
        Assert.Empty(ocr.GetProperty("mismatches").EnumerateArray());
        Assert.Empty(vision.GetProperty("mismatches").EnumerateArray());
        Assert.True(ocr.GetProperty("bothCalls").GetInt32() > 0);
        Assert.All(vision.GetProperty("bothCalls").EnumerateObject(), c => Assert.True(c.Value.GetInt32() > 0, c.Name));

        // The card reads what the CPU reads; the brand of the camera box exactly as the accuracy set has it.
        Assert.Equal(Strings(ocr.GetProperty("cpu")), Strings(ocr.GetProperty("card")));
        Assert.Equal(Strings(ocr.GetProperty("expected"))[0], Strings(ocr.GetProperty("card"))[0]);
        Assert.All(vision.GetProperty("models").EnumerateObject(), m => Assert.True(m.Value.GetProperty("Same").GetBoolean(), m.Name + ": " + m.Value.GetProperty("Diff").GetString()));
        Assert.All(vision.GetProperty("models").EnumerateObject(), m => Assert.True(m.Value.GetProperty("OnCard").GetBoolean(), m.Name + " did not pass its test on the card."));

        // Each add-on's ONNX Runtime from its own plugin folder; DirectML only from a plugin folder (never the system's).
        var modules = Strings(r.GetProperty("modules"));
        var root = GpuPlugins.Root;
        var runtimes = modules.Where(m => Path.GetFileNameWithoutExtension(m).Equals(OperatingSystem.IsWindows() ? "onnxruntime" : "libonnxruntime", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(runtimes, m => m.StartsWith(Path.Combine(root, GpuPlugins.Ocr), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runtimes, m => m.StartsWith(Path.Combine(root, GpuPlugins.Vision), StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows())
        {
            var directMl = modules.Where(m => Path.GetFileName(m).Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(directMl);
            Assert.All(directMl, m => Assert.StartsWith(root, m, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
