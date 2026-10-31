using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

public class MarkRecognizerTests
{
    private const double _width = 600;

    private static InkStroke Stroke(IEnumerable<(double X, double Y)> pts, InkTool tool = InkTool.Pen)
        => new(
            Guid.NewGuid(),
            tool,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            pts.Select((p, i) => new InkPoint(p.X, p.Y, 0.5f, null, null, null, i)).ToArray());

    private static InkStroke Line(double x, double y0, double y1, double dx = 0, InkTool tool = InkTool.Pen, int n = 20)
        => Stroke(Enumerable.Range(0, n).Select(i =>
        {
            var t = i / (double)(n - 1);
            return (x + (dx * t), y0 + ((y1 - y0) * t));
        }), tool);

    private static IReadOnlyList<Mark> Recognize(params InkStroke[] strokes)
        => new MarkRecognizer().Recognize(3, _width, strokes);

    [Fact]
    public void A_straight_vertical_pen_line_in_the_margin_longer_than_three_lines_is_a_mark()
    {
        var s = Line(30, 100, 260);
        var marks = Recognize(s);
        var m = Assert.Single(marks);
        Assert.Equal(3, m.PageIndex);
        Assert.Equal(MarkSide.Left, m.Side);
        Assert.Equal(100, m.TopPt, 3);
        Assert.Equal(260, m.BottomPt, 3);
        Assert.Equal([s.Id], m.StrokeIds);
    }

    [Fact]
    public void A_wavy_but_vertical_line_within_the_tolerance_is_a_mark()
    {
        var s = Stroke(Enumerable.Range(0, 41).Select(i => (30.0 + (4 * Math.Sin(i * 0.4)), 100 + (i * 4.0))));
        Assert.Single(Recognize(s));
    }

    [Fact]
    public void A_tilted_line_over_twenty_degrees_is_not_a_mark()
    {
        Assert.Empty(Recognize(Line(20, 100, 260, dx: 70)));
    }

    [Fact]
    public void A_zig_zag_or_scribble_is_not_a_mark()
    {
        var s = Stroke(Enumerable.Range(0, 21).Select(i => (i % 2 == 0 ? 20.0 : 60.0, 100 + (i * 8.0))));
        Assert.Empty(Recognize(s));
    }

    [Fact]
    public void A_line_shorter_than_three_lines_is_not_a_mark()
    {
        Assert.Empty(Recognize(Line(30, 100, 200)));
    }

    [Fact]
    public void A_vertical_line_in_the_middle_of_the_page_is_not_a_mark()
    {
        Assert.Empty(Recognize(Line(300, 100, 300)));
    }

    [Fact]
    public void Left_and_right_margins_are_both_recognised_and_report_their_side()
    {
        var marks = Recognize(Line(30, 100, 260), Line(570, 400, 560));
        Assert.Equal(2, marks.Count);
        Assert.Equal(MarkSide.Left, marks[0].Side);
        Assert.Equal(MarkSide.Right, marks[1].Side);
    }

    [Fact]
    public void Highlighter_strokes_are_never_margin_lines()
    {
        Assert.Empty(Recognize(Line(30, 100, 260, tool: InkTool.Highlighter)));
    }

    [Fact]
    public void Two_overlapping_or_close_lines_merge_into_one_mark_with_the_union_extent()
    {
        var a = Line(30, 100, 260);
        var b = Line(40, 200, 330);
        var c = Line(35, 380, 500); // gap 50 pt = 1.39 lines
        var m = Assert.Single(Recognize(a, b, c));
        Assert.Equal(100, m.TopPt, 3);
        Assert.Equal(500, m.BottomPt, 3);
        Assert.Equal(3, m.StrokeIds.Count);
    }

    [Fact]
    public void Lines_farther_apart_than_one_and_a_half_lines_stay_separate_marks()
    {
        var marks = Recognize(Line(30, 100, 260), Line(30, 320, 480)); // gap 60 pt
        Assert.Equal(2, marks.Count);
    }

    [Fact]
    public void Lines_on_opposite_sides_never_merge()
    {
        var marks = Recognize(Line(30, 100, 260), Line(570, 100, 260));
        Assert.Equal(2, marks.Count);
    }

    [Fact]
    public void Marks_are_ordered_by_top()
    {
        var marks = Recognize(Line(570, 500, 700), Line(30, 100, 260), Line(30, 400, 560));
        Assert.Equal([100, 400, 500], marks.Select(m => m.TopPt).ToArray());
    }

    [Fact]
    public void A_stroke_with_fewer_than_two_points_is_ignored()
    {
        Assert.Empty(Recognize(Stroke([(30, 100)])));
    }

    [Fact]
    public void Margin_notes_are_the_non_mark_strokes_near_the_mark_within_the_window()
    {
        var line = Line(30, 200, 360);
        var near = Stroke([(60, 250), (90, 260), (120, 255)]);
        var justAbove = Stroke([(40, 170), (70, 175)]); // within one line above
        var tooFarAbove = Stroke([(40, 100), (70, 110)]);
        var tooFarRight = Stroke([(400, 250), (430, 260)]);
        var onOtherSide = Stroke([(560, 250), (580, 260)]);
        var all = new[] { line, near, justAbove, tooFarAbove, tooFarRight, onOtherSide };
        var recognizer = new MarkRecognizer();
        var mark = Assert.Single(recognizer.Recognize(0, _width, all));

        var notes = recognizer.MarginNotes(mark, _width, all);

        Assert.Equal(new[] { near.Id, justAbove.Id }.Order(), notes.Select(s => s.Id).Order());
    }

    [Fact]
    public void Custom_line_metrics_change_the_minimum_extent()
    {
        var s = Line(30, 100, 200); // 100 pt
        Assert.Empty(new MarkRecognizer().Recognize(0, _width, [s]));
        Assert.Single(new MarkRecognizer(new FixedLineMetrics(20)).Recognize(0, _width, [s]));
    }
}
