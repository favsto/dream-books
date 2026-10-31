using DreamBooks.Core.Model;

namespace DreamBooks.Core.Journal;

/// <summary>Strokes appended on top of a page, in order.</summary>
public sealed record AddEntry(int PageIndex, IReadOnlyList<InkStroke> Strokes) : JournalEntry;
