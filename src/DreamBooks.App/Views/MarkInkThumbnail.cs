using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.Core.Marks;

namespace DreamBooks.App.Views;

/// <summary>
/// One marks-list row's picture (FR-103): the mark's band of the page (full width, its extent and one line above and
/// below, <see cref="MarkThumbnailCache.BandOf"/>) on paper, with the mark's strokes and margin notes drawn as vectors
/// at their own page position, scaled uniformly to fit and centred. Strokes are never drawn thinner than
/// <c>MarkThumbnailMinStrokeWidth</c>. Plain paper (the placeholder) until the ink arrives.
/// </summary>
/// <remarks>
/// The ink is asked from the <see cref="CacheProperty"/> (inherited from the list) when <see cref="Mark"/> or the cache
/// changes and when the element is loaded again, so only realised rows (the virtualised list's visible ones) load; a
/// recycled row cancels the load of its previous mark, an unloaded one its own. Decorative: the row carries the
/// automation name.
/// </remarks>
public sealed class MarkInkThumbnail : FrameworkElement
{
    public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
        nameof(Mark), typeof(Mark), typeof(MarkInkThumbnail), new FrameworkPropertyMetadata(null, OnSourceChanged));

    /// <summary>The thumbnail cache, set once on the list and inherited by its rows.</summary>
    internal static readonly DependencyProperty CacheProperty = DependencyProperty.RegisterAttached(
        "Cache",
        typeof(MarkThumbnailCache),
        typeof(MarkInkThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnSourceChanged));

    /// <summary>The page bitmaps (the Pages tab's cache, low-priority queue), set once on the list and inherited by its rows.</summary>
    internal static readonly DependencyProperty PageBitmapsProperty = DependencyProperty.RegisterAttached(
        "PageBitmaps",
        typeof(PageThumbnailCache),
        typeof(MarkInkThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnSourceChanged));

    private CancellationTokenSource? _bitmapLoad;
    private PageThumbnailCache? _bitmapsListeningTo;
    private BitmapSource? _pageBitmap;
    private CancellationTokenSource? _load;
    private MarkThumbnailCache? _listeningTo;
    private MarkThumbnail? _thumbnail;

    public MarkInkThumbnail()
    {
        Focusable = false;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _pageBitmap is null)
            {
                RequestBitmap(Mark, keepShown: true); // the panel is shown again: the page is asked now
            }
            else if (!IsVisible)
            {
                CancelBitmap();
            }
        };
        Loaded += (_, _) =>
        {
            Listen(Cache);
            ListenBitmaps(PageBitmaps);
            if (_load is null)
            {
                // Loaded again (an unload cancelled its read and stopped it listening): ask again, keeping what is
                // shown; a cache hit when nothing changed, a new read when its page was invalidated meanwhile.
                Request(keepShown: true);
            }
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            Listen(null);
            ListenBitmaps(null);
        };
    }

    public Mark? Mark
    {
        get => (Mark?)GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>True once the ink of the current mark is drawn (false while the placeholder shows).</summary>
    internal bool HasInk => _thumbnail is not null;

    /// <summary>How many strokes the drawn ink has (0 while the placeholder shows).</summary>
    internal int StrokeCount => _thumbnail?.Strokes.Count ?? 0;

    internal MarkThumbnailCache? Cache => (MarkThumbnailCache?)GetValue(CacheProperty);

    /// <summary>True once the page bitmap behind the ink is drawn.</summary>
    internal bool HasPageBitmap => _pageBitmap is not null;

    internal PageThumbnailCache? PageBitmaps => (PageThumbnailCache?)GetValue(PageBitmapsProperty);

    internal static void SetPageBitmaps(DependencyObject element, PageThumbnailCache? cache) => element.SetValue(PageBitmapsProperty, cache);

    internal static void SetCache(DependencyObject element, MarkThumbnailCache? cache) => element.SetValue(CacheProperty, cache);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(
            TryFindResource("PagePlaceholderBrush") as Brush,
            Pen(TryFindResource("MarkThumbnailBorderBrush") as Brush, 1),
            new Rect(size));
        if (_thumbnail is not { } thumbnail || thumbnail.BandPt.Width <= 0 || thumbnail.BandPt.Height <= 0)
        {
            return;
        }

        var band = thumbnail.BandPt;
        var scale = Math.Min(size.Width / band.Width, size.Height / band.Height);
        var offsetX = ((size.Width - (band.Width * scale)) / 2) - (band.X * scale);
        var offsetY = ((size.Height - (band.Height * scale)) / 2) - (band.Y * scale);
        var minimumDip = TryFindResource("MarkThumbnailMinStrokeWidth") as double? ?? 1;

        drawingContext.PushClip(new RectangleGeometry(new Rect(size)));
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));
        if (_pageBitmap is { PixelWidth: > 0, PixelHeight: > 0 } page)
        {
            // The page under the ink, in the same page-point space: its width is the band's, its height follows the bitmap.
            drawingContext.DrawImage(page, new Rect(0, 0, band.Width, band.Width * page.PixelHeight / page.PixelWidth));
        }

        InkDrawings.Draw(drawingContext, thumbnail.Strokes, scale, minimumDip); // thin ink stays visible
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Pen? Pen(Brush? brush, double thickness) => InkDrawings.RoundPen(brush, thickness);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkInkThumbnail thumbnail)
        {
            if (e.Property == PageBitmapsProperty)
            {
                thumbnail.ListenBitmaps(thumbnail.PageBitmaps);
            }

            if (e.Property == CacheProperty)
            {
                thumbnail.Listen(thumbnail.Cache);
            }

            thumbnail.Request();
        }
    }

    private void Listen(MarkThumbnailCache? cache)
    {
        if (ReferenceEquals(cache, _listeningTo))
        {
            return;
        }

        if (_listeningTo is { } old)
        {
            old.PageInvalidated -= OnPageInvalidated;
            old.Cleared -= OnCleared;
        }

        _listeningTo = cache;
        if (cache is not null)
        {
            cache.PageInvalidated += OnPageInvalidated;
            cache.Cleared += OnCleared;
        }
    }

    private void OnPageInvalidated(object? sender, int pageIndex)
    {
        if (Mark is { } mark && mark.PageIndex == pageIndex)
        {
            Request(keepShown: true); // the old ink stays until the new one arrives: no flash
        }
    }

    /// <summary>Another document: the old ink goes at once (an equal mark may have other notes now).</summary>
    private void OnCleared(object? sender, EventArgs e) => Request();

    /// <summary>Shows the current mark's ink: at once from the cache, otherwise the placeholder until it is read.</summary>
    private void Request(bool keepShown = false)
    {
        Cancel();
        var mark = Mark;
        RequestBitmap(mark, keepShown);
        var cache = Cache;
        if (mark is null || cache is null)
        {
            SetThumbnail(null);
            return;
        }

        if (cache.TryGet(mark, out var cached))
        {
            SetThumbnail(cached);
            return;
        }

        if (!keepShown)
        {
            SetThumbnail(null);
        }

        var cts = new CancellationTokenSource();
        _load = cts;
        _ = LoadAsync(cache, mark, cts);
    }

    private async Task LoadAsync(MarkThumbnailCache cache, Mark mark, CancellationTokenSource cts)
    {
        try
        {
            var thumbnail = await cache.GetAsync(mark, cts.Token);
            if (ReferenceEquals(_load, cts) && Equals(Mark, mark))
            {
                SetThumbnail(thumbnail);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A row whose ink cannot be read keeps its placeholder.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                _load = null;
            }

            cts.Dispose();
        }
    }

    private void ListenBitmaps(PageThumbnailCache? cache)
    {
        if (ReferenceEquals(cache, _bitmapsListeningTo))
        {
            return;
        }

        if (_bitmapsListeningTo is { } old)
        {
            old.Cleared -= OnBitmapsReset;
            old.BitmapsCleared -= OnBitmapsReset;
            old.RetryRequested -= OnBitmapsReset;
        }

        _bitmapsListeningTo = cache;
        if (cache is not null)
        {
            cache.Cleared += OnBitmapsReset;
            cache.BitmapsCleared += OnBitmapsReset;
            cache.RetryRequested += OnBitmapsReset;
        }
    }

    /// <summary>Another document, another screen scale, or the file is back: the page is asked again, keeping the one shown.</summary>
    private void OnBitmapsReset(object? sender, EventArgs e) => RequestBitmap(Mark, keepShown: true);

    /// <summary>
    /// The page behind the mark: at once from the cache, otherwise rendered through the Pages tab's low-priority queue
    /// while the ink shows alone. Rendered or not, the row never waits for it.
    /// </summary>
    private void RequestBitmap(Mark? mark, bool keepShown)
    {
        CancelBitmap();
        var cache = PageBitmaps;
        if (mark is null || cache is null)
        {
            SetPageBitmap(null);
            return;
        }

        if (cache.TryGetBitmap(mark.PageIndex, out var cached))
        {
            SetPageBitmap(cached);
            return;
        }

        if (IsHiddenAbove())
        {
            return; // nothing renders behind a closed panel or another tab; IsVisibleChanged asks when it is shown
        }

        if (!keepShown)
        {
            SetPageBitmap(null);
        }

        var cts = new CancellationTokenSource();
        _bitmapLoad = cts;
        _ = LoadBitmapAsync(cache, mark, cts);
    }

    /// <summary>True when an ancestor is not visible (the panel is closed or another tab shows): no render is started for it.</summary>
    private bool IsHiddenAbove()
    {
        for (DependencyObject? current = this; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return true;
            }
        }

        return false;
    }

    private async Task LoadBitmapAsync(PageThumbnailCache cache, Mark mark, CancellationTokenSource cts)
    {
        try
        {
            var bitmap = await cache.GetBitmapAsync(mark.PageIndex, cts.Token); // never throws
            if (ReferenceEquals(_bitmapLoad, cts) && Equals(Mark, mark) && bitmap is not null)
            {
                SetPageBitmap(bitmap);
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

    private void CancelBitmap()
    {
        if (_bitmapLoad is { } load)
        {
            _bitmapLoad = null;
            load.Cancel();
        }
    }

    private void SetPageBitmap(BitmapSource? bitmap)
    {
        if (!ReferenceEquals(_pageBitmap, bitmap))
        {
            _pageBitmap = bitmap;
            InvalidateVisual();
        }
    }

    private void Cancel()
    {
        CancelBitmap();
        if (_load is { } load)
        {
            _load = null;
            load.Cancel();
        }
    }

    private void SetThumbnail(MarkThumbnail? thumbnail)
    {
        if (!ReferenceEquals(_thumbnail, thumbnail))
        {
            _thumbnail = thumbnail;
            InvalidateVisual();
        }
    }
}
