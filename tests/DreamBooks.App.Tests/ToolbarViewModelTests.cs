using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Tests;

/// <summary>The toolbar's tools and the "second tap opens the panel" state machine (UI-02).</summary>
public class ToolbarViewModelTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly ToolsViewModel _tools = new();

    private ToolbarViewModel Create(RelayCommand? undo = null, RelayCommand? redo = null) =>
        new(_tools, undo ?? new RelayCommand(() => { }), redo ?? new RelayCommand(() => { }), () => null, _scheduler, () => _scheduler.Now);

    [Fact]
    public void The_toolbar_has_seven_buttons_in_order_with_italian_names_and_two_disabled()
    {
        var toolbar = Create();

        Assert.Equal(
            [
                ToolbarButtonKind.Pen, ToolbarButtonKind.Highlighter, ToolbarButtonKind.Eraser, ToolbarButtonKind.Selection,
                ToolbarButtonKind.Undo, ToolbarButtonKind.Redo, ToolbarButtonKind.Bookmark,
            ],
            toolbar.Buttons.Select(b => b.Kind));
        Assert.Equal(
            [Strings.ToolPen, Strings.ToolHighlighter, Strings.ToolEraser, Strings.ToolSelection, Strings.Undo, Strings.Redo, Strings.ToolBookmark],
            toolbar.Buttons.Select(b => b.Name));
        Assert.All(toolbar.Buttons, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));
        Assert.False(toolbar.Buttons.Single(b => b.Kind == ToolbarButtonKind.Selection).Command.CanExecute(null));
        Assert.False(toolbar.Buttons.Single(b => b.Kind == ToolbarButtonKind.Bookmark).Command.CanExecute(null));
        Assert.True(toolbar.Buttons.Single(b => b.Kind == ToolbarButtonKind.Pen).Command.CanExecute(null));
        Assert.Equal([true, true, true, true, false, false, false], toolbar.Buttons.Select(b => b.IsTool));
    }

    [Fact]
    public void Undo_and_redo_buttons_run_the_documents_commands()
    {
        var undone = 0;
        var redone = 0;
        var toolbar = Create(new RelayCommand(() => undone++), new RelayCommand(() => redone++));

        toolbar.Buttons.Single(b => b.Kind == ToolbarButtonKind.Undo).Command.Execute(null);
        toolbar.Buttons.Single(b => b.Kind == ToolbarButtonKind.Redo).Command.Execute(null);

        Assert.Equal(1, undone);
        Assert.Equal(1, redone);
    }

    [Theory]
    [InlineData(ToolbarTool.Highlighter, AppTool.Highlighter)]
    [InlineData(ToolbarTool.Eraser, AppTool.Eraser)]
    public void A_tap_on_an_inactive_tool_activates_it_without_opening_its_panel(ToolbarTool tool, AppTool expected)
    {
        var toolbar = Create();
        Assert.Equal(ToolbarTool.Pen, toolbar.ActiveTool);

        toolbar.TapTool(tool);

        Assert.Equal(tool, toolbar.ActiveTool);
        Assert.Equal(expected, _tools.ActiveTool);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
    }

    [Theory]
    [InlineData(ToolbarTool.Pen, OptionsPanel.Pen)]
    [InlineData(ToolbarTool.Highlighter, OptionsPanel.Highlighter)]
    [InlineData(ToolbarTool.Eraser, OptionsPanel.Eraser)]
    public void A_second_tap_on_the_active_tool_opens_its_panel_and_a_third_closes_it(ToolbarTool tool, OptionsPanel panel)
    {
        var toolbar = Create();
        _tools.ActiveTool = tool == ToolbarTool.Pen ? AppTool.Eraser : AppTool.Pen; // start from another tool
        toolbar.TapTool(tool); // first tap: activates
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);

        toolbar.TapTool(tool);
        Assert.Equal(panel, toolbar.OpenPanel);
        Assert.Equal(tool, toolbar.ActiveTool);

        toolbar.TapTool(tool);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Equal(tool, toolbar.ActiveTool);
    }

    [Fact]
    public void The_first_tap_on_the_pen_active_at_startup_opens_its_panel() // UI-02: the pen is already the active tool
    {
        var toolbar = Create();

        toolbar.TapTool(ToolbarTool.Pen);

        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);
    }

    [Fact]
    public void A_tap_on_another_tool_while_a_panel_is_open_switches_tool_and_closes_the_panel()
    {
        var toolbar = Create();
        toolbar.TapTool(ToolbarTool.Pen); // already active: opens the pen panel
        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);

        toolbar.TapTool(ToolbarTool.Eraser);

        Assert.Equal(ToolbarTool.Eraser, toolbar.ActiveTool);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
    }

    [Fact]
    public void Disabled_tools_cannot_become_active_and_leave_the_panel_as_it_is()
    {
        var toolbar = Create();
        toolbar.TapTool(ToolbarTool.Pen);
        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);

        toolbar.TapTool(ToolbarTool.Selection);
        toolbar.TapToolCommand.Execute(ToolbarTool.Selection);

        Assert.Equal(ToolbarTool.Pen, toolbar.ActiveTool);
        Assert.Equal(AppTool.Pen, _tools.ActiveTool);
        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);
        Assert.False(toolbar.TapToolCommand.CanExecute(ToolbarTool.Selection));
        Assert.True(toolbar.TapToolCommand.CanExecute(ToolbarTool.Eraser));
    }

    [Fact]
    public void Closing_the_panel_keeps_the_tool()
    {
        var toolbar = Create();
        toolbar.TapTool(ToolbarTool.Highlighter);
        toolbar.TapTool(ToolbarTool.Highlighter);

        toolbar.ClosePanel();

        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Equal(ToolbarTool.Highlighter, toolbar.ActiveTool);
    }

    [Fact]
    public void A_tool_change_from_elsewhere_follows_and_closes_a_panel_of_another_tool()
    {
        var toolbar = Create();
        var changed = new List<string?>();
        toolbar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        toolbar.TapTool(ToolbarTool.Pen);

        _tools.ActiveTool = AppTool.Highlighter; // e.g. a keyboard shortcut

        Assert.Equal(ToolbarTool.Highlighter, toolbar.ActiveTool);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Contains(nameof(ToolbarViewModel.ActiveTool), changed);
        Assert.Contains(nameof(ToolbarViewModel.OpenPanel), changed);
    }

    [Fact]
    public void The_back_end_of_the_pen_does_not_move_the_active_tool() // FR-30
    {
        var toolbar = Create();
        toolbar.TapTool(ToolbarTool.Highlighter);

        _tools.BeginInvertedEraser();
        Assert.Equal(ToolbarTool.Highlighter, toolbar.ActiveTool);
        _tools.EndInvertedEraser();

        Assert.Equal(ToolbarTool.Highlighter, toolbar.ActiveTool);
    }

    [Fact]
    public void The_option_panels_are_the_tools_ones()
    {
        var toolbar = Create();

        Assert.Same(_tools.Highlighter, toolbar.Highlighter);
        Assert.NotNull(toolbar.Pen);
        Assert.NotNull(toolbar.Eraser);
    }
}
