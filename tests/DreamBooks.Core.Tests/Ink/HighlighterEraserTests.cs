using DreamBooks.Core.Commands;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Ink;

/// <summary>
/// The eraser on highlighters (FR-31, FR-40, FR-46): its reach is the highlighter's inked extent (the round tip's half
/// width, the chisel nib), not the pressure pen's margin, and a partial erase leaves a visible gap the size of the eraser.
/// Defaults: 16 pt round, 22 pt chisel at 45°, 6 mm eraser.
/// </summary>
public class HighlighterEraserTests
{
    private const double _y = 100;
    private const double _tap = 100;
    private static readonly double _radius = Units.MmToPt(6) / 2; // the default 6 mm eraser, about 8.5 pt
    private static readonly HighlighterSettings _defaults = HighlighterSettings.Default;

    private static InkStroke Round() => Highlight(_defaults.RoundWidthPt, chisel: null);

    private static InkStroke Chisel() =>
        Highlight(_defaults.ChiselLengthPt, new ChiselTip(45, HighlighterSettings.ChiselRatio));

    private static InkStroke Highlight(double width, ChiselTip? chisel) => new(
        Guid.NewGuid(),
        InkTool.Highlighter,
        HighlighterPresets.Yellow,
        width,
        Enumerable.Range(0, 17).Select(i => new InkPoint(20 + (i * 10), _y, 0.5f, null, null, null, i * 10)).ToArray(),
        chisel);

    private static InkStroke Pen(double width) => new(
        Guid.NewGuid(),
        InkTool.Pen,
        InkColor.FromRgb(0, 0, 0),
        width,
        [new InkPoint(20, _y, 0.5f, null, null, null, 0), new InkPoint(180, _y, 0.5f, null, null, null, 10)]);

    /// <summary>Half the inked band of a horizontal pass: 8 pt round; 8.25·sin 45° + 2.75 ≈ 8.58 pt chisel.</summary>
    private static double HalfBand(InkStroke s) => s.Chisel is { } c
        ? ((1 - c.Ratio) * s.BaseWidthPt / 2 * Math.Sin(c.AngleDeg * Math.PI / 180)) + (c.Ratio * s.BaseWidthPt / 2)
        : s.BaseWidthPt / 2;

    public static TheoryData<string> Tips => new() { "round", "chisel" };

    private static InkStroke Of(string tip) => tip == "round" ? Round() : Chisel();

    [Theory]
    [MemberData(nameof(Tips))]
    public void Whole_stroke_pass_2pt_outside_the_inked_band_does_not_hit(string tip)
    {
        var stroke = Of(tip);
        var y = _y - HalfBand(stroke) - 2 - _radius; // the eraser's edge 2 pt above the band (a pen note's interline)

        Assert.False(EraserGeometry.Hits(stroke, new PointD(0, y), new PointD(200, y), _radius));
    }

    [Theory]
    [MemberData(nameof(Tips))]
    public void Whole_stroke_pass_1pt_inside_the_inked_band_hits(string tip)
    {
        var stroke = Of(tip);
        var y = _y - HalfBand(stroke) + 1 - _radius;

        Assert.True(EraserGeometry.Hits(stroke, new PointD(0, y), new PointD(200, y), _radius));
    }

    [Theory]
    [MemberData(nameof(Tips))]
    public void Erasing_a_pen_note_just_above_a_highlight_keeps_the_highlight(string tip)
    {
        var highlight = Of(tip);
        var noteY = _y - HalfBand(highlight) - 2 - _radius;
        var note = new InkStroke(
            Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1,
            [new InkPoint(40, noteY, 0.5f, null, null, null, 0), new InkPoint(120, noteY, 0.5f, null, null, null, 10)]);
        var page = new Page(0, 300, 300);
        var undo = new UndoManager();
        undo.Execute(new AddStrokesCommand(page, [highlight, note]));
        var editor = new PageEditor(page, undo);

        var session = editor.BeginErase(EraserMode.WholeStroke);
        Assert.True(session.MoveTo(new PointD(40, noteY), _radius));
        session.MoveTo(new PointD(120, noteY), _radius);
        Assert.True(session.Complete());

        Assert.Same(highlight, Assert.Single(page.Strokes));
    }

    [Theory]
    [MemberData(nameof(Tips))]
    public void Partial_tap_leaves_a_visible_gap_of_the_eraser_size_and_no_ink_on_the_erased_spot(string tip)
    {
        var stroke = Of(tip);
        var centre = new PointD(_tap, _y);

        var pieces = EraserGeometry.ErasePartial(stroke, centre, centre, _radius);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(stroke.Chisel, p.Chisel));
        var outlines = pieces.Select(p => (IReadOnlyList<IReadOnlyList<PointD>>)[HighlighterGeometry.Outline(p)]).ToArray();

        // No ink of either piece inside the eraser disc (a little tolerance for the 0.25 pt resampling).
        foreach (var q in Grid(centre, _radius - 0.5))
        {
            Assert.All(outlines, o => Assert.Equal(0, PolygonRaster.Winding(o, q)));
        }

        // The pieces do not overlap (a translucent overlap would darken the erased spot).
        var (_, leftMaxX, _, _) = PolygonRaster.Bounds(outlines[0]);
        var (rightMinX, _, _, _) = PolygonRaster.Bounds(outlines[1]);
        foreach (var q in Grid(centre, 3 * _radius))
        {
            Assert.False(
                PolygonRaster.Winding(outlines[0], q) != 0 && PolygonRaster.Winding(outlines[1], q) != 0,
                $"overlap at {q}");
        }

        if (stroke.Chisel is null)
        {
            Assert.True(rightMinX - leftMaxX >= (2 * _radius) - 0.5, $"visible gap {rightMinX - leftMaxX}");
        }
    }

    [Fact]
    public void Partial_erase_through_a_session_opens_the_gap_in_one_undo_step()
    {
        var stroke = Round();
        var page = new Page(0, 300, 300);
        var undo = new UndoManager();
        undo.Execute(new AddStrokesCommand(page, [stroke]));
        var session = new PageEditor(page, undo).BeginErase(EraserMode.Partial);

        Assert.True(session.MoveTo(new PointD(_tap, _y), _radius));
        Assert.True(session.Complete());

        Assert.Equal(2, page.Strokes.Count);
        var left = HighlighterGeometry.Outline(page.Strokes[0]).Max(p => p.X);
        var right = HighlighterGeometry.Outline(page.Strokes[1]).Min(p => p.X);
        Assert.True(right - left >= (2 * _radius) - 0.5);
    }

    [Fact]
    public void Pen_erase_reach_is_unchanged() // regression: radius + 0.75 x width (whole), radius only (partial)
    {
        var pen = Pen(4);
        var reach = _radius + (4 * EraserGeometry.HitMarginFactor);
        Assert.True(EraserGeometry.Hits(pen, new PointD(0, _y - reach + 0.01), new PointD(200, _y - reach + 0.01), _radius));
        Assert.False(EraserGeometry.Hits(pen, new PointD(0, _y - reach - 0.01), new PointD(200, _y - reach - 0.01), _radius));

        var centre = new PointD(_tap, _y);
        var pieces = EraserGeometry.ErasePartial(pen, centre, centre, _radius);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(_tap - _radius, pieces[0].Points[^1].X, 0.3);
        Assert.Equal(_tap + _radius, pieces[1].Points[0].X, 0.3);
    }

    [Fact]
    public void Outline_chooses_the_chisel_or_the_round_tip()
    {
        var round = Round();
        var chisel = Chisel();
        Assert.Equal(HighlighterGeometry.ConstantWidthOutline(round.Points, round.BaseWidthPt), HighlighterGeometry.Outline(round));
        Assert.Equal(
            HighlighterGeometry.ChiselOutline(chisel.Points, chisel.Chisel!.Value, chisel.BaseWidthPt),
            HighlighterGeometry.Outline(chisel));
    }

    [Fact]
    public void Nib_is_the_round_half_width_or_the_chisel_segment_and_its_rounded_ends()
    {
        var (half, radius) = HighlighterGeometry.InkNib(Round());
        Assert.Equal(default, half);
        Assert.Equal(8, radius);

        (half, radius) = HighlighterGeometry.InkNib(Chisel());
        var segment = 0.75 * 22 / 2;
        Assert.Equal(segment * Math.Cos(Math.PI / 4), half.X, 9);
        Assert.Equal(-segment * Math.Sin(Math.PI / 4), half.Y, 9);
        Assert.Equal(0.25 * 22 / 2, radius, 9);
    }

    private static IEnumerable<PointD> Grid(PointD centre, double radius)
    {
        for (var x = centre.X - radius; x <= centre.X + radius; x += 0.5)
        {
            for (var y = centre.Y - radius; y <= centre.Y + radius; y += 0.5)
            {
                if (PointD.Distance(new PointD(x, y), centre) <= radius)
                {
                    yield return new PointD(x, y);
                }
            }
        }
    }
}
