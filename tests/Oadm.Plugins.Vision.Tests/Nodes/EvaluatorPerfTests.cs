using System.Diagnostics;
using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Nodes;

public sealed class EvaluatorPerfTests(ITestOutputHelper output)
{
    /// <summary>Camera > Detect > Track, then 7 branches Area > Line > Count > Event and one Filter: 32 nodes.</summary>
    private static Pipeline ThirtyTwoNodes()
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var detect = b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings());
        var track = b.After(detect, NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        for (var i = 0; i < 7; i++)
        {
            var y = 0.1 + i * 0.12;
            var area = b.After(track, NodeKinds.Area, PipelineBuilder.Shape((0.05, 0.05), (0.95, 0.05), (0.95, 0.95), (0.5, 0.99), (0.05, 0.95)));
            var line = b.After(area, NodeKinds.Line, PipelineBuilder.Shape((0.0, y), (0.5, y + 0.01), (1.0, y)));
            b.After(b.After(line, NodeKinds.Count), NodeKinds.Event);
        }

        b.After(track, NodeKinds.Filter, show: true);
        return b.Build();
    }

    private static Detection[] Crowd(int frame)
    {
        var list = new Detection[VisionLimits.MaxObjects];
        for (var i = 0; i < list.Length; i++)
        {
            var col = i % 20;
            var row = i / 20;
            list[i] = Det.Person(0.025 + col * 0.05, 0.08 + row * 0.06 + frame * 0.004, score: 0.9 - (i * 0.001), w: 0.03, h: 0.05);
        }

        return list;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task One_frame_through_32_nodes_with_300_objects_is_fast()
    {
        var p = ThirtyTwoNodes();
        Assert.Equal(VisionLimits.MaxNodes, p.Nodes.Count);
        var h = new EvaluatorHarness(p);
        for (var f = 0; f < 5; f++)
        {
            await h.RunAsync(f * 0.125, Crowd(f));
        }

        const int Frames = 40;
        var watch = Stopwatch.StartNew();
        FrameResult last = null!;
        for (var f = 5; f < 5 + Frames; f++)
        {
            last = await h.RunAsync(f * 0.125, Crowd(f));
        }

        var perFrame = watch.Elapsed.TotalMilliseconds / Frames;
        output.WriteLine($"One frame through 32 nodes with 300 objects: {perFrame:0.00} ms");
        Assert.Equal(VisionLimits.MaxObjects, last.Nodes.Values.Max(n => n.Objects.Count));
        Assert.Contains(last.Nodes.Values, n => n.In > 0);
        Assert.True(h.Of(VisionEventKind.CountChanged).Any());
        // Measured about 7 ms (debug build, a 2026 desktop CPU); the budget is about 10 times that.
        Assert.True(perFrame < 75, $"{perFrame:0.00} ms per frame, budget 75 ms");
    }
}
