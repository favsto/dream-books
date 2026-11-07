using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;

namespace DreamBooks.Core.Tests.Ink;

public class SelectionHandlesTests
{
    private static readonly RectD _box = new(100, 100, 200, 160);

    [Theory]
    [InlineData(101, 101, SelectionHandle.TopLeft)]
    [InlineData(199, 102, SelectionHandle.TopRight)]
    [InlineData(198, 159, SelectionHandle.BottomRight)]
    [InlineData(103, 158, SelectionHandle.BottomLeft)]
    public void A_press_near_a_corner_grabs_that_corner(double x, double y, SelectionHandle expected)
        => Assert.Equal(expected, SelectionHandles.HitTest(_box, new PointD(x, y), 10));

    [Fact]
    public void A_press_inside_away_from_the_corners_moves()
        => Assert.Equal(SelectionHandle.Inside, SelectionHandles.HitTest(_box, new PointD(150, 130), 10));

    [Fact]
    public void A_press_outside_hits_nothing()
        => Assert.Equal(SelectionHandle.None, SelectionHandles.HitTest(_box, new PointD(400, 400), 10));

    [Fact]
    public void A_press_just_outside_within_tolerance_still_moves()
        => Assert.Equal(SelectionHandle.Inside, SelectionHandles.HitTest(_box, new PointD(150, 98), 10));

    [Fact]
    public void A_box_smaller_than_the_tolerance_has_no_corner_handles()
    {
        var dot = new RectD(50, 50, 52, 52);

        Assert.Equal(SelectionHandle.Inside, SelectionHandles.HitTest(dot, new PointD(50, 50), 10));
    }

    [Fact]
    public void Opposite_is_the_corner_across_the_box()
    {
        Assert.Equal(new PointD(200, 160), SelectionHandles.Opposite(_box, SelectionHandle.TopLeft));
        Assert.Equal(new PointD(100, 160), SelectionHandles.Opposite(_box, SelectionHandle.TopRight));
        Assert.Equal(new PointD(100, 100), SelectionHandles.Opposite(_box, SelectionHandle.BottomRight));
        Assert.Equal(new PointD(200, 100), SelectionHandles.Opposite(_box, SelectionHandle.BottomLeft));
    }

    [Fact]
    public void Corner_returns_the_corner_point()
        => Assert.Equal(new PointD(200, 100), SelectionHandles.Corner(_box, SelectionHandle.TopRight));
}
