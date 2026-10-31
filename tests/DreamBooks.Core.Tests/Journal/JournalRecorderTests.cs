using DreamBooks.Core.Commands;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;
using DreamBooks.Core.Tests.Documents;
using DreamBooks.Core.Tests.Undo;
using static DreamBooks.Core.Tests.Journal.JournalTestData;

namespace DreamBooks.Core.Tests.Journal;

public class JournalRecorderTests
{
    private static DocumentSession Open(FakePageSource source) => new(source, @"C:\docs\book.pdf", Fingerprint);

    private static JournalEntry[] Entries(ListSink sink)
        => sink.Lines.Select(line => JournalCodec.ParseEntry(line) ?? throw new InvalidOperationException("unparseable line")).ToArray();

    [Fact]
    public async Task Recorder_writes_stroke_effects_for_executed_undone_and_redone_commands()
    {
        var source = new FakePageSource(2);
        var baseline = Stroke(50);
        source.SetStrokes(1, [baseline]);
        using var session = Open(source);
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);

        await session.GoToPageAsync(1);
        Assert.True(session.CurrentEditor!.CommitStroke([P(20, 300), P(200, 300)], InkTool.Pen, Black, 2));
        var drawn = session.CurrentEditor.Page.Strokes[^1];
        var eraser = session.CurrentEditor.BeginErase(EraserMode.WholeStroke);
        Assert.True(eraser.MoveTo(new PointD(100, 50), 5));
        Assert.True(eraser.Complete());
        Assert.True(await session.UndoAsync()); // the erase: the baseline stroke comes back at index 0
        Assert.True(await session.RedoAsync()); // the erase again
        Assert.True(await session.UndoAsync()); // the erase
        Assert.True(await session.UndoAsync()); // the add: the drawn stroke goes away
        Assert.True(await session.RedoAsync()); // the add again

        var entries = Entries(sink);
        Assert.Equal(7, entries.Length);
        var add = Assert.IsType<AddEntry>(entries[0]);
        Assert.Equal(1, add.PageIndex);
        AssertSameStroke(drawn, Assert.Single(add.Strokes));
        var erase = Assert.IsType<EraseEntry>(entries[1]);
        Assert.Equal(1, erase.PageIndex);
        Assert.Equal([baseline.Id], erase.RemovedIds);
        Assert.Empty(erase.Added);

        var undoErase = Assert.IsType<EraseEntry>(entries[2]);
        Assert.Equal(1, undoErase.PageIndex);
        Assert.Empty(undoErase.RemovedIds);
        var back = Assert.Single(undoErase.Added);
        Assert.Equal(0, back.Index);
        AssertSameStroke(baseline, back.Stroke);

        var redoErase = Assert.IsType<EraseEntry>(entries[3]);
        Assert.Equal([baseline.Id], redoErase.RemovedIds);
        Assert.Empty(redoErase.Added);
        Assert.IsType<EraseEntry>(entries[4]);

        var undoAdd = Assert.IsType<EraseEntry>(entries[5]);
        Assert.Equal(1, undoAdd.PageIndex);
        Assert.Equal([drawn.Id], undoAdd.RemovedIds);
        Assert.Empty(undoAdd.Added);

        var redoAdd = Assert.IsType<AddEntry>(entries[6]);
        AssertSameStroke(drawn, Assert.Single(redoAdd.Strokes));
    }

    [Fact]
    public async Task Undo_of_a_partial_erase_is_recorded_as_removing_the_pieces_and_restoring_originals_at_their_indexes()
    {
        var source = new FakePageSource(1);
        var a = Stroke(10);
        var b = Stroke(20);
        var c = Stroke(30);
        source.SetStrokes(0, [a, b, c]);
        using var session = Open(source);
        var page = await session.EnsurePageAsync(0);
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);
        var piece1 = Stroke(21);
        var piece2 = Stroke(22);

        session.Undo.Execute(new EraseStrokesCommand(page, [a, b, c], [a, piece1, piece2]));
        Assert.True(session.Undo.Undo());

        var inverse = Assert.IsType<EraseEntry>(Entries(sink)[1]);
        Assert.Equal([piece1.Id, piece2.Id], inverse.RemovedIds);
        Assert.Equal([1, 2], inverse.Added.Select(s => s.Index));
        Assert.Equal([b.Id, c.Id], inverse.Added.Select(s => s.Stroke.Id));
    }

    [Fact]
    public async Task Recorder_erase_entry_contains_only_removed_ids_and_new_pieces_with_final_indexes()
    {
        var source = new FakePageSource(1);
        var a = Stroke(10);
        var b = Stroke(20);
        var c = Stroke(30);
        source.SetStrokes(0, [a, b, c]);
        using var session = Open(source);
        var page = await session.EnsurePageAsync(0);
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);
        var piece1 = Stroke(21);
        var piece2 = Stroke(22);

        session.Undo.Execute(new EraseStrokesCommand(page, [a, b, c], [piece1, a, piece2, c]));

        var erase = Assert.IsType<EraseEntry>(Assert.Single(Entries(sink)));
        Assert.Equal([b.Id], erase.RemovedIds);
        Assert.Equal([0, 2], erase.Added.Select(s => s.Index));
        Assert.Equal([piece1.Id, piece2.Id], erase.Added.Select(s => s.Stroke.Id));
    }

    [Fact]
    public async Task Recorder_records_a_real_partial_erase_as_removed_id_plus_pieces()
    {
        var source = new FakePageSource(1);
        var below = Stroke(10);
        var cut = Stroke(100);
        var above = Stroke(190);
        source.SetStrokes(0, [below, cut, above]);
        using var session = Open(source);
        await session.GoToPageAsync(0);
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);

        var eraser = session.CurrentEditor!.BeginErase(EraserMode.Partial);
        Assert.True(eraser.MoveTo(new PointD(300, 100), 5));
        Assert.True(eraser.Complete());

        var erase = Assert.IsType<EraseEntry>(Assert.Single(Entries(sink)));
        var after = session.CurrentEditor.Page.Strokes;
        Assert.Equal([cut.Id], erase.RemovedIds);
        Assert.Equal(2, erase.Added.Count);
        Assert.All(erase.Added, s => Assert.Equal(after[s.Index].Id, s.Stroke.Id));
        Assert.Equal([1, 2], erase.Added.Select(s => s.Index));
    }

    [Fact]
    public void Recorder_ignores_commands_that_are_not_stroke_commands_including_their_undo_and_redo()
    {
        using var session = Open(new FakePageSource(1));
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);

        session.Undo.Execute(new RecordingCommand("x", []));
        Assert.True(session.Undo.Undo());
        Assert.True(session.Undo.Redo());

        Assert.Empty(sink.Lines);
    }

    [Fact]
    public void RecordView_appends_a_view_line()
    {
        using var session = Open(new FakePageSource(3));
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);

        recorder.RecordView(2, 1.5, 10, 20.25);

        Assert.Equal(new ViewEntry(2, 1.5, 10, 20.25), Assert.Single(Entries(sink)));
    }

    [Fact]
    public async Task Recorder_stops_after_Dispose()
    {
        using var session = Open(new FakePageSource(1));
        await session.GoToPageAsync(0);
        var sink = new ListSink();
        var recorder = new JournalRecorder(session, sink);

        recorder.Dispose();
        recorder.Dispose();
        Assert.True(session.CurrentEditor!.CommitStroke([P(20, 300), P(200, 300)], InkTool.Pen, Black, 2));
        recorder.RecordView(0, 1, 0, 0);

        Assert.Empty(sink.Lines);
    }

    [Fact]
    public async Task Recorder_swallows_a_throwing_sink_raises_Faulted_once_and_keeps_recording_after_recovery()
    {
        using var session = Open(new FakePageSource(1));
        await session.GoToPageAsync(0);
        var sink = new ListSink { Failure = new IOException("disk full") };
        using var recorder = new JournalRecorder(session, sink);
        var faults = new List<Exception>();
        recorder.Faulted += (_, e) => faults.Add(e.Exception);
        var stateChanges = 0;
        session.Undo.StateChanged += (_, _) => stateChanges++;

        Assert.True(session.CurrentEditor!.CommitStroke([P(20, 300), P(200, 300)], InkTool.Pen, Black, 2));
        Assert.True(await session.UndoAsync());
        recorder.RecordView(0, 1, 0, 0);

        Assert.True(recorder.IsFaulted);
        Assert.Same(sink.Failure, Assert.Single(faults));
        Assert.Equal(2, stateChanges);
        Assert.Empty(session.CurrentEditor.Page.Strokes);

        sink.Failure = null;
        Assert.True(await session.RedoAsync());

        Assert.False(recorder.IsFaulted);
        Assert.IsType<AddEntry>(Assert.Single(Entries(sink)));

        sink.Failure = new IOException("again");
        Assert.True(await session.UndoAsync());

        Assert.True(recorder.IsFaulted);
        Assert.Equal(2, faults.Count);
    }

    [Fact]
    public async Task Recorder_faults_instead_of_throwing_when_a_stroke_cannot_be_serialized()
    {
        var source = new FakePageSource(1);
        using var session = Open(source);
        var page = await session.EnsurePageAsync(0);
        var sink = new ListSink();
        using var recorder = new JournalRecorder(session, sink);
        var faults = 0;
        recorder.Faulted += (_, _) => faults++;
        var bad = new InkStroke(Guid.NewGuid(), InkTool.Pen, Black, 2, [new InkPoint(double.NaN, 1, null, null, null, null, 0)]);

        session.Undo.Execute(new AddStrokesCommand(page, [bad]));

        Assert.Equal(1, faults);
        Assert.True(recorder.IsFaulted);
        Assert.Empty(sink.Lines);
    }
}
