using System.Windows;
using System.Windows.Media;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// The pen panel's stroke preview (UI-02, FR-21, FR-22): a wave drawn from light pressure to full pressure and back,
/// its width following the pressure curve and the base width as the ink does (<see cref="PressureMapping"/>), in the
/// pen's colour, at the screen scale of 100% zoom. Only a picture: it never takes input.
/// </summary>
public sealed class PressurePreview : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(PressureCurve), typeof(PressurePreview),
        new FrameworkPropertyMetadata(PressureCurve.Linear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WidthMmProperty = DependencyProperty.Register(
        nameof(WidthMm), typeof(double), typeof(PressurePreview),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InkColorProperty = DependencyProperty.Register(
        nameof(InkColor), typeof(InkColor), typeof(PressurePreview),
        new FrameworkPropertyMetadata(InkColor.FromRgb(0, 0, 0), FrameworkPropertyMetadataOptions.AffectsRender));

    private const int _samples = 48;
    private const double _dipPerMm = 96 / 25.4;

    public PressurePreview()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    public PressureCurve Curve
    {
        get => (PressureCurve)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double WidthMm
    {
        get => (double)GetValue(WidthMmProperty);
        set => SetValue(WidthMmProperty, value);
    }

    public InkColor InkColor
    {
        get => (InkColor)GetValue(InkColorProperty);
        set => SetValue(InkColorProperty, value);
    }

    /// <summary>The outline of the preview stroke in a box of <paramref name="size"/>: the upper edge, then the lower one back.</summary>
    internal IReadOnlyList<Point> Outline(Size size)
    {
        var upper = new List<Point>(_samples + 1);
        var lower = new List<Point>(_samples + 1);
        var margin = Math.Min(size.Height / 2, WidthMm * _dipPerMm * PressureMapping.MaxFactor / 2);
        var amplitude = Math.Max(0, (size.Height / 2) - margin) * 0.6;
        for (var i = 0; i <= _samples; i++)
        {
            var t = (double)i / _samples;
            var pressure = (float)Math.Sin(Math.PI * t); // light, full, light
            var half = PressureMapping.WidthAt(WidthMm * _dipPerMm, pressure, Curve) / 2;
            var x = margin + (t * Math.Max(0, size.Width - (2 * margin)));
            var y = (size.Height / 2) - (amplitude * Math.Sin(2 * Math.PI * t));
            upper.Add(new Point(x, y - half));
            lower.Add(new Point(x, y + half));
        }

        lower.Reverse();
        return [.. upper, .. lower];
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var outline = Outline(size);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outline[0], isFilled: true, isClosed: true);
            context.PolyLineTo(outline.Skip(1).ToList(), isStroked: false, isSmoothJoin: true);
        }

        geometry.Freeze();
        var color = InkColor;
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        drawingContext.DrawGeometry(brush, null, geometry);
    }
}
