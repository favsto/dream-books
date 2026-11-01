using System.Collections.Concurrent;
using System.Threading.Channels;
using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Search;

/// <summary>
/// Search page source for the scanner tests: records every search (page, folded query, hit budget), counts
/// searches in flight, and can hold, fail or shape the result of a search per page.
/// </summary>
internal sealed class FakeSearchPageSource(int pageCount) : ISearchPageSource
{
    private readonly Channel<int> _started = Channel.CreateUnbounded<int>();
    private int _inFlight;
    private int _maxInFlight;
    private int _pageCountReads;

    public int PageCount
    {
        get
        {
            Interlocked.Increment(ref _pageCountReads);
            return pageCount;
        }
    }

    public int PageCountReads => Volatile.Read(ref _pageCountReads);

    /// <summary>Searches in the order they started.</summary>
    public ConcurrentQueue<(int Page, string Query, int MaxHits)> Loads { get; } = new();

    public IEnumerable<int> LoadedPages => Loads.Select(l => l.Page);

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>The page's hit count before the budget (default 1) and whether it has text (default true).</summary>
    public Func<int, int> HitCount { get; set; } = _ => 1;

    public Func<int, bool> HasText { get; set; } = _ => true;

    /// <summary>When set, replaces the result entirely (the budget is not applied).</summary>
    public Func<int, SearchPageResult?>? Result { get; set; }

    /// <summary>When it returns a task for a page, that page's search awaits it.</summary>
    public Func<int, Task?> Gate { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the search throws it before returning a task.</summary>
    public Func<int, Exception?> ThrowSync { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the returned task faults with it.</summary>
    public Func<int, Exception?> ThrowAsync { get; set; } = _ => null;

    /// <summary>When true, every search yields once so that it completes asynchronously.</summary>
    public bool Yield { get; set; }

    public static SearchHit Hit(int page, int i) => new(page, [new SearchRect(72, 100 + (i * 14), 120, 112 + (i * 14))]);

    public Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct)
    {
        Loads.Enqueue((pageIndex, foldedQuery, maxHits));
        _started.Writer.TryWrite(pageIndex);
        if (ThrowSync(pageIndex) is { } failure)
        {
            throw failure;
        }

        return SearchAsync(pageIndex, maxHits);
    }

    /// <summary>Waits until the next search starts and returns its page.</summary>
    public async Task<int> NextStartedAsync() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private async Task<SearchPageResult> SearchAsync(int pageIndex, int maxHits)
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

            if (ThrowAsync(pageIndex) is { } failure)
            {
                throw failure;
            }

            if (Result is { } result)
            {
                return result(pageIndex)!;
            }

            var count = HitCount(pageIndex);
            var kept = Math.Min(count, maxHits);
            return new SearchPageResult(
                HasText(pageIndex) || count > 0,
                Enumerable.Range(0, kept).Select(i => Hit(pageIndex, i)).ToArray(),
                count > maxHits);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
