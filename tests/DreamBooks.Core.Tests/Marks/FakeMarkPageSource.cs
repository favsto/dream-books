using System.Collections.Concurrent;
using System.Threading.Channels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// Mark page source for the scanner tests: records the order of loads, counts loads in flight, and can
/// hold, fail or corrupt a load per page.
/// </summary>
internal sealed class FakeMarkPageSource(int pageCount) : IMarkPageSource
{
    private readonly Channel<int> _started = Channel.CreateUnbounded<int>();
    private int _inFlight;
    private int _maxInFlight;

    public int PageCount { get; } = pageCount;

    /// <summary>Pages in the order their loads started.</summary>
    public ConcurrentQueue<int> Loads { get; } = new();

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>The strokes per page; null means "certainly no annotations". Default: no strokes, not null.</summary>
    public Func<int, PageStrokes?> Strokes { get; set; } = _ => new PageStrokes([], 0);

    /// <summary>When it returns a task for a page, that page's load awaits it.</summary>
    public Func<int, Task?> Gate { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the load throws it before returning a task.</summary>
    public Func<int, Exception?> ThrowSync { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, the returned task faults with it.</summary>
    public Func<int, Exception?> ThrowAsync { get; set; } = _ => null;

    /// <summary>When it returns an exception for a page, reading the page dimensions throws it.</summary>
    public Func<int, Exception?> DimensionsFail { get; set; } = _ => null;

    /// <summary>When true, every load yields once so that it completes asynchronously.</summary>
    public bool Yield { get; set; }

    public PageDimensions GetPageDimensions(int pageIndex)
        => DimensionsFail(pageIndex) is { } failure ? throw failure : new PageDimensions(612, 792);

    public Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct)
    {
        Loads.Enqueue(pageIndex);
        _started.Writer.TryWrite(pageIndex);
        if (ThrowSync(pageIndex) is { } failure)
        {
            throw failure;
        }

        return LoadAsync(pageIndex);
    }

    /// <summary>Waits until the next load starts and returns its page.</summary>
    public async Task<int> NextStartedAsync() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private async Task<PageStrokes?> LoadAsync(int pageIndex)
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

            return Strokes(pageIndex);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
