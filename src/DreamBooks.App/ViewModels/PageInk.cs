using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The ink of a whole page for the scroll rail's preview bubble (UI-07): its strokes in page points, the page size, and
/// the outline settings to draw them with.
/// </summary>
public sealed record PageInk(
    int PageIndex,
    double WidthPt,
    double HeightPt,
    IReadOnlyList<InkStroke> Strokes,
    PressureCurve Curve,
    SmoothingLevel Smoothing);
