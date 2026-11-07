using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Ink;

public class PageEditorSelectionTests
{
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private static (Page Page, UndoManager Undo, PageEditor Editor) Setup(int index = 0)
    {
        var page = new Page(index, 200, 200);
        var undo = new UndoManager();
        return (page, undo, new PageEditor(page, undo));
    }

    /// <summary>Adds two strokes: a at (10,10)-(20,20) and b at (100,100)-(110,110), returning their ids.</summary>
    private static (Guid A, Guid B) Seed(PageEditor editor)
    {
        editor.CommitStroke([P(10, 10), P(20, 20)], InkTool.Pen, _black, 2);
        editor.CommitStroke([P(100, 100), P(110, 110)], InkTool.Pen, _black, 2);
        return (editor.Page.Strokes[0].Id, editor.Page.Strokes[1].Id);
    }

    [Fact]
    public void Move_is_one_undo_step_and_undo_restores_order()
    {
        var (page, undo, editor) = Setup();
        var (a, _) = Seed(editor);
        var before = page.Strokes.ToArray();

        var result = editor.MoveStrokes([a], 30, 40);

        var moved = Assert.Single(result);
        Assert.Equal(2, page.Strokes.Count);
        Assert.Equal(moved, page.Strokes[0].Id);
        Assert.Equal(40, page.Strokes[0].Points[0].X);
        Assert.Equal(50, page.Strokes[0].Points[0].Y);
        Assert.Equal(before[1], page.Strokes[1]);

        Assert.True(undo.Undo());
        Assert.Equal(before, page.Strokes);
        Assert.True(undo.Redo());
        Assert.Equal(moved, page.Strokes[0].Id);
    }

    [Fact]
    public void Move_with_unknown_ids_does_nothing()
    {
        var (page, undo, editor) = Setup();
        Seed(editor);
        var before = page.Strokes.ToArray();
        var depth = undo.CanUndo;

        var result = editor.MoveStrokes([Guid.NewGuid()], 5, 5);

        Assert.Empty(result);
        Assert.Equal(before, page.Strokes);
        Assert.Equal(depth, undo.CanUndo);
        Assert.Equal(2, CountUndo(undo));
    }

    [Fact]
    public void Scale_is_one_undo_step()
    {
        var (page, undo, editor) = Setup();
        var (a, _) = Seed(editor);

        var result = editor.ScaleStrokes([a], 2, new PointD(10, 10));

        Assert.Single(result);
        Assert.Equal(30, page.Strokes[0].Points[1].X);
        Assert.Equal(4, page.Strokes[0].BaseWidthPt);
        Assert.Equal(3, CountUndo(undo));
    }

    [Fact]
    public void Recolor_is_one_undo_step()
    {
        var (page, undo, editor) = Setup();
        var (_, b) = Seed(editor);
        var red = InkColor.FromRgb(255, 0, 0);

        var result = editor.RecolorStrokes([b], red);

        Assert.Single(result);
        Assert.Equal(red, page.Strokes[1].Color);
        Assert.Equal(_black, page.Strokes[0].Color);
        Assert.Equal(3, CountUndo(undo));
    }

    [Fact]
    public void Delete_removes_only_selected()
    {
        var (page, undo, editor) = Setup();
        var (a, b) = Seed(editor);

        var result = editor.DeleteStrokes([a]);

        Assert.Empty(result);
        Assert.Equal(b, Assert.Single(page.Strokes).Id);
        Assert.True(undo.Undo());
        Assert.Equal(2, page.Strokes.Count);
        Assert.Equal(a, page.Strokes[0].Id);
    }

    [Fact]
    public void Paste_empty_clipboard_does_nothing()
    {
        var (page, undo, editor) = Setup();

        var result = editor.PasteStrokes(new StrokeClipboard());

        Assert.Empty(result);
        Assert.Empty(page.Strokes);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Paste_offsets_by_12pt_and_stays_on_page()
    {
        var (page, _, editor) = Setup();
        var (a, _) = Seed(editor);
        var clipboard = new StrokeClipboard();
        clipboard.Set(editor.Strokes([a]));
        var corner = new InkStroke(Guid.NewGuid(), InkTool.Pen, _black, 2, [P(195, 195), P(199, 199)]);
        var edgeClipboard = new StrokeClipboard();
        edgeClipboard.Set([corner]);

        var pasted = editor.PasteStrokes(clipboard);
        var edgePasted = editor.PasteStrokes(edgeClipboard);

        Assert.Equal(22, page.Strokes.First(s => s.Id == pasted[0]).Points[0].X);
        Assert.NotEqual(a, pasted[0]);
        var edge = page.Strokes.First(s => s.Id == edgePasted[0]);
        Assert.All(edge.Points, p => Assert.InRange(p.X, 0, 200));
        Assert.All(edge.Points, p => Assert.InRange(p.Y, 0, 200));
    }

    [Fact]
    public void Paste_works_on_another_page()
    {
        var (_, _, first) = Setup(0);
        var (a, _) = Seed(first);
        var clipboard = new StrokeClipboard();
        clipboard.Set(first.Strokes([a]));
        var (second, undo, secondEditor) = Setup(1);

        var pasted = secondEditor.PasteStrokes(clipboard);

        Assert.Single(pasted);
        Assert.Single(second.Strokes);
        Assert.True(undo.Undo());
        Assert.Empty(second.Strokes);
    }

    [Fact]
    public void Strokes_returns_selected_in_z_order()
    {
        var (_, _, editor) = Setup();
        var (a, b) = Seed(editor);

        var found = editor.Strokes([b, a]);

        Assert.Equal([a, b], found.Select(s => s.Id));
    }

    private static int CountUndo(UndoManager undo)
    {
        var steps = 0;
        while (undo.Undo())
        {
            steps++;
        }

        return steps;
    }
}
