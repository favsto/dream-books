using System.Windows;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

public class StylusPointConverterTests
{
    [Fact]
    public void Stylus_point_is_converted_to_page_points()
    {
        var sp = new StylusPoint(200, 100, 0.75f);

        var p = StylusPointConverter.ToInkPoint(sp, scale: 2, timestampMs: 42, hasPressure: true);

        Assert.Equal(100, p.X, 9);
        Assert.Equal(50, p.Y, 9);
        Assert.Equal(0.75f, p.Pressure!.Value, 3);
        Assert.Equal(42, p.TimestampMs);
        Assert.Null(p.TipAngle);
    }

    [Fact]
    public void Missing_pressure_becomes_null()
    {
        var sp = new StylusPoint(10, 10);

        var p = StylusPointConverter.ToInkPoint(sp, scale: 1, timestampMs: 0, hasPressure: false);

        Assert.Null(p.Pressure);
    }

    [Fact]
    public void Missing_tilt_becomes_null_not_zero()
    {
        var p = StylusPointConverter.ToInkPoint(new StylusPoint(10, 10, 0.5f), 1, 0, hasPressure: true);

        Assert.Null(p.TiltX);
        Assert.Null(p.TiltY);
    }

    [Fact]
    public void Tilt_is_converted_to_degrees_when_the_device_reports_it()
    {
        var description = new StylusPointDescription(
        [
            new StylusPointPropertyInfo(StylusPointProperties.X),
            new StylusPointPropertyInfo(StylusPointProperties.Y),
            new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
            new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation, -9000, 9000, StylusPointPropertyUnit.Degrees, 100),
            new StylusPointPropertyInfo(StylusPointProperties.YTiltOrientation, -9000, 9000, StylusPointPropertyUnit.Degrees, 100),
        ]);
        var sp = new StylusPoint(10, 10, 0.5f, description, [4500, -1250]);

        var p = StylusPointConverter.ToInkPoint(sp, 1, 0, hasPressure: true);

        Assert.Equal(45f, p.TiltX!.Value, 3);
        Assert.Equal(-12.5f, p.TiltY!.Value, 3);
    }

    [Fact]
    public void Eraser_center_is_converted_to_page_points()
    {
        var p = StylusPointConverter.ToPage(new Point(30, 60), scale: 3);

        Assert.Equal(10, p.X, 9);
        Assert.Equal(20, p.Y, 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => StylusPointConverter.ToPage(new Point(1, 1), 0));
    }

    [Fact]
    public void Mouse_point_has_null_pressure()
    {
        var p = StylusPointConverter.FromMouse(new Point(30, 60), scale: 3, timestampMs: 7);

        Assert.Equal(10, p.X, 9);
        Assert.Equal(20, p.Y, 9);
        Assert.Null(p.Pressure);
        Assert.Null(p.TiltX);
        Assert.Null(p.TiltY);
        Assert.Equal(7, p.TimestampMs);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(8.0)]
    public void Point_round_trip_through_view_is_stable(double zoom)
    {
        var scale = zoom * Units.DipPerPt;
        var page = new PointD(123.456, 789.012);

        var view = StylusPointConverter.ToView(page, scale);
        var back = StylusPointConverter.FromMouse(view, scale, 0);
        var backFromStylus = StylusPointConverter.ToInkPoint(new StylusPoint(view.X, view.Y), scale, 0, hasPressure: false);

        Assert.Equal(page.X, back.X, 9);
        Assert.Equal(page.Y, back.Y, 9);
        Assert.Equal(page.X, backFromStylus.X, 9);
        Assert.Equal(page.Y, backFromStylus.Y, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Non_positive_scale_is_rejected(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StylusPointConverter.FromMouse(new Point(1, 1), scale, 0));
    }
}
