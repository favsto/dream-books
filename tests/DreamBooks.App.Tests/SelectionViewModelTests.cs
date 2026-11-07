using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.App.Tests;

public class SelectionViewModelTests
{
    private static readonly InkColor _black = InkColor.FromRgb(0, 0, 0);
    private static readonly PointD[] _loop = [new(0, 0), new(50, 0), new(50, 50), new(0, 50)];

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private sealed class Fixture
    {
        public Fixture()
        {
            Undo = new UndoManager();
            Editor = new PageEditor(new Page(0, 200, 200), Undo);
            Editor.CommitStroke([P(10, 10), P(20, 20)], InkTool.Pen, _black, 2);
            Editor.CommitStroke([P(100, 100), P(110, 110)], InkTool.Pen, _black, 2);
            Clipboard = new StrokeClipboard();
            Current = Editor;
            Selection = new SelectionViewModel(() => Current, Clipboard);
        }

        public UndoManager Undo { get; }

        public PageEditor Editor { get; }

        public PageEditor? Current { get; set; }

        public StrokeClipboard Clipboard { get; }

        public SelectionViewModel Selection { get; }
    }

    [Fact]
    public void SelectLoop_selects_enclosed_strokes()
    {
        var f = new Fixture();

        f.Selection.SelectLoop(_loop);

        Assert.Equal(SelectionState.Selected, f.Selection.State);
        Assert.True(f.Selection.HasSelection);
        Assert.Equal(f.Editor.Page.Strokes[0].Id, Assert.Single(f.Selection.Selected).Id);
        Assert.Equal(new RectD(10, 10, 20, 20), f.Selection.Bounds);
    }

    [Fact]
    public void SelectLoop_with_nothing_inside_stays_None()
    {
        var f = new Fixture();

        f.Selection.SelectLoop([new PointD(150, 0), new PointD(190, 0), new PointD(190, 40)]);

        Assert.Equal(SelectionState.None, f.Selection.State);
        Assert.False(f.Selection.HasSelection);
        Assert.Null(f.Selection.Bounds);
    }

    [Fact]
    public void SelectLoop_with_fewer_than_three_points_selects_nothing()
    {
        var f = new Fixture();

        f.Selection.SelectLoop([new PointD(0, 0), new PointD(200, 200)]);

        Assert.Equal(SelectionState.None, f.Selection.State);
    }

    [Fact]
    public void Move_previews_without_touching_the_page_until_Commit()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        var original = f.Editor.Page.Strokes.ToArray();

        f.Selection.BeginMove();
        f.Selection.MoveBy(30, 40);

        Assert.Equal(SelectionState.Moving, f.Selection.State);
        Assert.Equal(40, f.Selection.Preview[0].Points[0].X);
        Assert.Equal(original, f.Editor.Page.Strokes);

        f.Selection.Commit();

        Assert.Equal(SelectionState.Selected, f.Selection.State);
        Assert.Equal(40, f.Editor.Page.Strokes[0].Points[0].X);
        Assert.Equal(f.Editor.Page.Strokes[0].Id, Assert.Single(f.Selection.Selected).Id);
    }

    [Fact]
    public void Commit_move_is_one_undo_step()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        f.Selection.BeginMove();
        f.Selection.MoveBy(5, 5);
        f.Selection.MoveBy(30, 40);
        f.Selection.Commit();

        Assert.True(f.Undo.Undo());

        Assert.Equal(10, f.Editor.Page.Strokes[0].Points[0].X);
        Assert.Equal(2, f.Editor.Page.Strokes.Count);
    }

    [Fact]
    public void Resize_commits_one_scaled_step()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);

        f.Selection.BeginResize(new PointD(10, 10));
        f.Selection.ScaleTo(2);
        f.Selection.Commit();

        Assert.Equal(30, f.Editor.Page.Strokes[0].Points[1].X);
        Assert.Equal(4, f.Editor.Page.Strokes[0].BaseWidthPt);
        Assert.Equal(SelectionState.Selected, f.Selection.State);
    }

    [Fact]
    public void Cancel_restores_the_page()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        var original = f.Editor.Page.Strokes.ToArray();
        f.Selection.BeginMove();
        f.Selection.MoveBy(30, 40);

        f.Selection.Cancel();

        Assert.Equal(SelectionState.Selected, f.Selection.State);
        Assert.Equal(original, f.Editor.Page.Strokes);
        Assert.Equal(original[0], f.Selection.Preview[0]);
    }

    [Fact]
    public void Recolor_changes_colour_and_keeps_the_selection()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);

        f.Selection.Recolor(InkColor.FromRgb(255, 0, 0));

        Assert.Equal(InkColor.FromRgb(255, 0, 0), f.Editor.Page.Strokes[0].Color);
        Assert.True(f.Selection.HasSelection);
        Assert.Equal(f.Editor.Page.Strokes[0].Id, f.Selection.Selected[0].Id);
    }

    [Fact]
    public void Delete_removes_strokes_and_clears_selection()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);

        f.Selection.Delete();

        Assert.Single(f.Editor.Page.Strokes);
        Assert.Equal(SelectionState.None, f.Selection.State);
    }

    [Fact]
    public void Copy_fills_the_clipboard_and_CanPaste_follows()
    {
        var f = new Fixture();
        Assert.False(f.Selection.CanPaste);
        f.Selection.SelectLoop(_loop);

        f.Selection.Copy();

        Assert.True(f.Selection.CanPaste);
        Assert.Single(f.Clipboard.Strokes);
    }

    [Fact]
    public void Copy_then_Paste_on_other_page()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        f.Selection.Copy();
        var other = new PageEditor(new Page(1, 200, 200), f.Undo);
        f.Current = other;

        f.Selection.Paste();

        Assert.Single(other.Page.Strokes);
        Assert.Equal(22, other.Page.Strokes[0].Points[0].X);
        Assert.Equal(other.Page.Strokes[0].Id, Assert.Single(f.Selection.Selected).Id);
    }

    [Fact]
    public void Paste_with_empty_clipboard_does_nothing()
    {
        var f = new Fixture();
        var depth = f.Editor.Page.Strokes.Count;

        f.Selection.Paste();

        Assert.Equal(depth, f.Editor.Page.Strokes.Count);
        Assert.Equal(SelectionState.None, f.Selection.State);
    }

    [Fact]
    public void Undo_clears_selection()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        f.Selection.Recolor(InkColor.FromRgb(255, 0, 0));
        Assert.True(f.Selection.HasSelection);

        f.Undo.Undo();

        Assert.Equal(SelectionState.None, f.Selection.State);
    }

    [Fact]
    public void Selection_stays_on_its_own_page_when_the_current_page_changes() // FR-50
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);

        f.Current = new PageEditor(new Page(1, 200, 200), f.Undo);
        Assert.True(f.Selection.HasSelection);
        f.Selection.Delete();

        Assert.Equal(SelectionState.None, f.Selection.State);
        Assert.Single(f.Editor.Page.Strokes); // deleted on the page it was selected on
    }

    [Fact]
    public void SelectLoop_on_a_given_editor_works_when_it_is_not_the_current_page() // FR-50
    {
        var f = new Fixture();
        f.Current = new PageEditor(new Page(1, 200, 200), f.Undo);

        f.Selection.SelectLoop(_loop, f.Editor);

        Assert.True(f.Selection.HasSelection);
        Assert.Same(f.Editor, f.Selection.Editor);
        Assert.Equal(f.Editor.Page.Strokes[0].Id, Assert.Single(f.Selection.Selected).Id);
    }

    [Fact]
    public void Paste_goes_to_the_current_page_not_the_selections_page() // FR-51
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop, f.Editor);
        f.Selection.Copy();
        var other = new PageEditor(new Page(1, 200, 200), f.Undo);
        f.Current = other;

        f.Selection.Paste();

        Assert.Single(other.Page.Strokes);
        Assert.Equal(2, f.Editor.Page.Strokes.Count);
        Assert.Same(other, f.Selection.Editor);
    }

    [Fact]
    public void No_editor_means_commands_do_nothing()
    {
        var f = new Fixture();
        f.Current = null;

        f.Selection.SelectLoop(_loop);
        f.Selection.Paste();
        f.Selection.Delete();

        Assert.Equal(SelectionState.None, f.Selection.State);
        Assert.Equal(2, f.Editor.Page.Strokes.Count);
    }

    [Fact]
    public void Commands_follow_the_selection_and_the_clipboard() // FR-51, NFR-31
    {
        var f = new Fixture();
        Assert.False(f.Selection.CopyCommand.CanExecute(null));
        Assert.False(f.Selection.DeleteCommand.CanExecute(null));
        Assert.False(f.Selection.RecolorCommand.CanExecute(InkColor.FromRgb(1, 2, 3)));
        Assert.False(f.Selection.PasteCommand.CanExecute(null));

        f.Selection.SelectLoop(_loop);
        Assert.True(f.Selection.CopyCommand.CanExecute(null));
        Assert.True(f.Selection.DeleteCommand.CanExecute(null));
        Assert.True(f.Selection.RecolorCommand.CanExecute(InkColor.FromRgb(1, 2, 3)));

        f.Selection.CopyCommand.Execute(null);
        Assert.True(f.Selection.PasteCommand.CanExecute(null));
        f.Selection.RecolorCommand.Execute(InkColor.FromRgb(9, 9, 9));
        Assert.Equal(InkColor.FromRgb(9, 9, 9), f.Editor.Page.Strokes[0].Color);
        f.Selection.DeleteCommand.Execute(null);
        Assert.Single(f.Editor.Page.Strokes);
        Assert.False(f.Selection.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Clear_returns_to_None_and_raises_property_changes()
    {
        var f = new Fixture();
        f.Selection.SelectLoop(_loop);
        var changed = new List<string?>();
        f.Selection.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        f.Selection.Clear();

        Assert.Equal(SelectionState.None, f.Selection.State);
        Assert.Contains(nameof(SelectionViewModel.State), changed);
        Assert.Contains(nameof(SelectionViewModel.HasSelection), changed);
    }
}
