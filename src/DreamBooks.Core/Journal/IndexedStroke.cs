using DreamBooks.Core.Model;

namespace DreamBooks.Core.Journal;

/// <summary>A stroke and its index in the page's final stroke list.</summary>
public sealed record IndexedStroke(int Index, InkStroke Stroke);
