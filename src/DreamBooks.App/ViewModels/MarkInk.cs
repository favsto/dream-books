using DreamBooks.Core.Ink;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The ink a marks-list row draws (FR-103): the mark's own strokes and its margin notes, in page points, with the
/// page width (the row shows the mark's band across the whole page) and the outline settings to draw them with.
/// </summary>
public sealed record MarkInk(
    Mark Mark, double PageWidthPt, IReadOnlyList<InkStroke> Strokes, PressureCurve Curve, SmoothingLevel Smoothing);
