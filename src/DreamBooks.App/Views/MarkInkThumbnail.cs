using System.Windows;
using System.Windows.Media;
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

    private CancellationTokenSource? _load;
    private MarkThumbnailCache? _listeningTo;
    private MarkThumbnail? _thumbnail;

    public MarkInkThumbnail()
    {
        Focusable = false;
        Loaded += (_, _) =>
        {
            Listen(Cache);
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
        InkDrawings.Draw(drawingContext, thumbnail.Strokes, scale, minimumDip); // thin ink stays visible
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Pen? Pen(Brush? brush, double thickness) => InkDrawings.RoundPen(brush, thickness);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkInkThumbnail thumbnail)
        {
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

    private void Cancel()
    {
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
