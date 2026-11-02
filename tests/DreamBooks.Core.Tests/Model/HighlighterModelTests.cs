using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Model;

public class HighlighterModelTests
{
    private static readonly Guid _id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly InkPoint[] _pts = [new InkPoint(1, 2, null, null, null, null, 0)];

    [Fact]
    public void Presets_are_six_distinct_colours_with_the_highlighter_alpha()
    {
        Assert.Equal(6, HighlighterPresets.All.Count);
        Assert.Equal(6, HighlighterPresets.All.Select(c => (c.R, c.G, c.B)).Distinct().Count());
        Assert.All(HighlighterPresets.All, c => Assert.Equal(89, c.A));
        Assert.Equal(89, HighlighterPresets.Alpha);
    }

    [Fact]
    public void A_chisel_stroke_round_trips_through_JSON()
    {
        var stroke = new InkStroke(_id, InkTool.Highlighter, HighlighterPresets.Yellow, 14, _pts, new ChiselTip(45, 0.25));

        var result = StrokeJson.Parse(StrokeJson.Serialize(stroke));

        Assert.Equal(StrokeParseStatus.Ok, result.Status);
        Assert.Equal(new ChiselTip(45, 0.25), result.Stroke!.Chisel);
    }

    [Fact]
    public void A_stroke_without_chisel_writes_no_chisel_key()
    {
        var stroke = new InkStroke(_id, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2, _pts);

        var json = StrokeJson.Serialize(stroke);

        Assert.DoesNotContain("chisel", json);
        Assert.Null(StrokeJson.Parse(json).Stroke!.Chisel);
    }

    [Fact]
    public void Unknown_root_keys_are_ignored_on_read()
    {
        var json = "{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":2,\"extra\":{\"a\":1},\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}";

        var result = StrokeJson.Parse(json);

        Assert.Equal(StrokeParseStatus.Ok, result.Status);
        Assert.Null(result.Stroke!.Chisel);
    }

    [Theory]
    [InlineData(-0.1, 0.25)]
    [InlineData(180.1, 0.25)]
    [InlineData(45, -0.1)]
    [InlineData(45, 1.1)]
    public void A_chisel_outside_its_range_is_rejected_at_construction(double angle, double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChiselTip(angle, ratio));
    }

    [Fact]
    public void WithPoints_keeps_the_chisel()
    {
        var stroke = new InkStroke(_id, InkTool.Highlighter, HighlighterPresets.Pink, 14, _pts, new ChiselTip(90, 0.5));

        Assert.Equal(new ChiselTip(90, 0.5), stroke.WithPoints(_pts).Chisel);
    }
}
