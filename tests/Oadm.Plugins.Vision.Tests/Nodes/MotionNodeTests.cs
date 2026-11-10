using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Nodes;

public sealed class MotionNodeTests
{
    /// <summary>Camera > Detect > Track (confirm 1) > [Area] > node (shown).</summary>
    private static (EvaluatorHarness H, string Node) Tracked(string kind, JsonObject? settings = null, bool area = false)
    {
        var b = new PipelineBuilder();
        var camera = b.Add(NodeKinds.Camera);
        var last = b.After(b.After(camera, NodeKinds.Detect, PipelineBuilder.DetectSettings()), NodeKinds.Track, new JsonObject { [NodeKinds.ConfirmKey] = 1 });
        if (area)
        {
            last = b.After(last, NodeKinds.Area, PipelineBuilder.Shape((0, 0.5), (1, 0.5), (1, 1), (0, 1)));
        }

        var node = b.After(last, kind, settings, show: true);
        return (new EvaluatorHarness(b.Build()), node);
    }

    [Fact]
    public async Task Dwell_keeps_the_objects_inside_longer_than_its_time()
    {
        var (h, dwell) = Tracked(NodeKinds.Dwell, new JsonObject { [NodeKinds.MinSecondsKey] = 10 }, area: true);
        for (var t = 0.0; t <= 10; t += 0.5)
        {
            Assert.Empty((await h.RunAsync(t, Det.Person(0.2, 0.8), Det.Person(0.8, 0.3))).Nodes[dwell].Objects);
        }

        var r = await h.RunAsync(10.5, Det.Person(0.2, 0.8), Det.Person(0.8, 0.3));
        Assert.Equal(0.2, Assert.Single(r.Nodes[dwell].Objects).Detection.Box.BottomCentre.X, 3);
        Assert.Equal("1 object", r.Nodes[dwell].Live);
    }

    [Fact]
    public async Task Stationary_keeps_objects_that_stay_within_the_movement_allowed()
    {
        var (h, still) = Tracked(NodeKinds.Stationary, new JsonObject { [NodeKinds.StillSecondsKey] = 5, [NodeKinds.ToleranceKey] = 3 });
        for (var t = 0; t <= 4; t++)
        {
            // A parked car shakes by 1 %, a walker moves 5 % a second.
            var r = await h.RunAsync(t, Det.Car(0.3 + (t % 2) * 0.01, 0.8), Det.Person(0.6 + t * 0.05, 0.8));
            Assert.Empty(r.Nodes[still].Objects);
        }

        var five = await h.RunAsync(5, Det.Car(0.3, 0.8), Det.Person(0.85, 0.8));
        Assert.Equal("car", Assert.Single(five.Nodes[still].Objects).Detection.ClassName);

        // The car moves away: it settles again where it is.
        Assert.Empty((await h.RunAsync(6, Det.Car(0.36, 0.8))).Nodes[still].Objects);
    }

    [Fact]
    public async Task Direction_keeps_objects_moving_along_the_arrow_by_their_last_second()
    {
        // The arrow points down the picture; Tolerance 45°, Minimum movement 5 % of the height.
        var arrow = PipelineBuilder.Shape((0.5, 0.2), (0.5, 0.8));
        var (h, dir) = Tracked(NodeKinds.Direction, arrow);
        FrameResult r = null!;
        for (var i = 0; i <= 8; i++)
        {
            var t = i * 0.25;
            r = await h.RunAsync(t,
                Det.Person(0.1, 0.3 + t * 0.1),             // down: in
                Det.Person(0.4 + t * 0.015, 0.3 + t * 0.08), // down and a little right (about 18° in pixels): in
                Det.Person(0.7, 0.9 - t * 0.1),             // up: out
                Det.Car(0.9, 0.5 + t * 0.01));              // down too slowly: out
        }

        Assert.Equal(2, r.Nodes[dir].Objects.Count);
        Assert.All(r.Nodes[dir].Objects, o => Assert.True(o.Detection.Box.BottomCentre.X < 0.5));
        Assert.Equal("2 objects", r.Nodes[dir].Live);
    }

    [Fact]
    public async Task Direction_judges_the_last_second_only()
    {
        var (h, dir) = Tracked(NodeKinds.Direction, PipelineBuilder.Shape((0.5, 0.2), (0.5, 0.8)));

        // Down for 2 s, then up: after a second of going up it no longer counts.
        for (var i = 0; i <= 8; i++)
        {
            await h.RunAsync(i * 0.25, Det.Person(0.3, 0.3 + i * 0.025));
        }

        FrameResult r = null!;
        for (var i = 1; i <= 5; i++)
        {
            r = await h.RunAsync(2 + i * 0.25, Det.Person(0.3, 0.5 - i * 0.025));
        }

        Assert.Empty(r.Nodes[dir].Objects);
    }
}
