using DreamBooks.Core.Model;

namespace DreamBooks.Pdf;

/// <summary>
/// What <see cref="StrokeAnnotations.Read"/> found on a page (FR-81, FR-83): the app's strokes in
/// annotation order, the indexes of app annotations whose payload is newer or corrupt (left
/// untouched, drawn by PDFium as read-only pictures), and how many annotations are foreign.
/// </summary>
internal sealed record StrokeReadResult(
    IReadOnlyList<InkStroke> Strokes,
    IReadOnlyList<int> UnreadableAnnotationIndexes,
    int ForeignCount);
