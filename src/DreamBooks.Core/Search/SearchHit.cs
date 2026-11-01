namespace DreamBooks.Core.Search;

/// <summary>
/// One occurrence of the query on a page (FR-13): one rectangle per text line it covers, in reading order.
/// Equality is by value; the rectangles are copied and read-only.
/// </summary>
public sealed record SearchHit(int PageIndex, IReadOnlyList<SearchRect> Rects)
{
    public IReadOnlyList<SearchRect> Rects { get; } = Array.AsReadOnly(Rects.ToArray());

    public bool Equals(SearchHit? other)
        => other is not null && PageIndex == other.PageIndex && Rects.SequenceEqual(other.Rects);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PageIndex);
        foreach (SearchRect rect in Rects)
        {
            hash.Add(rect);
        }

        return hash.ToHashCode();
    }
}
