using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// The marks list's thumbnails (FR-103): the vector ink of each mark's band, kept for the last
/// <see cref="Capacity"/> marks shown (least recently used out), so scrolling back draws at once. The ink is read through
/// the injected source (off the UI thread) and its outlines are built on the thread pool; only the frozen geometries
/// are made on the UI thread. Used on the UI thread only.
/// </summary>
internal sealed class MarkThumbnailCache
{
    /// <summary>How many thumbnails are kept: a few screens of rows, a few hundred KB of geometry at most.</summary>
    internal const int DefaultCapacity = 64;

    /// <summary>The band shows this many text lines above and below the mark (the margin-note window).</summary>
    private const double _bandPadLines = MarkRules.NoteWindowLines;

    private readonly Func<Mark, CancellationToken, Task<MarkInk?>> _source;
    private readonly Dictionary<Mark, LinkedListNode<(Mark Key, MarkThumbnail Value)>> _entries = [];
    private readonly LinkedList<(Mark Key, MarkThumbnail Value)> _order = new();
    private int _generation;

    public MarkThumbnailCache(Func<Mark, CancellationToken, Task<MarkInk?>> source, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _source = source;
        Capacity = capacity;
    }

    /// <summary>The ink of a page changed: its thumbnails were dropped and the ones shown should load again.</summary>
    public event EventHandler<int>? PageInvalidated;

    /// <summary>Everything was dropped (another document): the shown rows load again.</summary>
    public event EventHandler? Cleared;

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>How many loads were started (tests).</summary>
    public int LoadCount { get; private set; }

    public bool TryGet(Mark mark, out MarkThumbnail? thumbnail)
    {
        if (_entries.TryGetValue(mark, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            thumbnail = node.Value.Value;
            return true;
        }

        thumbnail = null;
        return false;
    }

    /// <summary>
    /// The thumbnail of a mark: from the cache, or read and built (null when its ink cannot be read now; nothing is
    /// cached then, so the next request tries again). Cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<MarkThumbnail?> GetAsync(Mark mark, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (TryGet(mark, out var cached))
        {
            return cached;
        }

        LoadCount++;
        var generation = _generation;
        var ink = await _source(mark, ct); // back on the UI thread
        if (ink is null)
        {
            return null;
        }

        var outlines = await Task.Run(() => BuildOutlines(ink), ct);
        ct.ThrowIfCancellationRequested();
        var thumbnail = ToThumbnail(ink, outlines);
        if (generation != _generation)
        {
            return null; // read for a document that is gone: never stored or shown
        }

        Store(mark, thumbnail);
        return thumbnail;
    }

    /// <summary>
    /// Drops every thumbnail (the document changed: equal marks may now have other notes) and tells the shown rows to
    /// load again; reads started before are not stored.
    /// </summary>
    public void Clear()
    {
        _generation++;
        _entries.Clear();
        _order.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the thumbnails of a page and tells the shown ones to load again.</summary>
    public void Invalidate(int pageIndex)
    {
        foreach (var node in _entries.Values.Where(n => n.Value.Key.PageIndex == pageIndex).ToArray())
        {
            _entries.Remove(node.Value.Key);
            _order.Remove(node);
        }

        PageInvalidated?.Invoke(this, pageIndex);
    }

    /// <summary>The part of the page a row shows: the full width, the mark's extent and one line above and below.</summary>
    internal static Rect BandOf(Mark mark, double pageWidthPt)
    {
        var pad = _bandPadLines * MarkRules.DefaultLineHeightPt;
        var top = Math.Max(0, mark.TopPt - pad);
        var bottom = Math.Max(top + 1, mark.BottomPt + pad);
        return new Rect(0, top, Math.Max(1, pageWidthPt), bottom - top);
    }

    // Thread pool: the outlines as drawn on the page (same geometry as the page's ink layer).
    private static List<(InkColor Color, IReadOnlyList<IReadOnlyList<PointD>> Figures)> BuildOutlines(MarkInk ink)
    {
        var outlines = new List<(InkColor, IReadOnlyList<IReadOnlyList<PointD>>)>(ink.Strokes.Count);
        foreach (var stroke in ink.Strokes)
        {
            outlines.Add((stroke.Color, StrokeGeometry.BuildOutline(stroke, ink.Curve, ink.Smoothing)));
        }

        return outlines;
    }

    private static MarkThumbnail ToThumbnail(
        MarkInk ink, List<(InkColor Color, IReadOnlyList<IReadOnlyList<PointD>> Figures)> outlines)
    {
        var brushes = new Dictionary<InkColor, Brush>();
        var drawings = new List<GeometryDrawing>(outlines.Count);
        foreach (var (color, figures) in outlines)
        {
            if (!brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(CustomInkLayer.ToColor(color));
                brush.Freeze();
                brushes[color] = brush;
            }

            var drawing = new GeometryDrawing(brush, null, OutlineGeometry.ToStreamGeometry(figures));
            drawing.Freeze();
            drawings.Add(drawing);
        }

        return new MarkThumbnail(BandOf(ink.Mark, ink.PageWidthPt), drawings);
    }

    private void Store(Mark mark, MarkThumbnail thumbnail)
    {
        if (_entries.TryGetValue(mark, out var existing))
        {
            _order.Remove(existing);
        }

        var node = _order.AddFirst((mark, thumbnail));
        _entries[mark] = node;
        while (_entries.Count > Capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }
    }
}
