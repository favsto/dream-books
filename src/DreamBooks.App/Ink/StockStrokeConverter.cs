using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>Builds the stock WPF <see cref="Stroke"/> used by renderer mode A for a model stroke.</summary>
internal static class StockStrokeConverter
{
    private const float _mousePressure = 0.5f;

    public static Stroke ToWpfStroke(InkStroke s, double scale)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!(scale > 0) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be a positive finite number.");
        }

        // A stroke comes from one device: mouse strokes carry no pressure at all.
        var noPressure = s.Points.Any(p => p.Pressure is null);
        var points = new StylusPointCollection(s.Points.Count);
        foreach (var p in s.Points)
        {
            var pressure = noPressure ? _mousePressure : Math.Clamp(p.Pressure!.Value, 0f, 1f);
            points.Add(new StylusPoint(p.X * scale, p.Y * scale, pressure));
        }

        return new Stroke(points, CreateAttributes(s.Color, s.BaseWidthPt * scale, ignorePressure: noPressure));
    }

    /// <summary>Attributes shared by committed mode A strokes and the wet renderer.</summary>
    public static DrawingAttributes CreateAttributes(InkColor color, double widthDip, bool ignorePressure) => new()
    {
        Color = ToColor(color),
        Width = widthDip,
        Height = widthDip,
        FitToCurve = true,
        IgnorePressure = ignorePressure,
        StylusTip = StylusTip.Ellipse,
    };

    public static Color ToColor(InkColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
