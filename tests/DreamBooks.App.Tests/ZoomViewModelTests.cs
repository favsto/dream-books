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
    public void Manual_zoom_is_clamped()
    {
        var zoom = new ZoomViewModel { Zoom = 2 };
        Assert.Equal(2.0, zoom.Zoom);
        zoom.Zoom = 200;
        Assert.Equal(8.0, zoom.Zoom);
    }

    [Fact]
    public void Fit_width_mode_is_on_after_a_fit_zoom_and_off_after_any_manual_zoom()
    {
        var zoom = new ZoomViewModel();
        Assert.False(zoom.IsFitWidth);

        zoom.ApplyFitZoom(1.25);
        Assert.True(zoom.IsFitWidth);
        Assert.Equal(1.25, zoom.Zoom, 6);
        zoom.ZoomInCommand.Execute(null);
        Assert.False(zoom.IsFitWidth);

        zoom.ApplyFitZoom(1.25);
        zoom.Zoom = zoom.Zoom; // even the same value is a manual choice
        Assert.False(zoom.IsFitWidth);
    }

    [Fact]
    public void Locked_zoom_ignores_every_zoom_request()
    {
        var zoom = new ZoomViewModel { IsLocked = true };

        zoom.ZoomInCommand.Execute(null);
        zoom.ZoomOutCommand.Execute(null);
        zoom.ApplyFitZoom(1.25);
        zoom.Zoom = 3;
        Assert.Equal(1.0, zoom.Zoom);
        Assert.False(zoom.IsFitWidth);

        zoom.IsLocked = false;
        zoom.Zoom = 2;
        Assert.Equal(2.0, zoom.Zoom);
    }
}
