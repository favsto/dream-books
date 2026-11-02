using System.Collections.Concurrent;
using System.Threading.Channels;
using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Search;

/// <summary>
/// The search scan's view of a document, in memory, shared by the Core scanner tests and the App search tests (linked
/// into DreamBooks.App.Tests): records every search (page, folded query, hit budget) and the searches in flight; the
/// hits of a page come from <see cref="HitsByQuery"/> or <see cref="Hits"/> when they list it, otherwise
/// <see cref="HitCount"/> generated ones (default: the constructor's <c>hitsPerPage</c>); a page's search can be held,
/// failed or replaced. Never prints the query (NFR-12).
/// </summary>
internal sealed class FakeSearchPageSource(int pageCount, int hitsPerPage = 0) : ISearchPageSource
{
    private readonly Channel<int> _started = Channel.CreateUnbounded<int>();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource> _holds = [];
    private readonly Dictionary<int, TaskCompletionSource> _heldStarted = [];
    private int _inFlight;
    private int _maxInFlight;
    private int _pageCountReads;

    public int PageCount
    {
        get
        {
            Interlocked.Increment(ref _pageCountReads);
            return Disposed ? throw new ObjectDisposedException(nameof(FakeSearchPageSource)) : pageCount;
        }
    }

    public int PageCountReads => Volatile.Read(ref _pageCountReads);

    /// <summary>Searches in the order they started.</summary>
    public ConcurrentQueue<(int Page, string Query, int MaxHits)> Loads { get; } = new();

    public IEnumerable<int> LoadedPages => Loads.Select(l => l.Page);

    /// <summary>The pages searched, in the order the searches started.</summary>
    public IReadOnlyList<int> Searched => [.. LoadedPages];

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Hits per page, whatever the query.</summary>
    public Dictionary<int, SearchHit[]> Hits { get; } = [];

    /// <summary>Hits per folded query and page; a query listed here ignores <see cref="Hits"/>.</summary>
    public Dictionary<string, Dictionary<int, SearchHit[]>> HitsByQuery { get; } = [];

    /// <summary>The number of generated hits of a page that the dictionaries do not list (before the budget).</summary>
    public Func<int, int> HitCount { get; set; } = _ => hitsPerPage;

    /// <summary>Whether a page has text (a page with hits always has).</summary>
    public Func<int, bool> HasText { get; set; } = _ => true;

    /// <summary>Every page reports no text layer.</summary>
    public bool NoText { get; set; }

    /// <summary>When set, replaces the result entirely (the budget is not applied).</summary>
    public Func<int, SearchPageResult?>? Result { get; set; }

    /// <summary>When it returns a task for a page, that page's search awaits it.</summary>
    public Func<int, Task?> Gate { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the search throws it before returning a task.</summary>
    public Func<int, Exception?> ThrowSync { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the returned task faults with it.</summary>
    public Func<int, Exception?> ThrowAsync { get; set; } = _ => null;

    /// <summary>Pages whose search throws (after any hold).</summary>
    public HashSet<int> FailingPages { get; } = [];

    /// <summary>Every page's search throws (after any hold).</summary>
    public bool FailAll { get; set; }

    /// <summary>The renderer behind it was closed: <see cref="PageCount"/> throws.</summary>
    public bool Disposed { get; set; }

    /// <summary>When true, every search yields once so that it completes asynchronously.</summary>
    public bool Yield { get; set; }

    /// <summary>A generated hit: the <paramref name="i"/>-th line of a page.</summary>
    public static SearchHit NthHit(int page, int i) => new(page, [new SearchRect(72, 100 + (i * 14), 120, 112 + (i * 14))]);

    /// <summary>A one-line hit, 40 x 10 pt, at <paramref name="top"/>.</summary>
    public static SearchHit Hit(int page, double top, double left = 100) =>
        new(page, [new SearchRect(left, top, left + 40, top + 10)]);

    /// <summary>Holds the search of <paramref name="pageIndex"/> until the returned gate is completed.</summary>
    public TaskCompletionSource Hold(int pageIndex)
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _holds[pageIndex] = hold;
            _heldStarted[pageIndex] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return hold;
    }

    /// <summary>Completes when the held page's search has started.</summary>
    public Task Started(int pageIndex)
    {
        lock (_gate)
        {
            return _heldStarted[pageIndex].Task;
        }
    }

    /// <summary>Waits until the next search starts and returns its page.</summary>
    public async Task<int> NextStartedAsync() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct)
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            Loads.Enqueue((pageIndex, foldedQuery, maxHits));
            _holds.TryGetValue(pageIndex, out hold);
            if (_heldStarted.TryGetValue(pageIndex, out var started))
            {
                started.TrySetResult();
            }
        }

        _started.Writer.TryWrite(pageIndex);
        if (ThrowSync(pageIndex) is { } failure)
        {
            throw failure;
        }

        return SearchAsync(pageIndex, foldedQuery, maxHits, hold);
    }

    private async Task<SearchPageResult> SearchAsync(int pageIndex, string foldedQuery, int maxHits, TaskCompletionSource? hold)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int max;
        while (now > (max = Volatile.Read(ref _maxInFlight)))
        {
            Interlocked.CompareExchange(ref _maxInFlight, now, max);
        }

        try
        {
            if (Yield)
            {
                await Task.Yield();
            }

            if (Gate(pageIndex) is { } gate)
            {
                await gate;
            }

            if (hold is not null)
            {
                await hold.Task; // a held search ignores its token, as a PDFium job that has started does
            }

            if (ThrowAsync(pageIndex) is { } failure)
            {
                throw failure;
            }

            if (FailAll || FailingPages.Contains(pageIndex))
            {
                throw new InvalidOperationException("The page cannot be searched.");
            }

            if (Result is { } result)
            {
                return result(pageIndex)!;
            }

            var listed = (HitsByQuery.TryGetValue(foldedQuery, out var byPage) ? byPage : Hits).GetValueOrDefault(pageIndex);
            var hits = listed ?? Enumerable.Range(0, HitCount(pageIndex)).Select(i => NthHit(pageIndex, i)).ToArray();
            return new SearchPageResult(
                (!NoText && HasText(pageIndex)) || hits.Length > 0,
                hits.Take(maxHits).ToArray(),
                hits.Length > maxHits);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
