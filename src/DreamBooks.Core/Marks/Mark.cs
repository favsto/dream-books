namespace DreamBooks.Core.Marks;

/// <summary>
/// A recognised mark: one or more merged margin lines. Extent is in page points as displayed
/// (top-left origin, y down). Marks are derived data and never stored in the PDF.
/// Equality is by value, including the stroke ids in order; the ids are copied and read-only.
/// </summary>
public sealed record Mark(int PageIndex, MarkSide Side, double TopPt, double BottomPt, IReadOnlyList<Guid> StrokeIds)
{
    public IReadOnlyList<Guid> StrokeIds { get; } = Array.AsReadOnly(StrokeIds.ToArray());

    public bool Equals(Mark? other)
        => other is not null
            && PageIndex == other.PageIndex
            && Side == other.Side
            && TopPt.Equals(other.TopPt)
            && BottomPt.Equals(other.BottomPt)
            && StrokeIds.SequenceEqual(other.StrokeIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PageIndex);
        hash.Add(Side);
        hash.Add(TopPt);
        hash.Add(BottomPt);
        foreach (var id in StrokeIds)
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }
}
