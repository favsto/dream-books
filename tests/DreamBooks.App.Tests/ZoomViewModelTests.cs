using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Tests;

public class ZoomViewModelTests
{
    [Fact]
    public void Zoom_steps_and_clamps()
    {
        var zoom = new ZoomViewModel();
        Assert.Equal(1.0, zoom.Zoom);

        zoom.ZoomInCommand.Execute(null);
        Assert.Equal(1.25, zoom.Zoom);

        zoom.ZoomOutCommand.Execute(null);
        zoom.ZoomOutCommand.Execute(null);
        Assert.Equal(0.75, zoom.Zoom);

        zoom.Zoom = 20;
        Assert.Equal(8.0, zoom.Zoom);
        zoom.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, zoom.Zoom);

        zoom.Zoom = 0.1;
        Assert.Equal(0.25, zoom.Zoom);
        zoom.ZoomOutCommand.Execute(null);
        Assert.Equal(0.25, zoom.Zoom);
    }

    [Fact]
    public void Zoom_steps_from_an_off_grid_value()
    {
        var zoom = new ZoomViewModel { Zoom = 1.1 };
        zoom.ZoomInCommand.Execute(null);
        Assert.Equal(1.25, zoom.Zoom);

        zoom.Zoom = 1.1;
        zoom.ZoomOutCommand.Execute(null);
        Assert.Equal(1.0, zoom.Zoom);
    }

    [Fact]
    public void ZoomAt_multiplies_and_clamps()
    {
        var zoom = new ZoomViewModel();
        zoom.ZoomAt(2);
        Assert.Equal(2.0, zoom.Zoom);
        zoom.ZoomAt(100);
        Assert.Equal(8.0, zoom.Zoom);
    }

    [Fact]
    public void FitWidth_sets_zoom_from_viewport()
    {
        var zoom = new ZoomViewModel();
        zoom.FitWidth(1000, 600);
        Assert.Equal(1.25, zoom.Zoom, 6);
    }
}
