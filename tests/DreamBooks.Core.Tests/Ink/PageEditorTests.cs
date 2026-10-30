using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Ink;

public class PageEditorTests
{
    private static InkPoint P(double x, double y)
        => new(x, y, 0.5f, null, null, null, 0);

    private static readonly InkColor _red = InkColor.FromRgb(255, 0, 0);

    [Fact]
    public void CommitStroke_adds_one_undo_step_even_with_several_pieces()
    {
        var page = new Page(0, 100, 100);
        var undo = new UndoManager();
        var editor = new PageEditor(page, undo);

        var ok = editor.CommitStroke([P(50, 50), P(150, 50), P(150, 80), P(50, 80)], InkTool.Highlighter, _red, 12);

        Assert.True(ok);
        Assert.Equal(2, page.Strokes.Count);
        Assert.NotEqual(page.Strokes[0].Id, page.Strokes[1].Id);
        Assert.All(page.Strokes, s =>
        {
            Assert.Equal(InkTool.Highlighter, s.Tool);
            Assert.Equal(_red, s.Color);
            Assert.Equal(12, s.BaseWidthPt);
        });

        Assert.All(page.Strokes.SelectMany(s => s.Points), p =>
        {
            Assert.InRange(p.X, 0, 100);
            Assert.InRange(p.Y, 0, 100);
        });

        Assert.True(undo.Undo());
        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void CommitStroke_outside_page_adds_nothing_and_returns_false()
    {
        var page = new Page(0, 100, 100);
        var undo = new UndoManager();
        var editor = new PageEditor(page, undo);

        var ok = editor.CommitStroke([P(-5, 50), P(50, 50)], InkTool.Pen, _red, 2);

        Assert.False(ok);
        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void CommitStroke_single_point_inside_commits_a_dot()
    {
        var page = new Page(0, 100, 100);
        var editor = new PageEditor(page, new UndoManager());

        Assert.True(editor.CommitStroke([P(10, 10)], InkTool.Pen, _red, 2));
        Assert.Single(Assert.Single(page.Strokes).Points);
    }
}
