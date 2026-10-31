using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Documents;

public class DocumentSessionMultiPageTests
{
    private static readonly DocumentFingerprint _fingerprint = new(100, 200, new string('a', 64));
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private static InkStroke Stroke(double y) => new(Guid.NewGuid(), InkTool.Pen, _black, 2, [P(10, y), P(600, y)]);

    private static DocumentSession Open(FakePageSource source) => new(source, @"C:\docs\book.pdf", _fingerprint);

    private static void Draw(PageEditor editor, double y)
        => Assert.True(editor.CommitStroke([P(20, y), P(200, y)], InkTool.Pen, _black, 2));

    [Fact]
    public async Task EnsureEditorAsync_returns_the_same_editor_per_page_and_loads_strokes_once()
    {
        var source = new FakePageSource(3);
        var stroke = Stroke(50);
        source.SetStrokes(1, [stroke]);
        using var session = Open(source);
        Assert.False(session.TryGetEditor(1, out var missing));
        Assert.Null(missing);

        var first = await session.EnsureEditorAsync(1);
        var second = await session.EnsureEditorAsync(1);
        await session.GoToPageAsync(1);

        Assert.Same(first, second);
        Assert.Same(first, session.CurrentEditor);
        Assert.Same(await session.EnsurePageAsync(1), first.Page);
        Assert.Equal([stroke], first.Page.Strokes);
        Assert.True(session.TryGetEditor(1, out var tried));
        Assert.Same(first, tried);
        Assert.Equal(1, source.LoadCount(1));
        Assert.Equal(1, source.TotalLoads);
    }

    [Fact]
    public async Task GoToPageAsync_editor_is_the_one_EnsureEditorAsync_returns_later()
    {
        using var session = Open(new FakePageSource(2));

        await session.GoToPageAsync(0);

        Assert.Same(session.CurrentEditor, await session.EnsureEditorAsync(0));
    }

    [Fact]
    public async Task Concurrent_EnsureEditorAsync_calls_share_one_load()
    {
        var source = new FakePageSource(2) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = Open(source);

        var first = session.EnsureEditorAsync(1);
        var second = session.EnsureEditorAsync(1);
        Assert.False(first.IsCompleted);
        Assert.False(session.TryGetEditor(1, out _));
        source.Gate.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, source.LoadCount(1));
    }

    [Fact]
    public async Task A_failed_EnsureEditorAsync_does_not_poison_a_later_attempt()
    {
        var source = new FakePageSource(2) { FailNext = new IOException("boom") };
        using var session = Open(source);

        await Assert.ThrowsAsync<IOException>(() => session.EnsureEditorAsync(0));
        Assert.False(session.TryGetEditor(0, out _));

        var editor = await session.EnsureEditorAsync(0);

        Assert.Equal(0, editor.Page.Index);
        Assert.Equal(2, source.LoadCount(0));
    }

    [Fact]
    public async Task EnsureEditorAsync_rejects_out_of_range_indexes_and_a_disposed_session()
    {
        var session = Open(new FakePageSource(2));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EnsureEditorAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EnsureEditorAsync(2));
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.EnsureEditorAsync(0));
    }

    [Fact]
    public async Task Dispose_during_EnsureEditorAsync_does_not_throw()
    {
        var source = new FakePageSource(2) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = Open(source);
        var dirtyEvents = 0;
        session.DirtyChanged += (_, _) => dirtyEvents++;

        var pending = session.EnsureEditorAsync(1);
        session.Dispose();
        source.Gate.SetResult();
        var editor = await pending;

        Assert.Equal(1, editor.Page.Index);
        Draw(editor, 100);
        Assert.Equal(0, dirtyEvents);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Editors_of_several_pages_share_one_undo_history_and_dirty_state()
    {
        using var session = Open(new FakePageSource(3));
        var flips = new List<bool>();
        session.DirtyChanged += (_, _) => flips.Add(session.IsDirty);

        var zero = await session.EnsureEditorAsync(0);
        var two = await session.EnsureEditorAsync(2);
        Draw(zero, 100);
        Draw(two, 200);

        Assert.Equal(-1, session.CurrentPageIndex);
        Assert.True(session.IsDirty);

        Assert.True(session.Undo.Undo());
        Assert.True(session.IsDirty);
        Assert.True(session.Undo.Undo());
        Assert.False(session.IsDirty);
        Assert.Empty(zero.Page.Strokes);
        Assert.Empty(two.Page.Strokes);
        Assert.Equal([true, false], flips);
    }

    [Fact]
    public async Task Dirty_and_GetChanges_include_pages_edited_through_EnsureEditorAsync()
    {
        var source = new FakePageSource(4);
        var loaded = Stroke(50);
        source.SetStrokes(3, [loaded]);
        using var session = Open(source);
        await session.GoToPageAsync(0);

        var one = await session.EnsureEditorAsync(1);
        var three = await session.EnsureEditorAsync(3);
        Draw(one, 100);
        var eraser = three.BeginErase(EraserMode.WholeStroke);
        Assert.True(eraser.MoveTo(new PointD(100, 50), 3));
        Assert.True(eraser.Complete());

        Assert.True(session.IsDirty);
        var changes = session.GetChanges();
        Assert.Equal([1, 3], changes.Select(c => c.PageIndex));
        Assert.Equal([one.Page.Strokes[0]], changes[0].Added);
        Assert.Equal([loaded.Id], changes[1].RemovedIds);

        session.MarkSaved(session.CaptureSaveSnapshot());
        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());
    }

    [Fact]
    public async Task SetCurrentPageFromView_changes_current_without_loading_or_clearing_undo()
    {
        var source = new FakePageSource(3);
        using var session = Open(source);
        await session.GoToPageAsync(0);
        var editor = session.CurrentEditor!;
        Draw(editor, 100);

        session.SetCurrentPageFromView(2);

        Assert.Equal(2, session.CurrentPageIndex);
        Assert.False(session.IsPageLoaded(2));
        Assert.Equal(0, source.LoadCount(2));
        Assert.True(session.Undo.CanUndo);
        Assert.True(session.IsDirty);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetCurrentPageFromView(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetCurrentPageFromView(3));
        Assert.Equal(2, session.CurrentPageIndex);
    }

    [Fact]
    public async Task SetCurrentPageFromView_raises_only_on_change()
    {
        using var session = Open(new FakePageSource(3));
        var raised = new List<int>();
        session.CurrentPageChanged += (_, _) => raised.Add(session.CurrentPageIndex);

        session.SetCurrentPageFromView(1);
        session.SetCurrentPageFromView(1);
        session.SetCurrentPageFromView(2);
        await session.GoToPageAsync(2); // already current but without editor: GoToPageAsync reports the editor
        await session.GoToPageAsync(2); // already shown with its editor: no event
        session.SetCurrentPageFromView(2);

        Assert.Equal([1, 2, 2], raised);
        Assert.Same(await session.EnsureEditorAsync(2), session.CurrentEditor);
    }

    [Fact]
    public async Task SetCurrentPageFromView_supersedes_a_GoToPageAsync_in_flight()
    {
        var source = new FakePageSource(3) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = Open(source);

        var slow = session.GoToPageAsync(1);
        session.SetCurrentPageFromView(2);
        source.Gate.SetResult();
        await slow;

        Assert.Equal(2, session.CurrentPageIndex);
    }

    [Fact]
    public async Task Disposed_session_ignores_SetCurrentPageFromView()
    {
        var session = Open(new FakePageSource(3));
        await session.GoToPageAsync(0);
        var raised = 0;
        session.CurrentPageChanged += (_, _) => raised++;

        session.Dispose();
        session.SetCurrentPageFromView(2);
        session.SetCurrentPageFromView(99); // ignored too, not thrown

        Assert.Equal(0, session.CurrentPageIndex);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Undo_of_a_command_on_another_page_raises_RevealRequested_and_applies_the_undo()
    {
        using var session = Open(new FakePageSource(3));
        var reveals = new List<int>();
        var pageChanges = 0;
        session.RevealRequested += (_, e) => reveals.Add(e.PageIndex);
        await session.GoToPageAsync(0);
        Draw(session.CurrentEditor!, 100);
        await session.GoToPageAsync(2);
        session.CurrentPageChanged += (_, _) => pageChanges++;

        Assert.True(await session.UndoAsync());

        Assert.Equal([0], reveals);
        Assert.Equal(2, session.CurrentPageIndex);
        Assert.Empty((await session.EnsurePageAsync(0)).Strokes);

        Assert.True(await session.RedoAsync());

        Assert.Equal([0, 0], reveals);
        Assert.Equal(2, session.CurrentPageIndex);
        Assert.Single((await session.EnsurePageAsync(0)).Strokes);
        Assert.Equal(0, pageChanges);
        Assert.False(await session.RedoAsync());
        Assert.Equal([0, 0], reveals);
    }

    [Fact]
    public async Task Undo_does_not_call_GoToPage_anymore_but_GoToPageAsync_still_works()
    {
        var source = new FakePageSource(3);
        using var session = Open(source);
        var pageChanges = 0;
        session.CurrentPageChanged += (_, _) => pageChanges++;
        var one = await session.EnsureEditorAsync(1);
        Draw(one, 100);
        session.SetCurrentPageFromView(2);
        Assert.Equal(1, pageChanges);

        Assert.True(await session.UndoAsync());

        Assert.Equal(2, session.CurrentPageIndex);
        Assert.Null(session.CurrentEditor);
        Assert.False(session.IsPageLoaded(2));
        Assert.Equal(1, pageChanges);

        await session.GoToPageAsync(1);

        Assert.Equal(1, session.CurrentPageIndex);
        Assert.Same(one, session.CurrentEditor);
        Assert.Equal(2, pageChanges);
        Assert.Equal(0, source.LoadCount(2));
    }

    [Fact]
    public async Task Undo_and_redo_on_the_current_page_also_raise_RevealRequested()
    {
        using var session = Open(new FakePageSource(2));
        var reveals = new List<int>();
        session.RevealRequested += (_, e) => reveals.Add(e.PageIndex);
        await session.GoToPageAsync(1);
        Draw(session.CurrentEditor!, 100);

        Assert.True(await session.UndoAsync());
        Assert.True(await session.RedoAsync());

        Assert.Equal([1, 1], reveals); // the view decides whether a reveal needs scrolling
    }

    [Fact]
    public async Task Disposed_session_raises_no_RevealRequested()
    {
        var session = Open(new FakePageSource(2));
        var reveals = 0;
        session.RevealRequested += (_, _) => reveals++;
        await session.GoToPageAsync(0);
        Draw(session.CurrentEditor!, 100);

        session.Dispose();
        await session.UndoAsync();

        Assert.Equal(0, reveals);
    }
}
