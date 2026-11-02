using System.Windows.Media.Imaging;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// The Pages tab's thumbnails (FR-103, UI-04, NFR-06): low-resolution page bitmaps within <see cref="DefaultByteBudget"/>
/// bytes (least recently used out), and the pages' ink as vectors (<see cref="Ink"/>, at most
/// <see cref="DefaultInkCapacity"/> pages, the rail's <see cref="PageInkCache"/>). Both are read through injected sources
/// (the bitmaps from <see cref="PageBitmapCoordinator.RenderThumbnailAsync"/>, low priority and off the UI thread; the
/// ink as the marks list and the rail read it). UI thread only.
/// </summary>
/// <remarks>
/// A failed, empty or cancelled read is never cached, so the row asks again when it is shown again. <see cref="Clear"/>
/// (another document, or the same one reopened without saving) drops both and raises <see cref="Cleared"/>; reads
/// already under way are handed to their callers but never kept. <see cref="InvalidateInk"/> (the strokes of a page
/// changed) drops that page's ink and raises <see cref="InkInvalidated"/>, so its row draws it again; the bitmap stays,
/// since the page's PDF content does not change.
/// </remarks>
internal sealed class PageThumbnailCache
{
    /// <summary>Byte budget of the thumbnail bitmaps: about 175 thumbnails at 140 DIP and 150% scale.</summary>
    public const long DefaultByteBudget = 40L * 1024 * 1024;

    /// <summary>How many pages' ink is kept as frozen geometry.</summary>
    public const int DefaultInkCapacity = 64;

    private readonly Func<int, CancellationToken, Task<BitmapSource?>> _bitmaps;
    private readonly long _byteBudget;
    private readonly Dictionary<int, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new();
    private int _generation;

    public PageThumbnailCache(
        Func<int, CancellationToken, Task<BitmapSource?>> bitmaps,
        Func<int, CancellationToken, Task<PageInk?>> ink,
        long byteBudget = DefaultByteBudget,
        int inkCapacity = DefaultInkCapacity)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _bitmaps = bitmaps;
        _byteBudget = byteBudget;
        Ink = new PageInkCache(ink, inkCapacity);
    }

    /// <summary>Everything was dropped: rows show their placeholder and ask again.</summary>
    public event EventHandler? Cleared;

    /// <summary>A page's ink was dropped because its strokes changed: its row reads it again.</summary>
    public event EventHandler<int>? InkInvalidated;

    /// <summary><see cref="IsPaused"/> changed: rows give up their reads when paused and read what they miss when resumed.</summary>
    public event EventHandler? PausedChanged;

    /// <summary>
    /// True while the list is not shown (the panel closed, or the other tab): rows show only what is cached and start
    /// no read, so nothing renders behind a closed panel (while he writes, or after a document change).
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>The pages' ink, as frozen vector drawings.</summary>
    public PageInkCache Ink { get; }

    /// <summary>Bytes held by the cached bitmaps.</summary>
    public long Bytes { get; private set; }

    /// <summary>Number of cached bitmaps.</summary>
    public int Count => _entries.Count;

    public bool TryGetBitmap(int pageIndex, out BitmapSource? bitmap)
    {
        if (_entries.TryGetValue(pageIndex, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }

        bitmap = null;
        return false;
    }

    /// <summary>
    /// A page's thumbnail bitmap: from the cache, or rendered and kept. Null when it cannot be rendered now (failure,
    /// no document, the file released by a save, cancelled); never throws.
    /// </summary>
    public async Task<BitmapSource?> GetBitmapAsync(int pageIndex, CancellationToken ct)
    {
        if (TryGetBitmap(pageIndex, out var cached))
        {
            return cached;
        }

        var generation = _generation;
        BitmapSource? bitmap;
        try
        {
            bitmap = await _bitmaps(pageIndex, ct);
        }
#pragma warning disable CA1031 // A thumbnail that cannot be rendered keeps its placeholder (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        if (bitmap is null || ct.IsCancellationRequested)
        {
            return null;
        }

        if (generation == _generation)
        {
            Store(pageIndex, bitmap);
        }

        return bitmap;
    }

    /// <summary>Pauses (the list is hidden) or resumes (shown) the rows' reads.</summary>
    public void SetPaused(bool paused)
    {
        if (paused != IsPaused)
        {
            IsPaused = paused;
            PausedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The bitmaps were dropped (another screen scale): the rows render theirs again, keeping the old one shown.</summary>
    public event EventHandler? BitmapsCleared;

    /// <summary>The file is available again (after a save): rows missing their bitmap or ink read it again.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>How many times the bitmaps were dropped for a screen-scale change (tests).</summary>
    internal int BitmapClears { get; private set; }

    /// <summary>How many times the rows were asked to read what they miss (tests).</summary>
    internal int RetryRequests { get; private set; }

    /// <summary>
    /// The screen scale changed: every bitmap goes (they were rendered for the old scale), reads under way are not kept,
    /// and the shown rows render theirs again. The ink is vectors and stays.
    /// </summary>
    public void ClearBitmaps()
    {
        BitmapClears++;
        _generation++;
        _entries.Clear();
        _order.Clear();
        Bytes = 0;
        BitmapsCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The file is available again (the renderer is back after a save): a read that returned nothing because it ran
    /// while the file was released is tried again by the rows still shown.
    /// </summary>
    public void RetryMissing()
    {
        RetryRequests++;
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The strokes of a page changed: its ink is read again (the bitmap stays).</summary>
    public void InvalidateInk(int pageIndex)
    {
        Ink.Invalidate(pageIndex);
        InkInvalidated?.Invoke(this, pageIndex);
    }

    /// <summary>Another document, or the same one reopened: every bitmap and all ink go; reads under way are not kept.</summary>
    public void Clear()
    {
        _generation++;
        _entries.Clear();
        _order.Clear();
        Bytes = 0;
        Ink.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private static long SizeOf(BitmapSource bitmap) =>
        (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);

    private void Store(int pageIndex, BitmapSource bitmap)
    {
        if (_entries.Remove(pageIndex, out var existing))
        {
            _order.Remove(existing);
            Bytes -= existing.Value.Bytes;
        }

        var bytes = SizeOf(bitmap);
        if (bytes > _byteBudget)
        {
            return;
        }

        _entries[pageIndex] = _order.AddFirst(new Entry(pageIndex, bitmap, bytes));
        Bytes += bytes;
        while (Bytes > _byteBudget && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value.PageIndex);
            Bytes -= oldest.Value.Bytes;
        }
    }

    private sealed record Entry(int PageIndex, BitmapSource Bitmap, long Bytes);
}
