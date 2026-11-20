using System.Text.Json.Nodes;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>What one camera needs, from the overlays and limits read right before the writes.</summary>
public sealed class PlannerTests
{
    private static readonly OverlayCapabilities Eight = new(512, 16, 200, 8, 1, 1);

    private static TextOverlay Overlay(string text, OverlayPosition position = OverlayPosition.BottomLeft, int? size = null) =>
        new() { Text = text, Position = position, FontSize = size };

    private static DeviceOverlays Shown(params (int Camera, TextOverlay Overlay)[] overlays) =>
        new([.. overlays.Select((o, i) => new DeviceOverlay(i + 1, o.Camera, o.Overlay))], new Dictionary<int, int>());

    [Fact]
    public void Add_skips_an_overlay_the_camera_already_shows()
    {
        var plan = OverlayPlanner.Build(
            new OverlaysPayload { Overlays = [Overlay("%F %X"), Overlay("#n", OverlayPosition.TopRight)] },
            Shown((1, Overlay("%F %X", size: 48)), (1, Overlay("Other"))),
            Eight);
        Assert.Equal(["#n"], plan.Add.Select(a => a.Text));
        Assert.Equal(["%F %X"], plan.AlreadyThere.Select(a => a.Text)); // no size asked for: any size matches
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Replace_removes_every_other_text_overlay_of_view_area_1_only()
    {
        var plan = OverlayPlanner.Build(
            new OverlaysPayload { Mode = OverlaysMode.Replace, Overlays = [Overlay("%F %X"), Overlay("New")] },
            Shown((1, Overlay("%F %X")), (1, Overlay("Old")), (2, Overlay("Area 2"))),
            Eight);
        Assert.Equal(["Old"], plan.Remove.Select(r => r.Overlay.Text));
        Assert.Equal(["New"], plan.Add.Select(a => a.Text));

        var none = OverlayPlanner.Build(new OverlaysPayload { Mode = OverlaysMode.Replace }, Shown((1, Overlay("a")), (1, Overlay("b"))), Eight);
        Assert.Equal(2, none.Remove.Count);
        Assert.Empty(none.Add);
    }

    [Fact]
    public void Remove_matching_compares_the_text_only_and_names_what_is_missing()
    {
        var plan = OverlayPlanner.Build(
            new OverlaysPayload { Mode = OverlaysMode.RemoveMatching, Overlays = [Overlay("Lobby\r\nfloor 1"), Overlay("Gone")] },
            Shown((1, Overlay("Lobby\nfloor 1", OverlayPosition.Top, 32)), (1, Overlay("Keep")), (2, Overlay("Gone"))),
            Eight);
        Assert.Equal(["Lobby\nfloor 1"], plan.Remove.Select(r => r.Overlay.Text));
        Assert.Equal(["Gone"], plan.NotFound); // view area 2 is never touched
    }

    [Fact]
    public void Slots_are_counted_per_view_area_with_images_and_what_replace_frees()
    {
        var seven = Enumerable.Range(0, 6).Select(i => (1, Overlay("x" + i))).Append((2, Overlay("other area"))).ToArray();
        var full = new DeviceOverlays(Shown(seven).Texts, new Dictionary<int, int> { [1] = 1 });
        var error = Assert.Throws<OverlayDeviceException>(() => OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("a"), Overlay("b")] }, full, Eight));
        Assert.Equal("The camera has room for 1 more overlay in view area 1, not 2. Nothing was changed.", error.Message);

        Assert.Single(OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("a")] }, full, Eight).Add);
        var replace = OverlayPlanner.Build(new OverlaysPayload { Mode = OverlaysMode.Replace, Overlays = [.. Enumerable.Range(0, 7).Select(i => Overlay("n" + i))] }, full, Eight);
        Assert.Equal(7, replace.Add.Count);
        Assert.Equal(6, replace.Remove.Count);

        var twoEach = Assert.Throws<OverlayDeviceException>(() => OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("a")] },
            Shown([.. Enumerable.Range(0, 8).Select(i => (1, Overlay("x" + i)))]), new OverlayCapabilities(512, 16, 200, 16, 2, 1)));
        Assert.StartsWith("The camera has no free overlay slot", twoEach.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cameras_own_text_length_and_size_range_are_checked_before_any_write()
    {
        var small = new OverlayCapabilities(10, 16, 48, 4, 1, 1);
        Assert.EndsWith("Nothing was changed.", Assert.Throws<OverlayDeviceException>(() => OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("eleven char")] }, Shown(), small)).Message, StringComparison.Ordinal);
        Assert.Equal("The camera takes sizes from 16 to 48, not 60. Nothing was changed.",
            Assert.Throws<OverlayDeviceException>(() => OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("a", size: 60)] }, Shown(), small)).Message);
        Assert.Single(OverlayPlanner.Build(new OverlaysPayload { Overlays = [Overlay("a", size: 48)] }, Shown(), small).Add);
    }

    [Fact]
    public void A_custom_place_matches_only_the_same_place()
    {
        var at = new TextOverlay { Text = "x", Position = OverlayPosition.Custom, CustomPosition = [-0.25, -0.5] };
        Assert.True(OverlayPlanner.Same(at, at with { CustomPosition = [-0.2500001, -0.5] }));
        Assert.False(OverlayPlanner.Same(at, at with { CustomPosition = [0.25, -0.5] }));
        Assert.False(OverlayPlanner.Same(at, at with { Position = OverlayPosition.TopLeft, CustomPosition = null }));
        Assert.False(OverlayPlanner.Same(at, at with { FontSize = 20 }));
    }

    [Fact]
    public void The_recorded_list_plans_like_a_camera_with_one_overlay()
    {
        var recorded = DynamicOverlayApi.ParseList(JsonNode.Parse(Fixtures.Read("list-p3265v-12.11.json"))!["data"]);
        var plan = OverlayPlanner.Build(new OverlaysPayload { Mode = OverlaysMode.Replace, Overlays = [Overlay("%F")] }, recorded, Eight);
        Assert.Equal(1, Assert.Single(plan.Remove).Identity);
    }
}
