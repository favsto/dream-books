using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
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
    public async Task Open_with_unsaved_strokes_asks_and_keeps_page_on_no()
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
            Assert.Empty(_dialogs.ConfirmMessages);

            _dialogs.NextPath = second;
            _dialogs.ConfirmResult = false;
            await vm.OpenPdfCommand.ExecuteAsync(null);

            Assert.Equal([Strings.DiscardStrokesConfirm], _dialogs.ConfirmMessages);
            Assert.Same(editor, vm.Editor);
            Assert.Same(renderer, vm.Renderer);
            Assert.Single(vm.Editor!.Page.Strokes);
            Assert.True(vm.UndoCommand.CanExecute(null));

            _dialogs.ConfirmResult = true;
            await vm.OpenPdfCommand.ExecuteAsync(null);

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
    public async Task Page_change_with_strokes_asks_and_keeps_page_on_no()
    {
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400));
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Draw(vm);

            _dialogs.ConfirmResult = false;
            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Equal(1, vm.PageNumber);
            Assert.Single(vm.Editor!.Page.Strokes);

            _dialogs.ConfirmResult = true;
            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Equal(2, vm.PageNumber);
            Assert.Empty(vm.Editor!.Page.Strokes);
            Assert.False(vm.UndoCommand.CanExecute(null));
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
}
