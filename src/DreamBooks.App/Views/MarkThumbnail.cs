using System.Windows;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// A marks-list thumbnail (FR-103): the band of the page it shows (page points) and the frozen outline of each stroke
/// in it, in page points, filled with the stroke's colour.
/// </summary>
internal sealed record MarkThumbnail(Rect BandPt, IReadOnlyList<GeometryDrawing> Strokes);
