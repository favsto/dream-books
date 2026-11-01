namespace DreamBooks.Core.Search;

/// <summary>
/// The search of one page (FR-13): whether the page has any text character, its hits in reading order and
/// whether the hit limit cut the list short. No page text is kept. Equality is by value; the hits are copied.
/// </summary>
public sealed record SearchPageResult(bool HasTextLayer, IReadOnlyList<SearchHit> Hits, bool Truncated)
{
    public IReadOnlyList<SearchHit> Hits { get; } = Array.AsReadOnly(Hits.ToArray());

    public bool Equals(SearchPageResult? other)
        => other is not null
            && HasTextLayer == other.HasTextLayer
            && Truncated == other.Truncated
            && Hits.SequenceEqual(other.Hits);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(HasTextLayer);
        hash.Add(Truncated);
        foreach (SearchHit hit in Hits)
        {
            hash.Add(hit);
        }

        return hash.ToHashCode();
    }
}
