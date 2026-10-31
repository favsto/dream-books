using DreamBooks.Core.Model;

namespace DreamBooks.Core.Documents;

/// <summary>The strokes loaded for one page, plus how many stored strokes could not be read.</summary>
public sealed record PageStrokes(IReadOnlyList<InkStroke> Strokes, int UnreadableCount);
