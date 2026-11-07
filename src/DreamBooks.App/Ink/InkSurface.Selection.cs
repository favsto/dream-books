using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// The lasso on one page (FR-28, FR-50, FR-51): a loop drawn with the pen or the mouse selects strokes; a drag inside
/// the rectangle moves them, a drag from a corner resizes them; the page changes once, at pen-up. A drag shows the
/// strokes moved through the display strokes (the same path the eraser preview uses). Fingers do not select: they
/// scroll and zoom. The pen's barrel button makes the lasso the effective tool for as long as it is held.
/// </summary>
public sealed partial class InkSurface
{
    // Lasso gesture in progress (pen or mouse); the selection itself lives in the view model.
    private SelectionGesture? _selection;

    // The barrel button of the hovering pen is down (this surface switched the tool to the lasso for it).
    private bool _barrelDown;

    /// <summary>The loop, rectangle and handles of the lasso; exposed for tests.</summary>
    internal SelectionLayer SelectionOverlay => _selectionLayer;

    /// <summary>
    /// Starts a lasso gesture at a surface position (DIPs) if the effective tool is the lasso and this is the current
    /// page: on a handle it resizes, inside the rectangle it moves, anywhere else it starts a new loop.
    /// </summary>
    /// <param name="stylusId">The pen's stylus device Id, or null for the mouse.</param>
    internal bool BeginSelectionGesture(int? stylusId, Point position)
    {
        if (IsInking
            || Editor is not { } editor
            || ViewModel is not { } vm
            || !ReferenceEquals(editor.Page, Page)
            || !ReferenceEquals(editor, vm.Editor)
            || vm.Tools.EffectiveTool != AppTool.Selection)
        {
            return false;
        }

        var selection = vm.Selection;
        var point = StylusPointConverter.ToPage(position, Scale);
        var handle = selection.Bounds is { } bounds
            ? SelectionHandles.HitTest(bounds, point, HitTolerancePt())
            : SelectionHandle.None;
        var gesture = new SelectionGesture(stylusId, handle, point);
        switch (handle)
        {
            case SelectionHandle.Inside:
                selection.BeginMove();
                break;
            case SelectionHandle.None:
                selection.Clear();
                gesture.Loop.Add(point);
                break;
            default:
                var anchor = SelectionHandles.Opposite(selection.Bounds!.Value, handle);
                var distance = PointD.Distance(point, anchor);
                if (distance < 1)
                {
                    gesture = new SelectionGesture(stylusId, SelectionHandle.Inside, point);
                    selection.BeginMove();
                    break;
                }

                gesture.Anchor = anchor;
                gesture.StartDistance = distance;
                selection.BeginResize(anchor);
                break;
        }

        _selection = gesture;
        LockZoom(true);
        return true;
    }

    /// <summary>Feeds the live gesture a surface position (DIPs).</summary>
    internal void SelectionMoveTo(Point position)
    {
        if (_selection is not { } gesture || ViewModel is not { } vm)
        {
            return;
        }

        var point = StylusPointConverter.ToPage(position, Scale);
        switch (gesture.Handle)
        {
            case SelectionHandle.None:
                gesture.Loop.Add(point);
                _selectionLayer.SetLoop(gesture.Loop);
                break;
            case SelectionHandle.Inside:
                vm.Selection.MoveBy(point.X - gesture.Start.X, point.Y - gesture.Start.Y);
                break;
            default:
                vm.Selection.ScaleTo(PointD.Distance(point, gesture.Anchor) / gesture.StartDistance);
                break;
        }
    }

    /// <summary>
    /// Ends the gesture exactly once on every path (up, lost capture): a loop selects, a drag commits as one undo
    /// step, the zoom is unlocked and the barrel button's lasso is handed back if the button was released.
    /// </summary>
    internal void EndSelectionGesture()
    {
        if (_selection is not { } gesture)
        {
            return;
        }

        _selection = null; // Before releasing capture: the lost-capture handler must not finish twice.
        if (gesture.StylusId is not null)
        {
            if (Stylus.Captured == this)
            {
                Stylus.Capture(null);
            }
        }
        else if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        var vm = ViewModel;
        try
        {
            if (vm is not null)
            {
                if (gesture.Handle == SelectionHandle.None)
                {
                    _selectionLayer.SetLoop([]);
                    if (gesture.Loop.Count >= 3)
                    {
                        vm.Selection.SelectLoop(gesture.Loop);
                    }
                }
                else
                {
                    vm.Selection.Commit();
                }
            }
        }
        finally
        {
            LockZoom(false);
            ReleaseBarrelSelection();
            UpdateRendererEnabled(null);
            InkingEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The barrel button went down on a hovering pen: the lasso until it is released (FR-28).</summary>
    protected override void OnStylusButtonDown(StylusButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusButtonDown(e);
        if (IsBarrel(e) && !e.StylusDevice.Inverted && !IsInking && ViewModel is { } vm)
        {
            _barrelDown = true;
            vm.Tools.BeginBarrelSelection();
        }
    }

    protected override void OnStylusButtonUp(StylusButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusButtonUp(e);
        if (IsBarrel(e) && _barrelDown)
        {
            _barrelDown = false;
            if (_selection is null)
            {
                ViewModel?.Tools.EndBarrelSelection(); // a gesture still live ends it at pen-up
            }
        }
    }

    /// <summary>Hands the lasso back to the previous tool once neither the button nor a gesture needs it.</summary>
    private void ReleaseBarrelSelection()
    {
        if (!_barrelDown && ViewModel?.Tools.IsBarrelSelection == true)
        {
            ViewModel.Tools.EndBarrelSelection();
        }
    }

    private static bool IsBarrel(StylusButtonEventArgs e) =>
        e.StylusButton.Guid == StylusPointProperties.BarrelButton.Id && IsPen(e.StylusDevice);

    private void SelectionStylusPoints(StylusEventArgs e)
    {
        var points = e.GetStylusPoints(this);
        for (var i = 0; i < points.Count; i++)
        {
            SelectionMoveTo(new Point(points[i].X, points[i].Y));
        }
    }

    /// <summary>A corner handle is grabbed within half its hit size (44 DIPs, section 4), in page points.</summary>
    private double HitTolerancePt()
    {
        var hitDip = TryFindResource("SelectionHandleHitSize") is double size ? size : 44;
        return hitDip / 2 / Scale;
    }

    /// <summary>The page strokes with the dragged selection in place of the originals, or null when nothing is dragged here.</summary>
    private IReadOnlyList<InkStroke>? SelectionPreviewStrokes()
    {
        if (ViewModel is not { } vm
            || vm.Selection is not { State: SelectionState.Moving or SelectionState.Resizing } selection
            || Editor is not { } editor
            || !ReferenceEquals(editor, vm.Editor)
            || !ReferenceEquals(editor.Page, Page))
        {
            return null;
        }

        var replacements = new Dictionary<Guid, InkStroke>();
        for (var i = 0; i < selection.Selected.Count && i < selection.Preview.Count; i++)
        {
            replacements[selection.Selected[i].Id] = selection.Preview[i];
        }

        return editor.Page.Strokes.Select(s => replacements.GetValueOrDefault(s.Id, s)).ToArray();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectionViewModel.Preview) or nameof(SelectionViewModel.State) or nameof(SelectionViewModel.Selected))
        {
            SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());
        }

        ShowSelection();
    }

    /// <summary>The rectangle follows the selection when it belongs to this page, otherwise it is hidden.</summary>
    private void ShowSelection()
    {
        var shown = ViewModel is { } vm
            && Editor is { } editor
            && ReferenceEquals(editor, vm.Editor)
            && ReferenceEquals(editor.Page, Page)
            && vm.Selection.HasSelection;
        _selectionLayer.SetBounds(shown ? ViewModel!.Selection.Bounds : null);
    }

    private sealed class SelectionGesture(int? stylusId, SelectionHandle handle, PointD start)
    {
        public int? StylusId { get; } = stylusId;

        public SelectionHandle Handle { get; } = handle;

        public PointD Start { get; } = start;

        public PointD Anchor { get; set; }

        public double StartDistance { get; set; } = 1;

        public List<PointD> Loop { get; } = [];
    }
}
