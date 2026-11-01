using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// A page's ink for the scroll rail's preview bubble (UI-07): the page size in points and the frozen outline of each
/// stroke, in page points, filled with the stroke's colour.
/// </summary>
internal sealed record PageInkPicture(int PageIndex, double WidthPt, double HeightPt, IReadOnlyList<GeometryDrawing> Strokes);
