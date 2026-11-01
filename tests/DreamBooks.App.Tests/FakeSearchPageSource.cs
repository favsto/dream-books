using DreamBooks.Core.Search;

namespace DreamBooks.App.Tests;

/// <summary>
/// The search scan's view of a document, in memory: hits per page (for any query, or per folded query), pages without
/// text, failing pages, gates that hold a page's search, and a thread-safe log of the pages searched. Never prints the
/// query (NFR-12).
/// </summary>
internal sealed class FakeSearchPageSource(int pageCount) : ISearchPageSource
{
    private readonly object _gate = new();
    private readonly List<int> _searched = [];
    private readonly Dictionary<int, TaskCompletionSource> _gates = [];
    private readonly Dictionary<int, TaskCompletionSource> _started = [];

    public int PageCount => Disposed ? throw new ObjectDisposedException(nameof(FakeSearchPageSource)) : pageCount;

    /// <summary>Hits per page, whatever the query.</summary>
    public Dictionary<int, SearchHit[]> Hits { get; } = [];

    /// <summary>Hits per folded query and page; a query listed here ignores <see cref="Hits"/>.</summary>
    public Dictionary<string, Dictionary<int, SearchHit[]>> HitsByQuery { get; } = [];

    /// <summary>Every page reports no text layer.</summary>
    public bool NoText { get; set; }

    /// <summary>Every page's search throws.</summary>
    public bool FailAll { get; set; }

    /// <summary>The renderer behind it was closed: <see cref="PageCount"/> throws.</summary>
    public bool Disposed { get; set; }

    public IReadOnlyList<int> Searched
    {
        get
        {
            lock (_gate)
            {
                return [.. _searched];
            }
        }
    }

    /// <summary>Holds the search of <paramref name="pageIndex"/> until the returned gate is completed.</summary>
    public TaskCompletionSource Hold(int pageIndex)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _gates[pageIndex] = gate;
            _started[pageIndex] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return gate;
    }

    /// <summary>Completes when the held page's search has started.</summary>
    public Task Started(int pageIndex)
    {
        lock (_gate)
        {
            return _started[pageIndex].Task;
        }
    }

    public async Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct)
    {
        TaskCompletionSource? gate;
        lock (_gate)
        {
            _searched.Add(pageIndex);
            _gates.TryGetValue(pageIndex, out gate);
            if (_started.TryGetValue(pageIndex, out var started))
            {
                started.TrySetResult();
            }
        }

        if (gate is not null)
        {
            await gate.Task;
        }

        if (FailAll)
        {
            throw new InvalidOperationException("The page cannot be searched.");
        }

        var hits = (HitsByQuery.TryGetValue(foldedQuery, out var byPage) ? byPage : Hits).GetValueOrDefault(pageIndex) ?? [];
        return new SearchPageResult(!NoText, hits.Take(maxHits).ToArray(), hits.Length > maxHits);
    }

    /// <summary>A one-line hit, 40 x 10 pt, at <paramref name="top"/>.</summary>
    public static SearchHit Hit(int page, double top, double left = 100) =>
        new(page, [new SearchRect(left, top, left + 40, top + 10)]);
}
