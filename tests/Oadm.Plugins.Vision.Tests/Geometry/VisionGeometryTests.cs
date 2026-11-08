using Oadm.Plugins.Vision.Geometry;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Geometry;

public sealed class VisionGeometryTests
{
    private static readonly RelativePoint[] Square = [new(0.2, 0.2), new(0.6, 0.2), new(0.6, 0.6), new(0.2, 0.6)];

    // A concave "U": the notch between x 0.4 and 0.6 above y 0.6 is outside.
    private static readonly RelativePoint[] U =
        [new(0.2, 0.2), new(0.4, 0.2), new(0.4, 0.6), new(0.6, 0.6), new(0.6, 0.2), new(0.8, 0.2), new(0.8, 0.8), new(0.2, 0.8)];

    [Theory]
    [InlineData(0.4, 0.4, true)]
    [InlineData(0.1, 0.4, false)]
    [InlineData(0.7, 0.7, false)]
    [InlineData(0.59, 0.21, true)]
    public void A_point_is_inside_a_square_or_not(double x, double y, bool inside) =>
        Assert.Equal(inside, VisionGeometry.Inside(new RelativePoint(x, y), Square));

    [Fact]
    public void A_concave_polygon_leaves_its_notch_out()
    {
        Assert.False(VisionGeometry.Inside(new RelativePoint(0.5, 0.4), U));
        Assert.True(VisionGeometry.Inside(new RelativePoint(0.5, 0.7), U));
        Assert.True(VisionGeometry.Inside(new RelativePoint(0.3, 0.4), U));
    }

    [Fact]
    public void Fewer_than_three_points_hold_nothing() =>
        Assert.False(VisionGeometry.Inside(new RelativePoint(0.5, 0.5), [new(0, 0), new(1, 1)]));

    [Fact]
    public void A_box_overlaps_by_a_corner_an_edge_or_by_holding_the_polygon()
    {
        Assert.True(VisionGeometry.Overlaps(new RelativeBox(0.55, 0.55, 0.2, 0.2), Square)); // box corner inside
        Assert.True(VisionGeometry.Overlaps(new RelativeBox(0.1, 0.1, 0.8, 0.8), Square)); // polygon inside the box
        Assert.True(VisionGeometry.Overlaps(new RelativeBox(0.3, 0.1, 0.05, 0.8), Square)); // a thin box through it
        Assert.False(VisionGeometry.Overlaps(new RelativeBox(0.7, 0.7, 0.1, 0.1), Square));
        Assert.False(VisionGeometry.Overlaps(new RelativeBox(0.45, 0.3, 0.1, 0.2), U)); // in the notch
    }

    [Fact]
    public void IoU_of_equal_disjoint_and_half_shifted_boxes()
    {
        var a = new RelativeBox(0.1, 0.1, 0.2, 0.2);
        Assert.Equal(1, VisionGeometry.IoU(a, a), 9);
        Assert.Equal(0, VisionGeometry.IoU(a, new RelativeBox(0.5, 0.5, 0.1, 0.1)));
        Assert.Equal(1.0 / 3, VisionGeometry.IoU(a, new RelativeBox(0.2, 0.1, 0.2, 0.2)), 9);
    }

    [Fact]
    public void Moving_down_over_a_line_drawn_left_to_right_is_In_and_back_is_Out()
    {
        RelativePoint a = new(0.1, 0.5), b = new(0.9, 0.5);
        Assert.Equal(CrossDirection.In, VisionGeometry.Crossing(new(0.5, 0.4), new(0.5, 0.6), a, b));
        Assert.Equal(CrossDirection.Out, VisionGeometry.Crossing(new(0.5, 0.6), new(0.5, 0.4), a, b));

        // Drawn the other way round, the sides swap.
        Assert.Equal(CrossDirection.Out, VisionGeometry.Crossing(new(0.5, 0.4), new(0.5, 0.6), b, a));
    }

    [Fact]
    public void A_step_beside_the_segment_or_along_it_does_not_cross()
    {
        RelativePoint a = new(0.1, 0.5), b = new(0.4, 0.5);
        Assert.Null(VisionGeometry.Crossing(new(0.6, 0.4), new(0.6, 0.6), a, b)); // past its end
        Assert.Null(VisionGeometry.Crossing(new(0.2, 0.3), new(0.3, 0.4), a, b)); // on one side
        Assert.Null(VisionGeometry.Crossing(new(0.15, 0.5), new(0.3, 0.5), a, b)); // along it
    }

    [Fact]
    public void A_step_that_ends_on_the_line_crosses_once_and_the_next_from_it_does_not()
    {
        RelativePoint a = new(0.1, 0.5), b = new(0.9, 0.5);
        Assert.Equal(CrossDirection.In, VisionGeometry.Crossing(new(0.5, 0.4), new(0.5, 0.5), a, b));
        Assert.Null(VisionGeometry.Crossing(new(0.5, 0.5), new(0.5, 0.6), a, b));
    }

    [Fact]
    public void A_polyline_counts_the_first_segment_crossed()
    {
        RelativePoint[] line = [new(0.1, 0.5), new(0.5, 0.5), new(0.5, 0.9)];
        Assert.Equal(CrossDirection.In, VisionGeometry.Crossing(new(0.3, 0.4), new(0.3, 0.6), line));

        // The second segment goes down: its right side is the picture's left.
        Assert.Equal(CrossDirection.In, VisionGeometry.Crossing(new(0.6, 0.7), new(0.4, 0.7), line));
        Assert.Null(VisionGeometry.Crossing(new(0.7, 0.3), new(0.8, 0.3), line));
    }
}
