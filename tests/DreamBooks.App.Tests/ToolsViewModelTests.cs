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
    public void Selecting_the_highlighter_changes_the_effective_tool_and_keeps_the_back_end_erase() // FR-30
    {
        var tools = new ToolsViewModel();
        var changed = new List<string?>();
        tools.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tools.ActiveTool = AppTool.Highlighter;

        Assert.Equal(AppTool.Highlighter, tools.EffectiveTool);
        Assert.True(tools.IsDrawingTool);
        Assert.Contains(nameof(ToolsViewModel.EffectiveTool), changed);
        Assert.Contains(nameof(ToolsViewModel.IsDrawingTool), changed);

        tools.BeginInvertedEraser();
        Assert.Equal(AppTool.Eraser, tools.EffectiveTool);
        Assert.False(tools.IsDrawingTool);

        tools.EndInvertedEraser();
        Assert.Equal(AppTool.Highlighter, tools.EffectiveTool);
        Assert.Equal(AppTool.Highlighter, tools.ActiveTool);
    }

    [Fact]
    public void Pen_and_highlighter_draw_the_eraser_does_not()
    {
        Assert.True(new ToolsViewModel { ActiveTool = AppTool.Pen }.IsDrawingTool);
        Assert.True(new ToolsViewModel { ActiveTool = AppTool.Highlighter }.IsDrawingTool);
        Assert.False(new ToolsViewModel { ActiveTool = AppTool.Eraser }.IsDrawingTool);
    }

    [Fact]
    public void The_highlighter_is_a_toggle_next_to_the_pen()
    {
        var tools = new ToolsViewModel();

        tools.ToggleHighlighterCommand.Execute(null);
        Assert.Equal(AppTool.Highlighter, tools.ActiveTool);

        tools.ToggleHighlighterCommand.Execute(null);
        Assert.Equal(AppTool.Pen, tools.ActiveTool);

        tools.ActiveTool = AppTool.Eraser;
        tools.ToggleHighlighterCommand.Execute(null);
        Assert.Equal(AppTool.Highlighter, tools.ActiveTool);
    }

    [Fact]
    public void The_live_stroke_takes_the_colour_and_width_of_the_active_tool()
    {
        var tools = new ToolsViewModel();
        Assert.Equal(tools.Color, tools.ActiveInkColor);
        Assert.Equal(tools.BaseWidthPt, tools.ActiveInkWidthPt);

        tools.ActiveTool = AppTool.Highlighter;
        tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Pink);
        Assert.Equal(HighlighterPresets.Pink, tools.ActiveInkColor);
        Assert.Equal(HighlighterPresets.Alpha, tools.ActiveInkColor.A);
        Assert.Equal(HighlighterSettings.Default.RoundWidthPt, tools.ActiveInkWidthPt);

        tools.Highlighter.IsChisel = true;
        Assert.Equal(HighlighterSettings.Default.ChiselLengthPt, tools.ActiveInkWidthPt);
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
    public void Tools_expose_no_renderer_choice()
    {
        var properties = typeof(ToolsViewModel).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Renderer", properties);
        Assert.DoesNotContain("IsCustomRenderer", properties);
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var tools = new ToolsViewModel();

        Assert.Equal(PressureCurve.Firm, tools.Curve);
        Assert.Equal(SmoothingLevel.Strong, tools.Smoothing);
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
