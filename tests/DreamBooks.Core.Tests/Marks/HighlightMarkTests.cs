using DreamBooks.Core.Ink;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// Highlights as marks (FR-100, spec 3c section 6): a cluster of highlighter strokes that touch or overlap vertically
/// is a mark when its inked extent is at least <see cref="MarkRules.HighlightMinHeightPt"/> tall and
/// <see cref="MarkRules.HighlightMinWidthPt"/> wide; its side is Centre in the middle band of the page, else Left or
/// Right by its centre. Smaller clusters stay notes.
/// </summary>
public class HighlightMarkTests
{
    private const double _width = 600;

    /// <summary>
    /// A straight horizontal round-tip highlight whose inked box is <paramref name="left"/>..<paramref name="right"/>
    /// by <paramref name="top"/>..<paramref name="bottom"/>: the centre-line runs half the width inside each end.
    /// </summary>
    private static InkStroke Highlight(double left, double right, double top, double bottom)
    {
        var w = bottom - top;
        var y = (top + bottom) / 2;
        return new InkStroke(
            Guid.NewGuid(),
            InkTool.Highlighter,
            HighlighterPresets.Yellow,
            w,
            [new InkPoint(left + (w / 2), y, 0.5f, null, null, null, 0), new InkPoint(right - (w / 2), y, 0.5f, null, null, null, 1)]);
    }

    private static InkStroke Pen(IEnumerable<(double X, double Y)> pts)
        => new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            pts.Select((p, i) => new InkPoint(p.X, p.Y, 0.5f, null, null, null, i)).ToArray());

    private static InkStroke MarginLine(double x, double top, double bottom)
        => Pen(Enumerable.Range(0, 20).Select(i => (x, top + ((bottom - top) * i / 19.0))));

    private static (IReadOnlyList<Mark> Marks, IReadOnlyList<Note> Notes) Page(params InkStroke[] strokes)
        => new MarkRecognizer().RecognizePage(2, _width, strokes);

    [Fact]
    public void The_thresholds_are_one_text_line_tall_and_sixty_points_wide()
    {
        Assert.Equal(MarkRules.DefaultLineHeightPt, MarkRules.HighlightMinHeightPt);
        Assert.Equal(60, MarkRules.HighlightMinWidthPt);
        Assert.Equal(0.36, MarkRules.HighlightCentreBandFraction);
    }

    [Fact]
    public void A_line_tall_highlight_in_the_middle_is_a_centre_mark()
    {
        var hl = Highlight(240, 360, 300, 316);
        var (marks, notes) = Page(hl);
        var mark = Assert.Single(marks);
        Assert.Equal(2, mark.PageIndex);
        Assert.Equal(MarkSide.Centre, mark.Side);
        Assert.Equal(300, mark.TopPt, 6);
        Assert.Equal(316, mark.BottomPt, 6);
        Assert.Equal([hl.Id], mark.StrokeIds);
        Assert.Empty(notes);
    }

    [Fact]
    public void The_same_highlight_near_an_edge_takes_the_side_of_its_centre()
    {
        Assert.Equal(MarkSide.Left, Assert.Single(Page(Highlight(20, 140, 300, 316)).Marks).Side);
        Assert.Equal(MarkSide.Right, Assert.Single(Page(Highlight(460, 580, 300, 316)).Marks).Side);
    }

    [Fact]
    public void The_centre_band_is_the_middle_thirty_six_percent_inclusive()
    {
        // The band is 192..408 on a 600 pt page; a 100 pt wide highlight centred on each edge.
        Assert.Equal(MarkSide.Centre, Assert.Single(Page(Highlight(142, 242, 300, 316)).Marks).Side);
        Assert.Equal(MarkSide.Centre, Assert.Single(Page(Highlight(358, 458, 300, 316)).Marks).Side);
        Assert.Equal(MarkSide.Left, Assert.Single(Page(Highlight(141, 241, 300, 316)).Marks).Side);
        Assert.Equal(MarkSide.Right, Assert.Single(Page(Highlight(359, 459, 300, 316)).Marks).Side);
    }

    [Fact]
    public void A_highlight_shorter_than_a_line_is_a_note()
    {
        var hl = Highlight(240, 360, 300, 314);
        var (marks, notes) = Page(hl);
        Assert.Empty(marks);
        Assert.Equal([hl.Id], Assert.Single(notes).StrokeIds);
    }

    [Fact]
    public void A_highlight_narrower_than_sixty_points_is_a_note()
    {
        var hl = Highlight(250, 300, 300, 316);
        var (marks, notes) = Page(hl);
        Assert.Empty(marks);
        Assert.Equal([hl.Id], Assert.Single(notes).StrokeIds);
    }

    [Fact]
    public void Exactly_one_line_tall_and_sixty_points_wide_is_a_mark()
    {
        var h = MarkRules.HighlightMinHeightPt;
        var w = MarkRules.HighlightMinWidthPt;
        Assert.Single(Page(Highlight(270, 270 + w, 300, 300 + h)).Marks);
        Assert.Empty(Page(Highlight(270, 270 + w - 0.01, 300, 300 + h)).Marks);
        Assert.Empty(Page(Highlight(270, 270 + w, 300, 300 + h - 0.01)).Marks);
    }

    [Fact]
    public void Two_highlights_touching_vertically_merge_into_one_mark()
    {
        // Each one is 8 pt tall (a note alone); together 16 pt.
        var upper = Highlight(240, 360, 300, 308);
        var lower = Highlight(250, 380, 308, 316);
        var (marks, notes) = Page(lower, upper);
        var mark = Assert.Single(marks);
        Assert.Equal(300, mark.TopPt, 6);
        Assert.Equal(316, mark.BottomPt, 6);
        Assert.Equal([upper.Id, lower.Id], mark.StrokeIds);
        Assert.Empty(notes);
    }

    [Fact]
    public void Highlights_with_a_vertical_gap_do_not_merge()
    {
        var upper = Highlight(240, 360, 300, 308);
        var lower = Highlight(240, 360, 308.5, 316);
        var (marks, notes) = Page(upper, lower);
        Assert.Empty(marks);
        Assert.Equal([upper.Id, lower.Id], Assert.Single(notes).StrokeIds); // one note, as before
    }

    [Fact]
    public void Overlapping_highlights_of_two_lines_make_one_mark_spanning_both()
    {
        var a = Highlight(300, 500, 300, 316);
        var b = Highlight(80, 250, 312, 330);
        var mark = Assert.Single(Page(a, b).Marks);
        Assert.Equal(300, mark.TopPt, 6);
        Assert.Equal(330, mark.BottomPt, 6);
        Assert.Equal(MarkSide.Centre, mark.Side); // centre of 80..500 is 290
    }

    [Fact]
    public void A_chisel_highlight_counts_its_inked_extent()
    {
        // A horizontal stroke with a 20 pt chisel at 90 degrees: the nib stands vertical, so the ink is 20 pt tall.
        var points = new[] { new InkPoint(200, 300, 0.5f, null, null, null, 0), new InkPoint(400, 300, 0.5f, null, null, null, 1) };
        var upright = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, HighlighterPresets.Yellow, 20, points, new ChiselTip(90, 0.25));
        var mark = Assert.Single(Page(upright).Marks);
        Assert.Equal(290, mark.TopPt, 6);
        Assert.Equal(310, mark.BottomPt, 6);

        // At 0 degrees the nib lies flat: the ink is only r * L = 5 pt tall, a note.
        var flat = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, HighlighterPresets.Yellow, 20, points, new ChiselTip(0, 0.25));
        Assert.Empty(Page(flat).Marks);
    }

    private static InkStroke DefaultPass(double angleDeg, bool chisel)
    {
        var settings = HighlighterSettings.Default with { Chisel = chisel, ChiselAngleDeg = angleDeg };
        return new InkStroke(
            Guid.NewGuid(),
            InkTool.Highlighter,
            settings.StrokeColor,
            settings.WidthPt,
            [new InkPoint(240, 307, 0.5f, null, null, null, 0), new InkPoint(360, 307, 0.5f, null, null, null, 1)],
            settings.Tip);
    }

    [Fact]
    public void One_pass_of_the_default_round_tip_over_a_line_is_a_mark()
    {
        var mark = Assert.Single(Page(DefaultPass(45, chisel: false)).Marks);
        Assert.Equal(16, mark.BottomPt - mark.TopPt, 6);
        Assert.Equal(MarkSide.Centre, mark.Side);
    }

    [Fact]
    public void One_horizontal_pass_of_the_default_chisel_at_45_degrees_over_a_line_is_a_mark()
    {
        // The nib shows L * (r + (1 - r) * |sin(theta)|) across a horizontal pass: 22 * (0.25 + 0.75 sin 45) = 17.2 pt.
        Assert.Equal(22, HighlighterSettings.Default.ChiselLengthPt);
        var mark = Assert.Single(Page(DefaultPass(HighlighterSettings.Default.ChiselAngleDeg, chisel: true)).Marks);
        Assert.Equal(22 * (0.25 + (0.75 * Math.Sin(Math.PI / 4))), mark.BottomPt - mark.TopPt, 6);
        Assert.Equal(MarkSide.Centre, mark.Side);
    }

    [Fact]
    public void The_default_chisel_height_follows_the_width_formula_at_0_and_90_degrees()
    {
        // Upright nib (90): the full 22 pt, a mark. Flat nib (0): r * L = 5.5 pt, under a line, a note.
        var upright = Assert.Single(Page(DefaultPass(90, chisel: true)).Marks);
        Assert.Equal(22, upright.BottomPt - upright.TopPt, 6);

        var (marks, notes) = Page(DefaultPass(0, chisel: true));
        Assert.Empty(marks);
        Assert.Single(notes);
    }

    [Fact]
    public void A_highlight_thinner_than_a_line_with_a_custom_tip_is_still_a_note()
    {
        var thin = Highlight(240, 360, 300, 312);
        var (marks, notes) = Page(thin);
        Assert.Empty(marks);
        Assert.Equal([thin.Id], Assert.Single(notes).StrokeIds);
    }

    [Fact]
    public void A_margin_line_beside_a_highlight_is_still_a_margin_mark()
    {
        var line = MarginLine(30, 280, 360);
        var hl = Highlight(240, 360, 300, 316);
        var marks = Page(line, hl).Marks;
        Assert.Equal(2, marks.Count);
        var margin = Assert.Single(marks, m => m.Side == MarkSide.Left);
        Assert.Equal([line.Id], margin.StrokeIds);
        Assert.Equal(280, margin.TopPt, 6);
        Assert.Equal(360, margin.BottomPt, 6);
        Assert.Equal([hl.Id], Assert.Single(marks, m => m.Side == MarkSide.Centre).StrokeIds);
    }

    [Fact]
    public void A_highlight_in_the_left_margin_does_not_join_a_margin_line_mark()
    {
        var line = MarginLine(30, 280, 360);
        var hl = Highlight(10, 110, 300, 316);
        var marks = Page(line, hl).Marks;
        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, m => m.StrokeIds.SequenceEqual([line.Id]));
        Assert.Contains(marks, m => m.StrokeIds.SequenceEqual([hl.Id]));
    }

    [Fact]
    public void Pen_strokes_never_join_a_highlight_cluster()
    {
        // A pen word overlapping a small highlight: neither is a mark, both are one note.
        var hl = Highlight(240, 360, 300, 308);
        var word = Pen(Enumerable.Range(0, 12).Select(i => (250 + (i * 2.5), i % 2 == 0 ? 305.0 : 320.0)));
        var (marks, notes) = Page(hl, word);
        Assert.Empty(marks);
        Assert.Equal([hl.Id, word.Id], Assert.Single(notes).StrokeIds);
    }

    [Fact]
    public void Highlighter_strokes_are_never_margin_notes()
    {
        var r = new MarkRecognizer();
        var line = MarginLine(30, 280, 360);
        var small = Highlight(60, 100, 300, 308);
        var big = Highlight(50, 170, 300, 316);
        var all = new[] { line, small, big };
        var margin = Assert.Single(r.Recognize(0, _width, all), m => m.StrokeIds.Contains(line.Id));
        Assert.Empty(r.MarginNotes(margin, _width, all));
    }

    [Fact]
    public void A_centre_mark_has_no_margin_notes()
    {
        var r = new MarkRecognizer();
        var hl = Highlight(240, 360, 300, 316);
        var word = Pen(Enumerable.Range(0, 12).Select(i => (500 + (i * 2.5), i % 2 == 0 ? 300.0 : 312.0)));
        var all = new[] { hl, word };
        var mark = Assert.Single(r.Recognize(0, _width, all));
        Assert.Equal(MarkSide.Centre, mark.Side);
        Assert.Empty(r.MarginNotes(mark, _width, all));
    }
}
