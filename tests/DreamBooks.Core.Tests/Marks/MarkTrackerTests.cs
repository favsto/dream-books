using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Core.Tests.Documents;

namespace DreamBooks.Core.Tests.Marks;

public class MarkTrackerTests
{
    private static readonly DocumentFingerprint _fingerprint = new(100, 200, new string('a', 64));
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static InkPoint[] LinePoints(double x, double top, double bottom)
        => Enumerable.Range(0, 20)
            .Select(i => new InkPoint(x, top + ((bottom - top) * i / 19.0), 0.5f, null, null, null, i))
            .ToArray();

    private static DocumentSession Open(FakePageSource source) => new(source, @"C:\docs\book.pdf", _fingerprint);

    private static void DrawLine(PageEditor editor, double x, double top, double bottom)
        => Assert.True(editor.CommitStroke(LinePoints(x, top, bottom), InkTool.Pen, _black, 1.5));

    [Fact]
    public async Task Committing_a_margin_line_adds_a_mark_for_that_page()
    {
        using var session = Open(new FakePageSource(3));
        var index = new MarkIndex(3);
        using var tracker = new MarkTracker(session, new MarkRecognizer(), index);
        var editor = await session.EnsureEditorAsync(1);
        Assert.True(index.IsScanned(1)); // the load itself was recognised: no marks yet
        Assert.Empty(index.MarksOnPage(1));

        DrawLine(editor, 30, 100, 260);

        var mark = Assert.Single(index.MarksOnPage(1));
        Assert.Equal(MarkSide.Left, mark.Side);
        Assert.Equal([editor.Page.Strokes[0].Id], mark.StrokeIds);
        Assert.False(index.IsScanned(0));
    }

    [Fact]
    public async Task Erasing_the_line_removes_the_mark_and_undo_brings_it_back()
    {
        using var session = Open(new FakePageSource(2));
        var index = new MarkIndex(2);
        using var tracker = new MarkTracker(session, new MarkRecognizer(), index);
        var editor = await session.EnsureEditorAsync(0);
        DrawLine(editor, 30, 100, 260);
        var drawn = Assert.Single(index.MarksOnPage(0));

        var eraser = editor.BeginErase(EraserMode.WholeStroke);
        Assert.True(eraser.MoveTo(new PointD(30, 180), 4));
        Assert.True(eraser.Complete());
        Assert.Empty(index.MarksOnPage(0));

        Assert.True(await session.UndoAsync());
        Assert.Equal([drawn], index.MarksOnPage(0));

        Assert.True(await session.RedoAsync());
        Assert.Empty(index.MarksOnPage(0));
    }

    [Fact]
    public async Task Two_lines_on_the_same_passage_are_one_mark()
    {
        using var session = Open(new FakePageSource(1));
        var index = new MarkIndex(1);
        using var tracker = new MarkTracker(session, new MarkRecognizer(), index);
        var editor = await session.EnsureEditorAsync(0);

        DrawLine(editor, 30, 100, 260);
        DrawLine(editor, 36, 270, 400);

        var mark = Assert.Single(index.MarksOnPage(0));
        Assert.Equal(2, mark.StrokeIds.Count);
        Assert.Equal(100, mark.TopPt, 3);
        Assert.Equal(400, mark.BottomPt, 3);
    }

    [Fact]
    public async Task Pages_loaded_later_are_recognised_when_they_finish_loading()
    {
        var source = new FakePageSource(3);
        var line = new InkStroke(Guid.NewGuid(), InkTool.Pen, _black, 1.5, LinePoints(580, 100, 300));
        source.SetStrokes(0, [line]);
        source.SetStrokes(2, [line]);
        using var session = Open(source);
        await session.EnsurePageAsync(0); // loaded before the tracker exists
        var index = new MarkIndex(3);
        using var tracker = new MarkTracker(session, new MarkRecognizer(), index);
        Assert.Equal(MarkSide.Right, Assert.Single(index.MarksOnPage(0)).Side);

        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = session.EnsurePageAsync(2);
        Assert.False(index.IsScanned(2));
        source.Gate.SetResult();
        await load.WaitAsync(_timeout);

        var deadline = DateTime.UtcNow + _timeout;
        while (!index.IsScanned(2))
        {
            Assert.True(DateTime.UtcNow < deadline, "page 2 was never recognised");
            await Task.Delay(5);
        }

        Assert.Equal([line.Id], Assert.Single(index.MarksOnPage(2)).StrokeIds);
    }

    [Fact]
    public async Task A_page_count_mismatch_with_the_index_is_ignored()
    {
        using var session = Open(new FakePageSource(3));
        var index = new MarkIndex(1); // e.g. reset for another document
        using var tracker = new MarkTracker(session, new MarkRecognizer(), index);

        var editor = await session.EnsureEditorAsync(2);
        DrawLine(editor, 30, 100, 260);

        Assert.Equal(0, index.ScannedCount);
    }

    [Fact]
    public async Task Dispose_unsubscribes()
    {
        using var session = Open(new FakePageSource(2));
        var index = new MarkIndex(2);
        var tracker = new MarkTracker(session, new MarkRecognizer(), index);
        var editor = await session.EnsureEditorAsync(0);
        var changed = 0;
        index.Changed += (_, _) => changed++;

        tracker.Dispose();
        tracker.Dispose();
        DrawLine(editor, 30, 100, 260);
        await session.EnsurePageAsync(1);

        Assert.Equal(0, changed);
        Assert.Empty(index.MarksOnPage(0));
        Assert.False(index.IsScanned(1));
    }
}
