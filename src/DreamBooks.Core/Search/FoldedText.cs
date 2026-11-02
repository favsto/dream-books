namespace DreamBooks.Core.Search;

/// <summary>
/// Text folded for searching (see <see cref="TextFolding"/>). <see cref="SourceIndex"/> has one entry per
/// character of <see cref="Folded"/>: the index, in the source string, of the character it came from (the first
/// UTF-16 unit of a surrogate pair; the first character of a collapsed whitespace run). It never decreases.
/// </summary>
public sealed record FoldedText(string Folded, int[] SourceIndex);
