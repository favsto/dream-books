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
    public void A_straight_vertical_pen_line_in_the_margin_longer_than_two_lines_is_a_mark()
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
    public void A_tilted_line_over_twenty_five_degrees_is_not_a_mark()
    {
        Assert.Empty(Recognize(Line(20, 100, 260, dx: 80)));
    }

    [Fact]
    public void A_zig_zag_or_scribble_is_not_a_mark()
    {
        var s = Stroke(Enumerable.Range(0, 21).Select(i => (i % 2 == 0 ? 20.0 : 60.0, 100 + (i * 8.0))));
        Assert.Empty(Recognize(s));
    }

    [Fact]
    public void A_line_shorter_than_two_lines_is_not_a_mark()
    {
        Assert.Empty(Recognize(Line(30, 100, 125)));
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
        var c = Line(35, 350, 500); // gap 20 pt = 1.33 lines
        var m = Assert.Single(Recognize(a, b, c));
        Assert.Equal(100, m.TopPt, 3);
        Assert.Equal(500, m.BottomPt, 3);
        Assert.Equal(3, m.StrokeIds.Count);
    }

    [Fact]
    public void Lines_farther_apart_than_one_and_a_half_lines_stay_separate_marks()
    {
        var marks = Recognize(Line(30, 100, 260), Line(30, 290, 450)); // gap 30 pt = 2 lines
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
        var justAbove = Stroke([(40, 188), (70, 195)]); // within one line (15 pt) above
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
    public void Recognising_identical_strokes_twice_gives_equal_marks()
    {
        var strokes = new[] { Line(30, 100, 260), Line(40, 200, 330) };
        var a = Assert.Single(Recognize(strokes));
        var b = Assert.Single(Recognize(strokes));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void A_different_stroke_set_gives_an_unequal_mark()
    {
        var a = Assert.Single(Recognize(Line(30, 100, 260)));
        var b = Assert.Single(Recognize(Line(30, 100, 260)));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Mark_stroke_ids_cannot_be_mutated_through_a_cast()
    {
        var m = Assert.Single(Recognize(Line(30, 100, 260)));
        Assert.False(m.StrokeIds is List<Guid>);
        Assert.False(m.StrokeIds is Guid[]);
    }

    [Fact]
    public void A_zero_point_stroke_cannot_exist()
    {
        // InkStroke rejects empty point lists, so MarginNotes can never see one; its guard is defensive.
        Assert.Throws<ArgumentException>(() => Stroke([]));
    }

    [Fact]
    public void Highlighter_strokes_near_a_mark_are_not_margin_notes()
    {
        var line = Line(30, 200, 360);
        var hl = Stroke([(60, 250), (90, 260)], InkTool.Highlighter);
        var r = new MarkRecognizer();
        var all = new[] { line, hl };
        var mark = Assert.Single(r.Recognize(0, _width, all));
        Assert.Empty(r.MarginNotes(mark, _width, all));
    }

    [Fact]
    public void An_upward_drawn_line_is_a_mark()
    {
        var m = Assert.Single(Recognize(Line(30, 260, 100)));
        Assert.Equal(100, m.TopPt, 3);
        Assert.Equal(260, m.BottomPt, 3);
    }

    [Fact]
    public void A_gap_of_exactly_one_and_a_half_lines_merges_and_just_over_does_not()
    {
        Assert.Single(Recognize(Line(30, 100, 260), Line(30, 282.5, 442.5)));
        Assert.Equal(2, Recognize(Line(30, 100, 260), Line(30, 283, 443)).Count);
    }

    [Fact]
    public void An_extent_of_exactly_two_lines_is_a_mark_and_just_under_is_not()
    {
        Assert.Single(Recognize(Line(30, 100, 130)));
        Assert.Empty(Recognize(Line(30, 100, 129.9)));
    }

    [Fact]
    public void The_margin_zone_edge_is_inside_and_just_beyond_is_outside()
    {
        Assert.Single(Recognize(Line(108, 100, 260)));
        Assert.Empty(Recognize(Line(108.5, 100, 260)));
        Assert.Single(Recognize(Line(492, 100, 260)));
        Assert.Empty(Recognize(Line(491.5, 100, 260)));
    }

    [Fact]
    public void Right_side_margin_notes_are_found_on_the_right_only()
    {
        var line = Line(570, 200, 360);
        var near = Stroke([(500, 250), (540, 260)]);
        var tooFar = Stroke([(300, 250), (340, 260)]);
        var leftSide = Stroke([(40, 250), (60, 260)]);
        var r = new MarkRecognizer();
        var all = new[] { line, near, tooFar, leftSide };
        var mark = Assert.Single(r.Recognize(0, _width, all));
        Assert.Equal(near.Id, Assert.Single(r.MarginNotes(mark, _width, all)).Id);
    }

    [Fact]
    public void An_average_deviation_just_under_twenty_five_degrees_is_a_mark_and_just_over_is_not()
    {
        // Two legs going away from the vertical and back, 100 pt down each: horizontal travel / vertical travel = tan(angle).
        static InkStroke Detour(double degrees)
        {
            var w = 100 * Math.Tan(degrees * Math.PI / 180);
            return Stroke([(30.0, 100.0), (30 + w, 200.0), (30.0, 300.0)]);
        }

        Assert.Single(Recognize(Detour(24.9)));
        Assert.Empty(Recognize(Detour(25.1)));
    }

    [Fact]
    public void A_line_retraced_up_and_down_several_times_is_a_mark()
    {
        // Down, up, down, up, down over 66 pt, wandering 3 pt sideways: a thickened line (seen on the device).
        var pts = new List<(double X, double Y)>();
        double[] turns = [546, 610, 547, 604, 549, 607];
        for (var leg = 0; leg < turns.Length - 1; leg++)
        {
            for (var i = 0; i < 60; i++)
            {
                var t = i / 60.0;
                pts.Add((32 + (1.5 * Math.Sin((leg + t) * 1.3)), turns[leg] + ((turns[leg + 1] - turns[leg]) * t)));
            }
        }

        var m = Assert.Single(Recognize(Stroke(pts)));
        Assert.Equal(546, m.TopPt, 0);
        Assert.Equal(610, m.BottomPt, 0);
    }

    [Fact]
    public void An_invalid_page_width_is_rejected()
    {
        var r = new MarkRecognizer();
        var s = new[] { Line(30, 100, 260) };
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Recognize(0, double.NaN, s));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Recognize(0, 0, s));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Recognize(0, -5, s));
        var mark = new Mark(0, MarkSide.Left, 0, 100, []);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.MarginNotes(mark, double.NaN, s));
    }

    [Fact]
    public void An_invalid_line_height_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineMetrics(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedLineMetrics(double.NaN));
        var r = new MarkRecognizer(new BadMetrics());
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Recognize(0, _width, [Line(30, 100, 260)]));
    }

    private sealed class BadMetrics : IPageLineMetrics
    {
        public double LineHeightPt(int pageIndex) => -1;
    }

    [Fact]
    public void Custom_line_metrics_change_the_minimum_extent()
    {
        var s = Line(30, 100, 125); // 25 pt: under two default (15 pt) lines, over two 10 pt lines
        Assert.Empty(new MarkRecognizer().Recognize(0, _width, [s]));
        Assert.Single(new MarkRecognizer(new FixedLineMetrics(10)).Recognize(0, _width, [s]));
    }

    /// <summary>
    /// A margin line as the digitizer delivers it (bug: no mark was ever found on the device): hundreds of raw samples
    /// less than a point apart, each with ±0.75 pt of jitter (±1 DIP at 100 % zoom), a dwell where the pen lands and
    /// a slight bow. Measured sample by sample its path is about 1.6 times its length, so the path rule rejected it.
    /// </summary>
    [Theory]
    [InlineData(80, 400)]
    [InlineData(120, 300)]
    [InlineData(200, 600)]
    public void A_dense_jittery_hand_drawn_margin_line_is_a_mark(double heightPt, int samples)
    {
        var random = new Random(11);
        double Jitter() => ((random.NextDouble() * 2) - 1) * 0.75;
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i < 10; i++)
        {
            pts.Add((40 + (Jitter() * 0.3), 100 + (Jitter() * 0.3))); // the pen lands and rests
        }

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / (samples - 1);
            var eased = t - (Math.Sin(2 * Math.PI * t) / (2 * Math.PI)); // slow at both ends
            pts.Add((40 + (3 * Math.Sin(Math.PI * t)) + Jitter(), 100 + (heightPt * eased) + Jitter()));
        }

        var m = Assert.Single(Recognize(Stroke(pts)));
        Assert.Equal(MarkSide.Left, m.Side);
    }

    [Fact]
    public void The_path_rule_does_not_depend_on_how_densely_the_same_line_is_sampled()
    {
        // The same small zig-zag-free waver, at 20 and at 2000 samples.
        static InkStroke Sampled(int n) => Stroke(Enumerable.Range(0, n).Select(i =>
        {
            var t = i / (double)(n - 1);
            return (40 + (0.6 * Math.Sin(i * 2.0)), 100 + (100 * t));
        }));

        Assert.Single(Recognize(Sampled(20)));
        Assert.Single(Recognize(Sampled(2000)));
    }

    [Fact]
    public void A_square_bracket_in_the_margin_is_a_mark()
    {
        // "[" over two lines with 15 pt serifs: path 102 pt over 72 pt end to end (ratio 1.42).
        Assert.Single(Recognize(Stroke([(55.0, 100.0), (40.0, 100.0), (40.0, 172.0), (55.0, 172.0)])));
    }

    [Fact]
    public void A_scribble_drawn_with_many_samples_is_still_not_a_mark()
    {
        // Back and forth 30 pt wide every 10 pt down, sampled densely.
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i <= 2000; i++)
        {
            var t = i / 2000.0;
            var phase = (t * 16) % 2;
            pts.Add((20 + (30 * (phase < 1 ? phase : 2 - phase)), 100 + (160 * t)));
        }

        Assert.Empty(Recognize(Stroke(pts)));
    }
}
