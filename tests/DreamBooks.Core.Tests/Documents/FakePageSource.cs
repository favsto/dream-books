using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Documents;

/// <summary>In-memory page source that counts loads and can hold or fail them on demand.</summary>
internal sealed class FakePageSource(int pageCount) : IPageSource
{
    private readonly Dictionary<int, PageStrokes> _strokes = [];
    private readonly Dictionary<int, int> _loads = [];

    public int PageCount { get; } = pageCount;

    public int TotalLoads => _loads.Values.Sum();

    /// <summary>When set, every load awaits this task before returning.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, the next load throws it (once).</summary>
    public Exception? FailNext { get; set; }

    /// <summary>When true, the next load ends cancelled although nobody cancelled it (once).</summary>
    public bool CancelNext { get; set; }

    /// <summary>When true, loads observe the token they are given.</summary>
    public bool ObserveToken { get; set; }

    public void SetStrokes(int pageIndex, IReadOnlyList<InkStroke> strokes, int unreadable = 0)
        => _strokes[pageIndex] = new PageStrokes(strokes, unreadable);

    public int LoadCount(int pageIndex) => _loads.GetValueOrDefault(pageIndex);

    public PageDimensions GetPageDimensions(int pageIndex) => new(612, 792);

    public async Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default)
    {
        lock (_loads)
        {
            _loads[pageIndex] = _loads.GetValueOrDefault(pageIndex) + 1;
        }

        if (Gate is { } gate)
        {
            await (ObserveToken ? gate.Task.WaitAsync(ct) : gate.Task);
        }

        if (FailNext is { } failure)
        {
            FailNext = null;
            throw failure;
        }

        if (CancelNext)
        {
            CancelNext = false;
            throw new TaskCanceledException();
        }

        return _strokes.GetValueOrDefault(pageIndex) ?? new PageStrokes([], 0);
    }
}
