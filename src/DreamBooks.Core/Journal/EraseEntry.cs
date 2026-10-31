namespace DreamBooks.Core.Journal;

/// <summary>
/// An erase on one page: the Ids it removed and the strokes it added (pieces of partially erased strokes),
/// each with its index in the page's stroke list after the erase.
/// </summary>
public sealed record EraseEntry(int PageIndex, IReadOnlyList<Guid> RemovedIds, IReadOnlyList<IndexedStroke> Added) : JournalEntry;
