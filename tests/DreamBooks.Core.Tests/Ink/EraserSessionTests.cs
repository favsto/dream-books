using DreamBooks.Core.Commands;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Ink;

public class EraserSessionTests
{
    private static InkPoint P(double x, double y)
        => new(x, y, 0.5f, null, null, null, 0);

    private static InkStroke Stroke(params InkPoint[] points)
        => new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2, points);

    private static (Page Page, UndoManager Undo, PageEditor Editor) Setup(params InkStroke[] strokes)
    {
        var page = new Page(0, 200, 200);
        var undo = new UndoManager();
        if (strokes.Length > 0)
        {
            undo.Execute(new AddStrokesCommand(page, strokes));
            undo.Clear();
        }

        return (page, undo, new PageEditor(page, undo));
    }

    [Fact]
    public void WholeStroke_session_removes_hit_strokes_and_is_one_undo_step()
    {
        var a = Stroke(P(50, 0), P(50, 100));
        var b = Stroke(P(150, 0), P(150, 100));
        var (page, undo, editor) = Setup(a, b);

        var session = editor.BeginErase(EraserMode.WholeStroke);
        Assert.False(session.MoveTo(new PointD(0, 50), 1));
        Assert.True(session.MoveTo(new PointD(100, 50), 1));
        Assert.False(session.MoveTo(new PointD(110, 50), 1));
        Assert.True(session.Complete());

        Assert.Same(b, Assert.Single(page.Strokes));
        Assert.True(undo.Undo());
        Assert.Equal([a, b], page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Partial_session_with_many_moves_is_one_undo_step_and_undo_restores_original()
    {
        var original = Stroke(P(0, 100), P(200, 100));
        var other = Stroke(P(0, 10), P(200, 10));
        var (page, undo, editor) = Setup(other, original);

        var session = editor.BeginErase(EraserMode.Partial);
        for (var x = 20; x <= 180; x += 40)
        {
            Assert.True(session.MoveTo(new PointD(x, 100), 5));
        }

        Assert.True(session.Complete());

        Assert.Same(other, page.Strokes[0]);
        Assert.Equal(3, page.Strokes.Count); // the sweep 20 -> 180 erased everything in between
        Assert.True(undo.Undo());
        Assert.Equal([other, original], page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Partial_pieces_keep_the_z_position_of_the_original()
    {
        var below = Stroke(P(0, 10), P(200, 10));
        var cut = Stroke(P(0, 100), P(200, 100));
        var above = Stroke(P(0, 190), P(200, 190));
        var (page, _, editor) = Setup(below, cut, above);

        var session = editor.BeginErase(EraserMode.Partial);
        session.MoveTo(new PointD(100, 100), 5);
        session.Complete();

        Assert.Equal(4, page.Strokes.Count);
        Assert.Same(below, page.Strokes[0]);
        Assert.Same(above, page.Strokes[3]);
        Assert.True(page.Strokes[1].Points[^1].X < page.Strokes[2].Points[0].X);
    }

    [Fact]
    public void Session_without_hits_adds_no_undo_step()
    {
        var (page, undo, editor) = Setup(Stroke(P(50, 0), P(50, 100)));

        var session = editor.BeginErase(EraserMode.Partial);
        Assert.False(session.MoveTo(new PointD(150, 50), 3));

        Assert.False(session.Complete());
        Assert.False(undo.CanUndo);
        Assert.Single(page.Strokes);
    }

    [Fact]
    public void Page_is_unchanged_until_Complete()
    {
        var a = Stroke(P(50, 0), P(50, 100));
        var (page, _, editor) = Setup(a);

        var session = editor.BeginErase(EraserMode.WholeStroke);
        session.MoveTo(new PointD(50, 50), 3);

        Assert.Empty(session.Strokes);
        Assert.Same(a, Assert.Single(page.Strokes));
    }

    [Fact]
    public void Cancel_discards_changes_and_finishes_the_session()
    {
        var (page, undo, editor) = Setup(Stroke(P(50, 0), P(50, 100)));

        var session = editor.BeginErase(EraserMode.WholeStroke);
        session.MoveTo(new PointD(50, 50), 3);
        session.Cancel();

        Assert.Single(page.Strokes);
        Assert.False(undo.CanUndo);
        Assert.Throws<InvalidOperationException>(() => session.MoveTo(new PointD(0, 0), 3));
    }

    [Fact]
    public void MoveTo_after_Complete_throws()
    {
        var (_, _, editor) = Setup();

        var session = editor.BeginErase(EraserMode.Partial);
        session.Complete();

        Assert.Throws<InvalidOperationException>(() => session.MoveTo(new PointD(0, 0), 3));
    }

    [Fact]
    public void Many_strokes_far_from_the_eraser_are_left_untouched()
    {
        var strokes = Enumerable.Range(0, 3000)
            .Select(i => Stroke(P(i % 100, 150 + (i / 100)), P((i % 100) + 1, 150 + (i / 100))))
            .ToArray();
        var (_, _, editor) = Setup(strokes);

        var session = editor.BeginErase(EraserMode.Partial);

        Assert.False(session.MoveTo(new PointD(10, 10), 3));
        Assert.Equal(3000, session.Strokes.Count);
    }
}
