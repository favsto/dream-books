using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// The ticks of one kind (marks or notes, FR-101, UI-07) on the scroll rail's track: short horizontal bars across the
/// track (centred, or moved sideways by <see cref="TickOffset"/>), all in
/// one frozen geometry drawn in one call, however many marks there are. Decorative: it never hit-tests (the pen
/// reaches the page under it, FR-30), never takes focus and has no automation peer (the marks list is the accessible
/// way to the marks).
/// </summary>
public sealed class RailTickLayer : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(RailTickLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickLengthProperty = DependencyProperty.Register(
        nameof(TickLength), typeof(double), typeof(RailTickLayer),
        new FrameworkPropertyMetadata(0.0, OnShapeChanged));

    public static readonly DependencyProperty TickThicknessProperty = DependencyProperty.Register(
        nameof(TickThickness), typeof(double), typeof(RailTickLayer),
        new FrameworkPropertyMetadata(0.0, OnShapeChanged));

    public static readonly DependencyProperty TickOffsetProperty = DependencyProperty.Register(
        nameof(TickOffset), typeof(double), typeof(RailTickLayer),
        new FrameworkPropertyMetadata(0.0, OnShapeChanged));

    private IReadOnlyList<double> _centres = [];
    private StreamGeometry? _geometry;

    public RailTickLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SizeChanged += (_, _) => Rebuild(); // the bars are centred across the width
    }

    /// <summary>The ticks' brush (a theme token, opaque with transparency effects off).</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>A tick's horizontal length in DIPs.</summary>
    public double TickLength
    {
        get => (double)GetValue(TickLengthProperty);
        set => SetValue(TickLengthProperty, value);
    }

    /// <summary>A tick's vertical thickness in DIPs.</summary>
    public double TickThickness
    {
        get => (double)GetValue(TickThicknessProperty);
        set => SetValue(TickThicknessProperty, value);
    }

    /// <summary>How far a tick's centre sits from the layer's centre, in DIPs (negative: to the left).</summary>
    public double TickOffset
    {
        get => (double)GetValue(TickOffsetProperty);
        set => SetValue(TickOffsetProperty, value);
    }

    /// <summary>The ticks' centres, in DIPs from the layer's top, ascending.</summary>
    internal IReadOnlyList<double> Centres => _centres;

    /// <summary>What is drawn: one figure per tick; null when there are none.</summary>
    internal Geometry? TickGeometry => _geometry;

    /// <summary>Replaces the ticks (ascending centres, already merged).</summary>
    internal void SetCentres(IReadOnlyList<double> centres)
    {
        ArgumentNullException.ThrowIfNull(centres);
        _centres = centres;
        Rebuild();
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null; // decorative

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_geometry is { } geometry && Fill is { } fill)
        {
            drawingContext.DrawGeometry(fill, null, geometry);
        }
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RailTickLayer)d).Rebuild();

    private void Rebuild()
    {
        _geometry = null;
        if (_centres.Count > 0)
        {
            var length = Math.Max(0, TickLength);
            var half = Math.Max(0, TickThickness) / 2;
            var left = ((ActualWidth - length) / 2) + TickOffset;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                foreach (var centre in _centres)
                {
                    context.BeginFigure(new Point(left, centre - half), isFilled: true, isClosed: true);
                    context.PolyLineTo(
                        [new Point(left + length, centre - half), new Point(left + length, centre + half), new Point(left, centre + half)],
                        isStroked: false,
                        isSmoothJoin: false);
                }
            }

            geometry.Freeze();
            _geometry = geometry;
        }

        InvalidateVisual();
    }
}
