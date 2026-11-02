using System.Diagnostics.CodeAnalysis;

namespace DreamBooks.Core.Search;

/// <summary>
/// Document-wide hits of ONE query (FR-13, FR-105), filled page by page by a <see cref="SearchScanner"/> and read
/// by the UI. A new query gets a new index: nothing here is ever cleared. The page table is copy-on-write: writers
/// publish a new table of immutable per-page entries (hits sorted by position) under a lock, readers grab the
/// current reference and work on it without locking. <see cref="Changed"/> is raised outside the lock. A page is
/// "searched" once it has an authoritative result (possibly no hits). No page text is kept, only rectangles.
/// </summary>
/// <remarks>
/// A hit's position is its topmost rectangle's top, then its leftmost left (displayed page points, y down): the
/// hits of a page are sorted by it, because a rotated page returns them in PDFium's order, not by position.
/// </remarks>
public sealed class SearchIndex
{
    /// <summary>A hit less than this above the position does not count as "previous" (the view was just aligned to it).</summary>
    private const double _previousToleranceYPt = 1.0;

    private static readonly SearchHit[] _noHits = [];

    private readonly object _gate = new();
    private Table _table;
    private volatile bool _truncated;

    public SearchIndex(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        _table = new Table(new PageEntry?[pageCount], 0, 0, 0);
    }

    /// <summary>Raised (on the writer's thread) after a page's result changed; equal results raise nothing.</summary>
    public event EventHandler<SearchIndexChangedEventArgs>? Changed;

    public int PageCount => Current.Pages.Length;

    public int SearchedCount => Current.SearchedCount;

    public int TotalHits => Current.TotalHits;

    /// <summary>True once any searched page reported a text layer (or a hit).</summary>
    public bool AnyPageHasText => Current.TextPageCount > 0;

    /// <summary>
    /// True when the hit cap cut the search short: the hits here may not be all the document's. Set by the scanner
    /// after it published the page that reached the cap; setting it raises no <see cref="Changed"/> (the scanner
    /// reports the end of the search through its own Progress event, raised after the flag is set).
    /// </summary>
    public bool Truncated
    {
        get => _truncated;
        set => _truncated = value;
    }

    private Table Current
    {
        get
        {
            lock (_gate)
            {
                return _table;
            }
        }
    }

    /// <summary>
    /// Replaces the page's result. <paramref name="hasText"/> says whether the page has a text layer (a page with
    /// hits counts as having one in any case). Equal hits (in any order) with the same flag change nothing and
    /// raise nothing.
    /// </summary>
    public void SetPage(int pageIndex, IReadOnlyList<SearchHit> hits, bool hasText = true)
    {
        var entry = PageEntry.Create(pageIndex, hits, hasText);
        lock (_gate)
        {
            var pages = _table.Pages;
            if (pageIndex < 0 || pageIndex >= pages.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }

            var current = pages[pageIndex];
            if (current is not null && current.SameAs(entry))
            {
                return;
            }

            var copy = (PageEntry?[])pages.Clone();
            copy[pageIndex] = entry;
            _table = new Table(
                copy,
                _table.SearchedCount + (current is null ? 1 : 0),
                _table.TotalHits - (current?.Hits.Length ?? 0) + entry.Hits.Length,
                _table.TextPageCount - (current?.HasText == true ? 1 : 0) + (entry.HasText ? 1 : 0));
        }

        Changed?.Invoke(this, new SearchIndexChangedEventArgs(pageIndex));
    }

    public bool IsSearched(int pageIndex)
    {
        var pages = Current.Pages;
        return pageIndex >= 0 && pageIndex < pages.Length && pages[pageIndex] is not null;
    }

    /// <summary>Every hit, by page then position.</summary>
    public IReadOnlyList<SearchHit> AllHits() => Current.Pages.SelectMany(p => p?.Hits ?? _noHits).ToArray();

    /// <summary>The page's hits by position; none for a page not searched yet or out of range.</summary>
    public IReadOnlyList<SearchHit> HitsOnPage(int pageIndex)
    {
        var pages = Current.Pages;
        return pageIndex >= 0 && pageIndex < pages.Length ? pages[pageIndex]?.Hits ?? _noHits : _noHits;
    }

    /// <summary>
    /// The first hit strictly after the position (a later page, or the same page with its top below
    /// <paramref name="yPt"/>); with <paramref name="wrap"/>, the document's first hit when there is none after.
    /// </summary>
    public bool TryNext(int pageIndex, double yPt, bool wrap, [MaybeNullWhen(false)] out SearchHit hit)
    {
        var pages = Current.Pages;
        hit = FirstFrom(pages, pageIndex, (_, h) => Position.Of(h).TopPt > yPt) ?? (wrap ? FirstFrom(pages, -1, null) : null);
        return hit is not null;
    }

    /// <summary>
    /// The last hit strictly before the position (an earlier page, or the same page with its top more than 1 pt
    /// above <paramref name="yPt"/>); with <paramref name="wrap"/>, the document's last hit when there is none before.
    /// </summary>
    public bool TryPrevious(int pageIndex, double yPt, bool wrap, [MaybeNullWhen(false)] out SearchHit hit)
    {
        var pages = Current.Pages;
        hit = LastFrom(pages, pageIndex, (_, h) => Position.Of(h).TopPt < yPt - _previousToleranceYPt)
            ?? (wrap ? LastFrom(pages, pages.Length, null) : null);
        return hit is not null;
    }

    /// <summary>
    /// The hit after <paramref name="current"/> in index order, so that hits sharing a line are visited one by one;
    /// wraps like the position overload. A hit the index does not hold steps from its position.
    /// </summary>
    public bool TryNext(SearchHit current, bool wrap, [MaybeNullWhen(false)] out SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(current);
        var pages = Current.Pages;
        int found = IndexOnPage(pages, current);
        var at = Position.Of(current);
        hit = FirstFrom(pages, current.PageIndex, (i, h) => found >= 0 ? i > found : Position.Of(h).CompareTo(at) > 0)
            ?? (wrap ? FirstFrom(pages, -1, null) : null);
        return hit is not null;
    }

    /// <summary>The hit before <paramref name="current"/> in index order; the mirror of <see cref="TryNext(SearchHit, bool, out SearchHit)"/>.</summary>
    public bool TryPrevious(SearchHit current, bool wrap, [MaybeNullWhen(false)] out SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(current);
        var pages = Current.Pages;
        int found = IndexOnPage(pages, current);
        var at = Position.Of(current);
        hit = LastFrom(pages, current.PageIndex, (i, h) => found >= 0 ? i < found : Position.Of(h).CompareTo(at) < 0)
            ?? (wrap ? LastFrom(pages, pages.Length, null) : null);
        return hit is not null;
    }

    private static int IndexOnPage(PageEntry?[] pages, SearchHit hit)
        => hit.PageIndex >= 0 && hit.PageIndex < pages.Length && pages[hit.PageIndex] is { } entry
            ? Array.IndexOf(entry.Hits, hit)
            : -1;

    // The first hit on page `from` that satisfies `onFromPage` (given its index on the page and itself), else the
    // first hit of a later page. A `from` outside the document makes every hit of the following pages qualify.
    private static SearchHit? FirstFrom(PageEntry?[] pages, int from, Func<int, SearchHit, bool>? onFromPage)
    {
        for (int p = Math.Max(from, 0); p < pages.Length; p++)
        {
            if (pages[p] is not { } entry)
            {
                continue;
            }

            for (int i = 0; i < entry.Hits.Length; i++)
            {
                if (p > from || onFromPage!(i, entry.Hits[i]))
                {
                    return entry.Hits[i];
                }
            }
        }

        return null;
    }

    // The mirror of FirstFrom, walking backwards from page `from`.
    private static SearchHit? LastFrom(PageEntry?[] pages, int from, Func<int, SearchHit, bool>? onFromPage)
    {
        for (int p = Math.Min(from, pages.Length - 1); p >= 0; p--)
        {
            if (pages[p] is not { } entry)
            {
                continue;
            }

            for (int i = entry.Hits.Length - 1; i >= 0; i--)
            {
                if (p < from || onFromPage!(i, entry.Hits[i]))
                {
                    return entry.Hits[i];
                }
            }
        }

        return null;
    }

    /// <summary>An immutable snapshot of the page table with its running totals.</summary>
    private sealed record Table(PageEntry?[] Pages, int SearchedCount, int TotalHits, int TextPageCount);

    /// <summary>A hit's sort key: topmost top, then leftmost left.</summary>
    private readonly record struct Position(double TopPt, double LeftPt) : IComparable<Position>
    {
        public static Position Of(SearchHit hit)
        {
            if (hit.Rects.Count == 0)
            {
                return default;
            }

            double top = double.PositiveInfinity, left = double.PositiveInfinity;
            foreach (var rect in hit.Rects)
            {
                top = Math.Min(top, rect.TopPt);
                left = Math.Min(left, rect.LeftPt);
            }

            return new Position(top, left);
        }

        public int CompareTo(Position other)
        {
            int byTop = TopPt.CompareTo(other.TopPt);
            return byTop != 0 ? byTop : LeftPt.CompareTo(other.LeftPt);
        }
    }

    /// <summary>One page's result, immutable: hits sorted by position.</summary>
    private sealed class PageEntry(SearchHit[] hits, bool hasText)
    {
        public SearchHit[] Hits { get; } = hits;

        public bool HasText { get; } = hasText;

        public static PageEntry Create(int pageIndex, IReadOnlyList<SearchHit> hits, bool hasText)
        {
            ArgumentNullException.ThrowIfNull(hits);
            var sorted = new SearchHit[hits.Count];
            for (int i = 0; i < sorted.Length; i++)
            {
                var hit = hits[i] ?? throw new ArgumentException("A hit is null.", nameof(hits));
                if (hit.PageIndex != pageIndex)
                {
                    throw new ArgumentException("A hit belongs to another page.", nameof(hits));
                }

                sorted[i] = hit;
            }

            // Stable: hits at the same position keep the source's reading order.
            var keys = sorted.Select(Position.Of).ToArray();
            var order = Enumerable.Range(0, sorted.Length).OrderBy(i => keys[i]).ToArray();
            return new PageEntry(order.Select(i => sorted[i]).ToArray(), hasText || sorted.Length > 0);
        }

        public bool SameAs(PageEntry other) => HasText == other.HasText && Hits.AsSpan().SequenceEqual(other.Hits);
    }
}
