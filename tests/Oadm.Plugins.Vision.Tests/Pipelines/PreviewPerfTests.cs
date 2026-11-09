using System.Diagnostics;
using System.IO.Compression;
using System.Text;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>
/// A preview's state goes to its page after every frame, up to 25 a second (docs/spec/vision-center.md "Page"): its size and
/// the time to write and read it for a busy draft (Detect, Filter, Track, Area, Line of 100 objects with trails).
/// </summary>
public sealed class PreviewPerfTests(ITestOutputHelper output)
{
    internal static PreviewEvent Busy()
    {
        var objects = Enumerable.Range(0, 100).Select(i => new VisionObject
        {
            Detection = new Detection(49, "orange", 0.2512345, new RelativeBox(0.01 * i, 0.3 + (0.001 * i), 0.0612345, 0.1012345)),
            TrackId = i + 1,
            Trail = [.. Enumerable.Range(0, 12).Select(t => new RelativePoint(0.01 * i + (t * 0.003), 0.4 + (0.001 * t)))],
            InsideSeconds = 3.25,
        }).ToList();
        var untracked = objects.Select(o => o with { TrackId = null, Trail = [], InsideSeconds = null }).ToList();
        return new PreviewEvent
        {
            PreviewId = "p",
            At = DateTimeOffset.UnixEpoch,
            Resolution = new Resolution(1280, 720),
            Nodes =
            [
                new NodeOutput { NodeId = "cam", Kind = PortKind.Picture, Live = "1280x720 @ 15 fps" },
                new NodeOutput { NodeId = "det", Kind = PortKind.Objects, Objects = untracked, Milliseconds = 18.4, Live = "100 objects · 18 ms" },
                new NodeOutput { NodeId = "fil", Kind = PortKind.Objects, Objects = untracked[..60], Dropped = untracked[60..], Live = "60 objects" },
                new NodeOutput { NodeId = "trk", Kind = PortKind.TrackedObjects, Objects = objects[..60], Live = "60 tracked" },
                new NodeOutput { NodeId = "area", Kind = PortKind.TrackedObjects, Objects = objects[..30], Live = "30 inside" },
                new NodeOutput { NodeId = "line", Kind = PortKind.Crossings, In = 120, Out = 4, Live = "In 120 · Out 4" },
            ],
        };
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_busy_preview_state_is_small_and_quick_to_write_and_read()
    {
        var busy = Busy();
        var preview = busy with { Nodes = [.. busy.Nodes.Select(n => n.NodeId == "trk" ? n : PipelineRuntime.WithoutTrails(n))] }; // as the runtime sends it
        var json = VisionJson.Serialize(preview);
        var bytes = Encoding.UTF8.GetByteCount(json);
        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }

        const int Runs = 200;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Runs; i++)
        {
            VisionJson.Serialize(preview);
        }

        var write = watch.Elapsed.TotalMilliseconds / Runs;
        watch.Restart();
        for (var i = 0; i < Runs; i++)
        {
            VisionJson.Deserialize<PreviewEvent>(json);
        }

        var read = watch.Elapsed.TotalMilliseconds / Runs;
        output.WriteLine($"{bytes / 1024.0:0.0} KB, gzip {zipped.Length / 1024.0:0.0} KB; write {write:0.00} ms, read {read:0.00} ms; at 25 a second {bytes * 25 / 1024.0:0} KB/s");
        Assert.True(write < 20 && read < 30, $"write {write:0.0} ms, read {read:0.0} ms");
    }

    [Fact]
    public void Only_the_track_node_carries_trails_to_the_page()
    {
        var busy = Busy();
        var area = busy.Nodes.Single(n => n.NodeId == "area");
        var stripped = PipelineRuntime.WithoutTrails(area);
        Assert.All(stripped.Objects, o => Assert.Empty(o.Trail));
        Assert.Equal(area.Objects.Select(o => o.TrackId), stripped.Objects.Select(o => o.TrackId));
        var detect = busy.Nodes.Single(n => n.NodeId == "det");
        Assert.Same(detect, PipelineRuntime.WithoutTrails(detect)); // nothing to strip: no copy
    }
}
