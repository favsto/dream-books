using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// One Pages tab row's picture (FR-103, UI-04): the page as white paper of <c>PagesThumbnailWidth</c> with the page's
/// proportions, its low-resolution bitmap drawn over it once rendered, and the page's ink drawn on top as vectors
/// (<see cref="InkDrawings"/>, never thinner than <c>PagesThumbnailMinStrokeWidth</c>). Until the bitmap arrives, or for
/// a page that cannot be rendered, the paper stays (the row shows its number under it).
/// </summary>
/// <remarks>
/// The bitmap and the ink come from the <see cref="CacheProperty"/> (inherited from the list) when <see cref="Page"/> or
/// the cache changes and when the element is loaded again, so only realised rows load; a recycled row cancels the reads
/// of its previous page, an unloaded one its own. A stroke change on its page (<see cref="PageThumbnailCache.InkInvalidated"/>)
/// reads the ink again keeping the old one shown meanwhile; <see cref="PageThumbnailCache.Cleared"/> starts over.
/// Decorative: the row carries the automation name; never hit-tested for the pen (the sheet takes it first).
/// </remarks>
public sealed class PageThumbnail : FrameworkElement
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageItem), typeof(PageThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnSourceChanged));

    /// <summary>The thumbnail cache, set once on the list and inherited by its rows.</summary>
    internal static readonly DependencyProperty CacheProperty = DependencyProperty.RegisterAttached(
        "Cache",
        typeof(PageThumbnailCache),
        typeof(PageThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnSourceChanged));

    private CancellationTokenSource? _bitmapLoad;
    private CancellationTokenSource? _inkLoad;
    private PageThumbnailCache? _listeningTo;
    private BitmapSource? _bitmap;
    private PageInkPicture? _ink;

    public PageThumbnail()
    {
        Focusable = false;
        Loaded += (_, _) =>
        {
            Listen(Cache);
            if (_bitmapLoad is null && _inkLoad is null)
            {
                Request(keepShown: true); // loaded again: cache hits when nothing changed
            }
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            Listen(null);
        };
    }

    public PageItem? Page
    {
        get => (PageItem?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>True once the page bitmap is drawn.</summary>
    internal bool HasBitmap => _bitmap is not null;

    /// <summary>True once the page's ink is known (possibly no strokes).</summary>
    internal bool HasInk => _ink is not null;

    /// <summary>How many strokes the drawn ink has.</summary>
    internal int StrokeCount => _ink?.Strokes.Count ?? 0;

    internal PageThumbnailCache? Cache => (PageThumbnailCache?)GetValue(CacheProperty);

    internal static void SetCache(DependencyObject element, PageThumbnailCache? cache) => element.SetValue(CacheProperty, cache);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = TryFindResource("PagesThumbnailWidth") as double? ?? PageBitmapRenderer.ThumbnailWidthDip;
        return new Size(width, width * (Page?.AspectRatio ?? 1));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var box = new Rect(size);
        drawingContext.DrawRectangle(TryFindResource("PagePlaceholderBrush") as Brush, null, box);
        if (_bitmap is { } bitmap)
        {
            drawingContext.DrawImage(bitmap, box);
        }

        if (_ink is { } ink && ink.Strokes.Count > 0 && ink.WidthPt > 0)
        {
            var scale = size.Width / ink.WidthPt;
            var minimumDip = TryFindResource("PagesThumbnailMinStrokeWidth") as double? ?? 0.75;
            drawingContext.PushClip(new RectangleGeometry(box));
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            InkDrawings.Draw(drawingContext, ink.Strokes, scale, minimumDip);
            drawingContext.Pop();
            drawingContext.Pop();
        }

        // The page's edge, over everything, so white pages read on the panel in both themes.
        var frame = InkDrawings.RoundPen(TryFindResource("MarkThumbnailBorderBrush") as Brush, 1);
        drawingContext.DrawRectangle(null, frame, new Rect(0.5, 0.5, Math.Max(0, size.Width - 1), Math.Max(0, size.Height - 1)));
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageThumbnail thumbnail)
        {
            if (e.Property == CacheProperty)
            {
                thumbnail.Listen(thumbnail.Cache);
            }

            thumbnail.Request();
        }
    }

    private void Listen(PageThumbnailCache? cache)
    {
        if (ReferenceEquals(cache, _listeningTo))
        {
            return;
        }

        if (_listeningTo is { } old)
        {
            old.InkInvalidated -= OnInkInvalidated;
            old.Cleared -= OnCleared;
            old.PausedChanged -= OnPausedChanged;
        }

        _listeningTo = cache;
        if (cache is not null)
        {
            cache.InkInvalidated += OnInkInvalidated;
            cache.Cleared += OnCleared;
            cache.PausedChanged += OnPausedChanged;
        }
    }

    /// <summary>Hidden: reads under way are given up (what is shown stays). Shown again: what is missing is read.</summary>
    private void OnPausedChanged(object? sender, EventArgs e)
    {
        if (Cache is not { } cache)
        {
            return;
        }

        if (cache.IsPaused)
        {
            Cancel();
        }
        else
        {
            Request(keepShown: true); // cache hits for what is already there
        }
    }

    private void OnInkInvalidated(object? sender, int pageIndex)
    {
        if (Page is { } page && page.PageIndex == pageIndex && Cache is { } cache)
        {
            RequestInk(cache, page, keepShown: true); // the old ink stays until the new one arrives: no flash
        }
    }

    /// <summary>Another document, or the same one reopened: the old picture goes at once.</summary>
    private void OnCleared(object? sender, EventArgs e) => Request();

    /// <summary>Shows the current page: at once from the caches, otherwise the paper until each part is read.</summary>
    private void Request(bool keepShown = false)
    {
        Cancel();
        if (Page is not { } page || Cache is not { } cache)
        {
            SetBitmap(null);
            SetInk(null);
            return;
        }

        RequestBitmap(cache, page, keepShown);
        RequestInk(cache, page, keepShown);
    }

    private void RequestBitmap(PageThumbnailCache cache, PageItem page, bool keepShown)
    {
        CancelBitmap();
        if (cache.TryGetBitmap(page.PageIndex, out var cached))
        {
            SetBitmap(cached);
            return;
        }

        if (!keepShown)
        {
            SetBitmap(null);
        }

        if (cache.IsPaused)
        {
            return; // read when the list is shown again
        }

        var cts = new CancellationTokenSource();
        _bitmapLoad = cts;
        _ = LoadBitmapAsync(cache, page, cts);
    }

    private void RequestInk(PageThumbnailCache cache, PageItem page, bool keepShown)
    {
        CancelInk();
        if (cache.Ink.TryGet(page.PageIndex, out var cached))
        {
            SetInk(cached);
            return;
        }

        if (!keepShown)
        {
            SetInk(null);
        }

        if (cache.IsPaused)
        {
            return; // read when the list is shown again
        }

        var cts = new CancellationTokenSource();
        _inkLoad = cts;
        _ = LoadInkAsync(cache, page, cts);
    }

    private async Task LoadBitmapAsync(PageThumbnailCache cache, PageItem page, CancellationTokenSource cts)
    {
        try
        {
            var bitmap = await cache.GetBitmapAsync(page.PageIndex, cts.Token); // never throws
            if (ReferenceEquals(_bitmapLoad, cts) && ReferenceEquals(Page, page) && bitmap is not null)
            {
                SetBitmap(bitmap);
            }
        }
        finally
        {
            if (ReferenceEquals(_bitmapLoad, cts))
            {
                _bitmapLoad = null;
            }

            cts.Dispose();
        }
    }

    private async Task LoadInkAsync(PageThumbnailCache cache, PageItem page, CancellationTokenSource cts)
    {
        try
        {
            var ink = await cache.Ink.GetAsync(page.PageIndex, cts.Token);
            if (ReferenceEquals(_inkLoad, cts) && ReferenceEquals(Page, page) && ink is not null)
            {
                SetInk(ink);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A page whose ink cannot be read shows its bitmap without ink.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            if (ReferenceEquals(_inkLoad, cts))
            {
                _inkLoad = null;
            }

            cts.Dispose();
        }
    }

    private void Cancel()
    {
        CancelBitmap();
        CancelInk();
    }

    private void CancelBitmap()
    {
        if (_bitmapLoad is { } load)
        {
            _bitmapLoad = null;
            load.Cancel();
        }
    }

    private void CancelInk()
    {
        if (_inkLoad is { } load)
        {
            _inkLoad = null;
            load.Cancel();
        }
    }

    private void SetBitmap(BitmapSource? bitmap)
    {
        if (!ReferenceEquals(_bitmap, bitmap))
        {
            _bitmap = bitmap;
            InvalidateVisual();
        }
    }

    private void SetInk(PageInkPicture? ink)
    {
        if (!ReferenceEquals(_ink, ink))
        {
            _ink = ink;
            InvalidateVisual();
        }
    }
}
