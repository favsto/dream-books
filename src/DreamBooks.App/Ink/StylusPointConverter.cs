using System.Windows;
using System.Windows.Input;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Converts between view coordinates (DIPs relative to the page surface) and page points (FR-26).
/// <c>scale</c> is always <c>Zoom × Units.DipPerPt</c>: page point = DIP / scale.
/// </summary>
internal static class StylusPointConverter
{
    private const double _defaultTiltResolution = 100; // hundredths of a degree

    /// <param name="hasPressure">
    /// True when the tablet reports <see cref="StylusPointProperties.NormalPressure"/>; WPF always fills
    /// <see cref="StylusPoint.PressureFactor"/> (0.5 without a sensor), so only the device can tell.
    /// </param>
    public static InkPoint ToInkPoint(StylusPoint sp, double scale, long timestampMs, bool hasPressure)
    {
        RequireScale(scale);
        return new InkPoint(
            sp.X / scale,
            sp.Y / scale,
            hasPressure ? Math.Clamp(sp.PressureFactor, 0f, 1f) : null,
            TiltDegrees(sp, StylusPointProperties.XTiltOrientation),
            TiltDegrees(sp, StylusPointProperties.YTiltOrientation),
            null,
            timestampMs);
    }

    public static InkPoint FromMouse(Point p, double scale, long timestampMs)
    {
        RequireScale(scale);
        return new InkPoint(p.X / scale, p.Y / scale, null, null, null, null, timestampMs);
    }

    public static Point ToView(PointD pagePt, double scale)
    {
        RequireScale(scale);
        return new Point(pagePt.X * scale, pagePt.Y * scale);
    }

    private static float? TiltDegrees(StylusPoint sp, StylusPointProperty property)
    {
        if (!sp.HasProperty(property))
        {
            return null;
        }

        var info = sp.Description.GetPropertyInfo(property);
        var resolution = info.Unit == StylusPointPropertyUnit.Degrees && info.Resolution > 0
            ? info.Resolution
            : _defaultTiltResolution;
        return (float)(sp.GetPropertyValue(property) / resolution);
    }

    private static void RequireScale(double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be a positive finite number.");
        }
    }
}
