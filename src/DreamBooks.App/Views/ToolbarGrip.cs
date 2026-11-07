using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// The toolbar's grip (UI-02, 4b): six small dots at the end of the pill. A finger or the mouse drags the toolbar by it
/// and the toolbar snaps to the nearest window edge on release; the pen does not drag (it presses the toolbar's controls
/// and writes, FR-30), so a pen press on the grip does nothing. The pointer positions it reports are in the coordinates
/// of <see cref="ReferenceElement"/> (the document area the toolbar lies over).
/// </summary>
public sealed class ToolbarGrip : FrameworkElement
{
    /// <summary>The element the reported positions are relative to.</summary>
    public static readonly DependencyProperty ReferenceElementProperty = DependencyProperty.Register(
        nameof(ReferenceElement), typeof(UIElement), typeof(ToolbarGrip));

    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical), typeof(bool), typeof(ToolbarGrip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private Point? _start;

    public ToolbarGrip()
    {
        Focusable = false;
        Cursor = Cursors.SizeAll;
        Stylus.SetIsPressAndHoldEnabled(this, false);
    }

    /// <summary>The drag started: the pointer position and nothing else.</summary>
    internal event EventHandler? DragStarted;

    /// <summary>The pointer moved while dragging: how far it is from where it started.</summary>
    internal event EventHandler<Vector>? DragDelta;

    /// <summary>The pointer was released (or lost): where, in the reference element's coordinates.</summary>
    internal event EventHandler<Point>? DragCompleted;

    public UIElement? ReferenceElement
    {
        get => (UIElement?)GetValue(ReferenceElementProperty);
        set => SetValue(ReferenceElementProperty, value);
    }

    /// <summary>True when the toolbar is vertical (left or right edge): the dots run in a column.</summary>
    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    /// <summary>True while a drag is under way.</summary>
    internal bool IsDragging => _start is not null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var length = TryFindResource("ToolbarGripLength") as double? ?? 0;
        var target = TryFindResource("TouchTarget") as double? ?? 0;
        return IsVertical ? new Size(target, length) : new Size(length, target);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = RenderSize;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(size)); // the whole grip takes the pointer
        var dot = TryFindResource("ToolbarGripDotSize") as double? ?? 0;
        var gap = TryFindResource("ToolbarGripDotGap") as double? ?? 0;
        if (dot <= 0 || TryFindResource("FlyoutHeaderBrush") is not Brush brush)
        {
            return;
        }

        // Two lines of three dots across the short side of the grip.
        var step = dot + gap;
        var along = IsVertical ? size.Width : size.Height;
        var across = IsVertical ? size.Height : size.Width;
        var startAlong = (along - ((3 * dot) + (2 * gap))) / 2;
        var startAcross = (across - (dot + gap + dot)) / 2;
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 2; j++)
            {
                var a = startAlong + (i * step);
                var b = startAcross + (j * step);
                var centre = IsVertical ? new Point(a + (dot / 2), b + (dot / 2)) : new Point(b + (dot / 2), a + (dot / 2));
                drawingContext.DrawEllipse(brush, null, centre, dot / 2, dot / 2);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        if (IsPen(e) || !CaptureMouse())
        {
            return; // the pen never drags the toolbar
        }

        _start = Position(e);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_start is { } start && IsMouseCaptured)
        {
            DragDelta?.Invoke(this, Position(e) - start);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        if (_start is not null)
        {
            Finish(Position(e));
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnLostMouseCapture(e);
        if (_start is not null)
        {
            Finish(Position(e)); // capture taken away: the toolbar lands where the pointer is
        }
    }

    /// <summary>Test hook: the same as a press, drag and release by a finger or the mouse.</summary>
    internal void SimulateDrag(Point from, Point to)
    {
        _start = from;
        DragStarted?.Invoke(this, EventArgs.Empty);
        DragDelta?.Invoke(this, to - from);
        Finish(to);
    }

    private void Finish(Point release)
    {
        _start = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        DragCompleted?.Invoke(this, release);
    }

    private Point Position(MouseEventArgs e) => e.GetPosition(ReferenceElement ?? this);

    private static bool IsPen(MouseEventArgs e) => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus;
}
