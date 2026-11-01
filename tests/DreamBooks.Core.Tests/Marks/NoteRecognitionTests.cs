using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// Notes (FR-101): the strokes of a page that are not margin lines, grouped by vertical proximity (a gap of more than
/// <see cref="MarkRules.NoteGapLines"/> lines starts a new note), without the trivial ones (dots, taps).
/// </summary>
public class NoteRecognitionTests
{
    private const double _width = 600;
    private const double _line = MarkRules.DefaultLineHeightPt;

    private static InkStroke Stroke(IEnumerable<(double X, double Y)> pts, InkTool tool = InkTool.Pen)
        => new(
            Guid.NewGuid(),
            tool,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            pts.Select((p, i) => new InkPoint(p.X, p.Y, 0.5f, null, null, null, i)).ToArray());

    /// <summary>A handwriting-like scribble 30 pt wide spanning <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    private static InkStroke Word(double x, double top, double bottom, InkTool tool = InkTool.Pen)
        => Stroke(Enumerable.Range(0, 12).Select(i => (x + (i * 2.5), i % 2 == 0 ? top : bottom)), tool);

    private static InkStroke MarginLine(double x, double top, double bottom)
        => Stroke(Enumerable.Range(0, 20).Select(i => (x, top + ((bottom - top) * i / 19.0))));

    private static IReadOnlyList<Note> Notes(params InkStroke[] strokes)
        => new MarkRecognizer().RecognizePage(4, _width, strokes).Notes;

    [Fact]
    public void A_page_without_strokes_has_no_notes()
    {
        var page = new MarkRecognizer().RecognizePage(0, _width, []);
        Assert.Empty(page.Marks);
        Assert.Empty(page.Notes);
    }

    [Fact]
    public void One_handwritten_word_is_one_note_with_its_extent_and_page()
    {
        var w = Word(200, 300, 312);
        var note = Assert.Single(Notes(w));
        Assert.Equal(4, note.PageIndex);
        Assert.Equal(300, note.TopPt);
        Assert.Equal(312, note.BottomPt);
        Assert.Equal([w.Id], note.StrokeIds);
    }

    [Fact]
    public void Strokes_close_together_vertically_form_one_note_wherever_they_are_across_the_page()
    {
        var a = Word(60, 300, 312);
        var b = Word(450, 320, 330); // far to the right, 8 pt below: same note
        var c = Word(250, 380, 392); // 50 pt below b: still within four lines
        var note = Assert.Single(Notes(c, a, b));
        Assert.Equal(300, note.TopPt);
        Assert.Equal(392, note.BottomPt);
        Assert.Equal([a.Id, b.Id, c.Id], note.StrokeIds); // in vertical order
    }

    [Fact]
    public void A_gap_of_exactly_four_lines_joins_and_just_over_splits()
    {
        var gap = MarkRules.NoteGapLines * _line;
        Assert.Single(Notes(Word(100, 100, 112), Word(100, 112 + gap, 124 + gap)));
        Assert.Equal(2, Notes(Word(100, 100, 112), Word(100, 112.5 + gap, 124.5 + gap)).Count);
    }

    [Fact]
    public void Notes_are_ordered_from_the_top()
    {
        var notes = Notes(Word(100, 600, 612), Word(100, 100, 112), Word(100, 350, 362));
        Assert.Equal([100, 350, 600], notes.Select(n => n.TopPt).ToArray());
    }

    [Fact]
    public void Margin_lines_are_marks_and_never_notes()
    {
        var line = MarginLine(30, 100, 260);
        var word = Word(300, 150, 162);
        var page = new MarkRecognizer().RecognizePage(0, _width, [line, word]);
        Assert.Equal([line.Id], Assert.Single(page.Marks).StrokeIds);
        Assert.Equal([word.Id], Assert.Single(page.Notes).StrokeIds);
    }

    [Fact]
    public void A_dot_or_a_tap_alone_is_not_a_note_but_joins_a_nearby_one()
    {
        var dot = Stroke([(200, 300), (201, 301), (200.5, 302)]);
        var tap = Stroke([(400, 500)]);
        Assert.Empty(Notes(dot));
        Assert.Empty(Notes(tap));
        Assert.Empty(Notes(dot, tap));

        var word = Word(100, 290, 302);
        var note = Assert.Single(Notes(word, dot));
        Assert.Equal([word.Id, dot.Id], note.StrokeIds);
    }

    [Fact]
    public void A_cluster_just_under_half_a_line_in_both_directions_is_trivial_and_just_reaching_it_is_not()
    {
        var half = MarkRules.NoteMinSizeLines * _line;
        Assert.Empty(Notes(Stroke([(100, 100), (100 + half - 0.1, 100 + half - 0.1)])));
        Assert.Single(Notes(Stroke([(100, 100), (100 + half, 100.5)]))); // a short dash: wide enough
        Assert.Single(Notes(Stroke([(100, 100), (100.5, 100 + half)]))); // a short tick: tall enough
    }

    [Fact]
    public void Highlighter_strokes_count_as_notes_for_now()
    {
        var hl = Stroke([(100, 300), (400, 302)], InkTool.Highlighter);
        Assert.Equal([hl.Id], Assert.Single(Notes(hl)).StrokeIds);
    }

    [Fact]
    public void Recognising_the_same_strokes_twice_gives_equal_notes_and_marks()
    {
        var strokes = new[] { MarginLine(30, 100, 260), Word(200, 120, 132), Word(220, 400, 412) };
        var r = new MarkRecognizer();
        var a = r.RecognizePage(1, _width, strokes);
        var b = r.RecognizePage(1, _width, strokes);
        Assert.Equal(a.Notes, b.Notes);
        Assert.Equal(a.Marks, b.Marks);
        Assert.Equal(a.Notes[0].GetHashCode(), b.Notes[0].GetHashCode());
        Assert.Equal(r.Recognize(1, _width, strokes), a.Marks);
    }

    [Fact]
    public void Notes_compare_by_value_and_their_ids_cannot_be_changed_through_a_cast()
    {
        var id = Guid.NewGuid();
        var ids = new List<Guid> { id };
        var a = new Note(2, 10, 20, ids);
        ids.Add(Guid.NewGuid());
        Assert.Equal(new Note(2, 10, 20, [id]), a);
        Assert.NotEqual(new Note(2, 10, 21, [id]), a);
        Assert.False(a.StrokeIds is List<Guid>);
        Assert.False(a.StrokeIds is Guid[]);
    }

    [Fact]
    public void Fausto_real_other_strokes_are_notes_and_his_margin_lines_are_not()
    {
        string[] lines = [RealPenStrokes.StraightShortLeft, RealPenStrokes.RetracedLeft, RealPenStrokes.StraightRight];
        string[] others = [RealPenStrokes.Underline, RealPenStrokes.WavyStrokeAcrossText, RealPenStrokes.TallLetterInText];
        var lineStrokes = lines.Select(RealPenStrokes.Parse).ToArray();
        var otherStrokes = others.Select(RealPenStrokes.Parse).ToArray();
        var page = new MarkRecognizer().RecognizePage(0, RealPenStrokes.PageWidthPt, [.. lineStrokes, .. otherStrokes]);
        var noteIds = page.Notes.SelectMany(n => n.StrokeIds).ToHashSet();
        Assert.All(lineStrokes, s => Assert.DoesNotContain(s.Id, noteIds));
        Assert.All(otherStrokes, s => Assert.Contains(s.Id, noteIds));
    }

    [Fact]
    public void Fausto_line_drawn_in_three_passes_is_one_mark_and_no_note()
    {
        var passes = new[] { RealPenStrokes.LinePassShort, RealPenStrokes.LinePassLong, RealPenStrokes.LinePassLonger }
            .Select(RealPenStrokes.Parse)
            .ToArray();
        var page = new MarkRecognizer().RecognizePage(0, RealPenStrokes.PageWidthPt, passes);

        var mark = Assert.Single(page.Marks);
        Assert.Equal(passes.Select(s => s.Id).Order(), mark.StrokeIds.Order());
        Assert.Equal(MarkSide.Left, mark.Side);
        Assert.Empty(page.Notes);
    }

    [Fact]
    public void A_short_nearly_vertical_pass_along_a_margin_line_belongs_to_its_mark_and_can_extend_it()
    {
        var line = MarginLine(40, 200, 240);
        var along = MarginLine(43, 210, 230); // 20 pt: too short alone, on top of the line
        var below = MarginLine(41, 250, 270); // continues the line after a 10 pt gap
        var page = new MarkRecognizer().RecognizePage(0, _width, [line, along, below]);

        var mark = Assert.Single(page.Marks);
        Assert.Equal([line.Id, along.Id, below.Id], mark.StrokeIds);
        Assert.Equal(200, mark.TopPt);
        Assert.Equal(270, mark.BottomPt);
        Assert.Empty(page.Notes);
    }

    [Fact]
    public void Strokes_beside_a_margin_line_that_are_not_passes_of_it_stay_notes()
    {
        var line = MarginLine(30, 200, 260);
        var letterAway = MarginLine(30 + _line + 1, 210, 222); // a vertical letter of a margin note, more than a line away
        var crossing = Stroke([(20, 230), (300, 232)]); // an underline crossing the line's height
        var tilted = Stroke([(28, 205), (48, 220)]); // a short slanted stroke on the line (53 degrees)
        var dotOnIt = Stroke([(30, 230), (30.5, 231)]); // a dot: never a pass
        var page = new MarkRecognizer().RecognizePage(0, _width, [line, letterAway, crossing, tilted, dotOnIt]);

        Assert.Equal([line.Id], Assert.Single(page.Marks).StrokeIds);
        var noteIds = page.Notes.SelectMany(n => n.StrokeIds).ToHashSet();
        Assert.Contains(letterAway.Id, noteIds);
        Assert.Contains(crossing.Id, noteIds);
        Assert.Contains(tilted.Id, noteIds);
        Assert.Contains(dotOnIt.Id, noteIds); // joins the note it sits in
    }

    [Fact]
    public void A_pass_exactly_one_line_from_the_margin_line_belongs_to_it_and_just_beyond_does_not()
    {
        var r = new MarkRecognizer();
        var line = MarginLine(30, 200, 260);
        Assert.Empty(r.RecognizePage(0, _width, [line, MarginLine(30 + _line, 210, 230)]).Notes);
        Assert.Single(r.RecognizePage(0, _width, [line, MarginLine(30 + _line + 0.5, 210, 230)]).Notes);
        Assert.Empty(r.RecognizePage(0, _width, [line, MarginLine(30, 260 + (MarkRules.MergeGapLines * _line), 280 + (MarkRules.MergeGapLines * _line))]).Notes);
        Assert.Single(r.RecognizePage(0, _width, [line, MarginLine(30, 260.5 + (MarkRules.MergeGapLines * _line), 280.5 + (MarkRules.MergeGapLines * _line))]).Notes);
    }

    [Fact]
    public void A_short_vertical_stroke_in_the_margin_without_a_margin_line_is_a_note()
    {
        var s = MarginLine(40, 210, 230);
        var page = new MarkRecognizer().RecognizePage(0, _width, [s]);
        Assert.Empty(page.Marks);
        Assert.Equal([s.Id], Assert.Single(page.Notes).StrokeIds);
    }

    [Fact]
    public void The_margin_notes_of_a_mark_never_repeat_its_own_passes()
    {
        var line = MarginLine(40, 200, 240);
        var along = MarginLine(43, 210, 230);
        var word = Word(70, 215, 227);
        var strokes = new[] { line, along, word };
        var r = new MarkRecognizer();
        var mark = Assert.Single(r.Recognize(0, _width, strokes));
        Assert.Equal([word.Id], r.MarginNotes(mark, _width, strokes).Select(s => s.Id));
    }
}
