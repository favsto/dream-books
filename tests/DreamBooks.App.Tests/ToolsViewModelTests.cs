using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

public class ToolsViewModelTests
{
    [Fact]
    public void Back_end_switches_to_eraser_and_restores_previous_tool()
    {
        var tools = new ToolsViewModel();
        Assert.Equal(AppTool.Pen, tools.EffectiveTool);

        tools.BeginInvertedEraser();
        Assert.Equal(AppTool.Eraser, tools.EffectiveTool);

        tools.EndInvertedEraser();
        Assert.Equal(AppTool.Pen, tools.EffectiveTool);
    }

    [Fact]
    public void Back_end_works_whatever_tool_is_active()
    {
        var tools = new ToolsViewModel { ActiveTool = AppTool.Eraser };

        tools.BeginInvertedEraser();
        tools.EndInvertedEraser();

        Assert.Equal(AppTool.Eraser, tools.EffectiveTool);
        Assert.Equal(AppTool.Eraser, tools.ActiveTool);
    }

    [Fact]
    public void Base_width_is_clamped_and_converted()
    {
        var tools = new ToolsViewModel();
        Assert.Equal(0.5, tools.BaseWidthMm);

        tools.BaseWidthMm = 10;
        Assert.Equal(5, tools.BaseWidthMm);
        tools.BaseWidthMm = 0.1;
        Assert.Equal(0.25, tools.BaseWidthMm);
        Assert.Equal(Units.MmToPt(tools.BaseWidthMm), tools.BaseWidthPt);
    }

    [Fact]
    public void Curve_controls_enabled_only_for_custom_renderer()
    {
        var tools = new ToolsViewModel();
        Assert.True(tools.IsCustomRenderer);

        tools.Renderer = RendererMode.StockWpf;
        Assert.False(tools.IsCustomRenderer);
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var tools = new ToolsViewModel();

        Assert.Equal(PressureCurve.Linear, tools.Curve);
        Assert.Equal(SmoothingLevel.Light, tools.Smoothing);
        Assert.Equal(EraserMode.WholeStroke, tools.EraserMode);
        Assert.Equal(6, tools.EraserSizeMm);
        Assert.Equal(Palette.Colors[0], tools.Color);
        Assert.Equal(8, Palette.Colors.Count);

        tools.EraserSizeMm = 100;
        Assert.Equal(20, tools.EraserSizeMm);
        tools.EraserSizeMm = 0;
        Assert.Equal(2, tools.EraserSizeMm);
    }
}
