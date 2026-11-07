using System.Windows;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The lasso gestures on a page surface (FR-50, FR-51): loop, move, resize, one undo step each.</summary>
public sealed class InkSurfaceSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-lasso-" + Guid.NewGuid().ToString("N"));

    public InkSurfaceSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_loop_selects_the_strokes_it_encloses_and_shows_the_rectangle() // FR-50
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);

            Assert.True(vm.Selection.HasSelection);
            Assert.Equal(vm.Editor!.Page.Strokes[0].Id, Assert.Single(vm.Selection.Selected).Id);
            Assert.NotNull(surface.SelectionOverlay.Bounds);
            Assert.Empty(surface.SelectionOverlay.Loop);
            Assert.False(surface.IsInking);
        });
    }

    [Fact]
    public async Task Other_tools_do_not_start_a_lasso_gesture() // FR-50
    {
        await WithDocumentAsync((vm, surface) =>
        {
            vm.Tools.ActiveTool = AppTool.Pen;

            Assert.False(surface.BeginSelectionGesture(stylusId: null, View(surface, 10, 10)));
            Assert.False(surface.IsInking);
        });
    }

    [Fact]
    public async Task A_loop_in_progress_marks_the_surface_as_inking_and_locks_the_zoom() // FR-50
    {
        await WithDocumentAsync((vm, surface) =>
        {
            vm.Tools.ActiveTool = AppTool.Selection;

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0)));
            surface.SelectionMoveTo(View(surface, 100, 0));

            Assert.True(surface.IsInking);
            Assert.True(vm.ZoomVm.IsLocked);
            Assert.Equal(2, surface.SelectionOverlay.Loop.Count);

            surface.EndSelectionGesture();
            Assert.False(surface.IsInking);
            Assert.False(vm.ZoomVm.IsLocked);
        });
    }

    [Fact]
    public async Task Dragging_inside_previews_the_move_and_commits_one_undo_step() // FR-51
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);
            var original = vm.Editor!.Page.Strokes[0];

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 65, 65)));
            surface.SelectionMoveTo(View(surface, 85, 95));

            Assert.Equal(original, vm.Editor.Page.Strokes[0]); // the page is untouched while dragging
            Assert.Equal(70, Assert.Single(surface.DisplayStrokes).Points[0].X, 6);
            Assert.Equal(80, surface.DisplayStrokes[0].Points[0].Y, 6);

            surface.EndSelectionGesture();

            Assert.Equal(70, vm.Editor.Page.Strokes[0].Points[0].X, 6);
            Assert.Equal(80, vm.Editor.Page.Strokes[0].Points[0].Y, 6);
            Assert.True(vm.Selection.HasSelection);
            Assert.Equal(SelectionState.Selected, vm.Selection.State);
            Assert.True(vm.Undo.Undo());
            Assert.Equal(original.Points[0].X, vm.Editor.Page.Strokes[0].Points[0].X);
        });
    }

    [Fact]
    public async Task Dragging_a_corner_resizes_about_the_opposite_corner() // FR-51
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 80, 80)));
            surface.SelectionMoveTo(View(surface, 110, 110));
            surface.EndSelectionGesture();

            var stroke = vm.Editor!.Page.Strokes[0];
            Assert.Equal(50, stroke.Points[0].X, 6);
            Assert.Equal(110, stroke.Points[1].X, 6);
            Assert.Equal(4, stroke.BaseWidthPt, 6);
        });
    }

    [Fact]
    public async Task A_press_outside_the_selection_clears_it_and_starts_a_new_loop() // FR-50
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 180, 280)));

            Assert.False(vm.Selection.HasSelection);
            surface.EndSelectionGesture();
            Assert.Null(surface.SelectionOverlay.Bounds);
        });
    }

    [Fact]
    public async Task Cancelling_a_live_drag_leaves_the_page_untouched() // FR-51
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);
            var original = vm.Editor!.Page.Strokes[0];
            surface.BeginSelectionGesture(stylusId: null, View(surface, 65, 65));
            surface.SelectionMoveTo(View(surface, 85, 95));

            surface.CancelLiveGesture();

            Assert.False(surface.IsInking);
            Assert.False(vm.ZoomVm.IsLocked);
            Assert.Equal(original, vm.Editor.Page.Strokes[0]);
            Assert.Equal(original, Assert.Single(surface.DisplayStrokes));
        });
    }

    [Fact]
    public async Task With_the_pen_tool_a_press_on_a_barrel_made_selection_moves_it_and_a_press_outside_does_not() // FR-28, FR-51
    {
        await WithDocumentAsync((vm, surface) =>
        {
            vm.Editor!.CommitStroke(
                [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                2);
            surface.SetBarrelHeld(true);
            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0)));
            surface.SelectionMoveToBatch([View(surface, 120, 0), View(surface, 120, 120), View(surface, 0, 120)]);
            surface.EndSelectionGesture();
            surface.SetBarrelHeld(false);
            Assert.Equal(AppTool.Pen, vm.Tools.EffectiveTool);
            Assert.True(vm.Selection.HasSelection);

            Assert.False(surface.BeginSelectionGesture(stylusId: null, View(surface, 180, 280))); // outside: the pen writes
            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 65, 65))); // inside: drag, button or not
            surface.SelectionMoveTo(View(surface, 85, 95));
            surface.EndSelectionGesture();

            Assert.Equal(70, vm.Editor.Page.Strokes[0].Points[0].X, 6);
        });
    }

    [Fact]
    public async Task The_pen_corner_of_a_selection_resizes_it_with_the_pen_tool() // FR-51
    {
        await WithDocumentAsync((vm, surface) =>
        {
            vm.Editor!.CommitStroke(
                [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                2);
            surface.SetBarrelHeld(true);
            surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0));
            surface.SelectionMoveToBatch([View(surface, 120, 0), View(surface, 120, 120), View(surface, 0, 120)]);
            surface.EndSelectionGesture();
            surface.SetBarrelHeld(false);

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 80, 80)));
            surface.SelectionMoveTo(View(surface, 110, 110));
            surface.EndSelectionGesture();

            Assert.Equal(110, vm.Editor.Page.Strokes[0].Points[1].X, 6);
        });
    }

    [Fact]
    public async Task The_wet_ink_is_off_while_the_pen_hovers_over_a_selection() // FR-51, NFR-04
    {
        await WithDocumentAsync((vm, surface) =>
        {
            vm.Editor!.CommitStroke(
                [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                2);
            surface.SetBarrelHeld(true);
            surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0));
            surface.SelectionMoveToBatch([View(surface, 120, 0), View(surface, 120, 120), View(surface, 0, 120)]);
            surface.EndSelectionGesture();
            surface.SetBarrelHeld(false);
            surface.UpdateHoverOverSelection(View(surface, 180, 280), device: null);
            Assert.True(surface.ActiveRenderer.Enabled); // away from the selection the pen writes

            surface.UpdateHoverOverSelection(View(surface, 65, 65), device: null);

            Assert.False(surface.ActiveRenderer.Enabled); // over it the pen drags: no wet ink under the drag
            surface.UpdateHoverOverSelection(View(surface, 180, 280), device: null);
            Assert.True(surface.ActiveRenderer.Enabled);
        });
    }

    [Fact]
    public async Task The_lasso_works_on_a_visible_page_that_is_not_the_current_one() // FR-50
    {
        await WithDocumentAsync((vm, _) =>
        {
            var other = new PageEditor(new Page(1, 200, 300), vm.Undo);
            other.CommitStroke(
                [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                2);
            var surface = new InkSurface { ViewModel = vm, Editor = other, Scale = Units.DipPerPt };
            surface.Measure(new Size(4000, 4000));
            surface.Arrange(new Rect(surface.DesiredSize));
            surface.UpdateLayout();
            vm.Tools.ActiveTool = AppTool.Selection;

            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0)));
            surface.SelectionMoveTo(View(surface, 120, 0));
            surface.SelectionMoveTo(View(surface, 120, 120));
            surface.SelectionMoveTo(View(surface, 0, 120));
            surface.EndSelectionGesture();

            Assert.True(vm.Selection.HasSelection);
            Assert.Same(other, vm.Selection.Editor);
            Assert.NotNull(surface.SelectionOverlay.Bounds);
        });
    }

    [Fact]
    public async Task A_move_batch_previews_once_and_a_loop_batch_keeps_every_point() // FR-51, NFR-04
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);
            surface.BeginSelectionGesture(stylusId: null, View(surface, 65, 65));
            var previews = 0;
            vm.Selection.PropertyChanged += (_, e) => previews += e.PropertyName == nameof(SelectionViewModel.Preview) ? 1 : 0;

            surface.SelectionMoveToBatch([View(surface, 66, 66), View(surface, 70, 70), View(surface, 85, 95)]);

            Assert.Equal(1, previews);
            Assert.Equal(70, surface.DisplayStrokes[0].Points[0].X, 6); // the last point of the batch
            surface.EndSelectionGesture();

            vm.Selection.Clear();
            surface.BeginSelectionGesture(stylusId: null, View(surface, 180, 280));
            surface.SelectionMoveToBatch([View(surface, 181, 280), View(surface, 182, 281), View(surface, 183, 282)]);
            Assert.Equal(4, surface.SelectionOverlay.Loop.Count); // start + 3
            surface.EndSelectionGesture();
        });
    }

    [Fact]
    public async Task Swapping_the_view_model_ends_a_live_lasso_gesture() // FR-50
    {
        await WithDocumentAsync((vm, surface) =>
        {
            SelectAll(vm, surface);
            surface.BeginSelectionGesture(stylusId: null, View(surface, 65, 65));
            surface.SelectionMoveTo(View(surface, 85, 95));
            Assert.True(surface.IsInking);

            surface.ViewModel = null;

            Assert.False(surface.IsInking);
            Assert.False(vm.ZoomVm.IsLocked);
        });
    }

    [Fact]
    public async Task The_barrel_lasso_ends_when_the_button_is_released_over_another_surface() // FR-28
    {
        await WithDocumentAsync((vm, first) =>
        {
            var second = new InkSurface { ViewModel = vm, Editor = vm.Editor, Scale = Units.DipPerPt };

            first.SetBarrelHeld(true);
            Assert.Equal(AppTool.Selection, vm.Tools.EffectiveTool);

            second.SetBarrelHeld(false); // button-up lands on a different page

            Assert.False(vm.Tools.IsBarrelSelection);
            Assert.Equal(AppTool.Pen, vm.Tools.EffectiveTool);
        });
    }

    [Fact]
    public async Task The_barrel_lasso_ends_when_the_pen_leaves_the_surface_without_a_gesture() // FR-28
    {
        await WithDocumentAsync((vm, surface) =>
        {
            surface.SetBarrelHeld(true);

            surface.OnPenLeft();

            Assert.False(vm.Tools.IsBarrelSelection);
        });
    }

    [Fact]
    public async Task The_barrel_lasso_survives_the_pen_leaving_during_a_gesture_until_it_ends() // FR-28
    {
        await WithDocumentAsync((vm, surface) =>
        {
            surface.SetBarrelHeld(true);
            Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0)));

            surface.OnPenLeft();
            Assert.True(vm.Tools.IsBarrelSelection);

            surface.EndSelectionGesture();
            Assert.False(vm.Tools.IsBarrelSelection);
        });
    }

    private static Point View(InkSurface surface, double xPt, double yPt) => new(xPt * surface.Scale, yPt * surface.Scale);

    /// <summary>One stroke from (50,50) to (80,80), then a lasso loop round the whole page's top-left 120 × 120 pt.</summary>
    private static void SelectAll(MainViewModel vm, InkSurface surface)
    {
        vm.Editor!.CommitStroke(
            [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            2);
        vm.Tools.ActiveTool = AppTool.Selection;
        Assert.True(surface.BeginSelectionGesture(stylusId: null, View(surface, 0, 0)));
        surface.SelectionMoveTo(View(surface, 120, 0));
        surface.SelectionMoveTo(View(surface, 120, 120));
        surface.SelectionMoveTo(View(surface, 0, 120));
        surface.EndSelectionGesture();
    }

    private async Task WithDocumentAsync(Action<MainViewModel, InkSurface> test)
    {
        var path = Path.Combine(_dir, "page.pdf");
        TestPdfFactory.Write(path, (200, 300));
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path });
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            Assert.NotNull(vm.Editor);
            WpfTestThread.Run(() =>
            {
                var surface = new InkSurface { ViewModel = vm, Editor = vm.Editor, Scale = Units.DipPerPt };
                surface.Measure(new Size(4000, 4000));
                surface.Arrange(new Rect(surface.DesiredSize));
                surface.UpdateLayout();
                test(vm, surface);
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }
}
