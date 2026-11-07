using DreamBooks.Core.Settings;

namespace DreamBooks.Core.Tests.Settings;

public class ToolbarPlacementTests
{
    [Theory]
    [InlineData(500, 700, 1000, 800, ToolbarEdge.Bottom)] // near the bottom edge
    [InlineData(500, 20, 1000, 800, ToolbarEdge.Top)]
    [InlineData(30, 400, 1000, 800, ToolbarEdge.Left)]
    [InlineData(980, 400, 1000, 800, ToolbarEdge.Right)]
    [InlineData(500, 400, 1000, 800, ToolbarEdge.Bottom)] // the centre: the ties and the nearest go to the default
    [InlineData(10, 10, 1000, 800, ToolbarEdge.Top)] // top-left corner: equally near top and left, the tie order is bottom, top, left, right
    [InlineData(990, 790, 1000, 800, ToolbarEdge.Bottom)] // bottom-right corner: bottom wins the tie
    [InlineData(0, 800, 1000, 800, ToolbarEdge.Bottom)]
    public void The_nearest_window_edge_wins(double x, double y, double width, double height, ToolbarEdge expected)
    {
        Assert.Equal(expected, ToolbarPlacement.NearestEdge(x, y, width, height));
    }

    [Theory]
    [InlineData(-50, 400, 1000, 800, ToolbarEdge.Left)] // released outside the window
    [InlineData(1500, 400, 1000, 800, ToolbarEdge.Right)]
    [InlineData(500, 5000, 1000, 800, ToolbarEdge.Bottom)]
    [InlineData(500, -100, 1000, 800, ToolbarEdge.Top)]
    public void A_release_outside_the_window_counts_as_on_the_nearest_edge(double x, double y, double width, double height, ToolbarEdge expected)
    {
        Assert.Equal(expected, ToolbarPlacement.NearestEdge(x, y, width, height));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(200, 120)]
    [InlineData(0, 0)]
    [InlineData(-5, 10)]
    [InlineData(double.NaN, 100)]
    public void A_tiny_or_broken_window_never_throws_and_gives_an_edge(double width, double height)
    {
        var edge = ToolbarPlacement.NearestEdge(width / 2, height / 2, width, height);

        Assert.True(Enum.IsDefined(edge));
    }

    [Fact]
    public void A_broken_point_gives_the_default_edge()
    {
        Assert.Equal(ToolbarEdge.Bottom, ToolbarPlacement.NearestEdge(double.NaN, double.PositiveInfinity, 1000, 800));
    }
}
