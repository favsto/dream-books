using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;

namespace DreamBooks.App.Tests;

/// <summary>
/// The scan's view of a <see cref="FakePageSource"/>: pages without stored strokes return null (the cheap
/// pre-check), loads are recorded (thread-safe: the scan runs on the pool), and a page can be held by a gate.
/// </summary>
internal sealed class FakeMarkPageSource(FakePageSource pages) : IMarkPageSource
{
    private readonly object _gate = new();
    private readonly List<int> _loads = [];

    public int PageCount { get; } = pages.PageCount;

    /// <summary>Pages whose load waits until the gate is completed.</summary>
    public Dictionary<int, TaskCompletionSource> Gates { get; } = [];

    /// <summary>Completed when the load of a gated page has started.</summary>
    public Dictionary<int, TaskCompletionSource> Started { get; } = [];

    public IReadOnlyList<int> Loads
    {
        get
        {
            lock (_gate)
            {
                return [.. _loads];
            }
        }
    }

    public PageDimensions GetPageDimensions(int pageIndex) => pages.GetPageDimensions(pageIndex);

    /// <summary>Holds the load of <paramref name="pageIndex"/> until the returned gate is completed.</summary>
    public TaskCompletionSource Hold(int pageIndex)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            Gates[pageIndex] = gate;
            Started[pageIndex] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return gate;
    }

    public async Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct)
    {
        TaskCompletionSource? gate;
        lock (_gate)
        {
            _loads.Add(pageIndex);
            Gates.TryGetValue(pageIndex, out gate);
            if (Started.TryGetValue(pageIndex, out var started))
            {
                started.TrySetResult();
            }
        }

        if (gate is not null)
        {
            await gate.Task;
        }

        return pages.Strokes.TryGetValue(pageIndex, out var strokes) ? new PageStrokes(strokes, 0) : null;
    }
}
