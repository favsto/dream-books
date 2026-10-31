using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;
using DreamBooks.Core.Tests.Documents;
using static DreamBooks.Core.Tests.Journal.JournalTestData;

namespace DreamBooks.Core.Tests.Journal;

public class JournalReplayerTests
{
    private static DocumentSession Open(FakePageSource source) => new(source, @"C:\docs\book.pdf", Fingerprint);

    private static JournalContents Contents(params JournalEntry[] entries)
        => JournalCodec.Parse([Header(), .. entries.Select(JournalCodec.SerializeEntry)]);

    private static void Draw(DocumentSession session, double y)
        => Assert.True(session.CurrentEditor!.CommitStroke([P(20, y), P(200, y)], InkTool.Pen, Black, 2));

    private static void Erase(DocumentSession session, EraserMode mode, PointD at)
    {
        var eraser = session.CurrentEditor!.BeginErase(mode);
        Assert.True(eraser.MoveTo(at, 5));
        Assert.True(eraser.Complete());
    }

    [Fact]
    public async Task Replay_rebuilds_strokes_and_an_undo_history_back_to_the_baseline()
    {
        var source = new FakePageSource(3);
        var page0 = new[] { Stroke(50), Stroke(150) };
        var page1 = new[] { Stroke(100), Stroke(250) };
        source.SetStrokes(0, page0);
        source.SetStrokes(1, page1);

        using var a = Open(source);
        var sink = new ListSink();
        using (new JournalRecorder(a, sink))
        {
            await a.GoToPageAsync(0);
            Draw(a, 400);
            Draw(a, 500);
            Erase(a, EraserMode.WholeStroke, new PointD(300, 50)); // a whole baseline stroke
            await a.GoToPageAsync(1);
            Erase(a, EraserMode.Partial, new PointD(300, 100)); // a baseline stroke cut into pieces
            Draw(a, 600);
            Assert.True(await a.UndoAsync());
            Assert.True(await a.UndoAsync());
            Assert.True(await a.RedoAsync());
            await a.GoToPageAsync(0);
            Assert.True(await a.UndoAsync()); // the partial erase on page 1, undone from page 0
            Assert.True(await a.UndoAsync()); // the whole-stroke erase on page 0
            Assert.True(await a.RedoAsync());
            Draw(a, 700); // clears the redo stack
        }

        using var b = Open(source);
        var result = await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(sink.Lines.Count, result.Applied);
        Assert.Equal(0, result.Skipped);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(Ids((await a.EnsurePageAsync(i)).Strokes), Ids((await b.EnsurePageAsync(i)).Strokes));
        }

        Assert.True(b.Undo.CanUndo);
        Assert.False(b.Undo.CanRedo);
        Assert.True(b.IsDirty);
        Assert.False(b.IsPageLoaded(2));

        var undone = 0;
        while (b.Undo.CanUndo)
        {
            Assert.True(await b.UndoAsync());
            undone++;
        }

        Assert.True(undone > 0);
        Assert.Equal(Ids(page0), Ids((await b.EnsurePageAsync(0)).Strokes));
        Assert.Equal(Ids(page1), Ids((await b.EnsurePageAsync(1)).Strokes));
        Assert.False(b.IsDirty);
        Assert.True(b.Undo.CanRedo);
    }

    [Fact]
    public async Task Undo_of_a_command_executed_before_the_journal_was_truncated_restores_correctly_after_replay()
    {
        var s0 = Stroke(50);
        var source = new FakePageSource(1);
        source.SetStrokes(0, [s0]);
        using var a = Open(source);
        await a.GoToPageAsync(0);
        Draw(a, 300);
        var x = (await a.EnsurePageAsync(0)).Strokes[^1];

        // Simulated save: the file now holds S0 + X and the journal restarts empty.
        a.MarkSaved(a.CaptureSaveSnapshot());
        var saved = new FakePageSource(1);
        saved.SetStrokes(0, [s0, x]);
        var sink = new ListSink();
        using (new JournalRecorder(a, sink))
        {
            Assert.True(await a.UndoAsync()); // undoes X, a command older than the journal
        }

        var erase = Assert.IsType<EraseEntry>(JournalCodec.ParseEntry(Assert.Single(sink.Lines)));
        Assert.Equal([x.Id], erase.RemovedIds);

        using var b = Open(saved);
        var result = await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(new ReplayResult(1, 0, null), result);
        Assert.Equal([s0.Id], Ids((await b.EnsurePageAsync(0)).Strokes));
        Assert.True(b.IsDirty);
        Assert.True(await b.UndoAsync());
        Assert.Equal([s0.Id, x.Id], Ids((await b.EnsurePageAsync(0)).Strokes));
        Assert.False(b.IsDirty);
    }

    [Fact]
    public async Task Undo_of_an_erase_replays_as_reinsertion_at_the_right_index()
    {
        var source = new FakePageSource(1);
        var strokes = new[] { Stroke(50), Stroke(150), Stroke(250) };
        source.SetStrokes(0, strokes);
        using var a = Open(source);
        await a.GoToPageAsync(0);
        Erase(a, EraserMode.WholeStroke, new PointD(300, 150)); // the middle stroke
        var sink = new ListSink();
        using (new JournalRecorder(a, sink))
        {
            Assert.True(await a.UndoAsync());
        }

        // The journal starts after the erase: replay on a file without the middle stroke.
        var erasedFile = new FakePageSource(1);
        erasedFile.SetStrokes(0, [strokes[0], strokes[2]]);
        using var b = Open(erasedFile);
        var result = await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(new ReplayResult(1, 0, null), result);
        Assert.Equal(Ids(strokes), Ids((await b.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Undo_of_a_partial_erase_replays_to_the_original_order()
    {
        var source = new FakePageSource(1);
        var strokes = new[] { Stroke(10), Stroke(100), Stroke(190) };
        source.SetStrokes(0, strokes);
        using var a = Open(source);
        var sink = new ListSink();
        using (new JournalRecorder(a, sink))
        {
            await a.GoToPageAsync(0);
            Erase(a, EraserMode.Partial, new PointD(300, 100));
            Assert.True(await a.UndoAsync());
        }

        using var b = Open(source);
        await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(Ids(strokes), Ids((await b.EnsurePageAsync(0)).Strokes));
        Assert.False(b.IsDirty);
    }

    [Fact]
    public async Task Redo_after_undo_replays_to_the_same_state_as_the_live_session()
    {
        var source = new FakePageSource(1);
        source.SetStrokes(0, [Stroke(10), Stroke(100), Stroke(190)]);
        using var a = Open(source);
        var sink = new ListSink();
        using (new JournalRecorder(a, sink))
        {
            await a.GoToPageAsync(0);
            Draw(a, 300);
            Erase(a, EraserMode.Partial, new PointD(300, 100));
            Erase(a, EraserMode.WholeStroke, new PointD(300, 10));
            Assert.True(await a.UndoAsync());
            Assert.True(await a.UndoAsync());
            Assert.True(await a.UndoAsync());
            Assert.True(await a.RedoAsync());
            Assert.True(await a.RedoAsync());
        }

        using var b = Open(source);
        var result = await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(sink.Lines.Count, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(Ids((await a.EnsurePageAsync(0)).Strokes), Ids((await b.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Seeded_random_sequence_with_a_mid_sequence_save_replays_to_the_live_state()
    {
        const int pageCount = 3;
        var random = new Random(42);
        var source = new FakePageSource(pageCount);
        for (var p = 0; p < pageCount; p++)
        {
            source.SetStrokes(p, [Stroke(100 + (p * 10)), Stroke(400 + (p * 10))]);
        }

        using var a = Open(source);
        await a.GoToPageAsync(0);
        var sink = new ListSink();
        var recorder = new JournalRecorder(a, sink);
        FakePageSource? saved = null;
        var counts = new int[5];
        try
        {
            for (var step = 0; step < 50; step++)
            {
                if (step == 25)
                {
                    // Simulated save: the file takes the live state and the journal restarts empty.
                    a.MarkSaved(a.CaptureSaveSnapshot());
                    saved = new FakePageSource(pageCount);
                    for (var p = 0; p < pageCount; p++)
                    {
                        saved.SetStrokes(p, (await a.EnsurePageAsync(p)).Strokes.ToArray());
                    }

                    recorder.Dispose();
                    sink = new ListSink();
                    recorder = new JournalRecorder(a, sink);

                    // Undo across the save boundary: commands older than the new journal.
                    Assert.True(await a.UndoAsync());
                    Assert.True(await a.UndoAsync());
                }

                var op = random.Next(5);
                counts[op]++;
                switch (op)
                {
                    case 0:
                        await a.GoToPageAsync(random.Next(pageCount));
                        Draw(a, random.Next(20, 780));
                        break;
                    case 1:
                    case 2:
                        {
                            var strokes = a.CurrentEditor!.Page.Strokes;
                            if (strokes.Count > 0)
                            {
                                var target = strokes[random.Next(strokes.Count)];
                                var point = target.Points[0];
                                var eraser = a.CurrentEditor.BeginErase(op == 1 ? EraserMode.WholeStroke : EraserMode.Partial);
                                eraser.MoveTo(new PointD(point.X + 40, point.Y), 4);
                                eraser.Complete();
                            }

                            break;
                        }

                    case 3:
                        await a.UndoAsync();
                        break;
                    default:
                        await a.RedoAsync();
                        break;
                }
            }
        }
        finally
        {
            recorder.Dispose();
        }

        Assert.All(counts, c => Assert.True(c > 0));
        Assert.NotEmpty(sink.Lines);
        using var b = Open(saved!);
        var result = await new JournalReplayer().ReplayAsync(b, JournalCodec.Parse([Header(), .. sink.Lines]));

        Assert.Equal(sink.Lines.Count, result.Applied);
        Assert.Equal(0, result.Skipped);
        for (var p = 0; p < pageCount; p++)
        {
            Assert.Equal(Ids((await a.EnsurePageAsync(p)).Strokes), Ids((await b.EnsurePageAsync(p)).Strokes));
        }

        Assert.Equal(a.IsDirty, b.IsDirty);
        while (b.Undo.CanUndo)
        {
            Assert.True(await b.UndoAsync());
        }

        Assert.False(b.IsDirty);
    }

    [Fact]
    public async Task Replay_of_erase_of_a_baseline_stroke_removes_it()
    {
        var source = new FakePageSource(1);
        var kept = Stroke(10);
        var erased = Stroke(20);
        source.SetStrokes(0, [kept, erased]);
        using var session = Open(source);

        var result = await new JournalReplayer().ReplayAsync(session, Contents(new EraseEntry(0, [erased.Id], [])));

        Assert.Equal(new ReplayResult(1, 0, null), result);
        Assert.Equal([kept.Id], Ids((await session.EnsurePageAsync(0)).Strokes));
        Assert.True(session.IsDirty);
        Assert.True(await session.UndoAsync());
        Assert.Equal([kept.Id, erased.Id], Ids((await session.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Replay_inserts_erase_pieces_at_their_recorded_indexes_clamped_to_the_list()
    {
        var source = new FakePageSource(1);
        var a = Stroke(10);
        var b = Stroke(20);
        var c = Stroke(30);
        source.SetStrokes(0, [a, b, c]);
        using var session = Open(source);
        var p1 = Stroke(21);
        var p2 = Stroke(22);
        var far = Stroke(99);

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(new EraseEntry(0, [b.Id], [new IndexedStroke(1, p1), new IndexedStroke(2, p2), new IndexedStroke(40, far)])));

        Assert.Equal(new ReplayResult(1, 0, null), result);
        Assert.Equal([a.Id, p1.Id, p2.Id, c.Id, far.Id], Ids((await session.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Replay_skips_erase_of_missing_ids_and_counts_them()
    {
        var source = new FakePageSource(1);
        var present = Stroke(10);
        var other = Stroke(20);
        source.SetStrokes(0, [present, other]);
        using var session = Open(source);
        var missing1 = Guid.NewGuid();
        var missing2 = Guid.NewGuid();

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(
                new EraseEntry(0, [missing1, missing2], []), // nothing left to do: skipped as a whole
                new EraseEntry(0, [present.Id, missing1], []))); // applied, one missing Id counted

        Assert.Equal(1, result.Applied);
        Assert.Equal(2, result.Skipped);
        Assert.Equal([other.Id], Ids((await session.EnsurePageAsync(0)).Strokes));
        Assert.True(await session.UndoAsync());
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Replay_skips_added_strokes_whose_id_is_already_on_the_page()
    {
        var source = new FakePageSource(1);
        var existing = Stroke(10);
        source.SetStrokes(0, [existing]);
        using var session = Open(source);
        var fresh = Stroke(20);

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(new AddEntry(0, [existing]), new AddEntry(0, [existing, fresh])));

        Assert.Equal(new ReplayResult(1, 2, null), result);
        Assert.Equal([existing.Id, fresh.Id], Ids((await session.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Replay_applies_across_pages_and_leaves_unvisited_pages_unloaded()
    {
        var source = new FakePageSource(5);
        using var session = Open(source);
        var s1 = Stroke(10);
        var s3 = Stroke(20);

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(new AddEntry(1, [s1]), new AddEntry(3, [s3]), new EraseEntry(3, [s3.Id], []), new AddEntry(3, [s3]), new ViewEntry(4, 1, 0, 0)));

        Assert.Equal(4, result.Applied);
        Assert.Equal([s1.Id], Ids((await session.EnsurePageAsync(1)).Strokes));
        Assert.Equal([s3.Id], Ids((await session.EnsurePageAsync(3)).Strokes));
        Assert.False(session.IsPageLoaded(0));
        Assert.False(session.IsPageLoaded(2));
        Assert.False(session.IsPageLoaded(4));
        Assert.Equal(0, source.LoadCount(4));
    }

    [Fact]
    public async Task Replay_skips_entries_for_pages_out_of_range()
    {
        var source = new FakePageSource(2);
        using var session = Open(source);
        var ok = Stroke(10);

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(new AddEntry(2, [Stroke(20)]), new EraseEntry(9, [Guid.NewGuid()], []), new AddEntry(1, [ok])));

        Assert.Equal(new ReplayResult(1, 2, null), result);
        Assert.Equal([ok.Id], Ids((await session.EnsurePageAsync(1)).Strokes));
    }

    [Fact]
    public async Task Replay_does_not_write_new_journal_lines()
    {
        var source = new FakePageSource(1);
        using var session = Open(source);
        var sink = new ListSink();
        var stroke = Stroke(10);
        var contents = Contents(new AddEntry(0, [stroke]), new EraseEntry(0, [stroke.Id], []), new AddEntry(0, [stroke]));

        await new JournalReplayer().ReplayAsync(session, contents);
        using var recorder = new JournalRecorder(session, sink);

        Assert.Empty(sink.Lines);
        Draw(await GoTo(session, 0), 300);
        Assert.IsType<AddEntry>(JournalCodec.ParseEntry(Assert.Single(sink.Lines)));
    }

    [Fact]
    public async Task Last_view_entry_wins()
    {
        using var session = Open(new FakePageSource(3));
        var stroke = Stroke(10);

        var result = await new JournalReplayer().ReplayAsync(
            session,
            Contents(new ViewEntry(0, 1, 0, 0), new AddEntry(1, [stroke]), new ViewEntry(2, 1.5, 10, 20), new EraseEntry(1, [stroke.Id], [])));

        Assert.Equal(new ViewEntry(2, 1.5, 10, 20), result.LastView);
        Assert.Equal(2, result.Applied);
        Assert.False(session.IsPageLoaded(2));
    }

    [Fact]
    public async Task Replay_refuses_a_journal_with_an_unknown_format_version()
    {
        var source = new FakePageSource(1);
        using var session = Open(source);
        var future = new JournalContents(
            new JournalHeader(2, @"C:\docs\book.pdf", Fingerprint),
            [new AddEntry(0, [Stroke(10)]), new AddEntry(0, [Stroke(20)])],
            false,
            0);

        var result = await new JournalReplayer().ReplayAsync(session, future);

        Assert.Equal(new ReplayResult(0, 2, null), result);
        Assert.Equal(0, source.TotalLoads);
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public async Task Replay_refuses_a_journal_without_header()
    {
        using var session = Open(new FakePageSource(1));

        var result = await new JournalReplayer().ReplayAsync(session, new JournalContents(null, [new AddEntry(0, [Stroke(10)])], false, 0));

        Assert.Equal(new ReplayResult(0, 1, null), result);
    }

    [Fact]
    public async Task Replay_honours_cancellation()
    {
        using var session = Open(new FakePageSource(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new JournalReplayer().ReplayAsync(session, Contents(new AddEntry(0, [Stroke(10)])), cts.Token));
        Assert.False(session.Undo.CanUndo);
    }

    private static async Task<DocumentSession> GoTo(DocumentSession session, int pageIndex)
    {
        await session.GoToPageAsync(pageIndex);
        return session;
    }
}
