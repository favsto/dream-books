using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Documents;

public class DocumentSessionTests
{
    private static readonly DocumentFingerprint _fingerprint = new(100, 200, new string('a', 64));
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private static InkStroke Stroke(double y) => new(Guid.NewGuid(), InkTool.Pen, _black, 2, [P(10, y), P(600, y)]);

    private static DocumentSession Open(FakePageSource source, string path = @"C:\docs\book.pdf")
        => new(source, path, _fingerprint);

    private static void Draw(DocumentSession session, double y)
        => Assert.True(session.CurrentEditor!.CommitStroke([P(20, y), P(200, y)], InkTool.Pen, _black, 2));

    [Fact]
    public async Task Pages_load_lazily_and_only_once()
    {
        var source = new FakePageSource(3);
        var session = Open(source);

        Assert.False(session.IsPageLoaded(1));
        var first = await session.EnsurePageAsync(1);
        var second = await session.EnsurePageAsync(1);
        await session.GoToPageAsync(1);
        await session.GoToPageAsync(0);
        await session.GoToPageAsync(1);

        Assert.Same(first, second);
        Assert.Same(first, session.CurrentEditor!.Page);
        Assert.True(session.IsPageLoaded(1));
        Assert.Equal(1, source.LoadCount(1));
        Assert.Equal(1, source.LoadCount(0));
        Assert.Equal(0, source.LoadCount(2));
        Assert.Equal(1, first.Index);
        Assert.Equal(612, first.WidthPt);
        Assert.Equal(792, first.HeightPt);
    }

    [Fact]
    public async Task Opening_a_thousand_page_document_loads_no_strokes_until_a_page_is_shown()
    {
        var source = new FakePageSource(1000);
        var session = Open(source);

        Assert.Equal(1000, session.PageCount);
        Assert.Equal(0, source.TotalLoads);
        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());
        Assert.Null(session.CurrentEditor);

        await session.GoToPageAsync(500);

        Assert.Equal(1, source.TotalLoads);
        Assert.Equal(1, source.LoadCount(500));
        Assert.Equal(500, session.CurrentPageIndex);
    }

    [Fact]
    public async Task Strokes_survive_changing_page_and_coming_back()
    {
        var source = new FakePageSource(3);
        var loaded = Stroke(50);
        source.SetStrokes(0, [loaded]);
        var session = Open(source);

        await session.GoToPageAsync(0);
        Draw(session, 100);
        var onPageZero = session.CurrentEditor!.Page.Strokes.ToArray();
        await session.GoToPageAsync(2);
        Draw(session, 300);
        await session.GoToPageAsync(0);

        Assert.Equal(onPageZero, session.CurrentEditor!.Page.Strokes);
        Assert.Equal(2, onPageZero.Length);
        Assert.Same(loaded, onPageZero[0]);
        Assert.Single((await session.EnsurePageAsync(2)).Strokes);
        Assert.True(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Undo_history_is_shared_across_pages_and_undo_navigates_to_the_command_page()
    {
        var source = new FakePageSource(3);
        var session = Open(source);
        var pageChanges = 0;
        session.CurrentPageChanged += (_, _) => pageChanges++;

        await session.GoToPageAsync(0);
        Draw(session, 100);
        await session.GoToPageAsync(1);
        Draw(session, 200);
        await session.GoToPageAsync(2);
        Assert.Equal(3, pageChanges);

        Assert.True(await session.UndoAsync());
        Assert.Equal(1, session.CurrentPageIndex);
        Assert.Equal(1, session.CurrentEditor!.Page.Index);
        Assert.Empty((await session.EnsurePageAsync(1)).Strokes);
        Assert.Single((await session.EnsurePageAsync(0)).Strokes);
        Assert.Equal(4, pageChanges);

        Assert.True(await session.UndoAsync());
        Assert.Equal(0, session.CurrentPageIndex);
        Assert.Empty((await session.EnsurePageAsync(0)).Strokes);
        Assert.False(await session.UndoAsync());
        Assert.Equal(5, pageChanges);

        Assert.True(await session.RedoAsync());
        Assert.Equal(0, session.CurrentPageIndex);
        Assert.Equal(5, pageChanges);
        Assert.Single((await session.EnsurePageAsync(0)).Strokes);

        Assert.True(await session.RedoAsync());
        Assert.Equal(1, session.CurrentPageIndex);
        Assert.Single((await session.EnsurePageAsync(1)).Strokes);
        Assert.False(await session.RedoAsync());
        Assert.Equal(6, pageChanges);
    }

    [Fact]
    public async Task IsDirty_is_false_after_open_true_after_commit_false_after_undo_back_to_baseline()
    {
        var source = new FakePageSource(2);
        source.SetStrokes(0, [Stroke(50)]);
        var session = Open(source);
        var flips = new List<bool>();
        session.DirtyChanged += (_, _) => flips.Add(session.IsDirty);

        await session.GoToPageAsync(0);
        Assert.False(session.IsDirty);

        Draw(session, 100);
        Assert.True(session.IsDirty);
        Draw(session, 150);
        Assert.True(session.IsDirty);

        Assert.True(await session.UndoAsync());
        Assert.True(session.IsDirty);
        Assert.True(await session.UndoAsync());
        Assert.False(session.IsDirty);

        Assert.True(await session.RedoAsync());
        Assert.True(session.IsDirty);

        Assert.Equal([true, false, true], flips);
    }

    [Fact]
    public async Task IsDirty_is_false_again_after_MarkSaved_and_true_after_next_change()
    {
        var source = new FakePageSource(2);
        var session = Open(source);
        var flips = new List<bool>();
        session.DirtyChanged += (_, _) => flips.Add(session.IsDirty);

        await session.GoToPageAsync(0);
        Draw(session, 100);
        await session.GoToPageAsync(1);
        Draw(session, 100);
        Assert.True(session.IsDirty);

        session.MarkSaved(session.CaptureSaveSnapshot());
        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());
        Assert.Equal(@"C:\docs\book.pdf", session.Path);

        Draw(session, 200);
        Assert.True(session.IsDirty);
        Assert.True(await session.UndoAsync());
        Assert.False(session.IsDirty);
        // Undoing a change that was already saved makes the document differ from the file again.
        Assert.True(await session.UndoAsync());
        Assert.True(session.IsDirty);
        Assert.Empty((await session.EnsurePageAsync(1)).Strokes);
        var change = Assert.Single(session.GetChanges());
        Assert.Equal(1, change.PageIndex);
        Assert.Single(change.RemovedIds);

        Assert.Equal([true, false, true, false, true], flips);
    }

    [Fact]
    public async Task GetChanges_lists_added_strokes_and_removed_baseline_ids_only()
    {
        var source = new FakePageSource(3);
        var a = Stroke(50);
        var b = Stroke(150);
        source.SetStrokes(1, [a, b]);
        var session = Open(source);

        await session.GoToPageAsync(1);
        var page = session.CurrentEditor!.Page;
        Draw(session, 300);
        Draw(session, 400);
        var added = page.Strokes.Skip(2).ToArray();

        // Erase loaded stroke a and the first new stroke, whole, in two gestures.
        foreach (var y in new[] { 50.0, 300.0 })
        {
            var eraser = session.CurrentEditor.BeginErase(EraserMode.WholeStroke);
            Assert.True(eraser.MoveTo(new PointD(100, y), 3));
            Assert.True(eraser.Complete());
        }

        await session.GoToPageAsync(0);

        var change = Assert.Single(session.GetChanges());
        Assert.Equal(1, change.PageIndex);
        Assert.Equal([a.Id], change.RemovedIds);
        Assert.Equal([added[1]], change.Added);
    }

    [Fact]
    public async Task Erasing_a_loaded_stroke_reports_its_id_as_removed_and_the_pieces_as_added()
    {
        var source = new FakePageSource(1);
        var loaded = Stroke(100);
        var other = Stroke(500);
        source.SetStrokes(0, [loaded, other]);
        var session = Open(source);
        await session.GoToPageAsync(0);

        var eraser = session.CurrentEditor!.BeginErase(EraserMode.Partial);
        Assert.True(eraser.MoveTo(new PointD(300, 100), 5));
        Assert.True(eraser.Complete());

        var page = session.CurrentEditor.Page;
        var pieces = page.Strokes.Where(s => s.Id != other.Id).ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.True(session.IsDirty);

        var change = Assert.Single(session.GetChanges());
        Assert.Equal([loaded.Id], change.RemovedIds);
        Assert.Equal(pieces, change.Added);
        Assert.DoesNotContain(change.Added, s => s.Id == loaded.Id || s.Id == other.Id);

        Assert.True(await session.UndoAsync());
        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());
    }

    [Fact]
    public async Task GetChanges_never_reports_strokes_of_unloaded_pages()
    {
        var source = new FakePageSource(5);
        source.SetStrokes(4, [Stroke(50)]);
        source.SetStrokes(3, [Stroke(50)]);
        var session = Open(source);

        await session.GoToPageAsync(2);
        Draw(session, 100);
        await session.GoToPageAsync(0);
        Draw(session, 100);

        var changes = session.GetChanges();
        Assert.Equal([0, 2], changes.Select(c => c.PageIndex));
        Assert.All(changes, c => Assert.Empty(c.RemovedIds));
        Assert.All(changes, c => Assert.Single(c.Added));
        Assert.False(session.IsPageLoaded(3));
        Assert.False(session.IsPageLoaded(4));
        Assert.Equal(0, source.LoadCount(3));
        Assert.Equal(0, source.LoadCount(4));
    }

    [Fact]
    public async Task MarkSaved_with_a_new_path_rekeys_the_session()
    {
        var source = new FakePageSource(1);
        var session = Open(source);
        await session.GoToPageAsync(0);
        Draw(session, 100);
        var newFingerprint = new DocumentFingerprint(999, 888, new string('b', 64));

        Assert.Equal(@"C:\docs\book.pdf", session.Path);
        Assert.Equal(_fingerprint, session.OpenFingerprint);

        session.MarkSaved(session.CaptureSaveSnapshot(), @"D:\copy.pdf", newFingerprint);

        Assert.Equal(@"D:\copy.pdf", session.Path);
        Assert.Equal(newFingerprint, session.OpenFingerprint);
        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());

        session.MarkSaved(session.CaptureSaveSnapshot());
        Assert.Equal(@"D:\copy.pdf", session.Path);
        Assert.Equal(newFingerprint, session.OpenFingerprint);
    }

    [Fact]
    public async Task Concurrent_EnsurePageAsync_calls_share_one_load()
    {
        var source = new FakePageSource(2) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = Open(source);

        var first = session.EnsurePageAsync(1);
        var second = session.EnsurePageAsync(1);
        Assert.False(first.IsCompleted);
        source.Gate.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, source.LoadCount(1));
    }

    [Fact]
    public async Task Cancelling_one_caller_does_not_cancel_the_shared_load()
    {
        var source = new FakePageSource(2) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), ObserveToken = true };
        var session = Open(source);
        using var cts = new CancellationTokenSource();

        var cancelled = session.EnsurePageAsync(1, cts.Token);
        var other = session.EnsurePageAsync(1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        source.Gate.SetResult();
        var page = await other;

        Assert.Same(page, await session.EnsurePageAsync(1));
        Assert.Equal(1, source.LoadCount(1));
    }

    [Fact]
    public async Task A_failed_load_is_not_cached_and_a_later_call_retries()
    {
        var source = new FakePageSource(2) { FailNext = new IOException("boom") };
        var session = Open(source);

        await Assert.ThrowsAsync<IOException>(() => session.EnsurePageAsync(0));
        Assert.False(session.IsPageLoaded(0));

        var page = await session.EnsurePageAsync(0);

        Assert.True(session.IsPageLoaded(0));
        Assert.Equal(0, page.Index);
        Assert.Equal(2, source.LoadCount(0));
    }

    [Fact]
    public async Task EnsurePageAsync_rejects_out_of_range_indexes()
    {
        var session = Open(new FakePageSource(2));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EnsurePageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EnsurePageAsync(2));
    }

    [Fact]
    public async Task Duplicate_ids_from_the_source_keep_the_first_stroke_only()
    {
        var source = new FakePageSource(1);
        var a = Stroke(50);
        var duplicate = new InkStroke(a.Id, InkTool.Pen, _black, 2, [P(1, 1)]);
        var b = Stroke(150);
        source.SetStrokes(0, [a, duplicate, b]);
        var session = Open(source);

        var page = await session.EnsurePageAsync(0);

        Assert.Equal([a, b], page.Strokes);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task UnreadableAnnotationCount_sums_loaded_pages()
    {
        var source = new FakePageSource(3);
        source.SetStrokes(0, [], unreadable: 2);
        source.SetStrokes(2, [Stroke(50)], unreadable: 1);
        var session = Open(source);
        Assert.Equal(0, session.UnreadableAnnotationCount);

        await session.EnsurePageAsync(0);
        await session.EnsurePageAsync(2);

        Assert.Equal(3, session.UnreadableAnnotationCount);
    }

    [Fact]
    public async Task Stroke_drawn_after_the_snapshot_keeps_the_document_dirty_and_is_in_the_next_changes()
    {
        var session = Open(new FakePageSource(1));
        await session.GoToPageAsync(0);
        Draw(session, 100);
        var snapshot = session.CaptureSaveSnapshot();

        Draw(session, 200); // drawn while the save is running
        var late = session.CurrentEditor!.Page.Strokes[^1];
        session.MarkSaved(snapshot);

        Assert.True(session.IsDirty);
        var change = Assert.Single(session.GetChanges());
        Assert.Equal(0, change.PageIndex);
        Assert.Empty(change.RemovedIds);
        Assert.Equal([late], change.Added);
    }

    [Fact]
    public async Task Erase_after_the_snapshot_keeps_it_dirty_and_the_next_changes_list_the_removal()
    {
        var source = new FakePageSource(2);
        var a = Stroke(50);
        var b = Stroke(50);
        source.SetStrokes(0, [a]);
        source.SetStrokes(1, [b]);
        var session = Open(source);
        await session.EnsurePageAsync(1);
        await session.GoToPageAsync(0);
        Draw(session, 300);
        var snapshot = session.CaptureSaveSnapshot();
        Assert.Equal([0], snapshot.Sequences.Keys);

        // Erased while the save is running: one on the page in the snapshot, one on a page without changes.
        await session.GoToPageAsync(0);
        EraseAt(session, 100, 50);
        await session.GoToPageAsync(1);
        EraseAt(session, 100, 50);
        session.MarkSaved(snapshot);

        Assert.True(session.IsDirty);
        var changes = session.GetChanges();
        Assert.Equal([0, 1], changes.Select(c => c.PageIndex));
        Assert.Equal([a.Id], changes[0].RemovedIds);
        Assert.Equal([b.Id], changes[1].RemovedIds);
        Assert.All(changes, c => Assert.Empty(c.Added));
    }

    [Fact]
    public async Task Undo_of_an_edit_made_before_the_snapshot_after_MarkSaved_reports_it_as_a_change()
    {
        var session = Open(new FakePageSource(1));
        await session.GoToPageAsync(0);
        Draw(session, 100);
        var saved = session.CurrentEditor!.Page.Strokes[0];
        session.MarkSaved(session.CaptureSaveSnapshot());
        Assert.False(session.IsDirty);

        Assert.True(await session.UndoAsync());

        Assert.True(session.IsDirty);
        var change = Assert.Single(session.GetChanges());
        Assert.Equal([saved.Id], change.RemovedIds);
        Assert.Empty(change.Added);
    }

    [Fact]
    public async Task MarkSaved_without_edits_during_the_save_makes_the_document_clean()
    {
        var session = Open(new FakePageSource(2));
        var flips = new List<bool>();
        session.DirtyChanged += (_, _) => flips.Add(session.IsDirty);
        await session.GoToPageAsync(0);
        Draw(session, 100);
        await session.GoToPageAsync(1);
        Draw(session, 100);

        var snapshot = session.CaptureSaveSnapshot();
        Assert.Equal(2, snapshot.Changes.Count);
        Assert.Equal([0, 1], snapshot.Sequences.Keys.Order());
        Assert.Equal(snapshot.Changes.Select(c => c.PageIndex), session.GetChanges().Select(c => c.PageIndex));
        session.MarkSaved(snapshot);

        Assert.False(session.IsDirty);
        Assert.Empty(session.GetChanges());
        Assert.Equal([true, false], flips);
    }

    [Fact]
    public async Task Page_loaded_after_the_snapshot_is_unaffected_by_MarkSaved()
    {
        var source = new FakePageSource(2);
        var b = Stroke(50);
        source.SetStrokes(1, [b]);
        var session = Open(source);
        await session.GoToPageAsync(0);
        Draw(session, 100);
        var snapshot = session.CaptureSaveSnapshot();

        await session.GoToPageAsync(1);
        session.MarkSaved(snapshot);

        Assert.False(session.IsDirty);
        Assert.DoesNotContain(1, snapshot.Sequences.Keys);
        EraseAt(session, 100, 50);
        var change = Assert.Single(session.GetChanges());
        Assert.Equal(1, change.PageIndex);
        Assert.Equal([b.Id], change.RemovedIds);
    }

    [Fact]
    public async Task Erasing_one_loaded_stroke_and_drawing_one_keeps_the_page_dirty()
    {
        var source = new FakePageSource(1);
        var a = Stroke(50);
        var b = Stroke(150);
        source.SetStrokes(0, [a, b]);
        var session = Open(source);
        await session.GoToPageAsync(0);

        EraseAt(session, 100, 50);
        Draw(session, 300);

        var page = session.CurrentEditor!.Page;
        Assert.Equal(2, page.Strokes.Count);
        Assert.True(session.IsDirty);
        var change = Assert.Single(session.GetChanges());
        Assert.Equal([a.Id], change.RemovedIds);
        Assert.Equal([page.Strokes[1]], change.Added);
    }

    [Fact]
    public async Task Disposed_session_no_longer_reacts_to_commands_or_navigation()
    {
        var source = new FakePageSource(2);
        var session = Open(source);
        await session.GoToPageAsync(0);
        var dirtyEvents = 0;
        var pageEvents = 0;
        session.DirtyChanged += (_, _) => dirtyEvents++;
        session.CurrentPageChanged += (_, _) => pageEvents++;
        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.GoToPageAsync(1);

        session.Dispose();
        session.Dispose();
        Draw(session, 100);
        source.Gate.SetResult();
        await pending;

        Assert.False(session.IsDirty);
        Assert.Equal(0, dirtyEvents);
        Assert.Equal(0, pageEvents);
        Assert.Equal(0, session.CurrentPageIndex);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GoToPageAsync(1));
    }

    [Fact]
    public async Task Invalid_navigation_does_not_cancel_a_valid_one_in_flight()
    {
        var source = new FakePageSource(2) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = Open(source);

        var valid = session.GoToPageAsync(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.GoToPageAsync(99));
        source.Gate.SetResult();
        await valid;

        Assert.Equal(1, session.CurrentPageIndex);
    }

    [Fact]
    public async Task A_load_cancelled_by_the_source_surfaces_as_InvalidOperationException()
    {
        var source = new FakePageSource(1) { CancelNext = true };
        var session = Open(source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsurePageAsync(0));

        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(session.IsPageLoaded(0));
        await session.EnsurePageAsync(0);
        Assert.True(session.IsPageLoaded(0));
    }

    private static void EraseAt(DocumentSession session, double x, double y)
    {
        var eraser = session.CurrentEditor!.BeginErase(EraserMode.WholeStroke);
        Assert.True(eraser.MoveTo(new PointD(x, y), 3));
        Assert.True(eraser.Complete());
    }

    [Fact]
    public async Task A_slow_navigation_does_not_override_a_later_one()
    {
        var source = new FakePageSource(3);
        var session = Open(source);
        await session.EnsurePageAsync(2);
        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = session.GoToPageAsync(1);
        await session.GoToPageAsync(2);
        source.Gate.SetResult();
        await slow;

        Assert.Equal(2, session.CurrentPageIndex);
        Assert.True(session.IsPageLoaded(1));
    }

    [Fact]
    public async Task GoToPageAsync_never_clears_undo_and_reuses_one_editor_per_page()
    {
        using var session = new DocumentSession(new FakePageSource(2), @"C:\x.pdf", _fingerprint);
        var undo = session.Undo;
        Assert.Equal(-1, session.CurrentPageIndex);

        await session.GoToPageAsync(0);
        var editor = session.CurrentEditor;
        Draw(session, 100);
        await session.GoToPageAsync(1);
        await session.GoToPageAsync(0);

        Assert.Same(editor, session.CurrentEditor);
        Assert.True(undo.CanUndo);
    }
}
