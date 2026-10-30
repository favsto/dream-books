using System.Windows.Ink;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

public class StockStrokeConverterTests
{
    private static readonly InkColor _red = InkColor.FromRgb(0xD3, 0x2F, 0x2F);

    [Fact]
    public void Wpf_stroke_is_scaled_and_keeps_color_and_width()
    {
        var stroke = new InkStroke(
            Guid.NewGuid(),
            InkTool.Pen,
            _red,
            baseWidthPt: 1,
            [new(10, 20, 0.2f, null, null, null, 0), new(30, 40, 0.9f, null, null, null, 5)]);

        var wpf = StockStrokeConverter.ToWpfStroke(stroke, scale: 2);

        Assert.Equal(2, wpf.StylusPoints.Count);
        Assert.Equal(20, wpf.StylusPoints[0].X, 9);
        Assert.Equal(40, wpf.StylusPoints[0].Y, 9);
        Assert.Equal(60, wpf.StylusPoints[1].X, 9);
        Assert.Equal(80, wpf.StylusPoints[1].Y, 9);
        Assert.Equal(0.2f, wpf.StylusPoints[0].PressureFactor, 3);
        Assert.Equal(0.9f, wpf.StylusPoints[1].PressureFactor, 3);

        var da = wpf.DrawingAttributes;
        Assert.Equal(Color.FromArgb(255, 0xD3, 0x2F, 0x2F), da.Color);
        Assert.Equal(2, da.Width, 9);
        Assert.Equal(2, da.Height, 9);
        Assert.True(da.FitToCurve);
        Assert.False(da.IgnorePressure);
        Assert.Equal(StylusTip.Ellipse, da.StylusTip);
    }

    [Fact]
    public void Mouse_stroke_ignores_pressure_at_mid_factor()
    {
        var stroke = new InkStroke(
            Guid.NewGuid(),
            InkTool.Pen,
            _red,
            baseWidthPt: 1.5,
            [new(10, 20, null, null, null, null, 0), new(30, 40, null, null, null, null, 5)]);

        var wpf = StockStrokeConverter.ToWpfStroke(stroke, scale: 1);

        Assert.All(wpf.StylusPoints, sp => Assert.Equal(0.5f, sp.PressureFactor, 3));
        Assert.True(wpf.DrawingAttributes.IgnorePressure);
        Assert.Equal(1.5, wpf.DrawingAttributes.Width, 9);
    }

    [Fact]
    public void Out_of_range_pressure_is_clamped()
    {
        var stroke = new InkStroke(
            Guid.NewGuid(),
            InkTool.Pen,
            _red,
            baseWidthPt: 1,
            [new(10, 20, 1.5f, null, null, null, 0), new(30, 40, -0.2f, null, null, null, 5)]);

        var wpf = StockStrokeConverter.ToWpfStroke(stroke, scale: 1);

        Assert.Equal(1f, wpf.StylusPoints[0].PressureFactor, 3);
        Assert.Equal(0f, wpf.StylusPoints[1].PressureFactor, 3);
    }
}
