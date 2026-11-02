using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// The rule against Fausto's own pen strokes (<see cref="RealPenStrokes"/>), recorded after the first on-device test,
/// where no mark was recognised: his margin lines are 40-95 pt long (the rule wanted 108 pt) and some are retraced up
/// and down (the path rule rejected them). Each must be a mark; his other strokes must not.
/// </summary>
public class RealPenStrokeMarkTests
{
    public static TheoryData<string, MarkSide> MarginLines => new()
    {
        { nameof(RealPenStrokes.StraightShortLeft), MarkSide.Left }, // 44 pt, about three printed lines
        { nameof(RealPenStrokes.RetracedShortLeft), MarkSide.Left }, // 40 pt, down and up four times
        { nameof(RealPenStrokes.RetracedLeft), MarkSide.Left }, // 67 pt, down and up four times
        { nameof(RealPenStrokes.HatchedBandLeft), MarkSide.Left }, // 95 pt, a narrow band drawn up and down
        { nameof(RealPenStrokes.StraightRight), MarkSide.Right }, // 71 pt
    };

    public static TheoryData<string> OtherStrokes => new()
    {
        nameof(RealPenStrokes.Underline),
        nameof(RealPenStrokes.WavyStrokeAcrossText),
        nameof(RealPenStrokes.MarginHandwriting),
        nameof(RealPenStrokes.TallLetterInText),
    };

    [Theory]
    [MemberData(nameof(MarginLines))]
    public void A_real_margin_line_is_a_mark_on_its_side(string name, MarkSide side)
    {
        var stroke = Load(name);
        var mark = Assert.Single(new MarkRecognizer().Recognize(0, RealPenStrokes.PageWidthPt, [stroke]));
        Assert.Equal(side, mark.Side);
        Assert.Equal(stroke.Points.Min(p => p.Y), mark.TopPt, 6);
        Assert.Equal(stroke.Points.Max(p => p.Y), mark.BottomPt, 6);
    }

    [Theory]
    [MemberData(nameof(OtherStrokes))]
    public void A_real_stroke_that_is_not_a_margin_line_is_not_a_mark(string name)
    {
        Assert.Empty(new MarkRecognizer().Recognize(0, RealPenStrokes.PageWidthPt, [Load(name)]));
    }

    [Fact]
    public void All_the_real_strokes_on_one_page_give_exactly_the_margin_lines()
    {
        var strokes = MarginLines.Cast<object[]>().Concat(OtherStrokes.Cast<object[]>())
            .Select(row => Load((string)row[0]))
            .ToArray();
        var marks = new MarkRecognizer().Recognize(0, RealPenStrokes.PageWidthPt, strokes);
        var lineIds = strokes.Take(MarginLines.Count).Select(s => s.Id).ToHashSet();
        Assert.All(marks.SelectMany(m => m.StrokeIds), id => Assert.Contains(id, lineIds));
        Assert.Equal(lineIds.Order(), marks.SelectMany(m => m.StrokeIds).Order());
    }

    private static InkStroke Load(string name) =>
        RealPenStrokes.Parse((string)typeof(RealPenStrokes).GetField(name)!.GetRawConstantValue()!);
}
