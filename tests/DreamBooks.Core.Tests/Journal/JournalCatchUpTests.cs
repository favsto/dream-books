using DreamBooks.Core.Commands;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;
using DreamBooks.Core.Tests.Documents;
using static DreamBooks.Core.Tests.Journal.JournalTestData;

namespace DreamBooks.Core.Tests.Journal;

/// <summary>
/// The catch-up written right after a save: the unsaved delta against the new baseline, as entries that replay
/// onto the saved file to give back the session's exact strokes (FR-84).
/// </summary>
public sealed class JournalCatchUpTests
{
    [Fact]
    public void Empty_delta_writes_nothing()
    {
        Assert.Empty(JournalCatchUp.Build(new SaveSnapshot([], new Dictionary<int, IReadOnlyList<Guid>>())));
    }

    [Fact]
    public async Task Added_strokes_carry_their_index_in_the_current_list()
    {
        var a = Stroke(10);
        var source = new FakePageSource(2);
        source.SetStrokes(1, [a]);
        using var session = new DocumentSession(source, @"C:\docs\book.pdf", Fingerprint);
        var page = await session.EnsurePageAsync(1);
        var b = Stroke(20);
        var c = Stroke(30);
        session.Undo.Execute(new AddStrokesCommand(page, [b, c]));

        var entry = Assert.Single(JournalCatchUp.Build(session.CaptureSaveSnapshot()));

        Assert.Equal(1, entry.PageIndex);
        Assert.Empty(entry.RemovedIds);
        Assert.Equal([(1, b.Id), (2, c.Id)], entry.Added.Select(s => (s.Index, s.Stroke.Id)));
    }

    [Fact]
    public async Task Erase_of_a_baseline_stroke_is_a_removed_Id()
    {
        var a = Stroke(10);
        var b = Stroke(20);
        var source = new FakePageSource(1);
        source.SetStrokes(0, [a, b]);
        using var session = new DocumentSession(source, @"C:\docs\book.pdf", Fingerprint);
        var page = await session.EnsurePageAsync(0);
        session.Undo.Execute(new EraseStrokesCommand(page, [a, b], [b]));

        var entry = Assert.Single(JournalCatchUp.Build(session.CaptureSaveSnapshot()));

        Assert.Equal([a.Id], entry.RemovedIds);
        Assert.Empty(entry.Added);
    }

    [Fact]
    public async Task Partial_erase_pieces_replay_in_place_onto_the_saved_baseline()
    {
        var a = Stroke(10);
        var b = Stroke(20);
        var c = Stroke(30);
        var source = new FakePageSource(1);
        source.SetStrokes(0, [a, b, c]);
        using var session = new DocumentSession(source, @"C:\docs\book.pdf", Fingerprint);
        var page = await session.EnsurePageAsync(0);
        var left = b.WithPoints([P(10, 20), P(100, 20)]);
        var right = b.WithPoints([P(300, 20), P(600, 20)]);
        session.Undo.Execute(new EraseStrokesCommand(page, [a, b, c], [a, left, right, c]));
        var expected = Ids(page.Strokes);

        var entries = JournalCatchUp.Build(session.CaptureSaveSnapshot());

        var entry = Assert.Single(entries);
        Assert.Equal([b.Id], entry.RemovedIds);
        Assert.Equal([(1, left.Id), (2, right.Id)], entry.Added.Select(s => (s.Index, s.Stroke.Id)));

        // Replayed onto the file content (the baseline here), it gives the same sequence back.
        var fresh = new FakePageSource(1);
        fresh.SetStrokes(0, [a, b, c]);
        using var restored = new DocumentSession(fresh, @"C:\docs\book.pdf", Fingerprint);
        var result = await new JournalReplayer().ReplayAsync(restored, Contents(entries));
        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(expected, Ids((await restored.EnsurePageAsync(0)).Strokes));
    }

    [Fact]
    public async Task Delta_after_MarkSaved_is_only_what_changed_since_the_snapshot()
    {
        var source = new FakePageSource(2);
        using var session = new DocumentSession(source, @"C:\docs\book.pdf", Fingerprint);
        var page0 = await session.EnsurePageAsync(0);
        var page1 = await session.EnsurePageAsync(1);
        var saved = Stroke(10);
        session.Undo.Execute(new AddStrokesCommand(page0, [saved]));
        var snapshot = session.CaptureSaveSnapshot();

        // While the save runs: a new stroke on page 1, and the saved stroke undone on page 0.
        var during = Stroke(20);
        session.Undo.Execute(new AddStrokesCommand(page1, [during]));
        session.MarkSaved(snapshot);
        session.Undo.Undo(); // undoes "during"
        session.Undo.Redo();
        var entries = JournalCatchUp.Build(session.CaptureSaveSnapshot());

        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.PageIndex);
        Assert.Equal([during.Id], entry.Added.Select(s => s.Stroke.Id));
    }

    private static JournalContents Contents(IEnumerable<JournalEntry> entries)
    {
        var lines = new List<string> { Header() };
        lines.AddRange(entries.Select(JournalCodec.SerializeEntry));
        return JournalCodec.Parse(lines);
    }
}
