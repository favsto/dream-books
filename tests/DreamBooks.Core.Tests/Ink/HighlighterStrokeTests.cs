using DreamBooks.Core.Commands;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Ink;

public class HighlighterStrokeTests
{
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);

    private static InkPoint P(double x, double y, float pressure = 0.5f, long t = 0)
        => new(x, y, pressure, null, null, null, t);

    private static (Page Page, UndoManager Undo, PageEditor Editor) Setup()
    {
        var page = new Page(0, 400, 400);
        var undo = new UndoManager();
        return (page, undo, new PageEditor(page, undo));
    }

    // A slightly wobbly, nearly horizontal line: straightened in straight-line mode (FR-44).
    private static InkPoint[] NearlyHorizontal() =>
        [P(20, 100, t: 0), P(60, 101, t: 10), P(100, 99.5, t: 20), P(140, 100.5, t: 30), P(180, 101, t: 40)];

    [Fact]
    public void Default_settings_are_yellow_round_45_degrees_straight_off_16_and_22_points()
    {
        var settings = HighlighterSettings.Default;

        Assert.Equal(HighlighterPresets.Yellow, settings.Color);
        Assert.False(settings.Chisel);
        Assert.Equal(45, settings.ChiselAngleDeg);
        Assert.False(settings.StraightLine);
        Assert.Equal(16, settings.RoundWidthPt);
        Assert.Equal(22, settings.ChiselLengthPt);
        Assert.Equal(0.25, HighlighterSettings.ChiselRatio);
    }

    [Fact]
    public void Editor_starts_with_the_default_highlighter_settings()
    {
        var (_, _, editor) = Setup();

        Assert.Equal(HighlighterSettings.Default, editor.HighlighterSettings);
    }

    [Fact]
    public void Round_highlighter_stroke_has_tool_colour_alpha_and_round_width()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { Color = HighlighterPresets.Green });

        Assert.True(editor.CommitHighlighterStroke([P(20, 50), P(200, 50)]));

        var stroke = Assert.Single(page.Strokes);
        Assert.Equal(InkTool.Highlighter, stroke.Tool);
        Assert.Equal(HighlighterPresets.Green, stroke.Color);
        Assert.Equal(89, stroke.Color.A);
        Assert.Equal(HighlighterSettings.Default.RoundWidthPt, stroke.BaseWidthPt);
        Assert.Null(stroke.Chisel);
    }

    [Fact]
    public void Chisel_highlighter_stroke_has_chisel_length_and_tip()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { Chisel = true, ChiselAngleDeg = 30, Color = HighlighterPresets.Pink });

        Assert.True(editor.CommitHighlighterStroke([P(20, 50), P(200, 50)]));

        var stroke = Assert.Single(page.Strokes);
        Assert.Equal(InkTool.Highlighter, stroke.Tool);
        Assert.Equal(HighlighterPresets.Pink, stroke.Color);
        Assert.Equal(HighlighterSettings.Default.ChiselLengthPt, stroke.BaseWidthPt);
        Assert.Equal(new ChiselTip(30, 0.25), stroke.Chisel);
    }

    [Fact]
    public void An_opaque_colour_is_made_translucent_with_the_highlighter_alpha()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { Color = InkColor.FromRgb(10, 20, 30) });

        Assert.True(editor.CommitHighlighterStroke([P(20, 50), P(200, 50)]));

        Assert.Equal(new InkColor(89, 10, 20, 30), Assert.Single(page.Strokes).Color);
    }

    [Fact]
    public void Pressure_does_not_change_the_highlighter_width()
    {
        var (page, _, editor) = Setup();

        Assert.True(editor.CommitHighlighterStroke([P(20, 50, 0.05f), P(100, 60, 0.05f), P(200, 50, 0.05f)]));
        Assert.True(editor.CommitHighlighterStroke([P(20, 50, 1f), P(100, 60, 1f), P(200, 50, 1f)]));

        var light = page.Strokes[0];
        var heavy = page.Strokes[1];
        Assert.Equal(light.BaseWidthPt, heavy.BaseWidthPt);
        Assert.Equal(
            HighlighterGeometry.ConstantWidthOutline(light.Points, light.BaseWidthPt),
            HighlighterGeometry.ConstantWidthOutline(heavy.Points, heavy.BaseWidthPt));
    }

    [Fact]
    public void Highlighter_stroke_is_one_undo_step_and_is_clipped_to_the_page()
    {
        var (page, undo, editor) = Setup();

        Assert.True(editor.CommitHighlighterStroke([P(300, 50), P(500, 50), P(500, 80), P(300, 80)]));

        Assert.Equal(2, page.Strokes.Count);
        Assert.All(page.Strokes, s => Assert.Equal(InkTool.Highlighter, s.Tool));
        Assert.All(page.Strokes.SelectMany(s => s.Points), p => Assert.InRange(p.X, 0, 400));
        Assert.True(undo.Undo());
        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Highlighter_stroke_outside_the_page_adds_nothing()
    {
        var (page, undo, editor) = Setup();

        Assert.False(editor.CommitHighlighterStroke([P(-5, 50), P(50, 50)]));

        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Pen_stroke_is_unchanged_by_the_highlighter_settings()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { Chisel = true, StraightLine = true });
        InkPoint[] drawn = NearlyHorizontal();

        Assert.True(editor.CommitStroke(drawn, InkTool.Pen, _black, 1.5));

        var stroke = Assert.Single(page.Strokes);
        Assert.Equal(InkTool.Pen, stroke.Tool);
        Assert.Equal(_black, stroke.Color);
        Assert.Equal(1.5, stroke.BaseWidthPt);
        Assert.Null(stroke.Chisel);
        Assert.Equal(drawn, stroke.Points);
    }

    [Fact]
    public void Straight_line_off_keeps_the_drawn_points()
    {
        var (page, undo, editor) = Setup();
        var drawn = NearlyHorizontal();

        Assert.True(editor.CommitHighlighterStroke(drawn));

        Assert.Equal(drawn, Assert.Single(page.Strokes).Points);
        Assert.True(undo.Undo());
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Straight_line_on_commits_two_points_and_undo_restores_the_drawn_stroke()
    {
        var (page, undo, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { StraightLine = true, Chisel = true, Color = HighlighterPresets.Orange });
        var drawn = NearlyHorizontal();

        Assert.True(editor.CommitHighlighterStroke(drawn));

        var straight = Assert.Single(page.Strokes);
        Assert.Equal(2, straight.Points.Count);
        Assert.Equal(straight.Points[0].Y, straight.Points[1].Y);
        Assert.Equal(20, straight.Points[0].X);
        Assert.Equal(180, straight.Points[1].X);
        Assert.Equal(0, straight.Points[0].TimestampMs);
        Assert.Equal(40, straight.Points[1].TimestampMs);
        Assert.Equal(InkTool.Highlighter, straight.Tool);
        Assert.Equal(HighlighterPresets.Orange, straight.Color);
        Assert.Equal(new ChiselTip(45, 0.25), straight.Chisel);

        Assert.True(undo.Undo());
        Assert.Equal(drawn, Assert.Single(page.Strokes).Points);

        Assert.True(undo.Undo());
        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);

        Assert.True(undo.Redo());
        Assert.True(undo.Redo());
        Assert.Same(straight, Assert.Single(page.Strokes));
    }

    [Fact]
    public void Straight_line_on_keeps_a_stroke_that_is_not_near_horizontal()
    {
        var (page, undo, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { StraightLine = true });
        InkPoint[] drawn = [P(20, 100), P(100, 130), P(180, 160)];

        Assert.True(editor.CommitHighlighterStroke(drawn));

        Assert.Equal(drawn, Assert.Single(page.Strokes).Points);
        Assert.True(undo.Undo());
        Assert.False(undo.CanUndo); // no straightening step
    }

    [Fact]
    public void Straight_line_keeps_the_z_order_of_the_page()
    {
        var (page, _, editor) = Setup();
        Assert.True(editor.CommitStroke([P(10, 10), P(50, 50)], InkTool.Pen, _black, 1));
        var below = Assert.Single(page.Strokes);
        editor.SetHighlighterSettings(HighlighterSettings.Default with { StraightLine = true });

        Assert.True(editor.CommitHighlighterStroke(NearlyHorizontal()));

        Assert.Equal(2, page.Strokes.Count);
        Assert.Same(below, page.Strokes[0]);
        Assert.Equal(2, page.Strokes[1].Points.Count);
    }

    [Fact]
    public void Straightening_is_journalled_as_a_replacement_of_the_drawn_stroke()
    {
        var (page, undo, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { StraightLine = true });

        Assert.True(editor.CommitHighlighterStroke(NearlyHorizontal()));

        var command = Assert.IsType<EraseStrokesCommand>(undo.PeekUndo());
        var drawn = Assert.Single(command.Before);
        var straight = Assert.Single(command.After);
        Assert.NotEqual(drawn.Id, straight.Id);
        Assert.Same(straight, Assert.Single(page.Strokes));
        Assert.Equal("Straighten stroke", command.Description);
    }

    [Fact]
    public void Partial_eraser_pieces_keep_tool_colour_and_chisel()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { Chisel = true, ChiselAngleDeg = 120, Color = HighlighterPresets.Purple });
        Assert.True(editor.CommitHighlighterStroke([P(20, 200), P(380, 200)]));

        var session = editor.BeginErase(EraserMode.Partial);
        Assert.True(session.MoveTo(new PointD(200, 200), 5));
        Assert.True(session.Complete());

        Assert.Equal(2, page.Strokes.Count);
        Assert.All(page.Strokes, s =>
        {
            Assert.Equal(InkTool.Highlighter, s.Tool);
            Assert.Equal(HighlighterPresets.Purple, s.Color);
            Assert.Equal(HighlighterSettings.Default.ChiselLengthPt, s.BaseWidthPt);
            Assert.Equal(new ChiselTip(120, 0.25), s.Chisel);
        });
    }

    [Fact]
    public void WithPoints_keeps_tool_colour_width_and_chisel_of_a_highlighter()
    {
        var stroke = new InkStroke(Guid.NewGuid(), InkTool.Highlighter, HighlighterPresets.LightBlue, 8, [P(0, 0), P(10, 0)], new ChiselTip(15, 0.25));

        var copy = stroke.WithPoints([P(1, 1), P(2, 1)]);

        Assert.Equal(InkTool.Highlighter, copy.Tool);
        Assert.Equal(HighlighterPresets.LightBlue, copy.Color);
        Assert.Equal(8, copy.BaseWidthPt);
        Assert.Equal(new ChiselTip(15, 0.25), copy.Chisel);
        Assert.NotEqual(stroke.Id, copy.Id);
    }

    [Fact]
    public void Journal_round_trip_keeps_the_chisel_of_a_straightened_stroke()
    {
        var (page, _, editor) = Setup();
        editor.SetHighlighterSettings(HighlighterSettings.Default with { StraightLine = true, Chisel = true });
        Assert.True(editor.CommitHighlighterStroke(NearlyHorizontal()));
        var straight = Assert.Single(page.Strokes);

        var line = JournalCodec.SerializeEntry(new AddEntry(0, [straight]));
        var entry = Assert.IsType<AddEntry>(JournalCodec.ParseEntry(line));

        var read = Assert.Single(entry.Strokes);
        Assert.Equal(straight.Chisel, read.Chisel);
        Assert.Equal(straight.Color, read.Color);
        Assert.Equal(InkTool.Highlighter, read.Tool);
    }
}
