namespace DreamBooks.Core.Journal;

/// <summary>
/// Where the reader was. Not an undoable change. With <see cref="FitWidth"/> set (the multi-page view):
/// <see cref="PageIndex"/> is the page of the viewport's top edge, <see cref="ScrollX"/> the x of the viewport centre
/// in document-column points and <see cref="ScrollY"/> the top edge below that page's top in page points.
/// <see cref="FitWidth"/> null marks an entry of the single-page view (before phase 3a): the current page and the
/// viewport's scroll offsets in DIPs.
/// </summary>
public sealed record ViewEntry(int PageIndex, double Zoom, double ScrollX, double ScrollY, bool? FitWidth = null) : JournalEntry;
