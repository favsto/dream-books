using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

public sealed class MainViewModelHighlighterTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(100, 200, 0.2f, null, null, null, 0),
        new(300, 200, 0.9f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-app-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public MainViewModelHighlighterTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Options_reach_every_loaded_page_editor_and_editors_created_later()
    {
        var vm = await OpenAsync(pages: 3);
        try
        {
            var first = vm.Editor!;
            var second = await vm.EnsureEditorAsync(1);

            vm.Tools.Highlighter.IsChisel = true;
            vm.Tools.Highlighter.IsStraightLine = true;
            vm.Tools.Highlighter.ChiselAngleDeg = 120;

            var expected = vm.Tools.Highlighter.Settings;
            Assert.True(expected.Chisel);
            Assert.True(expected.StraightLine);
            Assert.Same(expected, first.HighlighterSettings);
            Assert.Same(expected, second.HighlighterSettings);

            var third = await vm.EnsureEditorAsync(2); // created after the change
            Assert.Same(expected, third.HighlighterSettings);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_document_opened_after_the_change_uses_the_current_options()
    {
        var vm = new MainViewModel(_dialogs);
        vm.Tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Green);
        vm.Tools.Highlighter.IsStraightLine = true;
        _dialogs.NextPath = WritePdf("a.pdf", 2);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            Assert.Same(vm.Tools.Highlighter.Settings, vm.Session!.HighlighterSettings);
            Assert.Same(vm.Tools.Highlighter.Settings, vm.Editor!.HighlighterSettings);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task With_the_highlighter_the_pen_stroke_is_a_highlighter_stroke()
    {
        var vm = await OpenAsync(pages: 1);
        try
        {
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.LightBlue);
            var editor = vm.Editor!;

            Assert.True(vm.CommitInk(editor, _line, vm.Tools.EffectiveTool, vm.Tools.Color, vm.Tools.BaseWidthPt));

            var stroke = Assert.Single(editor.Page.Strokes);
            Assert.Equal(InkTool.Highlighter, stroke.Tool);
            Assert.Equal(HighlighterPresets.LightBlue, stroke.Color);
            Assert.Equal(HighlighterPresets.Alpha, stroke.Color.A);
            Assert.Equal(HighlighterSettings.Default.RoundWidthPt, stroke.BaseWidthPt);
            Assert.Null(stroke.Chisel);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_chisel_straight_highlight_uses_the_options()
    {
        var vm = await OpenAsync(pages: 1);
        try
        {
            vm.Tools.ActiveTool = AppTool.Highlighter;
            vm.Tools.Highlighter.IsChisel = true;
            vm.Tools.Highlighter.ChiselAngleDeg = 30;
            vm.Tools.Highlighter.IsStraightLine = true;
            var editor = vm.Editor!;
            InkPoint[] wobbly =
            [
                new(100, 200, 0.5f, null, null, null, 0),
                new(200, 200.5, 0.5f, null, null, null, 5),
                new(300, 200, 0.5f, null, null, null, 10),
            ];

            Assert.True(vm.CommitInk(editor, wobbly, AppTool.Highlighter, vm.Tools.Color, vm.Tools.BaseWidthPt));

            var stroke = Assert.Single(editor.Page.Strokes);
            Assert.Equal(InkTool.Highlighter, stroke.Tool);
            Assert.Equal(new ChiselTip(30, HighlighterSettings.ChiselRatio), stroke.Chisel);
            Assert.Equal(HighlighterSettings.Default.ChiselLengthPt, stroke.BaseWidthPt);
            Assert.Equal(2, stroke.Points.Count); // straightened (FR-44)
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task With_the_pen_the_stroke_stays_a_pen_stroke()
    {
        var vm = await OpenAsync(pages: 1);
        try
        {
            vm.Tools.Highlighter.IsChisel = true; // highlighter options never touch the pen
            var editor = vm.Editor!;

            Assert.True(vm.CommitInk(editor, _line, AppTool.Pen, Palette.Colors[0], 1.5));

            var stroke = Assert.Single(editor.Page.Strokes);
            Assert.Equal(InkTool.Pen, stroke.Tool);
            Assert.Equal(Palette.Colors[0], stroke.Color);
            Assert.Equal(1.5, stroke.BaseWidthPt);
            Assert.Null(stroke.Chisel);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task The_eraser_commits_no_ink()
    {
        var vm = await OpenAsync(pages: 1);
        try
        {
            Assert.False(vm.CommitInk(vm.Editor!, _line, AppTool.Eraser, Palette.Colors[0], 1));
            Assert.Empty(vm.Editor!.Page.Strokes);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task The_back_end_erases_a_highlighter_stroke_while_the_highlighter_is_active() // FR-30, FR-34
    {
        var vm = await OpenAsync(pages: 1);
        try
        {
            vm.Tools.ActiveTool = AppTool.Highlighter;
            var editor = vm.Editor!;
            Assert.True(vm.CommitInk(editor, _line, AppTool.Highlighter, vm.Tools.Color, vm.Tools.BaseWidthPt));

            vm.Tools.BeginInvertedEraser();
            vm.BeginErase(editor);
            vm.EraseAt(new DreamBooks.Core.Geometry.PointD(200, 200));
            vm.EndErase();
            vm.Tools.EndInvertedEraser();

            Assert.Empty(editor.Page.Strokes);
            Assert.Equal(AppTool.Highlighter, vm.Tools.EffectiveTool);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    private async Task<MainViewModel> OpenAsync(int pages)
    {
        _dialogs.NextPath = WritePdf("a.pdf", pages);
        var vm = new MainViewModel(_dialogs);
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

    private string WritePdf(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, Enumerable.Repeat((612.0, 792.0), pages).ToArray());
        return path;
    }
}
