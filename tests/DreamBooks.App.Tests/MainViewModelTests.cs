using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private static readonly InkPoint[] _dot =
    [
        new(100, 100, 0.5f, null, null, null, 0),
        new(120, 120, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-app-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public MainViewModelTests() => Directory.CreateDirectory(_dir);

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
    public async Task Open_pdf_shows_page_one()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorMessage);
            Assert.Equal(1, vm.PageNumber);
            Assert.Equal(2, vm.PageCount);
            Assert.Equal(612, vm.PageSize.WidthPt);
            Assert.NotNull(vm.Editor);

            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Equal(2, vm.PageNumber);
            Assert.Equal(300, vm.Editor!.Page.WidthPt);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Undo_redo_commands_follow_UndoManager_state()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.False(vm.RedoCommand.CanExecute(null));

            var raised = 0;
            vm.UndoCommand.CanExecuteChanged += (_, _) => raised++;
            Draw(vm);
            Assert.True(vm.UndoCommand.CanExecute(null));
            Assert.False(vm.RedoCommand.CanExecute(null));
            Assert.True(raised > 0);

            vm.UndoCommand.Execute(null);
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.True(vm.RedoCommand.CanExecute(null));
            Assert.Empty(vm.Editor!.Page.Strokes);

            vm.RedoCommand.Execute(null);
            Assert.Single(vm.Editor.Page.Strokes);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Opening_another_document_after_Dont_save_replaces_the_session()
    {
        var first = WritePdf("a.pdf", (612, 792));
        var second = WritePdf("b.pdf", (300, 400));
        var vm = new MainViewModel(_dialogs);
        try
        {
            _dialogs.NextPath = first;
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Draw(vm);
            var editor = vm.Editor;
            var renderer = vm.Renderer;
            var session = vm.Session;

            _dialogs.NextPath = second;
            _dialogs.SaveChangesAnswer = Services.SaveChangesAnswer.DontSave;
            await vm.OpenPdfCommand.ExecuteAsync(null);

            Assert.Equal(1, _dialogs.SaveChangesAsks);
            Assert.NotSame(session, vm.Session);
            Assert.NotSame(editor, vm.Editor);
            Assert.NotSame(renderer, vm.Renderer);
            Assert.Empty(vm.Editor!.Page.Strokes);
            Assert.Equal(300, vm.PageSize.WidthPt);
            Assert.False(vm.UndoCommand.CanExecute(null));
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Page_change_keeps_the_strokes_and_does_not_ask()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Draw(vm);

            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Equal(2, vm.PageNumber);
            Assert.Empty(vm.Editor!.Page.Strokes);
            Assert.True(vm.UndoCommand.CanExecute(null));

            await vm.GoToPageCommand.ExecuteAsync(1);
            Assert.Single(vm.Editor!.Page.Strokes);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Open_damaged_pdf_sets_error_message_without_throwing()
    {
        var bad = Path.Combine(_dir, "bad.pdf");
        await File.WriteAllTextAsync(bad, "this is not a pdf");
        _dialogs.NextPath = bad;
        var vm = new MainViewModel(_dialogs);

        await vm.OpenPdfCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorInvalidFormat, vm.ErrorMessage);
        Assert.Null(vm.Renderer);
        Assert.Null(vm.Editor);
    }

    [Fact]
    public async Task Open_missing_file_sets_not_found_and_keeps_previous_document()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            var renderer = vm.Renderer;

            _dialogs.NextPath = Path.Combine(_dir, "missing.pdf");
            await vm.OpenPdfCommand.ExecuteAsync(null);

            Assert.Equal(Strings.ErrorFileNotFound, vm.ErrorMessage);
            Assert.Same(renderer, vm.Renderer);
            Assert.NotNull(vm.Editor);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Erase_gesture_is_one_undo_step()
    {
        var vm = await OpenAsync();
        try
        {
            var top = Line(vm, 100);
            var middle = Line(vm, 200);
            var bottom = Line(vm, 300);
            vm.Tools.EraserMode = EraserMode.WholeStroke;
            vm.Undo.Clear();

            vm.BeginErase(vm.Editor!);
            Assert.NotNull(vm.ActiveErase);
            vm.EraseAt(new PointD(100, 90));
            vm.EraseAt(new PointD(100, 210)); // the sweep crosses the top and middle lines
            Assert.Equal([bottom], vm.ActiveErase!.Strokes);
            Assert.Equal(3, vm.Editor!.Page.Strokes.Count); // the page changes only on EndErase
            vm.EndErase();

            Assert.Null(vm.ActiveErase);
            Assert.Equal([bottom], vm.Editor.Page.Strokes);
            Assert.True(vm.Undo.Undo());
            Assert.Equal([top, middle, bottom], vm.Editor.Page.Strokes);
            Assert.False(vm.Undo.CanUndo);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Erase_uses_selected_size_and_mode()
    {
        var vm = await OpenAsync();
        try
        {
            var line = Line(vm, 100);

            // 2 mm eraser (radius 2.83 pt) 8 pt from the line: no hit.
            vm.Tools.EraserMode = EraserMode.Partial;
            vm.Tools.EraserSizeMm = 2;
            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(100, 108));
            Assert.Equal([line], vm.ActiveErase!.Strokes);
            vm.EndErase();

            // 10 mm eraser (radius 14.2 pt) at the same spot: partial mode splits the line in two.
            vm.Tools.EraserSizeMm = 10;
            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(100, 108));
            vm.EndErase();

            var pieces = vm.Editor!.Page.Strokes;
            Assert.Equal(2, pieces.Count);
            Assert.DoesNotContain(line, pieces);
            var radius = Units.MmToPt(10) / 2;
            var halfChord = Math.Sqrt((radius * radius) - (8 * 8));
            Assert.Equal(100 - halfChord, pieces[0].Points[^1].X, 0);
            Assert.Equal(100 + halfChord, pieces[1].Points[0].X, 0);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task EndErase_without_hits_adds_no_undo_step()
    {
        var vm = await OpenAsync();
        try
        {
            Line(vm, 100);
            vm.Undo.Clear();
            var notifications = 0;
            vm.ErasePreviewChanged += (_, _) => notifications++;

            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(300, 500));
            vm.EndErase();

            Assert.Null(vm.ActiveErase);
            Assert.False(vm.Undo.CanUndo);
            Assert.Single(vm.Editor!.Page.Strokes);
            Assert.Equal(2, notifications); // begin and end: the view goes back to the page strokes
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Erase_gesture_locks_zoom_and_unlocks_it_on_end_and_cancel()
    {
        var vm = await OpenAsync();
        try
        {
            Line(vm, 100);

            vm.BeginErase(vm.Editor!);
            Assert.True(vm.ZoomVm.IsLocked);
            vm.EndErase();
            Assert.False(vm.ZoomVm.IsLocked);

            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(100, 100));
            vm.CancelErase();
            Assert.False(vm.ZoomVm.IsLocked);
            Assert.Null(vm.ActiveErase);
            Assert.Single(vm.Editor!.Page.Strokes); // cancel commits nothing

            vm.EndErase(); // no live gesture: no-op
            vm.CancelErase();
            vm.EraseAt(new PointD(100, 100));
            Assert.Single(vm.Editor.Page.Strokes);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task BeginErase_twice_keeps_the_live_session()
    {
        var vm = await OpenAsync();
        try
        {
            Line(vm, 100);
            vm.BeginErase(vm.Editor!);
            var session = vm.ActiveErase;
            vm.BeginErase(vm.Editor!);
            Assert.Same(session, vm.ActiveErase);
            vm.EndErase();
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Page_change_cancels_a_live_erase()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            vm.BeginErase(vm.Editor!);

            await vm.GoToPageCommand.ExecuteAsync(2);

            Assert.Null(vm.ActiveErase);
            Assert.False(vm.ZoomVm.IsLocked);
            vm.EndErase();
            Assert.False(vm.Undo.CanUndo);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Undo_during_an_erase_cancels_it_instead_of_overwriting_the_page()
    {
        var vm = await OpenAsync();
        try
        {
            var first = Line(vm, 100);
            Line(vm, 200);
            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(100, 100));

            vm.UndoCommand.Execute(null); // keyboard Ctrl+Z while the pen is down

            Assert.Null(vm.ActiveErase);
            vm.EndErase();
            Assert.Equal([first], vm.Editor!.Page.Strokes);
            Assert.False(vm.ZoomVm.IsLocked);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public void Erase_without_a_document_does_nothing()
    {
        var vm = new MainViewModel(_dialogs);
        vm.BeginErase(new PageEditor(new Page(0, 100, 100), new DreamBooks.Core.Undo.UndoManager())); // not this document's
        Assert.Null(vm.ActiveErasePage);
        vm.EraseAt(new PointD(1, 1));
        vm.EndErase();
        Assert.Null(vm.ActiveErase);
        Assert.False(vm.ZoomVm.IsLocked);
    }

    private async Task<MainViewModel> OpenAsync()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var vm = new MainViewModel(_dialogs);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        return vm;
    }

    /// <summary>Commits a 1 pt horizontal line from x = 50 to x = 150 at <paramref name="y"/>.</summary>
    private static InkStroke Line(MainViewModel vm, double y)
    {
        InkPoint[] points = [new(50, y, 0.5f, null, null, null, 0), new(150, y, 0.5f, null, null, null, 10)];
        Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1));
        return vm.Editor.Page.Strokes[^1];
    }

    private static void Draw(MainViewModel vm) =>
        Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));

    private static async Task CleanUpAsync(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            await renderer.DisposeAsync();
        }
    }

    private string WritePdf(string name, params (double w, double h)[] pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, pages);
        return path;
    }

    [Fact]
    public async Task Unexpected_open_failure_shows_unknown_error_and_keeps_previous_document() // NFR-11
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            var renderer = vm.Renderer;
            var editor = vm.Editor;
            vm.OpenDocument = (_, _) => throw new InvalidOperationException("boom");

            await vm.OpenPdfCommand.ExecuteAsync(null); // must not throw

            Assert.Equal(Strings.ErrorUnknown, vm.ErrorMessage);
            Assert.Same(renderer, vm.Renderer);
            Assert.Same(editor, vm.Editor);
            Assert.Equal(1, vm.PageNumber);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Cancelled_open_shows_no_error()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var vm = new MainViewModel(_dialogs);
        vm.OpenDocument = (_, _) => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.OpenPdfCommand.ExecuteAsync(null));

        Assert.Null(vm.ErrorMessage);
    }
}
