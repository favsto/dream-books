namespace DreamBooks.Core.Journal;

/// <summary>Where the reader was: page, zoom and scroll offsets. Not an undoable change.</summary>
public sealed record ViewEntry(int PageIndex, double Zoom, double ScrollX, double ScrollY) : JournalEntry;
