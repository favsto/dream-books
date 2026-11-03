using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;
using DreamBooks.Core.Undo;

namespace DreamBooks.App.Tests;

/// <summary>The pen, highlighter and eraser option panels and the shared custom palette (UI-02, FR-25, FR-31).</summary>
public class OptionPanelsViewModelTests
{
    private static readonly InkColor _teal = InkColor.FromRgb(0x00, 0x80, 0x80);

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    [Fact]
    public void The_pen_offers_eight_named_presets_and_three_widths()
    {
        var pen = new PenOptionsViewModel(new ToolsViewModel());

        Assert.Equal(8, pen.Presets.Count);
        Assert.Equal(Palette.Colors, pen.Presets.Select(p => p.Color));
        Assert.Equal(8, pen.Presets.Select(p => p.Color).Distinct().Count());
        Assert.All(pen.Presets, p => Assert.Equal(255, p.Color.A));
        Assert.Equal(
            [Strings.PenBlack, Strings.PenDarkBlue, Strings.PenBlue, Strings.PenRed, Strings.PenGreen, Strings.PenOrange, Strings.PenPurple, Strings.PenGrey],
            pen.Presets.Select(p => p.Name));
        Assert.Equal(3, pen.WidthPresets.Count);
        Assert.All(pen.WidthPresets, w => Assert.InRange(w, AppSettings.MinPenWidthMm, AppSettings.MaxPenWidthMm));
    }

    [Fact]
    public void Pen_commands_change_the_tools_colour_and_width()
    {
        var tools = new ToolsViewModel();
        var pen = new PenOptionsViewModel(tools);
        var changed = new List<string?>();
        pen.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pen.SelectColorCommand.Execute(Palette.Colors[3]);
        pen.SelectWidthCommand.Execute(pen.WidthPresets[2]);

        Assert.Equal(Palette.Colors[3], tools.Color);
        Assert.Equal(Palette.Colors[3], pen.Color);
        Assert.Equal(pen.WidthPresets[2], tools.BaseWidthMm);
        Assert.Contains(nameof(PenOptionsViewModel.Color), changed);
        Assert.Contains(nameof(PenOptionsViewModel.WidthMm), changed);

        pen.WidthMm = 1.7; // the slider
        Assert.Equal(1.7, tools.BaseWidthMm);
        pen.WidthMm = 99;
        Assert.Equal(AppSettings.MaxPenWidthMm, pen.WidthMm);
    }

    [Fact]
    public void A_colour_from_the_picker_is_added_to_the_palette_and_selected()
    {
        var tools = new ToolsViewModel();
        var pen = new PenOptionsViewModel(tools);

        pen.AddCustomColorCommand.Execute(_teal with { A = 10 }); // the picker's alpha is not kept

        Assert.Equal([_teal], pen.CustomColors);
        Assert.Equal(_teal, tools.Color);
        Assert.Equal(["#008080"], tools.Palette.Palette.Colors);
    }

    [Fact]
    public void A_custom_colour_can_be_removed_and_the_pen_keeps_its_colour()
    {
        var tools = new ToolsViewModel();
        var pen = new PenOptionsViewModel(tools);
        pen.AddCustomColorCommand.Execute(_teal);

        pen.RemoveCustomColorCommand.Execute(_teal);

        Assert.Empty(pen.CustomColors);
        Assert.Equal(_teal, tools.Color);
    }

    [Fact]
    public void The_palette_keeps_the_newest_twelve_colours()
    {
        var tools = new ToolsViewModel();
        var pen = new PenOptionsViewModel(tools);

        for (var i = 0; i < 14; i++)
        {
            pen.AddCustomColorCommand.Execute(InkColor.FromRgb((byte)i, 0, 0));
        }

        Assert.Equal(CustomPalette.MaxColors, pen.CustomColors.Count);
        Assert.Equal(InkColor.FromRgb(2, 0, 0), pen.CustomColors[0]);
        Assert.Equal(InkColor.FromRgb(13, 0, 0), pen.CustomColors[^1]);
    }

    [Fact]
    public void The_highlighter_uses_the_same_palette_with_its_alpha()
    {
        var tools = new ToolsViewModel();
        var pen = new PenOptionsViewModel(tools);
        var highlighter = tools.Highlighter;
        var changed = new List<string?>();
        highlighter.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pen.AddCustomColorCommand.Execute(_teal);

        Assert.Equal([_teal with { A = HighlighterPresets.Alpha }], highlighter.CustomColors);
        Assert.Contains(nameof(HighlighterOptionsViewModel.CustomColors), changed);

        var magenta = InkColor.FromRgb(0xFF, 0x00, 0xFF);
        highlighter.AddCustomColorCommand.Execute(magenta);
        Assert.Equal(magenta with { A = HighlighterPresets.Alpha }, highlighter.Color);
        Assert.Equal(HighlighterPresets.Alpha, highlighter.Settings.StrokeColor.A);
        Assert.Equal([_teal, magenta], pen.CustomColors);
        Assert.Equal(_teal, tools.Color); // the pen did not change

        highlighter.SelectColorCommand.Execute(highlighter.CustomColors[0]);
        Assert.Equal(_teal with { A = HighlighterPresets.Alpha }, highlighter.Color);

        highlighter.RemoveCustomColorCommand.Execute(highlighter.CustomColors[0]);
        Assert.Equal([magenta], pen.CustomColors);
    }

    [Fact]
    public void The_eraser_panel_sets_the_one_eraser_mode_and_the_size()
    {
        var tools = new ToolsViewModel();
        var eraser = new EraserOptionsViewModel(tools, () => null);
        Assert.True(eraser.IsWholeStroke);

        eraser.IsPartial = true;
        Assert.Equal(EraserMode.Partial, tools.EraserMode);
        Assert.False(eraser.IsWholeStroke);

        eraser.IsWholeStroke = true;
        Assert.Equal(EraserMode.WholeStroke, tools.EraserMode);

        eraser.SizeMm = 12;
        Assert.Equal(12, tools.EraserSizeMm);
        eraser.SizeMm = 99;
        Assert.Equal(AppSettings.MaxEraserSizeMm, eraser.SizeMm);
    }

    [Fact]
    public void Clearing_the_page_needs_the_confirmation_step_and_is_undoable()
    {
        var page = new Page(0, 200, 200);
        var undo = new UndoManager();
        var editor = new PageEditor(page, undo);
        Assert.True(editor.CommitStroke([P(10, 10), P(50, 50)], InkTool.Pen, Palette.Colors[0], 2));
        Assert.True(editor.CommitStroke([P(60, 10), P(90, 50)], InkTool.Pen, Palette.Colors[1], 2));
        var eraser = new EraserOptionsViewModel(new ToolsViewModel(), () => editor);

        Assert.False(eraser.ConfirmClearPageCommand.CanExecute(null));
        eraser.ConfirmClearPageCommand.Execute(null); // no request yet: nothing happens
        Assert.Equal(2, page.Strokes.Count);

        Assert.True(eraser.ClearPageCommand.CanExecute(null));
        eraser.ClearPageCommand.Execute(null);
        Assert.True(eraser.IsConfirmingClear);
        Assert.Equal(2, page.Strokes.Count); // asked, not done

        eraser.CancelClearPageCommand.Execute(null);
        Assert.False(eraser.IsConfirmingClear);
        Assert.Equal(2, page.Strokes.Count);

        eraser.ClearPageCommand.Execute(null);
        eraser.ConfirmClearPageCommand.Execute(null);
        Assert.False(eraser.IsConfirmingClear);
        Assert.Empty(page.Strokes);

        Assert.True(undo.Undo());
        Assert.Equal(2, page.Strokes.Count);
    }

    [Fact]
    public void Clear_page_is_disabled_without_a_page_or_without_strokes()
    {
        Assert.False(new EraserOptionsViewModel(new ToolsViewModel(), () => null).ClearPageCommand.CanExecute(null));

        var editor = new PageEditor(new Page(0, 100, 100), new UndoManager());
        Assert.False(new EraserOptionsViewModel(new ToolsViewModel(), () => editor).ClearPageCommand.CanExecute(null));
    }

    [Fact]
    public void A_confirmation_for_a_page_that_is_no_longer_current_clears_nothing()
    {
        var undo = new UndoManager();
        var first = new PageEditor(new Page(0, 100, 100), undo);
        var second = new PageEditor(new Page(1, 100, 100), undo);
        Assert.True(first.CommitStroke([P(10, 10), P(50, 50)], InkTool.Pen, Palette.Colors[0], 2));
        Assert.True(second.CommitStroke([P(10, 10), P(50, 50)], InkTool.Pen, Palette.Colors[0], 2));
        var current = first;
        var eraser = new EraserOptionsViewModel(new ToolsViewModel(), () => current);

        eraser.ClearPageCommand.Execute(null);
        current = second;
        eraser.ConfirmClearPageCommand.Execute(null);

        Assert.Single(first.Page.Strokes);
        Assert.Single(second.Page.Strokes);
        Assert.False(eraser.IsConfirmingClear);
    }
}
