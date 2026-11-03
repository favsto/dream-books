using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The toolbar inside the window's view model: one eraser mode, the pen's writing signal, clear page.</summary>
public sealed class MainViewModelToolbarTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(100, 200, 0.5f, null, null, null, 0),
        new(300, 200, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-app-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();

    public MainViewModelToolbarTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(true, 2)] // partial: the middle goes, both ends stay
    [InlineData(false, 0)] // whole stroke: the stroke goes
    public async Task The_eraser_panels_mode_reaches_the_pens_back_end(bool partial, int expectedStrokes) // FR-30, FR-31
    {
        var vm = await OpenAsync();
        try
        {
            var editor = vm.Editor!;
            Assert.True(vm.CommitInk(editor, _line, AppTool.Pen, vm.Tools.Color, 2));
            vm.Toolbar.TapTool(ToolbarTool.Highlighter); // any drawing tool: the back end erases anyway

            if (partial)
            {
                vm.Toolbar.Eraser.IsPartial = true;
            }
            else
            {
                vm.Toolbar.Eraser.IsWholeStroke = true;
            }

            vm.Tools.BeginInvertedEraser(); // the pen's back end (the IsInverted path)
            Assert.True(vm.Tools.IsInverted);
            Assert.Equal(AppTool.Eraser, vm.Tools.EffectiveTool);
            vm.BeginErase(editor);
            vm.EraseAt(new PointD(200, 200));
            vm.EndErase();
            vm.Tools.EndInvertedEraser();

            Assert.Equal(expectedStrokes, editor.Page.Strokes.Count);
            Assert.Equal(ToolbarTool.Highlighter, vm.Toolbar.ActiveTool);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_live_stroke_retracts_the_toolbar_and_pen_up_brings_it_back()
    {
        var vm = await OpenAsync();
        try
        {
            vm.Toolbar.TapTool(ToolbarTool.Pen);
            Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);

            vm.ZoomVm.IsLocked = true; // what the ink surface does while a stroke is live
            _scheduler.Advance(ToolbarVisibilityState.WritingBeforeRetract + ToolbarVisibilityState.RetractDuration);
            Assert.Equal(ToolbarPhase.Hidden, vm.Toolbar.Phase);
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);

            vm.ZoomVm.IsLocked = false;
            _scheduler.Advance(ToolbarVisibilityState.PenUpBeforeShow);
            Assert.Equal(ToolbarPhase.Visible, vm.Toolbar.Phase);
            Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Clear_page_clears_the_current_page_and_undo_brings_the_strokes_back()
    {
        var vm = await OpenAsync();
        try
        {
            var editor = vm.Editor!;
            Assert.False(vm.Toolbar.Eraser.ClearPageCommand.CanExecute(null));
            Assert.True(vm.CommitInk(editor, _line, AppTool.Pen, vm.Tools.Color, 2));
            Assert.True(vm.Toolbar.Eraser.ClearPageCommand.CanExecute(null)); // follows the undo history

            vm.Toolbar.Eraser.ClearPageCommand.Execute(null);
            vm.Toolbar.Eraser.ConfirmClearPageCommand.Execute(null);
            Assert.Empty(editor.Page.Strokes);
            Assert.True(vm.IsDirty);
            Assert.False(vm.Toolbar.Eraser.ClearPageCommand.CanExecute(null));

            await vm.UndoCommand.ExecuteAsync(null);
            Assert.Single(editor.Page.Strokes);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    private async Task<MainViewModel> OpenAsync()
    {
        var path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.Write(path, [(612.0, 792.0)]);
        _dialogs.NextPath = path;
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler, clock: () => _scheduler.Now);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        return vm;
    }

    private static async Task CleanUpAsync(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            await renderer.DisposeAsync();
        }
    }
}
