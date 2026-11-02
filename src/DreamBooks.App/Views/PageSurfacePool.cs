using DreamBooks.App.Ink;

namespace DreamBooks.App.Views;

/// <summary>
/// Recycles page surfaces so that scrolling through a long book creates no new ones (NFR-05, NFR-06): a surface
/// leaving the window is reset and kept as one of at most <see cref="SpareCount"/> spares, any further one is
/// discarded (detached from the view model, so nothing keeps it alive).
/// </summary>
internal sealed class PageSurfacePool(Func<InkSurface> create, Action<InkSurface> discard)
{
    /// <summary>Surfaces kept ready beyond the ones in use.</summary>
    public const int SpareCount = 2;

    private readonly Stack<InkSurface> _spares = new();

    /// <summary>How many surfaces this pool has ever created (tests: bounded by the window plus the spares).</summary>
    public int CreatedCount { get; private set; }

    public int SpareAvailable => _spares.Count;

    /// <summary>A spare surface, or a new one.</summary>
    public InkSurface Rent()
    {
        if (_spares.TryPop(out var spare))
        {
            return spare;
        }

        CreatedCount++;
        return create();
    }

    /// <summary>
    /// Takes back a surface that is no longer in the window: any live gesture is dropped and it shows nothing.
    /// The caller removes it from the visual tree first.
    /// </summary>
    public void Return(InkSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Reset(surface);
        if (_spares.Count < SpareCount)
        {
            _spares.Push(surface);
        }
        else
        {
            discard(surface);
        }
    }

    /// <summary>Discards every spare (the view model changed).</summary>
    public void Clear()
    {
        while (_spares.TryPop(out var spare))
        {
            discard(spare);
        }
    }

    /// <summary>Unbinds a surface from its page: no gesture, no editor, no page, no bitmap, no search highlights.</summary>
    public static void Reset(InkSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.CancelLiveGesture();
        surface.HideEraserCursor();
        surface.Editor = null;
        surface.Page = null;
        surface.PageImage = null;
        surface.SetSearchHighlights([], []);
        VirtualPagePanel.SetPageIndex(surface, -1);
    }
}
