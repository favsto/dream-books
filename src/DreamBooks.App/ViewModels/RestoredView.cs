namespace DreamBooks.App.ViewModels;

/// <summary>
/// Where the reader was before a crash (FR-04, from the journal): the zoom (already clamped) and the scroll
/// offsets of the viewport in DIPs (finite, not negative). The view applies it once, after its first layout.
/// </summary>
public sealed record RestoredView(double Zoom, double ScrollX, double ScrollY);
