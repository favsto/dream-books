using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// The ink of the pages shown in the scroll rail's preview bubble (UI-07), and of the Pages tab's thumbnails (FR-103,
/// through <see cref="PageThumbnailCache"/>, with a larger capacity), as frozen vector drawings, for the last
/// <see cref="Capacity"/> pages (least recently used out), so scrubbing back draws at once. The strokes are read through
/// the injected source (off the UI thread), their outlines built on the thread pool (<see cref="InkDrawings"/>, as the
/// marks list builds its thumbnails), the frozen drawings made on the UI thread. A page whose strokes changed is read
/// again (<see cref="Invalidate"/>); another document, or the same one reopened without saving, drops everything
/// (<see cref="Clear"/>). A read that either superseded is handed to its caller but never kept. UI thread only.
/// </summary>
internal sealed class PageInkCache
{
    /// <summary>How many pages are kept: a scrub's worth, a few hundred KB of geometry at most.</summary>
    internal const int DefaultCapacity = 16;

    private readonly Func<int, CancellationToken, Task<PageInk?>> _source;
    private readonly Dictionary<int, LinkedListNode<PageInkPicture>> _entries = [];
    private readonly LinkedList<PageInkPicture> _order = new();
    private readonly Dictionary<int, int> _pageVersions = [];
    private int _generation;

    public PageInkCache(Func<int, CancellationToken, Task<PageInk?>> source, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _source = source;
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    public bool TryGet(int pageIndex, out PageInkPicture? picture)
    {
        if (_entries.TryGetValue(pageIndex, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            picture = node.Value;
            return true;
        }

        picture = null;
        return false;
    }

    /// <summary>
    /// The ink of a page: from the cache, or read and built. Null when it cannot be read now (nothing is cached then, so
    /// the next request tries again). Cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<PageInkPicture?> GetAsync(int pageIndex, CancellationToken ct)
    {
        if (TryGet(pageIndex, out var cached))
        {
            return cached;
        }

        var generation = _generation;
        var version = _pageVersions.GetValueOrDefault(pageIndex);
        var ink = await _source(pageIndex, ct); // back on the UI thread
        if (ink is null)
        {
            return null;
        }

        var outlines = await Task.Run(() => InkDrawings.BuildOutlines(ink.Strokes, ink.Curve, ink.Smoothing), ct);
        ct.ThrowIfCancellationRequested();
        var picture = new PageInkPicture(pageIndex, ink.WidthPt, ink.HeightPt, InkDrawings.ToDrawings(outlines));
        if (generation == _generation && version == _pageVersions.GetValueOrDefault(pageIndex))
        {
            Store(picture);
        }

        return picture;
    }

    /// <summary>The strokes of a page changed: its ink is read again; a read already under way is not kept.</summary>
    public void Invalidate(int pageIndex)
    {
        _pageVersions[pageIndex] = _pageVersions.GetValueOrDefault(pageIndex) + 1;
        if (_entries.Remove(pageIndex, out var node))
        {
            _order.Remove(node);
        }
    }

    /// <summary>Drops every page (another document, or the same one reopened); reads under way are not kept.</summary>
    public void Clear()
    {
        _generation++;
        _entries.Clear();
        _order.Clear();
        _pageVersions.Clear();
    }

    private void Store(PageInkPicture picture)
    {
        if (_entries.Remove(picture.PageIndex, out var existing))
        {
            _order.Remove(existing);
        }

        _entries[picture.PageIndex] = _order.AddFirst(picture);
        while (_entries.Count > Capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value.PageIndex);
        }
    }
}
