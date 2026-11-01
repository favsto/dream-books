namespace DreamBooks.Core.Marks;

/// <summary>
/// A handwritten note (FR-101): a group of pen or highlighter strokes on one page that are not margin lines, close
/// together vertically (handwriting, shapes, underlines, margin text). Extent is in page points as displayed (top-left
/// origin, y down). Notes are derived data and never stored in the PDF. Equality is by value, including the stroke
/// ids in order; the ids are copied and read-only.
/// </summary>
public sealed record Note(int PageIndex, double TopPt, double BottomPt, IReadOnlyList<Guid> StrokeIds)
{
    public IReadOnlyList<Guid> StrokeIds { get; } = Array.AsReadOnly(StrokeIds.ToArray());

    public bool Equals(Note? other)
        => other is not null
            && PageIndex == other.PageIndex
            && TopPt.Equals(other.TopPt)
            && BottomPt.Equals(other.BottomPt)
            && StrokeIds.SequenceEqual(other.StrokeIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PageIndex);
        hash.Add(TopPt);
        hash.Add(BottomPt);
        foreach (var id in StrokeIds)
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }
}
