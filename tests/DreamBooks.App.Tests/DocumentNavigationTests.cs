using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The view model on the document session: pages, undo across pages, notices (FR-26, FR-62, FR-83).</summary>
public sealed class DocumentNavigationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-nav-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public DocumentNavigationTests() => Directory.CreateDirectory(_dir);

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
    public async Task Strokes_drawn_on_page_1_are_still_there_after_visiting_page_2()
    {
        var vm = await OpenFakeAsync(new FakePageSource((612, 792), (300, 400)));
        var stroke = Line(vm, 100);
        var editor = vm.Editor;

        await vm.GoToPageCommand.ExecuteAsync(2);
        Assert.Equal(2, vm.PageNumber);
        Assert.Empty(vm.Editor!.Page.Strokes);

        await vm.GoToPageCommand.ExecuteAsync(1);
        Assert.Equal(1, vm.PageNumber);
        Assert.Same(editor, vm.Editor);
        Assert.Equal([stroke], vm.Editor!.Page.Strokes);
    }

    [Fact]
    public async Task Strokes_survive_page_changes_with_a_real_pdf()
    {
        var path = Path.Combine(_dir, "real.pdf");
        TestPdfFactory.Write(path, (612, 792), (300, 400));
        _dialogs.NextPath = path;
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.NotNull(vm.Session);
            var stroke = Line(vm, 100);

            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Equal(300, vm.PageSize.WidthPt);
            await vm.GoToPageCommand.ExecuteAsync(1);

            Assert.Equal([stroke], vm.Editor!.Page.Strokes);
            Assert.Equal(612, vm.PageSize.WidthPt);
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Page_change_does_not_clear_undo_history()
    {
        var vm = await OpenFakeAsync(new FakePageSource((612, 792), (300, 400)));
        Line(vm, 100);

        await vm.GoToPageCommand.ExecuteAsync(2);

        Assert.True(vm.Undo.CanUndo);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_of_a_stroke_on_another_page_navigates_to_it()
    {
        var vm = await OpenFakeAsync(new FakePageSource((612, 792), (300, 400)));
        Line(vm, 100);
        var first = vm.Editor!;
        await vm.GoToPageCommand.ExecuteAsync(2);
        var pageChanges = 0;
        vm.PageChanged += (_, _) => pageChanges++;

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.PageNumber);
        Assert.Same(first, vm.Editor);
        Assert.Equal(612, vm.PageSize.WidthPt);
        Assert.Equal(1, pageChanges);
        Assert.Empty(first.Page.Strokes);
        Assert.True(vm.RedoCommand.CanExecute(null));

        await vm.GoToPageCommand.ExecuteAsync(2);
        await vm.RedoCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.PageNumber);
        Assert.Single(first.Page.Strokes);
    }

    [Fact]
    public async Task Opening_a_document_does_not_load_strokes_of_other_pages()
    {
        var source = new FakePageSource(Enumerable.Repeat((612.0, 792.0), 1000).ToArray());

        var vm = await OpenFakeAsync(source);

        Assert.Equal(1000, vm.PageCount);
        Assert.Equal([0], source.Loads);

        await vm.GoToPageCommand.ExecuteAsync(500);
        await vm.GoToPageCommand.ExecuteAsync(1);
        Assert.Equal([0, 499], source.Loads); // page 1 is not loaded twice
    }

    [Fact]
    public async Task Erase_in_progress_is_cancelled_on_page_change()
    {
        var vm = await OpenFakeAsync(new FakePageSource((612, 792), (300, 400)));
        var line = Line(vm, 100);
        vm.Undo.Clear();
        var page1 = vm.Editor!.Page;
        vm.BeginErase(vm.Editor!);
        vm.EraseAt(new PointD(100, 100));

        await vm.GoToPageCommand.ExecuteAsync(2);

        Assert.Null(vm.ActiveErase);
        Assert.False(vm.ZoomVm.IsLocked);
        vm.EndErase();
        Assert.False(vm.Undo.CanUndo);
        Assert.Equal([line], page1.Strokes);
    }

    [Fact]
    public async Task Unreadable_annotations_raise_the_notice_once_per_document()
    {
        var source = new FakePageSource((612, 792), (612, 792), (612, 792));
        source.Unreadable[1] = 2;
        source.Unreadable[2] = 1;
        var vm = await OpenFakeAsync(source);
        var notices = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Notice) && vm.Notice is not null)
            {
                notices++;
            }
        };
        Assert.Null(vm.Notice);
        Assert.False(vm.HasUnreadableAnnotations);

        await vm.GoToPageCommand.ExecuteAsync(2);
        Assert.Equal(Strings.NoticeUnreadableAnnotations, vm.Notice);
        Assert.True(vm.HasUnreadableAnnotations);
        await vm.GoToPageCommand.ExecuteAsync(3);
        await vm.GoToPageCommand.ExecuteAsync(1);
        Assert.Equal(1, notices);

        var unreadableFirst = new FakePageSource((612, 792));
        unreadableFirst.Unreadable[0] = 1;
        await OpenFakeAsync(unreadableFirst, vm);
        Assert.Equal(2, notices); // a new document raises it again
        Assert.True(vm.HasUnreadableAnnotations);

        await OpenFakeAsync(new FakePageSource((612, 792)), vm);
        Assert.Null(vm.Notice);
        Assert.False(vm.HasUnreadableAnnotations);
    }

    [Fact]
    public async Task Undo_forwards_to_the_current_session_and_is_inert_without_a_document()
    {
        var vm = new MainViewModel(_dialogs);
        Assert.NotNull(vm.Undo);
        Assert.False(vm.Undo.CanUndo);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null); // no document: nothing happens
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await OpenFakeAsync(new FakePageSource((612, 792)), vm);
        Assert.Contains(nameof(MainViewModel.Undo), changed);
        Assert.Same(vm.Session!.Undo, vm.Undo);
        var firstEditor = vm.Editor!;
        Line(vm, 100);
        Assert.True(vm.UndoCommand.CanExecute(null));

        changed.Clear();
        _dialogs.SaveChangesAnswer = Services.SaveChangesAnswer.DontSave; // the first document is dirty
        await OpenFakeAsync(new FakePageSource((300, 400)), vm);
        Assert.Contains(nameof(MainViewModel.Undo), changed);
        Assert.Same(vm.Session!.Undo, vm.Undo);
        Assert.False(vm.UndoCommand.CanExecute(null));

        var raised = 0;
        vm.UndoCommand.CanExecuteChanged += (_, _) => raised++;
        firstEditor.CommitStroke(
            [new(10, 10, 0.5f, null, null, null, 0), new(20, 20, 0.5f, null, null, null, 10)],
            InkTool.Pen,
            Palette.Colors[0],
            1); // the old document's history no longer drives the commands
        Assert.Equal(0, raised);
        Line(vm, 100);
        Assert.True(raised > 0);
    }

    [Fact]
    public async Task Switching_documents_disposes_the_previous_session()
    {
        var first = new FakePageSource((612, 792));
        var vm = await OpenFakeAsync(first);
        var session = vm.Session!;

        var second = new FakePageSource((300, 400));
        await OpenFakeAsync(second, vm);

        Assert.NotSame(session, vm.Session);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GoToPageAsync(0));
        Assert.Equal(300, vm.PageSize.WidthPt);
    }

    [Fact]
    public async Task Failed_document_open_keeps_the_previous_session()
    {
        var first = new FakePageSource((612, 792));
        var vm = await OpenFakeAsync(first);
        var session = vm.Session;
        var editor = vm.Editor;

        var broken = new FakePageSource((300, 400));
        broken.FailingPages.Add(0);
        await OpenFakeAsync(broken, vm);

        Assert.Equal(Strings.ErrorUnknown, vm.ErrorMessage);
        Assert.Same(session, vm.Session);
        Assert.Same(editor, vm.Editor);
        Assert.False(first.IsDisposed);
        Assert.True(broken.IsDisposed);
    }

    [Fact]
    public async Task Failed_page_navigation_sets_error_and_shows_the_page_without_an_editor()
    {
        var source = new FakePageSource((612, 792), (300, 400));
        source.FailingPages.Add(1);
        var vm = await OpenFakeAsync(source);
        var first = vm.Editor!;
        var changes = 0;
        vm.PageChanged += (_, _) => changes++;

        await vm.GoToPageCommand.ExecuteAsync(2); // must not throw

        // The view is on page 2 (the viewport scrolled there), so the session agrees: page 2 is current but has no
        // editor, like any page whose strokes are not loaded; page 1 keeps its strokes and editor.
        Assert.Equal(Strings.ErrorPageChangeFailed, vm.ErrorMessage);
        Assert.Equal(2, vm.PageNumber);
        Assert.Null(vm.Editor);
        Assert.Equal(300, vm.PageSize.WidthPt);
        Assert.Equal(1, changes);
        Assert.True(vm.Session!.TryGetEditor(0, out var kept));
        Assert.Same(first, kept);
    }

    private Task<MainViewModel> OpenFakeAsync(FakePageSource source) =>
        OpenFakeAsync(source, new MainViewModel(_dialogs));

    private async Task<MainViewModel> OpenFakeAsync(FakePageSource source, MainViewModel vm)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, [1, 2, 3]); // fingerprinted, never parsed
        _dialogs.NextPath = path;
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>Commits a 1 pt horizontal line from x = 50 to x = 150 at <paramref name="y"/>.</summary>
    private static InkStroke Line(MainViewModel vm, double y)
    {
        InkPoint[] points = [new(50, y, 0.5f, null, null, null, 0), new(150, y, 0.5f, null, null, null, 10)];
        Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1));
        return vm.Editor.Page.Strokes[^1];
    }
}
