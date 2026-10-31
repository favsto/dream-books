using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Documents;

public class DocumentSessionPageEventsTests
{
    private static readonly DocumentFingerprint _fingerprint = new(100, 200, new string('a', 64));
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private static DocumentSession Open(FakePageSource source) => new(source, @"C:\docs\book.pdf", _fingerprint);

    private static List<int> Record(DocumentSession session)
    {
        var raised = new List<int>();
        session.PageStrokesChanged += (_, e) =>
        {
            lock (raised)
            {
                raised.Add(e.PageIndex);
            }
        };
        return raised;
    }

    [Fact]
    public async Task PageStrokesChanged_fires_for_edits_undo_redo_and_page_load()
    {
        var source = new FakePageSource(3);
        using var session = Open(source);
        var raised = Record(session);

        var editor = await session.EnsureEditorAsync(1);
        Assert.Equal([1], raised); // the load

        await session.EnsurePageAsync(1); // already loaded: no event
        Assert.True(editor.CommitStroke([P(20, 50), P(200, 50)], InkTool.Pen, _black, 2));
        Assert.Equal([1, 1], raised);

        var eraser = editor.BeginErase(EraserMode.WholeStroke);
        Assert.True(eraser.MoveTo(new PointD(100, 50), 3));
        Assert.True(eraser.Complete());
        Assert.Equal([1, 1, 1], raised);

        Assert.True(await session.UndoAsync());
        Assert.True(await session.RedoAsync());
        Assert.Equal([1, 1, 1, 1, 1], raised);

        // A load that completes later, on another thread, also raises the event, once.
        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = session.EnsurePageAsync(2);
        var second = session.EnsurePageAsync(2);
        source.Gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(_timeout);
        await WaitUntilAsync(() => raised.Count == 6);
        Assert.Equal([1, 1, 1, 1, 1, 2], raised);
    }

    [Fact]
    public async Task No_event_after_Dispose()
    {
        var source = new FakePageSource(3);
        var session = Open(source);
        var raised = Record(session);
        var editor = await session.EnsureEditorAsync(0);
        source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.EnsurePageAsync(2);
        Assert.Equal([0], raised);

        session.Dispose();
        Assert.True(editor.CommitStroke([P(20, 50), P(200, 50)], InkTool.Pen, _black, 2));
        source.Gate.SetResult();
        await pending.WaitAsync(_timeout);
        await Task.Delay(50);

        Assert.Equal([0], raised);
    }

    [Fact]
    public async Task A_failed_load_raises_nothing_and_leaves_no_unobserved_exception()
    {
        var source = new FakePageSource(2) { FailNext = new IOException("damaged") };
        using var session = Open(source);
        var raised = Record(session);

        await Assert.ThrowsAsync<IOException>(() => session.EnsurePageAsync(1));
        await session.EnsurePageAsync(1); // not cached: loads again

        Assert.Equal([1], raised);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(5);
        }
    }
}
